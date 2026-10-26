using Xunit;
namespace RouteShield.Tests;
public class SubscriptionAttentionTests
{
    [Fact]
    public void Missing_usage_does_not_invent_a_warning() => Assert.False(new VpnSubscription().NeedsAttention);
    [Fact]
    public void Expiry_and_low_quota_are_reported_without_inventing_values()
    {
        var sub = new VpnSubscription { ExpiresAt = DateTimeOffset.UtcNow.AddDays(2) };
        Assert.True(sub.NeedsAttention); Assert.Contains("3 days", sub.AttentionText);
        sub.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
        Assert.Contains("expired", sub.AttentionText);
        sub.ExpiresAt = null; sub.TotalBytes = 100; sub.DownloadBytes = 95;
        Assert.Contains("10%", sub.AttentionText);
        sub.DownloadBytes = 100;
        Assert.Contains("No traffic", sub.AttentionText);
    }
}
