using RouteShield.Subscriptions;
using Xunit;

namespace RouteShield.Tests;

public class SubscriptionLibraryTests
{
    [Fact]
    public void Adding_first_and_second_subscription_keeps_both_even_before_a_download()
    {
        var subscriptions = new List<VpnSubscription>();
        var first = SubscriptionLibrary.Upsert(subscriptions, "One", " https://provider.example/one ");
        var second = SubscriptionLibrary.Upsert(subscriptions, "Two", "https://provider.example/two");
        Assert.Equal(2, subscriptions.Count);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("https://provider.example/one", first.Url);
    }

    [Fact]
    public void Adding_the_same_url_reuses_the_subscription_without_losing_its_identity()
    {
        var subscriptions = new List<VpnSubscription>();
        var first = SubscriptionLibrary.Upsert(subscriptions, "One", "https://provider.example/one");
        var second = SubscriptionLibrary.Upsert(subscriptions, "Renamed", "https://provider.example/one ");
        Assert.Same(first, second);
        Assert.Single(subscriptions);
        Assert.Equal("Renamed", first.Name);
    }

    [Theory]
    [InlineData("http://provider.example/sub")]
    [InlineData("https://user:secret@provider.example/sub")]
    [InlineData("")]
    public void Invalid_urls_do_not_mutate_the_library(string url)
    {
        var subscriptions = new List<VpnSubscription>();
        Assert.Throws<ArgumentException>(() => SubscriptionLibrary.Upsert(subscriptions, "One", url));
        Assert.Empty(subscriptions);
    }

    [Fact]
    public void Refresh_is_scoped_to_owner_and_preserves_identity_and_browser_pins()
    {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var first = new VpnProfile { SubscriptionId = one, Name = "Same name", ConfigText = Fixtures.Trojan, BrowserPinned = true };
        var other = new VpnProfile { SubscriptionId = two, Name = "Same name", ConfigText = Fixtures.Trojan };
        var manual = new VpnProfile { Name = "Manual", ConfigText = Fixtures.Shadowsocks };
        var profiles = new List<VpnProfile> { first, other, manual };
        var count = SubscriptionLibrary.ReplaceProfiles(profiles, one,
            [new("Updated label", "Trojan", Fixtures.Trojan), new("New", "Shadowsocks", Fixtures.Shadowsocks)]);
        Assert.Equal(2, count);
        Assert.Equal(4, profiles.Count);
        Assert.Contains(first, profiles);
        Assert.True(first.BrowserPinned);
        Assert.Equal("Updated label", first.Name);
        Assert.Contains(other, profiles);
        Assert.Equal("Same name", other.Name);
        Assert.Contains(manual, profiles);
    }

    [Fact]
    public void Rotated_configuration_keeps_the_owning_profiles_selection_identity_and_pin()
    {
        var sub = Guid.NewGuid();
        var existing = new VpnProfile { SubscriptionId = sub, Name = "Relay", ConfigText = Fixtures.Trojan, BrowserPinned = true };
        var id = existing.Id;
        var profiles = new List<VpnProfile> { existing };
        SubscriptionLibrary.ReplaceProfiles(profiles, sub, [new("Relay", "VLESS", Fixtures.VlessReality)]);
        Assert.Same(existing, Assert.Single(profiles));
        Assert.Equal(id, existing.Id);
        Assert.True(existing.BrowserPinned);
        Assert.Equal(Fixtures.VlessReality, existing.ConfigText);
    }

    [Fact]
    public void Empty_refresh_never_erases_existing_profiles()
    {
        var sub = Guid.NewGuid();
        var profile = new VpnProfile { SubscriptionId = sub, ConfigText = Fixtures.Trojan };
        var profiles = new List<VpnProfile> { profile };
        Assert.Equal(1, SubscriptionLibrary.ReplaceProfiles(profiles, sub, []));
        Assert.Same(profile, Assert.Single(profiles));
    }
}
