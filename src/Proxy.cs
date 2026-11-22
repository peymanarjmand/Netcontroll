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
    public const int ChromePort = 17831, DockerPort = 17832;
    readonly object gate = new object();
    readonly Dictionary<int, TcpListener> listeners = new Dictionary<int, TcpListener>();
    readonly HashSet<Session> sessions = new HashSet<Session>();
    readonly SemaphoreSlim capacity = new SemaphoreSlim(256);
    public readonly ConcurrentQueue<Activity> History = new ConcurrentQueue<Activity>();
    Settings settings = new Settings();
    string policyRevision = Guid.NewGuid().ToString("N");
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
        var ports = new HashSet<int> { ChromePort, DockerPort };
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
            if (Settings.Text(settings.Domains) != Settings.Text(next.Domains))
            {
                var previous = policyChanged;
                policyRevision = Guid.NewGuid().ToString("N");
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
        if (p == ChromePort)
            return settings.Match(h);
        if (p == DockerPort)
            return settings.Docker;
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
            s.Port == ChromePort ? "Chrome"
            : s.Port == DockerPort
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
            if (s.Port == ChromePort && request[0] == "GET" &&
                request[1].StartsWith("/chrome-rules/", StringComparison.Ordinal))
            {
                if (!lines.Any(
                        h => h.Equals("Host: 127.0.0.1:" + ChromePort, StringComparison.OrdinalIgnoreCase)) ||
                    !lines.Any(
                        h => h.Equals("X-Netcontroll-Client: chrome-v1", StringComparison.OrdinalIgnoreCase)) ||
                    lines.Any(
                        h => h.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase) &&
                             !h.StartsWith("Origin: chrome-extension://", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException();
                string revision = request[1].Substring("/chrome-rules/".Length);
                Task change;
                lock (gate) change = revision == policyRevision ? policyChanged.Task : Task.CompletedTask;
                await Task.WhenAny(change, Task.Delay(18000)).ConfigureAwait(false);
                string json;
                lock (gate) json =
                    Settings.Text(new { revision = policyRevision,
                                        rules = settings.Domains.Where(x => x.Enabled)
                                                    .OrderByDescending(x => x.Target.Length)
                                                    .Select(x => new { host = x.Target, sub = x.Subdomains,
                                                                       route = x.Mode != PathMode.System })
                                                    .ToArray() });
                byte[] body = Encoding.UTF8.GetBytes(json);
                byte[] header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nCache-Control: " +
                    "no-store\r\nConnection: close\r\nContent-Length: " +
                    body.Length + "\r\n\r\n");
                await input.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                await input.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
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
                Record(s, "", false, e.GetBaseException().Message);
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
    public void Dispose()
    {
        lock (gate)
        {
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
