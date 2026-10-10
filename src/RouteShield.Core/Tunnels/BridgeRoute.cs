namespace RouteShield.Tunnels;

public enum BridgeKind
{
    /// <summary>The tunnel's own profile — the same outbound the routed applications use.</summary>
    Active,

    /// <summary>Another saved profile, carried by its own outbound inside the same core.</summary>
    Profile,

    /// <summary>Straight out on the physical adapter, for a tab that should not use any VPN.</summary>
    Bypass
}

/// <summary>
/// A loopback proxy the browser extension can point a tab (Firefox) or a site (Chrome) at.
/// Each route becomes one mixed inbound in the running core, so a browser that is itself in
/// the routing policy can still send individual tabs elsewhere: the proxy connection is
/// loopback traffic the TUN never sees.
/// </summary>
public sealed record BridgeRoute(BridgeKind Kind, Guid? ProfileId, string Name, ParsedTunnel? Tunnel)
{
    public static BridgeRoute Bypass() => new(BridgeKind.Bypass, null, "No VPN", null);

    public static BridgeRoute Active(VpnProfile profile) => new(BridgeKind.Active, profile.Id, profile.Name, null);

    public static BridgeRoute Profile(VpnProfile profile, ParsedTunnel tunnel) =>
        new(BridgeKind.Profile, profile.Id, profile.Name, tunnel);
}

/// <summary>Where one <see cref="BridgeRoute"/> ended up listening.</summary>
public sealed record BridgeBinding(BridgeKind Kind, Guid? ProfileId, string Name, int Port);

/// <summary>Loopback ports and the control token for one core run, fixed up front so tests are deterministic.</summary>
public sealed record PortPlan(int ProxyPort, int ControlPort, string ControlSecret, IReadOnlyList<int> BridgePorts)
{
    /// <summary>
    /// Reserves fresh ports for a run. Bridge ports are asked for by preference: a route that
    /// listened on a port last time gets the same one again when it is still free, so a browser
    /// that resolved the route a moment before a reconnect keeps working without waiting for
    /// its next look at the state API.
    /// </summary>
    public static PortPlan Reserve(int bridgeCount, IReadOnlyList<int?>? preferredBridgePorts = null)
    {
        var preferred = new int?[2 + bridgeCount];
        for (var index = 0; index < bridgeCount; index++)
        {
            preferred[2 + index] = preferredBridgePorts is not null && index < preferredBridgePorts.Count
                ? preferredBridgePorts[index]
                : null;
        }

        var ports = RuntimeConfigBuilder.ReserveLoopbackPorts(preferred);

        return new PortPlan(
            ports[0],
            ports[1],
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ports.Skip(2).ToArray());
    }
}
