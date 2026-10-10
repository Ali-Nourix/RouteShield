namespace RouteShield.Tunnels;

/// <summary>What connecting to a profile comes down to, and what was left out on the way.</summary>
/// <param name="LeftOut">One line per library entry that could not join an automatic group, and why.</param>
public sealed record ResolvedTarget(ConnectionTarget Target, IReadOnlyList<string> LeftOut);

/// <summary>
/// Turns the profile the user picked into what the tunnel connects to: its own node, or for an
/// automatic entry every server of its subscription, best first.
/// </summary>
public static class TargetResolver
{
    /// <param name="lastDelays">
    /// The last latency test, by profile: the delay of a node that answered, null for one that
    /// did not. A node missing from it was not tested.
    /// </param>
    public static ResolvedTarget Resolve(VpnProfile profile, IEnumerable<VpnProfile> library, IReadOnlyDictionary<Guid, int?>? lastDelays = null)
    {
        if (!profile.IsAutomatic)
        {
            if (string.IsNullOrWhiteSpace(profile.ConfigText))
            {
                throw new InvalidOperationException($"Profile \"{profile.Name}\" has no configuration body.");
            }

            return new ResolvedTarget(ConnectionTarget.Single(TunnelParser.Parse(profile.ConfigText)), []);
        }

        var leftOut = new List<string>();
        var members = new List<(VpnProfile Profile, ParsedTunnel Tunnel)>();

        foreach (var candidate in library.Where(candidate => !candidate.IsAutomatic && candidate.SubscriptionId == profile.SubscriptionId))
        {
            ParsedTunnel tunnel;
            try
            {
                tunnel = TunnelParser.Parse(candidate.ConfigText);
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException)
            {
                leftOut.Add($"\"{candidate.Name}\" left out of the automatic group: {exception.Message}");
                continue;
            }

            // A provider's quota line is the one entry that must never be in the group: the core
            // carries traffic over the first member until a test passes, and over it for good
            // when none does. Every connection then dies on an address that runs no server.
            if (NodeInspector.IsPlaceholder(tunnel))
            {
                leftOut.Add($"\"{candidate.Name}\" left out of the automatic group: it points at {NodeInspector.AddressOf(tunnel)}, which is a note from the provider, not a server.");
                continue;
            }

            members.Add((candidate, tunnel));
        }

        if (members.Count == 0)
        {
            throw new InvalidOperationException($"\"{profile.Name}\" has no usable node. Refresh the subscription first.");
        }

        // The first member carries everything until the core's own test finishes, and is what it
        // falls back to when no test passes, so the order is the last test's: nodes that
        // answered by speed, then the untested, then the ones that failed. Ties keep library order.
        var ordered = members
            .Select((member, index) => (member.Profile, member.Tunnel, Index: index))
            .OrderBy(member => RankOf(member.Profile.Id, lastDelays))
            .ThenBy(member => DelayOf(member.Profile.Id, lastDelays))
            .ThenBy(member => member.Index)
            .Select(member => (member.Profile.Name, member.Tunnel))
            .ToArray();

        return new ResolvedTarget(ConnectionTarget.Automatic(profile.Name, ordered), leftOut);
    }

    private static int RankOf(Guid id, IReadOnlyDictionary<Guid, int?>? lastDelays) =>
        lastDelays is null || !lastDelays.TryGetValue(id, out var delay) ? 1 : delay is null ? 2 : 0;

    private static int DelayOf(Guid id, IReadOnlyDictionary<Guid, int?>? lastDelays) =>
        lastDelays is not null && lastDelays.TryGetValue(id, out var delay) && delay is { } value ? value : int.MaxValue;
}
