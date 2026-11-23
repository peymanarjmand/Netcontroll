using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Netcontroll
{
public sealed class Link
{
    public int Index;
    public string Name;
    public IPAddress Source;
    public int Cost;
    public bool Vpn;
}
public static class Network
{
    static readonly object adapterLock = new object();
    static HashSet<int> physical = new HashSet<int>();
    static DateTime refreshAt = DateTime.MinValue;
    static Network()
    {
        NetworkChange.NetworkAddressChanged += (s, e) =>
        { lock (adapterLock) refreshAt = DateTime.MinValue; };
    }
    static bool IsPhysical(int index)
    {
        lock (adapterLock)
        {
            if (DateTime.UtcNow >= refreshAt)
            {
                var result = new HashSet<int>();
                using (var query = new ManagementObjectSearcher(
                           "SELECT InterfaceIndex,PNPDeviceID FROM Win32_NetworkAdapter WHERE InterfaceIndex IS " +
                           "NOT NULL")) using (var rows = query.Get())
                {
                    foreach (ManagementObject row in rows)
                        using (row)
                        {
                            string id = (row["PNPDeviceID"] as string) ?? "";
                            if (new[] { "PCI\\", "USB\\", "BTH\\", "BTHENUM\\", "PCMCIA\\", "SD\\", "ACPI\\" }.Any(
                                    bus => id.StartsWith(bus, StringComparison.OrdinalIgnoreCase)))
                                result.Add(Convert.ToInt32(row["InterfaceIndex"]));
                        }
                }
                physical = result;
                refreshAt = DateTime.UtcNow.AddMinutes(1);
            }
            return physical.Contains(index);
        }
    }
    // MIB_IPFORWARDTABLE v1 contains a count and an array of 14 DWORD rows.
    [DllImport("iphlpapi.dll")]
    static extern uint GetIpForwardTable(IntPtr table, ref int size, bool order);
    static Dictionary<int, int> Costs(bool vpn)
    {
        int size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        var result = new Dictionary<int, int>();
        if (size <= 0)
            return result;
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(table, ref size, false) != 0)
                return result;
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                int row = 4 + i * 56, dest = Marshal.ReadInt32(table, row),
                    mask = Marshal.ReadInt32(table, row + 4);
                if (!((mask == 0 && dest == 0) || (vpn && mask == 128 && (dest == 0 || dest == 128))))
                    continue;
                int index = Marshal.ReadInt32(table, row + 16), metric = Marshal.ReadInt32(table, row + 36);
                if (!result.ContainsKey(index) || result[index] > metric)
                    result[index] = metric;
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }
    public static Link Choose(PathMode mode)
    {
        if (mode == PathMode.System)
            return null;
        bool wantVpn = mode == PathMode.Vpn;
        var costs = Costs(wantVpn);
        var links = new List<Link>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.Supports(NetworkInterfaceComponent.IPv4))
                continue;
            string label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            bool vpn = label.Contains("openvpn") || label.Contains("wintun") || label.Contains("tap-windows") ||
                       label.Contains("wireguard");
            if (vpn != wantVpn)
                continue;
            var props = nic.GetIPProperties();
            var v4 = props.GetIPv4Properties();
            if (v4 == null || !costs.ContainsKey(v4.Index))
                continue;
            if (!wantVpn && !IsPhysical(v4.Index))
                continue;
            var addr = props.UnicastAddresses.FirstOrDefault(
                a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) &&
                     a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred);
            if (addr != null)
                links.Add(new Link { Index = v4.Index, Name = nic.Name, Source = addr.Address,
                                     Cost = costs[v4.Index], Vpn = vpn });
        }
        var chosen = links.OrderBy(l => l.Cost).ThenBy(l => l.Index).FirstOrDefault();
        if (chosen == null)
            throw new InvalidOperationException(wantVpn ? "مسیر فعال VPN پیدا نشد."
                                                        : "مسیر اینترنت عادی پیدا نشد.");
        return chosen;
    }
    public static async Task<TcpClient> Connect(string host, int port, PathMode mode)
    {
        var link = Choose(mode);
        var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        Exception last = null;
        IPAddress destination = null;
        foreach (var addr in addresses.Where(a => link == null || a.AddressFamily == AddressFamily.InterNetwork))
        {
            var client = new TcpClient(addr.AddressFamily);
            try
            {
                if (link != null)
                {
                    // IP_UNICAST_IF takes a network-byte-order interface index on Windows.
                    client.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31,
                                                  IPAddress.HostToNetworkOrder(link.Index));
                    client.Client.Bind(new IPEndPoint(link.Source, 0));
                }
                var pending = client.ConnectAsync(addr, port);
                if (await Task.WhenAny(pending, Task.Delay(6000)).ConfigureAwait(false) != pending)
                    throw new TimeoutException();
                await pending.ConfigureAwait(false);
                return client;
            }
            catch (Exception ex)
            {
                last = ex;
                destination = addr;
                client.Close();
            }
        }
        if (destination == null)
            throw new InvalidOperationException("DNS برای این میزبان آدرس سازگار با مسیر انتخابی برنگرداند.");
        string source = link == null ? "مسیر سیستم" : link.Name + " (" + link.Source + ")";
        string reason = last is TimeoutException ? "پاسخ شروع اتصال TCP در ۶ ثانیه نرسید."
                                                 : last.GetBaseException().Message;
        throw new InvalidOperationException(source + " → " + new IPEndPoint(destination, port) + "   ·   " + reason, last);
    }
}
}
