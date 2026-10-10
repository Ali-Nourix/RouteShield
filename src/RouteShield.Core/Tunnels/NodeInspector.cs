using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace RouteShield.Tunnels;

/// <summary>
/// Recognises the entries a subscription carries that are not servers at all.
///
/// Panels put the account's remaining traffic and expiry into the subscription as extra "nodes"
/// so that any client shows them in its list — a name like "7.75 GB left" on a link that points
/// nowhere: 1.1.1.1:53966, 127.0.0.1:1, 0.0.0.0. Such an entry must never reach an automatic
/// group. When no member of a group passes its test, the core falls back to the first member,
/// and the first entry of a subscription is exactly where panels put these lines.
///
/// Only the address decides. The name does not: some panels write the remaining traffic into the
/// name of every real node, and a name rule would throw all of those away.
/// </summary>
public static class NodeInspector
{
    /// <summary>Public resolvers. Nothing proxies on them, and they are what panels point placeholders at.</summary>
    private static readonly HashSet<IPAddress> ResolverAddresses =
    [
        IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1"),
        IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"),
        IPAddress.Parse("9.9.9.9"), IPAddress.Parse("149.112.112.112"),
        IPAddress.Parse("208.67.222.222"), IPAddress.Parse("208.67.220.220"),
        IPAddress.Parse("94.140.14.14"), IPAddress.Parse("94.140.15.15")
    ];

    public static bool IsPlaceholder(ParsedTunnel tunnel)
    {
        if (tunnel.Outbound is { } outbound)
        {
            // A port range (Hysteria2 hopping) stands in for the single port.
            var hasPortRange = outbound["server_ports"] is JsonArray { Count: > 0 };
            return IsPlaceholder(
                TunnelParser.ReadString(outbound, "server"),
                hasPortRange ? null : TunnelParser.ReadInt(outbound, "server_port"));
        }

        if (tunnel.Endpoint?["peers"] is JsonArray { Count: > 0 } peers)
        {
            return peers.OfType<JsonObject>().All(peer =>
                IsPlaceholder(TunnelParser.ReadString(peer, "address"), TunnelParser.ReadInt(peer, "port")));
        }

        return false;
    }

    /// <summary>Where the node points, as "host:port", for saying why it was left out.</summary>
    public static string AddressOf(ParsedTunnel tunnel)
    {
        var node = tunnel.Outbound ?? (tunnel.Endpoint?["peers"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        if (node is null)
        {
            return "no address";
        }

        var host = TunnelParser.ReadString(node, tunnel.Outbound is null ? "address" : "server");
        var port = TunnelParser.ReadInt(node, tunnel.Outbound is null ? "port" : "server_port");

        if (string.IsNullOrWhiteSpace(host))
        {
            return "no address";
        }

        var shown = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return port is null ? shown : $"{shown}:{port}";
    }

    public static bool IsPlaceholder(string? host, int? port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (port is <= 1)
        {
            return true;
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address)
               || address.Equals(IPAddress.Any)
               || address.Equals(IPAddress.IPv6Any)
               || address.Equals(IPAddress.Broadcast)
               || address.IsIPv6LinkLocal
               || address.IsIPv6Multicast
               || IsIpv4LinkLocalOrMulticast(address)
               || ResolverAddresses.Contains(address);
    }

    private static bool IsIpv4LinkLocalOrMulticast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return (bytes[0] == 169 && bytes[1] == 254) || bytes[0] >= 224;
    }
}
