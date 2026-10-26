using System.Text.Json;
using RouteShield.Tunnels;

namespace RouteShield.Subscriptions;

public static class ProfileTools
{
    public static string Fingerprint(string config)
    {
        var node = TunnelParser.Clone(TunnelParser.Parse(config).Node);
        node.Remove("tag");
        return node.ToJsonString();
    }

    /// <summary>Never deduplicate across owners: separate providers must stay separate.</summary>
    public static IReadOnlyDictionary<Guid, Guid> RemoveDuplicates(ICollection<VpnProfile> profiles)
    {
        var kept = new Dictionary<(Guid? Owner, string Fingerprint), VpnProfile>();
        var removed = new Dictionary<Guid, Guid>();
        foreach (var profile in profiles.ToArray())
        {
            if (profile.IsAutomatic) continue;
            string key;
            try { key = Fingerprint(profile.ConfigText); }
            catch (Exception e) when (e is FormatException or NotSupportedException or InvalidOperationException or ArgumentException or JsonException) { continue; }
            if (kept.TryGetValue((profile.SubscriptionId, key), out var first))
            {
                first.BrowserPinned |= profile.BrowserPinned;
                first.IsFavorite |= profile.IsFavorite;
                removed[profile.Id] = first.Id;
                profiles.Remove(profile);
            }
            else kept[(profile.SubscriptionId, key)] = profile;
        }
        return removed;
    }

    /// <summary>Claimed country from a flag in a label, never measured geolocation.</summary>
    public static string ClaimedCountry(string name)
    {
        var runes = name.EnumerateRunes().ToArray();
        for (var index = 0; index + 1 < runes.Length; index++)
        {
            var a = runes[index].Value - 0x1F1E6;
            var b = runes[index + 1].Value - 0x1F1E6;
            if (a is >= 0 and < 26 && b is >= 0 and < 26) return $"{(char)('A' + a)}{(char)('A' + b)}";
        }
        return "Unknown";
    }

    public static bool Matches(VpnProfile profile, string query, string protocol, string country, bool favoritesOnly)
    {
        if (favoritesOnly && !profile.IsFavorite) return false;
        if (protocol != "All" && !profile.Format.Equals(protocol, StringComparison.OrdinalIgnoreCase)) return false;
        if (country != "All" && ClaimedCountry(profile.Name) != country) return false;
        return string.IsNullOrWhiteSpace(query) || (profile.Name + " " + profile.Format).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
