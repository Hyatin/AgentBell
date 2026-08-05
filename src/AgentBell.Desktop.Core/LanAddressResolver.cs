using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AgentBell.Desktop;

/// <summary>Contains only non-identifying facts needed to rank a LAN address.</summary>
public sealed record LanAddressCandidate
{
    /// <summary>Gets the candidate IPv4 address.</summary>
    public required IPAddress Address { get; init; }

    /// <summary>Gets whether the interface is operational.</summary>
    public required bool IsUp { get; init; }

    /// <summary>Gets whether the interface is loopback.</summary>
    public bool IsLoopbackInterface { get; init; }

    /// <summary>Gets whether the interface is a tunnel.</summary>
    public bool IsTunnel { get; init; }

    /// <summary>Gets whether the adapter appears virtual.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Gets whether the adapter has an IPv4 default gateway.</summary>
    public bool HasDefaultGateway { get; init; }

    /// <summary>Gets whether the adapter is Wi-Fi or physical Ethernet.</summary>
    public bool IsPreferredAdapterType { get; init; }

    /// <summary>Gets a deterministic lower-is-better interface ordering value.</summary>
    public int InterfaceOrder { get; init; } = int.MaxValue;
}

/// <summary>Selects one deterministic RFC1918 IPv4 address for the M2 listener.</summary>
public sealed class LanAddressResolver
{
    private static readonly string[] VirtualMarkers =
    [
        "virtual",
        "vmware",
        "hyper-v",
        "vethernet",
        "loopback",
        "tunnel",
        "wireguard",
        "tailscale",
        "docker",
        "wsl",
        "tap-windows",
    ];

    /// <summary>Resolves the current machine's best private LAN address.</summary>
    public IPAddress? ResolveCurrent() => Resolve(EnumerateCurrentCandidates());

    /// <summary>Ranks already-sanitized candidates for deterministic unit testing.</summary>
    public IPAddress? Resolve(IEnumerable<LanAddressCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var eligible = candidates
            .Where(candidate => candidate.IsUp)
            .Where(candidate => !candidate.IsLoopbackInterface)
            .Where(candidate => !candidate.IsTunnel)
            .Where(candidate => !candidate.IsVirtual)
            .Where(candidate => IsPrivateIpv4(candidate.Address))
            .ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var withGateway = eligible.Where(candidate => candidate.HasDefaultGateway).ToArray();
        var pool = withGateway.Length > 0 ? withGateway : eligible;
        return pool
            .OrderByDescending(candidate => candidate.IsPreferredAdapterType)
            .ThenBy(candidate => candidate.InterfaceOrder)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .First()
            .Address;
    }

    /// <summary>Checks the three RFC1918 IPv4 ranges without accepting loopback or APIPA.</summary>
    public static bool IsPrivateIpv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static IEnumerable<LanAddressCandidate> EnumerateCurrentCandidates()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var networkInterface in interfaces)
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var type = networkInterface.NetworkInterfaceType;
            var isVirtual = LooksVirtual(networkInterface.Name)
                || LooksVirtual(networkInterface.Description);
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork
                && !gateway.Address.Equals(IPAddress.Any));
            var interfaceOrder = int.MaxValue;
            try
            {
                interfaceOrder = properties.GetIPv4Properties()?.Index ?? int.MaxValue;
            }
            catch (NetworkInformationException)
            {
                // The stable address string remains the final ordering fallback.
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                yield return new LanAddressCandidate
                {
                    Address = unicast.Address,
                    IsUp = networkInterface.OperationalStatus == OperationalStatus.Up,
                    IsLoopbackInterface = type == NetworkInterfaceType.Loopback,
                    IsTunnel = type == NetworkInterfaceType.Tunnel,
                    IsVirtual = isVirtual,
                    HasDefaultGateway = hasGateway,
                    IsPreferredAdapterType = type is NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.Ethernet
                        or NetworkInterfaceType.GigabitEthernet
                        or NetworkInterfaceType.FastEthernetFx
                        or NetworkInterfaceType.FastEthernetT,
                    InterfaceOrder = interfaceOrder,
                };
            }
        }
    }

    private static bool LooksVirtual(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && VirtualMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
