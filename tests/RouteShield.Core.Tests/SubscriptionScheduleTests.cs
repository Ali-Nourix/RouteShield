using RouteShield.Subscriptions;
using Xunit;

namespace RouteShield.Tests;

public class SubscriptionScheduleTests
{
    [Fact]
    public void Disabled_schedule_and_free_pool_never_fetch()
    {
        var sub = new VpnSubscription { Url = "https://provider.example/sub" };
        Assert.False(SubscriptionSchedule.IsDue(sub, DateTimeOffset.UtcNow, 0, null));
        sub.Id = FreeConfigService.PoolId;
        Assert.False(SubscriptionSchedule.IsDue(sub, DateTimeOffset.UtcNow, 15, null));
    }
    [Fact]
    public void Refresh_interval_and_failed_attempt_backoff_are_respected()
    {
        var now = DateTimeOffset.UtcNow;
        var sub = new VpnSubscription { Url = "https://provider.example/sub", LastUpdated = now.AddMinutes(-61) };
        Assert.True(SubscriptionSchedule.IsDue(sub, now, 60, null));
        Assert.False(SubscriptionSchedule.IsDue(sub, now, 60, now.AddMinutes(-4)));
        Assert.True(SubscriptionSchedule.IsDue(sub, now, 60, now.AddMinutes(-6)));
        sub.LastUpdated = now.AddMinutes(-5);
        Assert.False(SubscriptionSchedule.IsDue(sub, now, 15, null));
    }
    [Fact]
    public void Never_updated_nodes_are_due_but_empty_urls_are_not()
    {
        var sub = new VpnSubscription { Url = "https://provider.example/sub" };
        Assert.True(SubscriptionSchedule.IsDue(sub, DateTimeOffset.UtcNow, 15, null));
        sub.Url = "";
        Assert.False(SubscriptionSchedule.IsDue(sub, DateTimeOffset.UtcNow, 15, null));
    }
}
