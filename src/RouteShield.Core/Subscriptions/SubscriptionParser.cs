using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteShield.Subscriptions;

public sealed record SubscriptionEntry(string Name, string Config);

/// <summary>
/// Turns a subscription body into profiles. Providers serve either a plain list of share links
/// or that same list Base64-encoded, and a few serve one sing-box or WireGuard document.
/// </summary>
public static class SubscriptionParser
{
    public const int MaxEntries = 500;

    public static IReadOnlyList<SubscriptionEntry> Parse(string body)
    {
        var text = body.Trim().TrimStart('\uFEFF');
        if (text.Length == 0)
        {
            throw new FormatException("The subscription returned an empty response.");
        }

        var entries = ReadEntries(Decode(text)).Take(MaxEntries).ToArray();
        if (entries.Length == 0)
        {
            throw new FormatException(
                "No VLESS, VMess, Trojan, Shadowsocks, Hysteria2, TUIC, AnyTLS, WireGuard or sing-box configuration was found in the response.");
        }

        return entries;
    }

    private static string Decode(string body)
    {
        if (SplitLines(body).Any(LooksLikeConfig))
        {
            return body;
        }

        try
        {
            var compact = string.Concat(body.Where(character => !char.IsWhiteSpace(character)));
            var decoded = Encoding.UTF8.GetString(DecodeBase64(compact)).Trim();
            return SplitLines(decoded).Any(LooksLikeConfig) ? decoded : body;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return body;
        }
    }

    private static IEnumerable<SubscriptionEntry> ReadEntries(string body)
    {
        if (body.TrimStart().StartsWith('{'))
        {
            yield return new SubscriptionEntry("sing-box subscription", body.Trim());
            yield break;
        }

        if (body.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SubscriptionEntry("WireGuard subscription", body.Trim());
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var line in SplitLines(body))
        {
            if (line.StartsWith('#') || !LooksLikeConfig(line) || !seen.Add(line))
            {
                continue;
            }

            index++;
            yield return new SubscriptionEntry(NameFor(line, index), line);
        }
    }

    private static string[] SplitLines(string body) => body
        .Replace("\r", string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Every share-link scheme the profile parser reads; a scheme missing here is dropped from a subscription unseen.</summary>
    private static readonly string[] LinkSchemes =
        ["vless://", "vmess://", "trojan://", "ss://", "hysteria2://", "hy2://", "tuic://", "anytls://"];

    private static bool LooksLikeConfig(string value) =>
        LinkSchemes.Any(scheme => value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) ||
        value.TrimStart().StartsWith('{') ||
        value.Contains("[Interface]", StringComparison.OrdinalIgnoreCase);

    private static string NameFor(string config, int index)
    {
        var label = config.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
            ? VmessLabel(config)
            : FragmentLabel(config);

        if (!string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        return $"{config.Split(':', 2)[0].ToUpperInvariant()} {index}";
    }

    private static string? VmessLabel(string config)
    {
        try
        {
            var payload = config[8..].Split('#', 2)[0];
            var json = JsonNode.Parse(Encoding.UTF8.GetString(DecodeBase64(payload)))?.AsObject();
            return json?["ps"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? FragmentLabel(string config)
    {
        var hash = config.LastIndexOf('#');
        if (hash < 0 || hash + 1 >= config.Length)
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(config[(hash + 1)..]);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - (normalized.Length % 4)) % 4);
        return Convert.FromBase64String(normalized);
    }
}
