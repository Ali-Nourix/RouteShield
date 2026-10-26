namespace RouteShield.Subscriptions;

public static class SubscriptionSchedule
{
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromMinutes(5);
    public static bool IsDue(VpnSubscription subscription, DateTimeOffset now, int intervalMinutes, DateTimeOffset? lastAttempt)
    {
        if (intervalMinutes <= 0 || subscription.Id == FreeConfigService.PoolId || string.IsNullOrWhiteSpace(subscription.Url)) return false;
        intervalMinutes = Math.Clamp(intervalMinutes, 15, 1440);
        if (lastAttempt is { } tried && now - tried < RetryBackoff) return false;
        return subscription.LastUpdated is null || now - subscription.LastUpdated >= TimeSpan.FromMinutes(intervalMinutes);
    }
}
