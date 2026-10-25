using RouteShield.Subscriptions;
using Xunit;

namespace RouteShield.Tests;

public class FreeConfigTests
{
    [Fact]
    public async Task One_failed_source_does_not_discard_the_other_source()
    {
        var calls = 0;
        var service = new FreeConfigService((_, _, _) => ++calls == 1
            ? Task.FromException<SubscriptionFetch>(new HttpRequestException("offline"))
            : Task.FromResult(new SubscriptionFetch([new("Node", Fixtures.Trojan)], null)));
        var result = await service.FetchAsync(null, CancellationToken.None);
        Assert.Single(result.Nodes);
        Assert.Single(result.SourceErrors);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task All_sources_failing_returns_no_candidates_or_fallback()
    {
        var service = new FreeConfigService((_, _, _) =>
            Task.FromException<SubscriptionFetch>(new HttpRequestException("offline")));
        var result = await service.FetchAsync(null, CancellationToken.None);
        Assert.Empty(result.Nodes);
        Assert.Equal(FreeConfigService.Sources.Count, result.SourceErrors.Count);
        Assert.Null(FreeConfigService.Fastest([]));
    }

    [Fact]
    public async Task Equivalent_links_across_sources_are_deduplicated_despite_different_labels()
    {
        var service = new FreeConfigService((_, _, _) => Task.FromResult(new SubscriptionFetch(
            [new("One", Fixtures.Trojan), new("Two", Fixtures.Trojan.Split('#')[0] + "#Another")], null)));
        var result = await service.FetchAsync(null, CancellationToken.None);
        Assert.Single(result.Nodes);
    }

    [Fact]
    public async Task Candidate_count_is_bounded_and_both_sources_are_sampled()
    {
        var source = 0;
        var service = new FreeConfigService((_, _, _) =>
        {
            var feed = ++source;
            return Task.FromResult(new SubscriptionFetch(Enumerable.Range(0, 500).Select(index =>
                new SubscriptionEntry("Node", $"trojan://pw@node{index}.feed{feed}.example.net:443#Node")).ToArray(), null));
        });
        var result = await service.FetchAsync(null, CancellationToken.None);
        Assert.Equal(FreeConfigService.MaxCandidates, result.Nodes.Count);
        Assert.Contains(result.Nodes, node => node.Config.Contains("feed1"));
        Assert.Contains(result.Nodes, node => node.Config.Contains("feed2"));
    }

    [Theory]
    [InlineData("trojan://pw@127.0.0.1:443")]
    [InlineData("trojan://pw@10.0.0.1:443")]
    [InlineData("trojan://pw@192.168.1.1:443")]
    [InlineData("trojan://pw@172.16.0.1:443")]
    [InlineData("trojan://pw@169.254.169.254:443")]
    [InlineData("trojan://pw@localhost:443")]
    [InlineData("trojan://pw@host.local:443")]
    [InlineData("trojan://pw@[::1]:443")]
    [InlineData("trojan://pw@[::ffff:10.0.0.1]:443")]
    [InlineData("trojan://pw@relay.example.net:443?insecure=1")]
    [InlineData("vmess://broken")]
    [InlineData("{\"type\":\"trojan\",\"server\":\"relay.example.net\",\"server_port\":443,\"password\":\"pw\"}")]
    public void Untrusted_feeds_cannot_import_local_insecure_or_runtime_configuration(string config)
    {
        Assert.Null(FreeConfigService.ReadPublicNode(new("Node", config)));
    }

    [Fact]
    public void Ordinary_share_links_are_accepted() => Assert.NotNull(
        FreeConfigService.ReadPublicNode(new("Node", Fixtures.Trojan)));

    [Fact]
    public async Task Cancellation_stops_before_fetching()
    {
        var service = new FreeConfigService((_, _, _) => throw new InvalidOperationException("Must not fetch"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(null, cancelled.Token));
    }

    [Fact]
    public void Fastest_ignores_failures_and_unknown_delays()
    {
        var slow = Guid.NewGuid();
        var fast = Guid.NewGuid();
        Assert.Equal(fast, FreeConfigService.Fastest([(slow, 300), (Guid.NewGuid(), null), (fast, 25), (Guid.NewGuid(), -1)]));
        Assert.Null(FreeConfigService.Fastest([(slow, null), (fast, -1)]));
        Assert.Null(FreeConfigService.Fastest([]));
    }
}
