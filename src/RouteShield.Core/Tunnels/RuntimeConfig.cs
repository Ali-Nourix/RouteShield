namespace RouteShield.Tunnels;

/// <summary>
/// One generated sing-box configuration together with the loopback endpoints the app
/// needs in order to talk to the core it just described.
/// </summary>
/// <param name="Json">The configuration handed to <c>sing-box run -c</c>.</param>
/// <param name="ProxyPort">Loopback mixed inbound, used by the exit-IP and latency probe.</param>
/// <param name="ControlPort">Loopback Clash API port, used for throughput counters.</param>
/// <param name="ControlSecret">Bearer token for the Clash API, regenerated on every start.</param>
/// <param name="Bridges">The browser-facing proxies and the ports they listen on.</param>
/// <param name="Members">The nodes of an automatic group, by tag; empty when one node was named.</param>
public sealed record RuntimeConfig(
    string Json,
    int ProxyPort,
    int ControlPort,
    string ControlSecret,
    IReadOnlyList<BridgeBinding> Bridges,
    IReadOnlyList<GroupMember> Members)
{
    public Uri ProxyUri => new($"http://127.0.0.1:{ProxyPort}");

    public Uri ControlUri => new($"http://127.0.0.1:{ControlPort}");

    public bool IsAutomatic => Members.Count > 0;

    /// <summary>The display name behind a member tag reported by the core, or the tag itself when unknown.</summary>
    public string MemberName(string tag) =>
        Members.FirstOrDefault(member => member.Tag == tag)?.Name ?? tag;
}
