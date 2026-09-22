using System.Text.Json.Nodes;

namespace RouteShield.Tunnels;

/// <summary>
/// A share link or JSON blob translated into the sing-box node that will carry traffic.
/// WireGuard arrives as an <see cref="Endpoint"/>; every other protocol as an <see cref="Outbound"/>.
/// </summary>
public sealed class ParsedTunnel
{
    public required string FormatName { get; init; }

    public required string DisplayName { get; init; }

    public JsonObject? Outbound { get; init; }

    public JsonObject? Endpoint { get; init; }

    /// <summary>
    /// False when the node has no IPv6 of its own — a WireGuard peer with only a v4 address.
    /// Sending it a v6 destination fails outright, so the tunnel is built without v6 instead.
    /// </summary>
    public bool CarriesIpv6 { get; init; } = true;

    /// <summary>
    /// The most this node can carry in one frame, when it says. A WireGuard peer wraps each
    /// packet in one datagram, so the tunnel interface must not offer more than the peer's MTU.
    /// </summary>
    public int? LinkMtu { get; init; }

    /// <summary>
    /// The WireGuard configuration exactly as the user supplied it, for engines that read the
    /// INI format themselves. Null for everything that did not arrive as a .conf.
    /// </summary>
    public string? WireGuardConf { get; init; }

    /// <summary>
    /// True when the .conf carries AmneziaWG obfuscation (junk packets, custom headers). The
    /// sing-box core speaks plain WireGuard only and would silently drop those settings, which
    /// against a DPI filter is the difference between connecting and not.
    /// </summary>
    public bool IsAmneziaWg { get; init; }

    public List<string> Warnings { get; } = [];

    public JsonObject Node =>
        Outbound ?? Endpoint ?? throw new InvalidOperationException("The configuration has no usable node.");
}
