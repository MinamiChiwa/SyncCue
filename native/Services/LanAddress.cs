using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SyncCue.Services;

public static class LanAddress
{
    public static string? ResolveShareAddress(string endpoint)
    {
        try
        {
            var uri = new Uri(endpoint);
            var port = uri.Port > 0 ? uri.Port : 3131;
            var host = uri.Host;
            if (host is "127.0.0.1" or "localhost" or "::1")
            {
                var ip = FirstLanIpv4();
                return ip is null ? null : $"ws://{ip}:{port}";
            }
            return $"ws://{host}:{port}";
        }
        catch
        {
            return null;
        }
    }

    public static string? FirstLanIpv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    var s = addr.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;
                    return s;
                }
            }
        }
        catch { /* ignore */ }
        return null;
    }
}
