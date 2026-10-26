using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RouteShield.Subscriptions;
using Xunit;

namespace RouteShield.Tests;

public class LibraryBackupTests
{
    private const string Password = "test-only-password-not-a-user-secret";
    private static AppSettings Settings()
    {
        var sub = new VpnSubscription { Name = "Provider", Url = "https://provider.example/private-token" };
        return new AppSettings
        {
            Subscriptions = [sub, new VpnSubscription { Id = FreeConfigService.PoolId, Url = FreeConfigService.Sources[0] }],
            Profiles = [new VpnProfile { Name = "Private", SubscriptionId = sub.Id, ConfigText = Fixtures.Trojan, IsFavorite = true, BrowserPinned = true },
                new VpnProfile { Name = "Free", SubscriptionId = FreeConfigService.PoolId, ConfigText = Fixtures.Shadowsocks }],
            DirectDomains = ["direct.example.com"], BlockedDomains = ["block.example.com"]
        };
    }
    [Fact]
    public void Portable_backup_is_encrypted_and_preserves_private_library_metadata()
    {
        var settings = Settings();
        var bytes = LibraryBackup.Export(settings, Password);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("private-token", text);
        Assert.DoesNotContain("s3cret", text);
        var restored = LibraryBackup.Read(bytes, Password);
        var profile = Assert.Single(restored.Profiles);
        Assert.Equal(Fixtures.Trojan, profile.Config);
        Assert.True(profile.Favorite); Assert.True(profile.BrowserPinned);
        Assert.Single(restored.Subscriptions);
        Assert.Equal("direct.example.com", Assert.Single(restored.DirectDomains));
        Assert.Equal("block.example.com", Assert.Single(restored.BlockedDomains));
    }
    [Fact]
    public void Export_uses_a_fresh_salt_and_nonce_each_time() => Assert.NotEqual(
        Convert.ToBase64String(LibraryBackup.Export(Settings(), Password)), Convert.ToBase64String(LibraryBackup.Export(Settings(), Password)));
    [Fact]
    public void Wrong_password_is_refused() => Assert.ThrowsAny<CryptographicException>(() =>
        LibraryBackup.Read(LibraryBackup.Export(Settings(), Password), "wrong-test-password"));
    [Fact]
    public void Tampered_ciphertext_is_refused()
    {
        var doc = JsonNode.Parse(LibraryBackup.Export(Settings(), Password))!.AsObject();
        var cipher = Convert.FromBase64String(doc["Ciphertext"]!.GetValue<string>());
        cipher[cipher.Length / 2] ^= 1;
        doc["Ciphertext"] = Convert.ToBase64String(cipher);
        Assert.ThrowsAny<CryptographicException>(() => LibraryBackup.Read(Encoding.UTF8.GetBytes(doc.ToJsonString()), Password));
    }
    [Fact]
    public void Merge_does_not_replace_current_profiles_or_duplicate_existing_provider()
    {
        var settings = Settings();
        var restored = LibraryBackup.Read(LibraryBackup.Export(settings, Password), Password);
        var existing = new VpnSubscription { Name = "Keep name", Url = restored.Subscriptions[0].Url };
        var manual = new VpnProfile { Name = "Keep manual", ConfigText = Fixtures.Shadowsocks };
        var profiles = new List<VpnProfile> { manual };
        var subs = new List<VpnSubscription> { existing };
        Assert.Equal(1, LibraryBackup.Merge(restored, profiles, subs));
        Assert.Single(subs); Assert.Equal("Keep name", existing.Name);
        Assert.Contains(manual, profiles);
        Assert.Equal(existing.Id, profiles.Single(p => p != manual).SubscriptionId);
        Assert.Equal(0, LibraryBackup.Merge(restored, profiles, subs));
        Assert.Equal(2, profiles.Count);
    }
    [Fact]
    public void Invalid_restore_is_validated_before_any_mutation()
    {
        var profiles = new List<VpnProfile>(); var subs = new List<VpnSubscription>();
        var bad = new LibraryBackupData(1, [new(Guid.NewGuid(), "Broken owner", Fixtures.Trojan, false, false)],
            [new(Guid.NewGuid(), "Would mutate", "https://provider.example/sub")], [], [], []);
        Assert.Throws<FormatException>(() => LibraryBackup.Merge(bad, profiles, subs));
        Assert.Empty(profiles); Assert.Empty(subs);
    }
    [Fact]
    public void Weak_password_and_oversized_import_are_refused()
    {
        Assert.Throws<ArgumentException>(() => LibraryBackup.Export(Settings(), "short"));
        Assert.Throws<FormatException>(() => LibraryBackup.Read(new byte[LibraryBackup.MaxBytes + 1], Password));
    }
    [Fact]
    public void Windows_startup_and_firewall_settings_are_not_restored()
    {
        var data = LibraryBackup.Read(LibraryBackup.Export(Settings(), Password), Password);
        var target = new AppSettings { StartWithWindows = false, AppKillSwitch = true, AutoConnect = false };
        _ = LibraryBackup.Merge(data, target.Profiles, target.Subscriptions);
        Assert.False(target.StartWithWindows); Assert.True(target.AppKillSwitch); Assert.False(target.AutoConnect);
    }
}
