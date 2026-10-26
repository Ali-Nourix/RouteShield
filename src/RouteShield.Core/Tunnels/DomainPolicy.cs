using System.Globalization;
using System.Net;

namespace RouteShield.Tunnels;

/// <summary>Explicit suffix rules; never accepts URLs, regexes, ports or engine scripts.</summary>
public static class DomainPolicy
{
    public const int MaxDomains = 200;
    public static IReadOnlyList<string> Parse(string text)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r", "").Split(['\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#')) continue;
            var value = line.TrimEnd('.');
            if (value.StartsWith("*.")) value = value[2..];
            if (value.StartsWith('.')) value = value[1..];
            try { value = new IdnMapping().GetAscii(value).ToLowerInvariant(); }
            catch (ArgumentException) { throw new FormatException("A routing domain is invalid. Use a domain such as example.com, not a URL."); }
            if (value.Length > 253 || IPAddress.TryParse(value, out _) || !value.Contains('.') ||
                value.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                    label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
                throw new FormatException("Use domain names only (example.com), one per line. URLs, IPs and wildcards inside a domain are not accepted.");
            domains.Add(value);
            if (domains.Count > MaxDomains) throw new FormatException("At most 200 domains are allowed in each routing list.");
        }
        return domains.OrderBy(domain => domain, StringComparer.Ordinal).ToArray();
    }

    public static void Validate(AppSettings settings)
    {
        // Validate persisted/imported lists too; the editor is not a trust boundary.
        _ = Parse(string.Join('\n', settings.DirectDomains));
        _ = Parse(string.Join('\n', settings.ProxyDomains));
        _ = Parse(string.Join('\n', settings.BlockedDomains));
    }
}
