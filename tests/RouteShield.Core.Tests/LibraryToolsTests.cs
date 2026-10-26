using System.Text.Json.Nodes;
using RouteShield.Subscriptions;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class LibraryToolsTests
{
    [Theory]
    [InlineData("socks5://user:p%40ss@relay.example.net:1080#Office", "socks", 1080, "Office")]
    [InlineData("socks://relay.example.net", "socks", 1080, "relay.example.net")]
    [InlineData("http://user:pw@relay.example.net:8080#Office", "http", 8080, "Office")]
    [InlineData("https://relay.example.net#TLS", "http", 443, "TLS")]
    public void Proxy_links_become_supported_sing_box_nodes(string link, string type, int port, string name)
    {
        var parsed = TunnelParser.Parse(link);
        Assert.Equal(type, parsed.Node["type"]!.GetValue<string>());
        Assert.Equal(port, parsed.Node["server_port"]!.GetValue<int>());
        Assert.Equal(name, parsed.DisplayName);
        if (link.StartsWith("https://")) Assert.True(parsed.Node["tls"]!["enabled"]!.GetValue<bool>());
    }
    [Fact]
    public void Percent_encoded_proxy_credentials_and_ipv6_work()
    {
        var parsed = TunnelParser.Parse("socks5://user:p%40ss@[2001:db8::1]:1080#Node");
        Assert.Equal("p@ss", parsed.Node["password"]!.GetValue<string>());
        Assert.Equal("2001:db8::1", parsed.Node["server"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("http://relay.example.net/path")]
    [InlineData("https://relay.example.net/?url=https://provider.example")]
    [InlineData("socks5://")]
    [InlineData("socks5://relay.example.net:99999")]
    public void Ambiguous_or_invalid_proxy_links_are_refused(string link) =>
        Assert.ThrowsAny<Exception>(() => TunnelParser.Parse(link));

    [Fact]
    public void Proxy_links_in_subscription_bodies_are_imported_but_free_mode_does_not_accept_them()
    {
        var entries = SubscriptionParser.Parse("socks5://relay.example.net:1080#One\nhttps://relay.example.net:443#Two");
        Assert.Equal(2, SubscriptionImport.From(entries).Nodes.Count);
        Assert.All(entries, e => Assert.Null(FreeConfigService.ReadPublicNode(e)));
    }
    [Theory]
    [InlineData("Germany 🇩🇪 #1", "DE")]
    [InlineData("🇺🇸 USA", "US")]
    [InlineData("🇮🇷 local", "IR")]
    [InlineData("DE node (unverified)", "Unknown")]
    [InlineData("🇩 one letter", "Unknown")]
    [InlineData("", "Unknown")]
    public void Country_filters_use_only_explicit_flag_labels_not_guessed_geolocation(string name, string expected) =>
        Assert.Equal(expected, ProfileTools.ClaimedCountry(name));

    [Fact]
    public void Filters_combine_without_silently_matching_another_country_or_protocol()
    {
        var node = new VpnProfile { Name = "🇩🇪 Frankfurt", Format = "VLESS", IsFavorite = true };
        Assert.True(ProfileTools.Matches(node, "frank", "VLESS", "DE", true));
        Assert.False(ProfileTools.Matches(node, "frank", "Trojan", "DE", true));
        Assert.False(ProfileTools.Matches(node, "frank", "VLESS", "US", true));
        node.IsFavorite = false;
        Assert.False(ProfileTools.Matches(node, "", "All", "All", true));
    }
    [Fact]
    public void Duplicate_cleanup_is_scoped_to_owner_and_merges_metadata()
    {
        var owner = Guid.NewGuid();
        var first = new VpnProfile { SubscriptionId = owner, ConfigText = Fixtures.Trojan };
        var duplicate = new VpnProfile { SubscriptionId = owner, ConfigText = Fixtures.Trojan.Split('#')[0] + "#Other", BrowserPinned = true, IsFavorite = true };
        var otherProvider = new VpnProfile { SubscriptionId = Guid.NewGuid(), ConfigText = Fixtures.Trojan };
        var manual = new VpnProfile { ConfigText = Fixtures.Trojan };
        var invalid = new VpnProfile { ConfigText = "" };
        var profiles = new List<VpnProfile> { first, duplicate, otherProvider, manual, invalid };
        var removed = ProfileTools.RemoveDuplicates(profiles);
        Assert.Equal(first.Id, Assert.Single(removed).Value);
        Assert.DoesNotContain(duplicate, profiles);
        Assert.True(first.BrowserPinned); Assert.True(first.IsFavorite);
        Assert.Contains(otherProvider, profiles); Assert.Contains(manual, profiles); Assert.Contains(invalid, profiles);
    }
    [Fact]
    public void Global_automatic_includes_separate_providers_and_manual_profiles_but_provider_group_stays_scoped()
    {
        var one = new VpnSubscription { Name = "One" };
        var two = new VpnSubscription { Name = "Two" };
        var profiles = new[]
        {
            new VpnProfile { SubscriptionId = one.Id, Name = "One", ConfigText = Fixtures.Trojan },
            new VpnProfile { SubscriptionId = two.Id, Name = "Two", ConfigText = Fixtures.VlessReality },
            new VpnProfile { Name = "Manual", ConfigText = Fixtures.Shadowsocks }
        };
        var global = new VpnProfile { Id = VpnProfile.GlobalAutomaticId, Format = VpnProfile.AutomaticFormat };
        var result = TargetResolver.Resolve(global, profiles, new Dictionary<Guid, int?> { [profiles[1].Id] = 12 });
        Assert.Equal(3, result.Target.Tunnels.Count);
        Assert.Equal("Two", result.Target.MemberNames[0]);
        Assert.Single(TargetResolver.Resolve(VpnProfile.AutomaticFor(one), profiles).Target.Tunnels);
    }
    [Theory]
    [InlineData("socks5://user:sensitive-value@relay.example.net:1080")]
    [InlineData("socks://user:sensitive-value@relay.example.net:1080")]
    [InlineData("http://user:sensitive-value@relay.example.net:8080")]
    [InlineData("https://user:sensitive-value@relay.example.net:443")]
    public void New_proxy_credentials_do_not_survive_diagnostics(string link) =>
        Assert.DoesNotContain("sensitive-value", AppLog.Redact(link));
}
