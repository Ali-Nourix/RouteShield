using System.Text;
using RouteShield.Subscriptions;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class SubscriptionTests
{
    /// <summary>The line a panel puts first in a subscription: the account's quota, on a link that points at a resolver.</summary>
    private const string QuotaNote =
        "vless://b1f0e4c2-0000-4000-8000-000000009ac4@1.1.1.1:53966?type=tcp&security=none#%F0%9F%93%8A%207.75%20GB%20left";

    private const string ExpiryNote =
        "vless://b1f0e4c2-0000-4000-8000-000000009ac4@127.0.0.1:1?type=tcp&security=none#Expires%202026-10-01";

    /// <summary>A real server whose panel wrote the remaining traffic into its name, as 3x-ui does.</summary>
    private const string NamedLikeANote =
        "vless://b1f0e4c2-0000-4000-8000-000000009ac4@de-1.example.net:443?type=tcp&security=tls&sni=de-1.example.net#DE-1%20%7C%207.75GB%20left";

    [Fact]
    public void Parser_keeps_every_link_scheme_the_profile_parser_reads()
    {
        var body = string.Join('\n', Fixtures.VlessReality, Fixtures.Hysteria2, Fixtures.Tuic, Fixtures.AnyTls,
            Fixtures.Hysteria2.Replace("hysteria2://", "hy2://").Replace("#Hop", "#Short"));

        var entries = SubscriptionParser.Parse(body);

        Assert.Equal(["Frankfurt", "Hop", "Quic", "Any", "Short"], entries.Select(entry => entry.Name));
    }

    [Fact]
    public void Parser_decodes_a_base64_body_that_holds_only_quic_links()
    {
        // Detection runs on the decoded text: a body of nothing but Hysteria2 and TUIC used to
        // look like no configuration at all.
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(Fixtures.Hysteria2 + "\n" + Fixtures.Tuic));

        var entries = SubscriptionParser.Parse(body);

        Assert.Equal(2, entries.Count);
        Assert.StartsWith("hysteria2://", entries[0].Config);
        Assert.StartsWith("tuic://", entries[1].Config);
    }

    [Fact]
    public void Import_sets_the_providers_notes_aside_and_keeps_the_servers()
    {
        var entries = SubscriptionParser.Parse(string.Join('\n', QuotaNote, ExpiryNote, Fixtures.VlessReality, NamedLikeANote, Fixtures.Trojan));

        var import = SubscriptionImport.From(entries);

        Assert.Equal(["📊 7.75 GB left", "Expires 2026-10-01"], import.Notes);
        Assert.Equal(["Frankfurt", "DE-1 | 7.75GB left", "Tokyo relay"], import.Nodes.Select(node => node.Name));
        Assert.Equal(["VLESS", "VLESS", "Trojan"], import.Nodes.Select(node => node.Format));
        Assert.Equal(0, import.Unreadable);
    }

    [Fact]
    public void Import_counts_entries_it_cannot_read()
    {
        var import = SubscriptionImport.From(
        [
            new SubscriptionEntry("Broken", "vmess://!!!not-base64"),
            new SubscriptionEntry("Frankfurt", Fixtures.VlessReality)
        ]);

        Assert.Single(import.Nodes);
        Assert.Empty(import.Notes);
        Assert.Equal(1, import.Unreadable);
    }

    [Fact]
    public void A_stored_note_is_recognised_and_a_real_server_is_not()
    {
        Assert.True(SubscriptionImport.IsNote(QuotaNote));
        Assert.False(SubscriptionImport.IsNote(NamedLikeANote));
        Assert.False(SubscriptionImport.IsNote("not a configuration"));
    }

    [Fact]
    public void Usage_header_is_read_in_bytes_and_unix_time()
    {
        var usage = SubscriptionUsage.Parse("upload=455727941; download=6174315083; total=1073741824000; expire=1671815872")!;

        Assert.Equal(455727941, usage.UploadBytes);
        Assert.Equal(6174315083, usage.DownloadBytes);
        Assert.Equal(6630043024, usage.UsedBytes);
        Assert.Equal(1073741824000 - 6630043024, usage.RemainingBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1671815872), usage.ExpiresAt);
    }

    [Fact]
    public void Usage_header_treats_zero_as_not_stated_for_total_and_expiry()
    {
        var usage = SubscriptionUsage.Parse("upload=0; download=1024; total=0; expire=0")!;

        Assert.Equal(1024, usage.UsedBytes);
        Assert.Null(usage.TotalBytes);
        Assert.Null(usage.RemainingBytes);
        Assert.Null(usage.ExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothing useful here")]
    [InlineData("upload=-5; total=abc")]
    public void Usage_header_without_numbers_is_no_usage(string? header) =>
        Assert.Null(SubscriptionUsage.Parse(header));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(8321499136, "7.75 GB")]
    [InlineData(1073741824000, "1000 GB")]
    [InlineData(5497558138880, "5 TB")]
    public void Bytes_are_shown_in_binary_units(long bytes, string expected) =>
        Assert.Equal(expected, SubscriptionUsage.FormatBytes(bytes));

    [Fact]
    public void Subscription_status_shows_what_is_left_and_when_it_ends()
    {
        var subscription = new VpnSubscription { LastCount = 23, LastUpdated = DateTimeOffset.Now };
        subscription.ApplyUsage(new SubscriptionUsage(0, 42_949_672_960, 53_687_091_200, DateTimeOffset.Now.AddDays(12).AddHours(1)));

        Assert.Equal("10 GB left of 50 GB · expires in 12 d", subscription.UsageText);
        Assert.Equal("23 nodes · updated just now · 10 GB left of 50 GB · expires in 12 d", subscription.StatusText);
        Assert.False(subscription.IsExhausted);
    }

    [Fact]
    public void Subscription_without_a_header_shows_the_providers_notes()
    {
        var subscription = new VpnSubscription { Notes = ["📊 7.75 GB left", "Expires 2026-10-01", "Support: @provider"] };

        Assert.Equal("📊 7.75 GB left · Expires 2026-10-01", subscription.UsageText);
        Assert.Null(subscription.Usage);
    }

    [Theory]
    [InlineData(53_687_091_199, 10, false)]
    [InlineData(53_687_091_200, 10, true)]
    [InlineData(1_024, -1, true)]
    public void Subscription_is_exhausted_when_used_up_or_expired(long downloaded, int daysLeft, bool exhausted)
    {
        var subscription = new VpnSubscription();
        subscription.ApplyUsage(new SubscriptionUsage(0, downloaded, 53_687_091_200, DateTimeOffset.Now.AddDays(daysLeft)));

        Assert.Equal(exhausted, subscription.IsExhausted);
    }

    [Fact]
    public void Clearing_usage_leaves_nothing_behind()
    {
        var subscription = new VpnSubscription();
        subscription.ApplyUsage(new SubscriptionUsage(1, 2, 3, DateTimeOffset.Now.AddDays(1)));
        subscription.ApplyUsage(null);

        Assert.Null(subscription.Usage);
        Assert.Equal(string.Empty, subscription.UsageText);
    }
}
