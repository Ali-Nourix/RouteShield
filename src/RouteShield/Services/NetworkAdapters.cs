using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RouteShield.Tunnels;

namespace RouteShield.Services;

/// <summary>One adapter as the Outbound setting offers it.</summary>
/// <param name="Name">The Windows connection name, which is also the name the core knows it by.</param>
public sealed record NetworkAdapterInfo(string Name, string Description, string Address, bool IsVirtual, bool HasGateway)
{
    public string Label => Address.Length > 0 ? $"{Name} · {Address}" : Name;
}

/// <summary>
/// Chooses which adapter the tunnel leaves on.
///
/// The problem this exists for: another VPN — a corporate client such as Cisco AnyConnect —
/// installs its own default route while it is connected, and the core, told to detect the
/// default interface, follows it. The proxy server is then dialled from inside the corporate
/// network, where the firewall refuses it, and RouteShield reports itself connected while
/// nothing reaches the outside. Naming a physical adapter instead keeps the tunnel on the
/// connection it was meant to use, whatever else comes and goes.
/// </summary>
public static class NetworkAdapters
{
    /// <summary>
    /// Adapters that belong to something other than a physical connection. Matched against the
    /// driver description, which is what names the product; the connection name is the user's to
    /// change and says nothing ("Ethernet 2" is this machine's Cisco adapter).
    /// </summary>
    private static readonly string[] VirtualMarkers =
    [
        "anyconnect", "cisco", "globalprotect", "palo alto", "forticlient", "fortinet", "pulse",
        "junos", "juniper", "sonicwall", "checkpoint", "check point", "zscaler", "netextender",
        "openvpn", "tap-windows", "tap adapter", "wintun", "wireguard", "tailscale", "zerotier",
        "softether", "hamachi", "radmin", "nordvpn", "nordlynx", "expressvpn", "surfshark",
        "mullvad", "proton", "windscribe", "cloudflare warp", "warp", "psiphon", "outline",
        "vmware", "virtualbox", "hyper-v", "vethernet", "bluetooth", "loopback", "teredo",
        "isatap", "6to4", "npcap", "docker", "wsl", "parsec", "mesh", "routeshield"
    ];

    /// <summary>Every adapter the user could sensibly pick, physical ones first.</summary>
    public static IReadOnlyList<NetworkAdapterInfo> List() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(Usable)
        .Select(Describe)
        .OrderBy(adapter => adapter.IsVirtual)
        .ThenByDescending(adapter => adapter.HasGateway)
        .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>
    /// The binding for the settings as they stand, or null to let the core follow the system.
    /// A named adapter that is no longer there falls back to the automatic choice rather than
    /// failing the connection.
    /// </summary>
    public static NetworkBinding? Resolve(AppSettings settings)
    {
        if (settings.OutboundBinding == OutboundBinding.FollowWindows)
        {
            return null;
        }

        if (settings.OutboundBinding == OutboundBinding.Fixed && settings.OutboundAdapter.Length > 0)
        {
            var named = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(adapter => Usable(adapter)
                                           && string.Equals(adapter.Name, settings.OutboundAdapter, StringComparison.OrdinalIgnoreCase));

            if (named is not null)
            {
                return Bind(named);
            }

            AppLog.Write(
                LogCategory.Network,
                $"The adapter \"{settings.OutboundAdapter}\" is not available; choosing one automatically instead.");
        }

        var physical = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => Usable(adapter) && !IsVirtual(adapter) && HasGateway(adapter))
            .OrderByDescending(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .ThenByDescending(adapter => adapter.Speed)
            .FirstOrDefault();

        if (physical is null)
        {
            AppLog.Write(LogCategory.Network, "No physical adapter could be identified; following the system default route.");
            return null;
        }

        return Bind(physical);
    }

    private static NetworkBinding Bind(NetworkInterface adapter) =>
        new(adapter.Name, Resolvers(adapter));

    /// <summary>
    /// The resolvers reachable on this adapter. The system list is no use once the adapter is
    /// pinned: it holds the ones belonging to whichever VPN we have just stopped dialling
    /// through, and those answer on an interface the core no longer uses.
    /// </summary>
    private static IReadOnlyList<string> Resolvers(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().DnsAddresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any))
                .Select(address => address.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool Usable(NetworkInterface adapter) =>
        adapter.OperationalStatus == OperationalStatus.Up
        && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
        && adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel
        && adapter.NetworkInterfaceType != NetworkInterfaceType.Ppp
        && Addresses(adapter).Count > 0;

    private static bool IsVirtual(NetworkInterface adapter)
    {
        var haystack = $"{adapter.Description} {adapter.Name}".ToLowerInvariant();
        return VirtualMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal));
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses
                .Any(gateway => gateway.Address is { } address
                                && address.AddressFamily == AddressFamily.InterNetwork
                                && !address.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static List<IPAddress> Addresses(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().UnicastAddresses
                .Select(entry => entry.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static NetworkAdapterInfo Describe(NetworkInterface adapter) => new(
        adapter.Name,
        adapter.Description,
        Addresses(adapter).FirstOrDefault()?.ToString() ?? string.Empty,
        IsVirtual(adapter),
        HasGateway(adapter));
}
