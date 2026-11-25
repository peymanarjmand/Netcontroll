using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

namespace Netcontroll
{
public enum PathMode
{
    System,
    Direct,
    Vpn
}
public class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public PathMode Mode { get; set; } = PathMode.Direct;
    public bool Enabled { get; set; } = true;
    public bool Subdomains { get; set; } = true;
    public bool Guard { get; set; }
    public int Port { get; set; }
    public Rule Copy()
    {
        return (Rule)MemberwiseClone();
    }
}
public sealed class ChromeRuleChange
{
    public string Host { get; set; }
    public PathMode Mode { get; set; }
    public bool Remove { get; set; }
    public bool Subdomains { get; set; }
}
public class Settings
{
    public List<Rule> Domains { get; set; } = new List<Rule>();
    public List<Rule> Addresses { get; set; } = new List<Rule>();
    public List<Rule> Apps { get; set; } = new List<Rule>();
    public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
    public PathMode Docker { get; set; } = PathMode.System;
    // Keep System for files written before default routing was introduced.
    // New installs opt into Direct in Load; upgrading never changes an existing route.
    public PathMode ChromeDefault { get; set; } = PathMode.System;
    public PathMode Codex { get; set; } = PathMode.Vpn;
    public string CodexExecutable { get; set; } = "";
    public bool WindowsDefaultDirect { get; set; }
    public static string Folder
    {
        get {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "Netcontroll");
        }
    }
    public static string FileName
    {
        get {
            return Path.Combine(Folder, "settings.json");
        }
    }
    public static Settings Load()
    {
        var value = File.Exists(FileName) ? Json<Settings>(File.ReadAllText(FileName))
                                         : new Settings { ChromeDefault = PathMode.Direct };
        if (value == null)
            throw new InvalidDataException("فایل تنظیمات برنامه معتبر نیست.");
        value.Validate();
        return value;
    }
    public static T Json<T>(string value)
    {
        return new JavaScriptSerializer().Deserialize<T>(value);
    }
    public static string Text(object value)
    {
        return new JavaScriptSerializer().Serialize(value);
    }
    public Settings Clone()
    {
        return Json<Settings>(Text(this));
    }
    public void Validate()
    {
        if (Domains == null || Addresses == null || Apps == null || Labels == null ||
            !ValidMode(ChromeDefault) || !ValidMode(Docker) || !ValidMode(Codex) ||
            Domains.Concat(Addresses).Concat(Apps).Any(r => r == null || !ValidMode(r.Mode)))
            throw new InvalidDataException("تنظیمات مسیر اتصال معتبر نیست.");
        CodexExecutable = CodexExecutable ?? "";
    }
    public static bool ValidMode(PathMode mode)
    {
        return mode == PathMode.System || mode == PathMode.Direct || mode == PathMode.Vpn;
    }
    public Settings WithChromeRule(ChromeRuleChange change)
    {
        if (change == null || !ValidMode(change.Mode))
            throw new ArgumentException("مسیر دامنه معتبر نیست.");
        string host = Host(change.Host);
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal))
            throw new ArgumentException("اتصال‌های محلی مرورگر از پراکسی عبور نمی‌کنند.");
        var next = Clone();
        var rule = next.Domains.FirstOrDefault(r => String.Equals(r.Target, host, StringComparison.OrdinalIgnoreCase));
        if (change.Remove)
            next.Domains.RemoveAll(r => String.Equals(r.Target, host, StringComparison.OrdinalIgnoreCase));
        else
        {
            if (rule == null)
            {
                rule = new Rule { Name = host, Target = host };
                next.Domains.Add(rule);
            }
            rule.Mode = change.Mode;
            rule.Subdomains = change.Subdomains;
            rule.Enabled = true;
            next.Domains.RemoveAll(r => r != rule && String.Equals(r.Target, host, StringComparison.OrdinalIgnoreCase));
        }
        return next;
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string temp = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, Text(this), new UTF8Encoding(false));
            if (File.Exists(FileName))
                File.Replace(temp, FileName, null);
            else
                File.Move(temp, FileName);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
    public static string Host(string input)
    {
        if (String.IsNullOrWhiteSpace(input))
            throw new ArgumentException("نام دامنه را وارد کنید.");
        string host = input.Trim().TrimEnd('.');
        if (host.StartsWith("*."))
            host = host.Substring(2);
        host = new IdnMapping().GetAscii(host).ToLowerInvariant();
        if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns ||
            host.Split('.').Any(x => x.Length == 0 || x.Length > 63 || x.StartsWith("-") || x.EndsWith("-") ||
                                     x.Any(c => !(char.IsLetterOrDigit(c) || c == '-'))))
            throw new ArgumentException("نام دامنه معتبر نیست. فقط نام میزبان را وارد کنید.");
        return host;
    }
    public static string IPv4(string input)
    {
        IPAddress ip;
        string s = input.Trim();
        if (!IPAddress.TryParse(s, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.ToString() != s || IPAddress.IsLoopback(ip) || ip.GetAddressBytes()[0] == 0 ||
            ip.GetAddressBytes()[0] >= 224)
            throw new ArgumentException("یک آدرس IPv4 معتبر وارد کنید.");
        return s;
    }
    public PathMode Match(string host)
    {
        string h = host.TrimEnd('.').ToLowerInvariant();
        var rule =
            Domains
                .Where(x => x.Enabled && (h == x.Target ||
                                          (x.Subdomains && h.EndsWith("." + x.Target, StringComparison.Ordinal))))
                .OrderByDescending(x => x.Target.Length)
                .FirstOrDefault();
        return rule == null ? ChromeDefault : rule.Mode;
    }
}
}
