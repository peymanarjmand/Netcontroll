// Behavioral integration checks against the production settings, serializer and loopback HTTP service.
// Compile with tests/CoreRouting.ps1; no external request, VPN, administrator access or user-data writes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Netcontroll;

public static class CoreRoutingTests
{
    static int assertions;
    static void Check(bool pass, string label)
    {
        if (!pass) throw new Exception("FAIL: " + label);
        assertions++;
        Console.WriteLine("PASS " + label);
    }
    sealed class Reply { public int Code; public string Body; }
    static Reply Request(int port, string path, string method = "GET", string body = "", string extra = "", bool clientHeader = true, string host = null)
    {
        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 6000;
            using (var stream = client.GetStream())
            {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string text = method + " " + path + " HTTP/1.1\r\nHost: " + (host ?? "127.0.0.1:" + port) +
                    "\r\nConnection: close\r\n" + (clientHeader ? "X-Netcontroll-Client: chrome-v1\r\n" : "") + extra;
                if (method == "POST")
                    text += "Content-Type: application/json\r\nContent-Length: " + payload.Length + "\r\n";
                byte[] head = Encoding.ASCII.GetBytes(text + "\r\n");
                stream.Write(head, 0, head.Length);
                if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    string answer = Encoding.UTF8.GetString(memory.ToArray());
                    int separator = answer.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    return new Reply { Code = Int32.Parse(answer.Split(' ')[1]), Body = answer.Substring(separator + 4) };
                }
            }
        }
    }
    static Dictionary<string, object> Policy(Reply response)
    {
        Check(response.Code == 200, "control API returns 200");
        return Settings.Json<Dictionary<string, object>>(response.Body);
    }
    static void ModelChecks()
    {
        var legacy = Settings.Json<Settings>("{\"Domains\":[],\"Apps\":[],\"Addresses\":[]}");
        Check(legacy.ChromeDefault == PathMode.System, "old settings keep system default");
        Check(!legacy.WindowsDefaultDirect, "old settings do not enable whole-system direct routing");
        var settings = new Settings { ChromeDefault = PathMode.Direct, Codex = PathMode.Vpn, CodexExecutable = @"C:\Apps\Codex.exe" };
        Check(settings.Match("ordinary.example") == PathMode.Direct, "unlisted domains use direct default");
        settings.Domains.Add(new Rule { Name = "Parent", Target = "example.com", Mode = PathMode.Vpn });
        settings.Domains.Add(new Rule { Name = "Child", Target = "local.example.com", Mode = PathMode.Direct, Subdomains = false });
        Check(settings.Match("www.example.com") == PathMode.Vpn, "VPN allowlist includes subdomains");
        Check(settings.Match("local.example.com") == PathMode.Direct, "specific exact domain wins");
        Check(settings.Match("other.local.example.com") == PathMode.Vpn, "exact-only rule does not override child requests");
        Check(settings.Match("notexample.com") == PathMode.Direct, "suffix boundary stays direct");
        Check(settings.Match("WWW.EXAMPLE.COM.") == PathMode.Vpn, "case and final dot normalization");
        settings.Domains[0].Enabled = false;
        Check(settings.Match("www.example.com") == PathMode.Direct, "disabled allowlist falls back to default");
        settings.ChromeDefault = PathMode.Vpn;
        Check(settings.Match("ordinary.example") == PathMode.Vpn, "inverse VPN default supported");
        settings.Domains[1].Mode = PathMode.System;
        Check(settings.Match("local.example.com") == PathMode.System, "explicit system rule overrides forced default");
        var clone = settings.Clone();
        Check(clone.ChromeDefault == PathMode.Vpn && clone.Codex == PathMode.Vpn && clone.CodexExecutable == settings.CodexExecutable, "new settings survive real serializer roundtrip");
        var changed = settings.WithChromeRule(new ChromeRuleChange { Host = "LOCAL.EXAMPLE.COM", Mode = PathMode.Vpn, Subdomains = true });
        Check(settings.Domains[1].Mode == PathMode.System && changed.Domains[1].Mode == PathMode.Vpn, "popup mutation clones settings");
        Check(changed.Domains[1].Name == "Child" && changed.Domains[1].Id == settings.Domains[1].Id, "popup preserves existing rule identity and name");
        changed.Domains.Add(changed.Domains[1].Copy());
        changed = changed.WithChromeRule(new ChromeRuleChange { Host = "local.example.com", Mode = PathMode.Direct });
        Check(changed.Domains.Count(r => r.Target == "local.example.com") == 1, "editing collapses duplicate legacy exact rules");
        changed = changed.WithChromeRule(new ChromeRuleChange { Host = "local.example.com", Remove = true });
        Check(changed.Match("local.example.com") == PathMode.Vpn, "reset removes exact rule and restores default or inheritance");
        bool rejected = false;
        try { changed.WithChromeRule(new ChromeRuleChange { Host = "localhost", Mode = PathMode.Vpn }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "local browser addresses cannot become public site rules");
        rejected = false;
        try { new Settings { ChromeDefault = (PathMode)77 }.Validate(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "unknown route enum fails closed");
    }
    static void LaunchChecks()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Netcontroll-Core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "resources"));
        try
        {
            string file = Path.Combine(folder, "Codex.exe");
            File.WriteAllText(file, "fixture, never executed");
            File.WriteAllText(Path.Combine(folder, "resources", "app.asar"), "fixture");
            Check(AppProfiles.IsCodexDesktop(file), "detect desktop layout without CLI or Store alias fallback");
            var info = AppProfiles.CodexStartInfo(new Settings { CodexExecutable = file });
            Check(!info.UseShellExecute && info.FileName == file, "desktop starts exact selected file with scoped environment");
            Check(info.EnvironmentVariables["HTTPS_PROXY"] == "http://127.0.0.1:17833", "Codex backend receives dedicated proxy");
            Check(info.EnvironmentVariables["NO_PROXY"] == "localhost,127.0.0.1,::1", "OAuth and local app callbacks remain local");
            Check(info.Arguments.Contains("--proxy-server=\"http://127.0.0.1:17833\""), "Electron desktop receives explicit Chromium proxy");
            Check(info.Arguments.Contains("--disable-quic") && !info.Arguments.Contains("--no-sandbox"), "proxied desktop disables QUIC while retaining sandbox");
            Check(AppProfiles.BuildStartInfo(file, 17900).Arguments.Length == 0, "arbitrary apps do not receive Chromium flags");
            File.Delete(Path.Combine(folder, "resources", "app.asar"));
            bool rejected = false;
            try { AppProfiles.CodexStartInfo(new Settings { CodexExecutable = file }); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "CLI-only executable is not presented as Codex Desktop");
        }
        finally { Directory.Delete(folder, true); }
    }
    static void RouterChecks()
    {
        int first = new Random().Next(30000, 52000);
        Router router = null;
        Settings state = new Settings { ChromeDefault = PathMode.Direct };
        for (int attempt = 0; attempt < 20; attempt++)
        {
            router = new Router(first, first + 1);
            try { router.Configure(state); break; }
            catch (SocketException) { router.Dispose(); router = null; first += 7; }
        }
        if (router == null) throw new Exception("Cannot allocate loopback test ports");
        using (router)
        {
            var policy = Policy(Request(first, "/chrome-rules/"));
            string token = (string)policy["token"], revision = (string)policy["revision"];
            Check((int)policy["defaultMode"] == 1 && (bool)policy["defaultRoute"], "policy sends default direct via local proxy");
            Check(token.Length == 64, "mutation token is independent high entropy capability");
            state.ChromeDefault = PathMode.Vpn;
            router.Configure(state);
            policy = Policy(Request(first, "/chrome-rules/"));
            Check((string)policy["revision"] != revision && (int)policy["defaultMode"] == 2, "default change revises effective Chrome policy");
            int mutations = 0;
            router.ChromeRuleRequested = change => {
                mutations++; state = state.WithChromeRule(change); router.Configure(state); return Task.FromResult(true);
            };
            string auth = "X-Netcontroll-Token: " + token + "\r\n";
            string body = "{\"host\":\"example.com\",\"mode\":\"vpn\",\"subdomains\":true}";
            Check(Request(first, "/chrome-site/", "POST", body).Code == 403, "missing mutation token rejected");
            Check(Request(first, "/chrome-site/", "POST", body, "X-Netcontroll-Token: wrong\r\n").Code == 403, "wrong token rejected");
            Check(Request(first, "/chrome-site/", "POST", body, auth + auth).Code == 403, "duplicate token headers rejected");
            Check(Request(first, "/chrome-site/", "POST", body, auth + "Origin: https://evil.example\r\n").Code == 403, "ordinary web origin rejected with even a correct token");
            Check(Request(first, "/chrome-site/", "POST", body, auth, false).Code == 403, "missing client marker rejected");
            Check(Request(first, "/chrome-site/", "POST", body, auth, true, "evil.example").Code == 403, "DNS rebinding Host rejected");
            Check(Request(first, "/chrome-site/", "OPTIONS", "", auth).Code == 405, "preflight cannot enable web page mutations");
            Check(Request(first, "/chrome-site/", "POST", "broken", auth).Code == 400, "invalid JSON rejected");
            Check(Request(first, "/chrome-site/", "POST", new string('x', 4097), auth).Code == 400, "oversized JSON rejected");
            Check(Request(first, "/chrome-site/", "POST", "{\"host\":\"example.com\",\"mode\":\"unknown\"}", auth).Code == 400, "unknown route rejected");
            Check(mutations == 0, "rejected requests never reach settings owner");
            Check(Request(first, "/chrome-site/", "POST", body, auth + "Origin: chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n").Code == 200, "authenticated extension mutation succeeds");
            Check(mutations == 1 && state.Match("assets.example.com") == PathMode.Vpn, "site mutation commits through settings owner");
            Check(Request(first, "/chrome-site/", "POST", "{\"host\":\"example.com\",\"mode\":\"default\"}", auth).Code == 200 && state.Domains.Count == 0, "reset removes only the requested host rule");
            router.ChromeRuleRequested = change => Task.FromResult(false);
            Check(Request(first, "/chrome-site/", "POST", body, auth).Code == 409, "busy settings owner produces conflict");
            router.ChromeRuleRequested = change => { throw new IOException("simulated save failure"); };
            Check(Request(first, "/chrome-site/", "POST", body, auth).Code == 500, "save error is not reported as success");
            router.ChromeRuleRequested = null;
            Check(Request(first, "/chrome-site/", "POST", body, auth).Code == 503, "unready desktop callback produces service-unavailable");
            foreach (int port in Enumerable.Range(first + 1, 4))
                using (var connection = new TcpClient())
                {
                    connection.Connect(IPAddress.Loopback, port);
                    Check(connection.Connected, "dedicated loopback service " + (port - first) + " listening");
                }
            state.ChromeDefault = PathMode.System;
            state.Codex = PathMode.System;
            router.Configure(state);
            TunnelClosesOnChange(router, first, "Chrome", () => {
                state.ChromeDefault = PathMode.Direct; router.Configure(state);
            }, "changing browser default closes an existing TCP tunnel");
            TunnelClosesOnChange(router, first + 2, "Codex", () => {
                state.Codex = PathMode.Vpn; router.Configure(state);
            }, "changing Codex route closes its existing TCP tunnel");
            Check(router.History.ToArray().Any(a => a.Source == "Codex" && a.Success && a.Mode == PathMode.System),
                  "Codex endpoint labels observed connections separately");
        }
    }
    static void TunnelClosesOnChange(Router router, int proxyPort, string source, Action change, string label)
    {
        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        try
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, proxyPort);
                client.ReceiveTimeout = 6000;
                var stream = client.GetStream();
                string authority = "127.0.0.1:" + ((IPEndPoint)target.LocalEndpoint).Port;
                byte[] header = Encoding.ASCII.GetBytes("CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\n\r\n");
                stream.Write(header, 0, header.Length);
                var answer = new StringBuilder();
                while (!answer.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int value = stream.ReadByte();
                    if (value < 0) throw new IOException("Proxy closed before CONNECT response");
                    answer.Append((char)value);
                    if (answer.Length > 4096) throw new IOException("Unexpected CONNECT response");
                }
                Check(answer.ToString().StartsWith("HTTP/1.1 200 "), "real loopback CONNECT tunnel established");
                using (var accepted = target.AcceptTcpClient())
                {
                    DateTime until = DateTime.UtcNow.AddSeconds(2);
                    while (!router.History.ToArray().Any(a => a.Source == source && a.Success) && DateTime.UtcNow < until)
                        System.Threading.Thread.Sleep(10);
                    change();
                    bool closed;
                    try { closed = stream.ReadByte() < 0; }
                    catch (IOException error)
                    {
                        var socket = error.InnerException as SocketException;
                        closed = socket != null && socket.SocketErrorCode != SocketError.TimedOut;
                    }
                    Check(closed, label);
                }
            }
        }
        finally { target.Stop(); }
    }
    public static int Main()
    {
        try { ModelChecks(); LaunchChecks(); RouterChecks(); Console.WriteLine("Passed " + assertions + " core assertions."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
