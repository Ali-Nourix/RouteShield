namespace RouteShield.Subscriptions;

/// <summary>Subscription changes scoped by owner, never by a server's display name.</summary>
public static class SubscriptionLibrary
{
    public static VpnSubscription Upsert(ICollection<VpnSubscription> subscriptions, string name, string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("A subscription needs an HTTPS URL without embedded credentials.", nameof(url));
        }

        var subscription = subscriptions.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.Ordinal));
        if (subscription is null)
        {
            subscription = new VpnSubscription();
            subscriptions.Add(subscription);
        }

        subscription.Name = string.IsNullOrWhiteSpace(name) ? "Subscription" : name.Trim();
        subscription.Url = url;
        return subscription;
    }

    public static int ReplaceProfiles(ICollection<VpnProfile> profiles, Guid subscriptionId, IReadOnlyList<ImportedNode> nodes)
    {
        // An empty/failed download must never erase a previously working subscription.
        if (nodes.Count == 0) return profiles.Count(profile => profile.SubscriptionId == subscriptionId);
        var owned = profiles.Where(profile => profile.SubscriptionId == subscriptionId).ToList();
        var available = new List<VpnProfile>(owned);
        var replacements = new List<VpnProfile>();
        foreach (var node in nodes)
        {
            var profile = available.FirstOrDefault(item => item.ConfigText == node.Config)
                ?? available.FirstOrDefault(item => item.Name == node.Name);
            if (profile is not null) available.Remove(profile);
            profile ??= new VpnProfile { SubscriptionId = subscriptionId, ConfigText = node.Config };
            profile.ConfigText = node.Config;
            profile.Name = node.Name;
            profile.Format = node.Format;
            replacements.Add(profile);
        }

        foreach (var profile in owned) profiles.Remove(profile);
        foreach (var profile in replacements) profiles.Add(profile);
        return replacements.Count;
    }
}
