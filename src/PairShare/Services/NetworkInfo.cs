using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PairShare.Services;

/// <summary>Figures out which address other devices on the Wi-Fi can reach this computer at.</summary>
public sealed class NetworkInfo(AppOptions options)
{
    private static readonly string[] VirtualHints =
        ["vEthernet", "VirtualBox", "VMware", "Hyper-V", "docker", "veth", "br-", "virbr", "WSL", "vmnet", "utun", "Loopback"];

    /// <summary>Best address first. Never empty.</summary>
    public IReadOnlyList<string> GetAddresses()
    {
        var result = new List<string>();
        if (options.Address is { } overridden)
        {
            result.Add(overridden);
        }

        foreach (var ip in DiscoverLanIPv4())
        {
            var text = ip.ToString();
            if (!result.Contains(text))
            {
                result.Add(text);
            }
        }

        if (result.Count == 0)
        {
            result.Add("localhost");
        }

        return result;
    }

    public string BaseUrl(string address) =>
        options.Port == 80 ? $"http://{address}" : $"http://{address}:{options.Port}";

    public string AppLink(string address) => BaseUrl(address) + "/";

    public string QuickPairLink(string address, string token) => BaseUrl(address) + "/q/" + token;

    private static IEnumerable<IPAddress> DiscoverLanIPv4()
    {
        var primary = PrimaryOutboundAddress();
        var candidates = new List<(IPAddress Ip, int Score)>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties props;
                try
                {
                    props = nic.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var looksVirtual = VirtualHints.Any(h =>
                    nic.Name.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains(h, StringComparison.OrdinalIgnoreCase));

                foreach (var unicast in props.UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || IsLinkLocal(ip))
                    {
                        continue;
                    }

                    var score = 0;
                    if (ip.Equals(primary)) score += 100;
                    if (hasGateway) score += 20;
                    if (IsPrivate(ip)) score += 10;
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet) score += 5;
                    if (looksVirtual) score -= 50;
                    candidates.Add((ip, score));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        if (primary is not null && !candidates.Any(c => c.Ip.Equals(primary)))
        {
            candidates.Add((primary, 100));
        }

        return candidates.OrderByDescending(c => c.Score).Select(c => c.Ip).Distinct();
    }

    /// <summary>
    /// The address the OS would use to reach the internet. "Connecting" a UDP socket
    /// sends nothing; it only asks the routing table which interface it would use.
    /// </summary>
    private static IPAddress? PrimaryOutboundAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
            return socket.LocalEndPoint is IPEndPoint { Address: var ip } && !IPAddress.Any.Equals(ip) ? ip : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static bool IsLinkLocal(IPAddress ip) => ip.GetAddressBytes() is [169, 254, ..];

    private static bool IsPrivate(IPAddress ip) => ip.GetAddressBytes() switch
    {
        [10, ..] => true,
        [172, >= 16 and <= 31, ..] => true,
        [192, 168, ..] => true,
        _ => false,
    };
}
