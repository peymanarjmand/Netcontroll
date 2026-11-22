using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Netcontroll
{
public sealed class Discovered
{
    public string IP, Origin, Adapter, Gateway;
    public PathMode Mode;
    public bool Known, Exact;
    public SavedRoute Saved;
    public string Description
    {
        get {
            return Origin + "   ·   " + (Exact ? "مسیر اختصاصی" : "بدون مسیر اختصاصی فعال") +
                   (String.IsNullOrEmpty(Adapter) ? "" : "   ·   " + Adapter + "   ·   " + Gateway);
        }
    }
}
public sealed class SavedRoute
{
    public string IP { get; set; }
    public string Gateway { get; set; }
    public int Index { get; set; }
    public int Metric { get; set; }
}
public static class Discovery
{
    [StructLayout(LayoutKind.Sequential)]
    struct Route
    {
        public uint Destination, Mask, Policy, Gateway;
        public int Index, Type, Protocol, Age, AutonomousSystem, Metric, Metric2, Metric3, Metric4, Metric5;
    }
    public sealed class Config
    {
        public KnownRule[] Rules { get; set; }
        public bool Adopted { get; set; }
    }
    public sealed class KnownRule
    {
        public string IP { get; set; }
        public string Mode { get; set; }
    }
    [DllImport("iphlpapi.dll")]
    static extern uint GetBestRoute(uint destination, uint source, out Route row);
    [DllImport("iphlpapi.dll")]
    static extern uint GetIpForwardTable(IntPtr table, ref int size, bool order);
    static string Address(uint value)
    {
        return new IPAddress(BitConverter.GetBytes(value)).ToString();
    }
    public static SavedRoute[] Persistent()
    {
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string file = Path.Combine(Entry.Base, @"system\SavedRoutes.ps1");
        using (var p = Process.Start(new ProcessStartInfo(
                   exe, "-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"" + file +
                            "\"") { UseShellExecute = false, CreateNoWindow = true,
                                      RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            {
                p.Kill();
                throw new TimeoutException("خواندن مسیرهای ذخیره‌شده طول کشید.");
            }
            if (p.ExitCode != 0)
                throw new IOException(error.Result);
            return Settings.Json<SavedRoute[]>(output.Result) ?? new SavedRoute[0];
        }
    }
    public static List<Discovered> Read()
    {
        var found = new Dictionary<string, Discovered>();
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var managed = Path.Combine(programs, @"Netcontroll Routes\routes.json");
        Config config = File.Exists(managed) ? Settings.Json<Config>(File.ReadAllText(managed)) : new Config();
        foreach (var entry in config.Rules ?? new KnownRule[0])
        {
            string ip = Settings.IPv4(entry.IP);
            found[ip] =
                new Discovered { IP = ip, Origin = "Netcontroll",
                                 Mode = entry.Mode == "Vpn" ? PathMode.Vpn : PathMode.Direct, Known = true };
        }
        var legacy = Path.Combine(programs, @"DirectServerRoutes\Update-Routes.ps1");
        if (!config.Adopted && File.Exists(legacy))
        {
            string line =
                File.ReadLines(legacy).FirstOrDefault(x => Regex.IsMatch(x, @"^\s*\$prefixes\s*=\s*@\("));
            foreach (Match m in Regex.Matches(line ?? "", @"'(\d+\.\d+\.\d+\.\d+)/32'"))
            {
                string ip = Settings.IPv4(m.Groups[1].Value);
                if (!found.ContainsKey(ip))
                    found[ip] = new Discovered { IP = ip, Origin = "تنظیم قبلی این لپ‌تاپ",
                                                 Known = true, Mode = PathMode.Direct };
            }
        }
        var names = new Dictionary<int, string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.Supports(NetworkInterfaceComponent.IPv4))
                continue;
            var v4 = nic.GetIPProperties().GetIPv4Properties();
            if (v4 != null)
                names[v4.Index] = nic.Name;
        }
        int size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (size > 0)
        {
            var mem = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpForwardTable(mem, ref size, false) == 0)
                {
                    int count = Marshal.ReadInt32(mem), step = Marshal.SizeOf(typeof(Route));
                    for (int i = 0; i < count; i++)
                    {
                        var r = (Route)Marshal.PtrToStructure(IntPtr.Add(mem, 4 + i * step), typeof(Route));
                        if (r.Mask != uint.MaxValue || r.Type != 4 || r.Gateway == 0 ||
                            !(r.Protocol == 3 || r.Protocol == 10006 || r.Protocol == 10007))
                            continue;
                        string ip = Address(r.Destination);
                        if (!found.ContainsKey(ip))
                            found[ip] = new Discovered { IP = ip, Origin = "مسیر میزبان ویندوز • منشأ نامشخص" };
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        foreach (var item in found.Values)
        {
            Route best;
            if (GetBestRoute(BitConverter.ToUInt32(IPAddress.Parse(item.IP).GetAddressBytes(), 0), 0, out best) !=
                0)
                continue;
            item.Exact = best.Mask == uint.MaxValue;
            item.Gateway = Address(best.Gateway);
            string name;
            if (names.TryGetValue(best.Index, out name))
                item.Adapter = name;
        }
        return found.Values.OrderByDescending(x => x.Known).ThenBy(x => x.IP, StringComparer.Ordinal).ToList();
    }
}
}
