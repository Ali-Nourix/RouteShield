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
public sealed record PortPlan(int ProxyPort, int ControlPort, string ControlSecret, IReadOnlyList<int> BridgePorts);
