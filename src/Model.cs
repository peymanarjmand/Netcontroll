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
public class Settings
{
    public List<Rule> Domains { get; set; } = new List<Rule>();
    public List<Rule> Addresses { get; set; } = new List<Rule>();
    public List<Rule> Apps { get; set; } = new List<Rule>();
    public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
    public PathMode Docker { get; set; } = PathMode.System;
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
        return File.Exists(FileName) ? Json<Settings>(File.ReadAllText(FileName)) : new Settings();
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
        return rule == null ? PathMode.System : rule.Mode;
    }
}
}
