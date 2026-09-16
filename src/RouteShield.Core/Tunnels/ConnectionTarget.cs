namespace RouteShield.Tunnels;

/// <summary>One node inside an automatic group: the tag it carries in the configuration and the name shown for it.</summary>
public sealed record GroupMember(string Tag, string Name);

/// <summary>
/// What the tunnel connects to. Usually one node; with automatic selection, every node of a
/// subscription at once, wrapped in a sing-box <c>urltest</c> group that measures them all,
/// carries traffic over the fastest one, and moves to the next when it fails. The selection
/// is the core's, made from real measurements every few minutes, which is what keeps a
/// subscription with a dying server usable without anyone touching the app.
/// </summary>
public sealed class ConnectionTarget
{
    /// <summary>Tags the group's members carry; the group itself carries <see cref="RuntimeConfigBuilder.ProxyTag"/>.</summary>
    public const string MemberTagPrefix = "auto-";

    private ConnectionTarget(string name, IReadOnlyList<ParsedTunnel> tunnels, IReadOnlyList<string> memberNames)
    {
        Name = name;
        Tunnels = tunnels;
        MemberNames = memberNames;
    }

    public string Name { get; }

    public IReadOnlyList<ParsedTunnel> Tunnels { get; }

    public IReadOnlyList<string> MemberNames { get; }

    /// <summary>True when the core, not the user, picks the node.</summary>
    public bool IsAutomatic => Tunnels.Count > 1;

    public static ConnectionTarget Single(ParsedTunnel tunnel) =>
        new(tunnel.DisplayName, [tunnel], [tunnel.DisplayName]);

    public static ConnectionTarget Automatic(string name, IReadOnlyList<(string Name, ParsedTunnel Tunnel)> members)
    {
        if (members.Count == 0)
        {
            throw new ArgumentException("An automatic group needs at least one node.", nameof(members));
        }

        return new ConnectionTarget(
            name,
            members.Select(member => member.Tunnel).ToArray(),
            members.Select(member => member.Name).ToArray());
    }

    public static string MemberTag(int index) => MemberTagPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
