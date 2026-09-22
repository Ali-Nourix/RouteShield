using RouteShield.Tunnels;

namespace RouteShield.Subscriptions;

/// <summary>One server a subscription delivered, ready to become a profile.</summary>
public sealed record ImportedNode(string Name, string Format, string Config);

/// <summary>
/// A subscription's entries sorted into what they are: servers, which become profiles, and the
/// notes a panel mixes in among them. A note is an entry such as "7.75 GB left" or
/// "Expires 2026-10-01" whose link points nowhere; it is kept as text on the subscription
/// instead of as a profile nobody can connect to, and that an automatic group would otherwise
/// fall back to.
/// </summary>
public sealed record SubscriptionImport(IReadOnlyList<ImportedNode> Nodes, IReadOnlyList<string> Notes, int Unreadable)
{
    public static SubscriptionImport From(IEnumerable<SubscriptionEntry> entries)
    {
        var nodes = new List<ImportedNode>();
        var notes = new List<string>();
        var unreadable = 0;

        foreach (var entry in entries)
        {
            ParsedTunnel tunnel;
            try
            {
                tunnel = TunnelParser.Parse(entry.Config);
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException)
            {
                unreadable++;
                continue;
            }

            if (NodeInspector.IsPlaceholder(tunnel))
            {
                notes.Add(entry.Name);
                continue;
            }

            nodes.Add(new ImportedNode(entry.Name, tunnel.FormatName, entry.Config));
        }

        return new SubscriptionImport(nodes, notes, unreadable);
    }

    /// <summary>True when a stored profile's configuration is one of those notes rather than a server.</summary>
    public static bool IsNote(string config)
    {
        try
        {
            return NodeInspector.IsPlaceholder(TunnelParser.Parse(config));
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
