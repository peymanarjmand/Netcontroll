using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Netcontroll
{
public sealed class Activity
{
    public DateTime Time;
    public string Host, Source, Local, Message;
    public PathMode Mode;
    public bool Success;
}
public sealed class Router : IDisposable
{
    public const int ChromePort = 17831, DockerPort = 17832, CodexPort = 17833,
                     ChromeVpnPort = 17834, ChromeDirectPort = 17835;
    readonly int chromePort, dockerPort, codexPort, chromeVpnPort, chromeDirectPort;
    public Router() : this(ChromePort, DockerPort, CodexPort, ChromeVpnPort, ChromeDirectPort) {}
    public Router(int chromePort, int dockerPort)
        : this(chromePort, dockerPort, dockerPort + 1, dockerPort + 2, dockerPort + 3) {}
    public Router(int chromePort, int dockerPort, int codexPort, int chromeVpnPort, int chromeDirectPort)
    {
        var ports = new[] { chromePort, dockerPort, codexPort, chromeVpnPort, chromeDirectPort };
        if (ports.Any(p => p < 1024 || p > 65535) || ports.Distinct().Count() != ports.Length)
            throw new ArgumentException("پورت‌های سرویس محلی معتبر نیستند.");
        this.chromePort = chromePort;
        this.dockerPort = dockerPort;
        this.codexPort = codexPort;
        this.chromeVpnPort = chromeVpnPort;
        this.chromeDirectPort = chromeDirectPort;
    }
    // The desktop owns settings and persists mutations on its Dispatcher. The router never
    // writes an out-of-date settings snapshot from a network thread.
    public Func<ChromeRuleChange, Task<bool>> ChromeRuleRequested { get; set; }
    readonly object gate = new object();
    readonly Dictionary<int, TcpListener> listeners = new Dictionary<int, TcpListener>();
    readonly HashSet<Session> sessions = new HashSet<Session>();
    readonly SemaphoreSlim capacity = new SemaphoreSlim(256);
    public readonly ConcurrentQueue<Activity> History = new ConcurrentQueue<Activity>();
    Settings settings = new Settings();
    string policyRevision = Guid.NewGuid().ToString("N");
    readonly string mutationToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    readonly Dictionary<Guid, DateTime> confirmedProfiles = new Dictionary<Guid, DateTime>();
    public int ConfirmedChromeProfiles
    {
        get
        {
            lock (gate) return confirmedProfiles.Values.Count(until => until > DateTime.UtcNow);
        }
    }
    TaskCompletionSource<bool> policyChanged =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    class Session
    {
        public int Port;
        public string Host;
        public PathMode Mode;
        public TcpClient Browser, Remote;
    }
    public void Configure(Settings next)
    {
        if (next == null)
            throw new ArgumentNullException("next");
        next.Validate();
        var ports = new HashSet<int> { chromePort, dockerPort, codexPort, chromeVpnPort, chromeDirectPort };
        foreach (var app in next.Apps.Where(r => r.Enabled))
        {
            if (app.Port < 17900 || app.Port > 17999 || !ports.Add(app.Port))
                throw new InvalidDataException("پورت برنامه نامعتبر است.");
        }
        lock (gate)
        {
            var added = new List<int>();
            try
            {
                foreach (int port in ports.Where(p => !listeners.ContainsKey(p)))
                {
                    var l = new TcpListener(IPAddress.Loopback, port);
                    l.ExclusiveAddressUse = true;
                    l.Start(128);
                    listeners.Add(port, l);
                    added.Add(port);
                    Accept(l, port);
                }
            }
            catch
            {
                foreach (int p in added)
                {
                    listeners[p].Stop();
                    listeners.Remove(p);
                }
                throw;
            }
            if (settings.ChromeDefault != next.ChromeDefault ||
                Settings.Text(settings.Domains) != Settings.Text(next.Domains))
            {
                var previous = policyChanged;
                policyRevision = Guid.NewGuid().ToString("N");
                confirmedProfiles.Clear();
                policyChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                previous.TrySetResult(true);
            }
            settings = next.Clone();
            foreach (int p in listeners.Keys.Where(p => !ports.Contains(p)).ToArray())
            {
                listeners[p].Stop();
                listeners.Remove(p);
            }
            foreach (var s in sessions.ToArray())
                if (!ports.Contains(s.Port) || (s.Host != null && Resolve(s.Port, s.Host) != s.Mode))
                {
                    s.Browser.Close();
                    if (s.Remote != null)
                        s.Remote.Close();
                }
        }
    }
    PathMode Resolve(int p, string h)
    {
        if (p == chromePort)
            return settings.Match(h);
        if (p == dockerPort)
            return settings.Docker;
        if (p == codexPort)
            return settings.Codex;
        if (p == chromeVpnPort)
            return PathMode.Vpn;
        if (p == chromeDirectPort)
            return PathMode.Direct;
        return settings.Apps.Single(x => x.Enabled && x.Port == p).Mode;
    }
    async void Accept(TcpListener l, int port)
    {
        try
        {
            while (true)
            {
                var c = await l.AcceptTcpClientAsync().ConfigureAwait(false);
                if (!capacity.Wait(0))
                {
                    c.Close();
                    continue;
                }
                var s = new Session { Port = port, Browser = c };
                lock (gate)
                {
                    sessions.Add(s);
                }
                Serve(s);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }
    static async Task<byte[]> Header(NetworkStream input)
    {
        using (var buffer = new MemoryStream())
        {
            byte[] block = new byte[2048];
            while (buffer.Length < 32768)
            {
                int n = await input.ReadAsync(block, 0, block.Length).ConfigureAwait(false);
                if (n == 0)
                    throw new EndOfStreamException();
                int offset = (int)buffer.Length;
                buffer.Write(block, 0, n);
                byte[] data = buffer.GetBuffer();
                for (int i = Math.Max(0, offset - 3); i < buffer.Length - 3; i++)
                    if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                        return buffer.ToArray();
            }
            throw new InvalidDataException("Header too large");
        }
    }
    void Record(Session s, string endpoint, bool ok, string message)
    {
        string name;
        lock (gate) name =
            s.Port == chromePort ? "Chrome"
            : s.Port == chromeVpnPort ? "Chrome · VPN"
            : s.Port == chromeDirectPort ? "Chrome · Direct"
            : s.Port == codexPort ? "Codex"
            : s.Port == dockerPort
                ? "Docker"
                : settings.Apps.Where(a => a.Port == s.Port).Select(a => a.Name).FirstOrDefault() ?? "App";
        History.Enqueue(new Activity { Time = DateTime.Now, Source = name, Host = s.Host, Mode = s.Mode,
                                       Local = endpoint, Success = ok, Message = message });
        Activity unused;
        while (History.Count > 120)
            History.TryDequeue(out unused);
    }
    async void Serve(Session s)
    {
        bool sent = false;
        var client = s.Browser;
        TcpClient remote = null;
        try
        {
            var input = client.GetStream();
            var read = Header(input);
            if (await Task.WhenAny(read, Task.Delay(10000)).ConfigureAwait(false) != read)
                return;
            byte[] data = await read.ConfigureAwait(false);
            string raw = Encoding.GetEncoding(28591).GetString(data);
            int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var lines = raw.Substring(0, split).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var request = lines[0].Split(' ');
            if (request.Length != 3 || (request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0"))
                throw new InvalidDataException();
            if (s.Port == chromePort && request[1].StartsWith("/chrome-", StringComparison.Ordinal))
            {
                if (!ValidControlHeaders(lines))
                {
                    await JsonResponse(input, 403, new { error = "درخواست افزونه معتبر نیست." }).ConfigureAwait(false);
                    return;
                }
                if (request[1] == "/chrome-site/")
                {
                    await ChangeChromeSite(input, request, lines, data, split + 4).ConfigureAwait(false);
                    return;
                }
                if (request[0] != "GET" || !request[1].StartsWith("/chrome-rules/", StringComparison.Ordinal))
                {
                    await JsonResponse(input, 404, new { error = "مسیر درخواستی وجود ندارد." }).ConfigureAwait(false);
                    return;
                }
                string revision = request[1].Substring("/chrome-rules/".Length);
                Task change;
                lock (gate)
                {
                    const string profileHeader = "X-Netcontroll-Profile:";
                    string profileLine = lines.FirstOrDefault(h => h.StartsWith(profileHeader, StringComparison.OrdinalIgnoreCase));
                    Guid profile;
                    if (profileLine != null && Guid.TryParseExact(profileLine.Substring(profileHeader.Length).Trim(), "D", out profile))
                    {
                        DateTime now = DateTime.UtcNow;
                        foreach (var old in confirmedProfiles.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                            confirmedProfiles.Remove(old);
                        if (revision == policyRevision && lines.Any(h => h.Equals("X-Netcontroll-Applied: " + revision, StringComparison.OrdinalIgnoreCase)))
                            confirmedProfiles[profile] = now.AddSeconds(50);
                        else
                            confirmedProfiles.Remove(profile);
                    }
                    change = revision == policyRevision ? policyChanged.Task : Task.CompletedTask;
                }
                await Task.WhenAny(change, Task.Delay(18000)).ConfigureAwait(false);
                await JsonResponse(input, 200, ChromePolicy()).ConfigureAwait(false);
                return;
            }
            bool tunnel = request[0] == "CONNECT";
            Uri uri;
            if (!Uri.TryCreate(tunnel ? "https://" + request[1] : request[1], UriKind.Absolute, out uri) ||
                uri.UserInfo.Length != 0 || (tunnel ? uri.PathAndQuery != "/" : uri.Scheme != "http"))
                throw new InvalidDataException();
            string host = uri.IdnHost.Trim('[', ']');
            if (host.Length == 0 || uri.Port <= 0 || host.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidDataException();
            lock (gate)
            {
                s.Host = host;
                s.Mode = Resolve(s.Port, host);
            }
            remote = await Network.Connect(host, uri.Port, s.Mode).ConfigureAwait(false);
            lock (gate)
            {
                if (!client.Connected || Resolve(s.Port, host) != s.Mode)
                    return;
                s.Remote = remote;
            }
            var output = remote.GetStream();
            if (tunnel)
            {
                byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await input.WriteAsync(ok, 0, ok.Length).ConfigureAwait(false);
            }
            else
            {
                var headers =
                    lines.Skip(1).Where(h => !h.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) &&
                                             !h.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) &&
                                             !h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
                string head = request[0] + " " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Authority +
                              "\r\n" + string.Concat(headers.Select(h => h + "\r\n")) +
                              "Connection: close\r\n\r\n";
                byte[] bytes = Encoding.GetEncoding(28591).GetBytes(head);
                await output.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            sent = true;
            Record(s, ((IPEndPoint)remote.Client.LocalEndPoint).Address.ToString(), true, "");
            int rest = split + 4;
            if (rest < data.Length)
                await output.WriteAsync(data, rest, data.Length - rest).ConfigureAwait(false);
            var upload = input.CopyToAsync(output);
            var download = output.CopyToAsync(input);
            var first = await Task.WhenAny(upload, download).ConfigureAwait(false);
            if (first == upload && upload.Status == TaskStatus.RanToCompletion)
            {
                try
                {
                    remote.Client.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException)
                {
                }
                await download.ConfigureAwait(false);
            }
            else
                await first.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (!sent && s.Host != null)
                Record(s, "", false, e.Message);
            if (!sent)
                try
                {
                    byte[] bytes = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    await client.GetStream().WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
                catch
                {
                }
        }
        finally
        {
            client.Close();
            if (remote != null)
                remote.Close();
            lock (gate)
            {
                sessions.Remove(s);
            }
            capacity.Release();
        }
    }
    object ChromePolicy()
    {
        lock (gate)
            return new {
                revision = policyRevision,
                token = mutationToken,
                defaultMode = (int)settings.ChromeDefault,
                defaultRoute = settings.ChromeDefault != PathMode.System,
                rules = settings.Domains.Where(x => x.Enabled)
                            .OrderByDescending(x => x.Target.Length)
                            .Select(x => new { host = x.Target, sub = x.Subdomains,
                                               mode = (int)x.Mode, route = x.Mode != PathMode.System }).ToArray()
            };
    }
    static string SingleHeader(string[] lines, string name)
    {
        string prefix = name + ":";
        string[] matches = lines.Skip(1).Where(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Substring(prefix.Length).Trim() : null;
    }
    bool ValidControlHeaders(string[] lines)
    {
        if (!String.Equals(SingleHeader(lines, "Host"), "127.0.0.1:" + chromePort, StringComparison.OrdinalIgnoreCase) ||
            SingleHeader(lines, "X-Netcontroll-Client") != "chrome-v1")
            return false;
        string[] origins = lines.Skip(1).Where(h => h.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (origins.Length == 0)
            return true; // Extension fetch may omit Origin; web pages cannot send our header without CORS.
        string origin = SingleHeader(lines, "Origin");
        const string scheme = "chrome-extension://";
        return origin != null && origin.StartsWith(scheme, StringComparison.Ordinal) &&
               origin.Length == scheme.Length + 32 && origin.Substring(scheme.Length).All(c => c >= 'a' && c <= 'p');
    }
    bool TokenMatches(string value)
    {
        if (value == null || value.Length != mutationToken.Length)
            return false;
        int difference = 0;
        for (int i = 0; i < value.Length; i++)
            difference |= value[i] ^ mutationToken[i];
        return difference == 0;
    }
    sealed class SiteRequest
    {
        public string host { get; set; }
        public string mode { get; set; }
        public bool subdomains { get; set; }
    }
    async Task ChangeChromeSite(NetworkStream input, string[] request, string[] lines, byte[] initial, int bodyOffset)
    {
        if (request[0] != "POST")
        {
            await JsonResponse(input, 405, new { error = "این درخواست باید با POST ارسال شود." }).ConfigureAwait(false);
            return;
        }
        if (!TokenMatches(SingleHeader(lines, "X-Netcontroll-Token")))
        {
            await JsonResponse(input, 403, new { error = "اتصال برنامه تغییر کرده است. افزونه را دوباره باز کنید." }).ConfigureAwait(false);
            return;
        }
        var handler = ChromeRuleRequested;
        if (handler == null)
        {
            await JsonResponse(input, 503, new { error = "پنجرهٔ برنامه هنوز آماده نیست. دوباره تلاش کنید." }).ConfigureAwait(false);
            return;
        }
        ChromeRuleChange change;
        try
        {
            int length;
            string contentType = SingleHeader(lines, "Content-Type");
            if (contentType == null || !String.Equals(contentType.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase) ||
                !Int32.TryParse(SingleHeader(lines, "Content-Length"), out length) || length <= 0 || length > 4096 ||
                lines.Skip(1).Any(h => h.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("بدنهٔ درخواست معتبر نیست.");
            var bytes = new byte[length];
            int copied = Math.Min(length, initial.Length - bodyOffset);
            if (copied > 0)
                Buffer.BlockCopy(initial, bodyOffset, bytes, 0, copied);
            while (copied < length)
            {
                var read = input.ReadAsync(bytes, copied, length - copied);
                if (await Task.WhenAny(read, Task.Delay(5000)).ConfigureAwait(false) != read)
                    throw new IOException("دریافت درخواست تمام نشد.");
                int count = await read.ConfigureAwait(false);
                if (count == 0)
                    throw new EndOfStreamException();
                copied += count;
            }
            var body = Settings.Json<SiteRequest>(new UTF8Encoding(false, true).GetString(bytes));
            if (body == null || (body.mode != "direct" && body.mode != "vpn" && body.mode != "system" && body.mode != "default"))
                throw new ArgumentException("مسیر انتخاب‌شده معتبر نیست.");
            change = new ChromeRuleChange {
                Host = Settings.Host(body.host), Subdomains = body.subdomains, Remove = body.mode == "default",
                Mode = body.mode == "vpn" ? PathMode.Vpn : body.mode == "direct" ? PathMode.Direct : PathMode.System
            };
            if (change.Host == "localhost" || change.Host.EndsWith(".localhost", StringComparison.Ordinal))
                throw new ArgumentException("اتصال‌های محلی مرورگر از پراکسی عبور نمی‌کنند.");
        }
        catch (Exception ex)
        {
            await JsonResponse(input, 400, new { error = "درخواست معتبر نیست. " + ex.GetBaseException().Message }).ConfigureAwait(false);
            return;
        }
        try
        {
            bool applied = await handler(change).ConfigureAwait(false);
            if (!applied)
                await JsonResponse(input, 409, new { error = "برنامه مشغول ثبت تغییر دیگری است. دوباره تلاش کنید." }).ConfigureAwait(false);
            else
                await JsonResponse(input, 200, ChromePolicy()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await JsonResponse(input, 500, new { error = "قانون ذخیره نشد. " + ex.GetBaseException().Message }).ConfigureAwait(false);
        }
    }
    static async Task JsonResponse(NetworkStream stream, int status, object value)
    {
        byte[] body = Encoding.UTF8.GetBytes(Settings.Text(value));
        string reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 403 ? "Forbidden"
            : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : status == 409 ? "Conflict"
            : status == 503 ? "Service Unavailable" : "Internal Server Error";
        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason +
            "\r\nContent-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
        await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
    }
    public void Dispose()
    {
        lock (gate)
        {
            ChromeRuleRequested = null;
            policyChanged.TrySetResult(true);
            foreach (var l in listeners.Values)
                l.Stop();
            listeners.Clear();
            foreach (var s in sessions)
            {
                s.Browser.Close();
                if (s.Remote != null)
                    s.Remote.Close();
            }
        }
    }
}
}
