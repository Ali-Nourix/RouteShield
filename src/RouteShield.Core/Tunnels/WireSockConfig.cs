using System.Text;

namespace RouteShield.Tunnels;

/// <summary>
/// Turns a WireGuard .conf into the one WireSock runs: the same file, with the routing policy
/// written as WireSock's own [Peer] directives.
///
/// WireSock is what TunnlTo drives. It does not install routes at all: it takes the packets of
/// the chosen applications at the network-driver level and sends them out on the network card
/// itself, underneath the routing table. That is why it keeps working while a corporate VPN
/// owns the default route — the corporate VPN's routes are never consulted.
///
/// The directives are written with the "#@ws:" prefix, which WireSock reads and every other
/// WireGuard client treats as a comment, so the file stays a valid WireGuard configuration.
/// </summary>
public static class WireSockConfig
{
    private const string Prefix = "#@ws:";

    private static readonly string[] OwnedKeys = ["AllowedApps", "DisallowedApps", "DisallowedIPs"];

    /// <summary>The ranges that stay on the local network when local access is allowed.</summary>
    private static readonly string[] PrivateRanges =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];

    /// <param name="conf">The WireGuard configuration as the user supplied it.</param>
    /// <param name="settings">The routing policy to express.</param>
    /// <param name="apps">The applications the policy names.</param>
    /// <param name="self">
    /// RouteShield's own executable. In "selected applications" mode it is tunnelled too, so the
    /// exit-address probe and subscription refreshes measure and use the tunnel.
    /// </param>
    public static string Build(string conf, AppSettings settings, IEnumerable<AppTarget> apps, string? self)
    {
        var lines = conf.Replace("\r\n", "\n").Split('\n');
        if (!lines.Any(line => IsSection(line, "Peer")))
        {
            throw new FormatException("No [Peer] section was found.");
        }

        var directives = Directives(settings, apps, self);
        var output = new StringBuilder(conf.Length + 256);

        foreach (var line in lines)
        {
            if (IsOwnedDirective(line))
            {
                continue;
            }

            output.Append(line).Append("\r\n");

            if (IsSection(line, "Peer"))
            {
                foreach (var directive in directives)
                {
                    output.Append(directive).Append("\r\n");
                }
            }
        }

        return output.ToString().TrimEnd() + "\r\n";
    }

    private static List<string> Directives(AppSettings settings, IEnumerable<AppTarget> apps, string? self)
    {
        // WireSock separates entries with commas, so a path that contains one cannot be named.
        var paths = apps
            .Select(app => app.Path.Trim().Trim('"'))
            .Where(path => path.Length > 0 && !path.Contains(','))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directives = new List<string>();

        switch (settings.RouteMode)
        {
            case RouteMode.SelectedAppsOnly:
                if (paths.Count == 0)
                {
                    throw new InvalidOperationException("Pick at least one application, or switch to the full system tunnel.");
                }

                if (self is { Length: > 0 } && !self.Contains(','))
                {
                    paths.Add(self);
                }

                directives.Add($"{Prefix}AllowedApps = {string.Join(", ", paths)}");
                break;

            case RouteMode.AllExceptSelected:
                if (paths.Count > 0)
                {
                    directives.Add($"{Prefix}DisallowedApps = {string.Join(", ", paths)}");
                }

                break;
        }

        if (settings.AllowLan)
        {
            directives.Add($"{Prefix}DisallowedIPs = {string.Join(", ", PrivateRanges)}");
        }

        return directives;
    }

    private static bool IsSection(string line, string name) =>
        line.Trim().Equals($"[{name}]", StringComparison.OrdinalIgnoreCase);

    /// <summary>Directives this builder writes, prefixed or not: the policy replaces them rather than adding a second copy.</summary>
    private static bool IsOwnedDirective(string line)
    {
        var text = line.Trim();
        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[Prefix.Length..].TrimStart();
        }

        var separator = text.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var key = text[..separator].Trim();
        return OwnedKeys.Any(owned => owned.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
