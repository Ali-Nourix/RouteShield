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

    public List<string> Warnings { get; } = [];

    public JsonObject Node =>
        Outbound ?? Endpoint ?? throw new InvalidOperationException("The configuration has no usable node.");
}
