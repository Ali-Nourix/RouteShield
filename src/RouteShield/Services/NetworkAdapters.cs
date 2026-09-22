using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RouteShield.Tunnels;

namespace RouteShield.Services;

/// <summary>
/// Which adapter the tunnel will use, and what the user should know about the choice.
/// </summary>
/// <param name="Binding">The adapter to pin, or null to follow the system default route.</param>
/// <param name="Warning">Set when the choice is not the one that was asked for, and why.</param>
public sealed record AdapterChoice(NetworkBinding? Binding, string? Warning);

/// <summary>One adapter as the Outbound setting offers it.</summary>
/// <param name="Name">The Windows connection name, which is also the name the core knows it by.</param>
/// <param name="Mtu">The largest IPv4 packet the adapter carries, when Windows reports it.</param>
public sealed record NetworkAdapterInfo(string Name, string Description, string Address, bool IsVirtual, bool HasGateway, int? Mtu = null)
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

    /// <summary>Addresses used only to ask the routing table a question; nothing is sent to them.</summary>
    private static readonly IPAddress[] ReachabilityTargets = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The binding the settings ask for, without checking whether it works. Used to describe the
    /// choice in the interface; connecting uses <see cref="ResolveAsync"/>, which verifies it.
    /// </summary>
    public static NetworkBinding? Resolve(AppSettings settings) => Candidates(settings).FirstOrDefault();

    /// <summary>
    /// The binding to connect with: the first candidate the operating system can actually route
    /// on. This matters because a corporate VPN in tunnel-all mode leaves no route on the
    /// physical adapter at all, and a socket bound to it fails immediately with "a socket
    /// operation was attempted to an unreachable network" — every dial, including the proxy
    /// server. Rather than bind to an adapter that cannot carry anything, the tunnel says so and
    /// follows the system default.
    /// </summary>
    public static async Task<AdapterChoice> ResolveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in Candidates(settings))
        {
            var adapter = Find(candidate.InterfaceName);
            if (adapter is null)
            {
                continue;
            }

            if (await CanRouteAsync(adapter, cancellationToken))
            {
                return new AdapterChoice(candidate, null);
            }

            AppLog.Write(
                LogCategory.Network,
                $"\"{candidate.InterfaceName}\" has no route to the internet right now; trying another adapter.");
        }

        if (settings.OutboundBinding == OutboundBinding.FollowWindows)
        {
            return new AdapterChoice(null, null);
        }

        // Nothing physical can carry traffic. In practice this means a corporate VPN is running
        // in tunnel-all mode: it takes the default route and leaves no route behind it, which no
        // setting here can undo. Say so, because the alternative is a tunnel that looks
        // connected and reaches nothing.
        var holder = DefaultRouteHolder();
        var blame = holder is null
            ? "No adapter can reach the internet on its own right now."
            : $"Only \"{holder.Name}\" ({holder.Description}) can reach the internet right now, so RouteShield has to go through it.";

        var warning = blame + " Another VPN running in full-tunnel mode does this; disconnect it if the connection fails.";
        AppLog.Write(LogCategory.Network, warning);

        return new AdapterChoice(null, warning);
    }

    /// <summary>The candidates for this setting, best first.</summary>
    private static List<NetworkBinding> Candidates(AppSettings settings)
    {
        if (settings.OutboundBinding == OutboundBinding.FollowWindows)
        {
            return [];
        }

        var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(Usable).ToList();
        var candidates = new List<NetworkBinding>();

        if (settings.OutboundBinding == OutboundBinding.Fixed && settings.OutboundAdapter.Length > 0)
        {
            var named = adapters.FirstOrDefault(adapter =>
                string.Equals(adapter.Name, settings.OutboundAdapter, StringComparison.OrdinalIgnoreCase));

            if (named is not null)
            {
                candidates.Add(new NetworkBinding(named.Name));
            }
        }

        candidates.AddRange(adapters
            .Where(adapter => !IsVirtual(adapter) && HasGateway(adapter))
            .OrderByDescending(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .ThenByDescending(adapter => adapter.Speed)
            .Select(adapter => new NetworkBinding(adapter.Name)));

        return candidates.DistinctBy(binding => binding.InterfaceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static NetworkInterface? Find(string name) => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(adapter => Usable(adapter) && string.Equals(adapter.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a socket bound to this adapter has anywhere to go. Only a routing-level refusal
    /// counts against it: a refused or timed-out connection still proves the packet left, which
    /// is all this asks. Nothing is sent — the handshake is abandoned either way.
    /// </summary>
    private static async Task<bool> CanRouteAsync(NetworkInterface adapter, CancellationToken cancellationToken)
    {
        var local = Addresses(adapter).FirstOrDefault();
        if (local is null)
        {
            return false;
        }

        foreach (var target in ReachabilityTargets)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                socket.Bind(new IPEndPoint(local, 0));

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ReachabilityTimeout);

                await socket.ConnectAsync(new IPEndPoint(target, 443), timeout.Token);
                return true;
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.NetworkUnreachable
                                                        or SocketError.HostUnreachable
                                                        or SocketError.AddressNotAvailable)
            {
                // The routing table has nothing for this destination on this adapter.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No answer in time, which still means the packet had somewhere to go.
                return true;
            }
            catch (SocketException)
            {
                // Refused, reset, filtered: the adapter carried it.
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The adapter holding the default route, when it belongs to another VPN. That is the
    /// situation where only an engine working below the routing table can reach the internet
    /// without going through it.
    /// </summary>
    public static NetworkAdapterInfo? OtherVpnHoldingDefaultRoute() =>
        DefaultRouteHolder() is { IsVirtual: true } holder ? holder : null;

    /// <summary>
    /// The MTU of the adapter that will carry the tunnel: the pinned one, or whatever holds the
    /// default route. Inside another VPN it is well under 1500, and a WireGuard peer has to fit.
    /// </summary>
    public static int? CarryingMtu(NetworkBinding? binding)
    {
        var carrier = binding is null
            ? DefaultRouteHolder()
            : Find(binding.InterfaceName) is { } adapter ? Describe(adapter) : null;

        return carrier?.Mtu;
    }

    /// <summary>The adapter Windows would use for a public address right now.</summary>
    public static NetworkAdapterInfo? DefaultRouteHolder()
    {
        try
        {
            // 1.1.1.1 in network order; the address is never contacted, only looked up.
            if (GetBestInterface(0x01010101, out var index) != 0)
            {
                return null;
            }

            var adapter = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(candidate => IndexOf(candidate) == index);

            return adapter is null ? null : Describe(adapter);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NetworkInformationException)
        {
            return null;
        }
    }

    private static int? IndexOf(NetworkInterface adapter)
    {
        try
        {
            return adapter.Supports(NetworkInterfaceComponent.IPv4) ? adapter.GetIPProperties().GetIPv4Properties().Index : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetBestInterface(uint destinationAddress, out int interfaceIndex);

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
        HasGateway(adapter),
        MtuOf(adapter));

    private static int? MtuOf(NetworkInterface adapter)
    {
        try
        {
            return adapter.Supports(NetworkInterfaceComponent.IPv4)
                   && adapter.GetIPProperties().GetIPv4Properties().Mtu is var mtu and > 0
                ? mtu
                : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
