using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class WireSockConfigTests
{
    private const string Self = @"C:\Tools\RouteShield\RouteShield.exe";

    private static string[] Lines(string conf) =>
        conf.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();

    [Fact]
    public void Selected_applications_become_allowed_apps_with_routeshield_itself()
    {
        var conf = WireSockConfig.Build(Fixtures.WireGuardConf, Fixtures.Settings(), Fixtures.Apps, Self);
        var directive = Lines(conf).Single(line => line.StartsWith("#@ws:AllowedApps", StringComparison.Ordinal));

        Assert.Contains(@"C:\Program Files\Mozilla Firefox\firefox.exe", directive);
        Assert.Contains(@"C:\Users\amir\AppData\Roaming\Telegram\Telegram.exe", directive);

        // RouteShield rides along, so the exit probe and subscription refreshes use the tunnel.
        Assert.EndsWith(Self, directive);
        Assert.DoesNotContain(Lines(conf), line => line.StartsWith("#@ws:DisallowedApps", StringComparison.Ordinal));
    }

    [Fact]
    public void Excluded_applications_become_disallowed_apps()
    {
        var conf = WireSockConfig.Build(Fixtures.WireGuardConf, Fixtures.Settings(RouteMode.AllExceptSelected), Fixtures.Apps, Self);
        var directive = Lines(conf).Single(line => line.StartsWith("#@ws:DisallowedApps", StringComparison.Ordinal));

        Assert.Contains("firefox.exe", directive);
        Assert.DoesNotContain(Self, directive);
        Assert.DoesNotContain(Lines(conf), line => line.StartsWith("#@ws:AllowedApps", StringComparison.Ordinal));
    }

    [Fact]
    public void A_full_tunnel_names_no_applications()
    {
        var conf = WireSockConfig.Build(Fixtures.WireGuardConf, Fixtures.Settings(RouteMode.FullTunnel), [], Self);

        Assert.DoesNotContain(Lines(conf), line => line.Contains("Apps", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_network_access_keeps_private_ranges_out_of_the_tunnel()
    {
        var conf = WireSockConfig.Build(Fixtures.WireGuardConf, Fixtures.Settings(allowLan: true), Fixtures.Apps, Self);
        var directive = Lines(conf).Single(line => line.StartsWith("#@ws:DisallowedIPs", StringComparison.Ordinal));

        Assert.Contains("192.168.0.0/16", directive);
        Assert.Contains("10.0.0.0/8", directive);
    }

    [Fact]
    public void Directives_sit_in_the_peer_section_and_the_rest_of_the_file_is_kept()
    {
        var lines = Lines(WireSockConfig.Build(Fixtures.AmneziaWg, Fixtures.Settings(), Fixtures.Apps, Self));

        var peer = Array.IndexOf(lines, "[Peer]");
        var allowed = Array.FindIndex(lines, line => line.StartsWith("#@ws:AllowedApps", StringComparison.Ordinal));

        Assert.True(peer >= 0 && allowed > peer);

        // Keys, endpoint and the AmneziaWG obfuscation are passed through untouched.
        Assert.Contains("Jc = 4", lines);
        Assert.Contains("S2 = 58", lines);
        Assert.Contains("Endpoint = wg.example.net:51820", lines);
        Assert.Contains(lines, line => line.StartsWith("PrivateKey = ", StringComparison.Ordinal));
    }

    [Fact]
    public void Directives_already_in_the_file_are_replaced_not_repeated()
    {
        var lines = Lines(WireSockConfig.Build(Fixtures.AmneziaWg, Fixtures.Settings(), Fixtures.Apps, Self));

        Assert.Single(lines, line => line.Contains("AllowedApps", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("old.exe", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_with_a_comma_is_left_out_rather_than_split_in_two()
    {
        AppTarget[] apps =
        [
            new() { DisplayName = "Odd", Path = @"C:\Games\Big, Bad\game.exe" },
            new() { DisplayName = "Firefox", Path = @"C:\Program Files\Mozilla Firefox\firefox.exe" }
        ];

        var directive = Lines(WireSockConfig.Build(Fixtures.WireGuardConf, Fixtures.Settings(), apps, Self))
            .Single(line => line.StartsWith("#@ws:AllowedApps", StringComparison.Ordinal));

        Assert.DoesNotContain("Big", directive);
        Assert.Contains("firefox.exe", directive);
    }

    [Fact]
    public void Selected_mode_without_applications_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WireSockConfig.Build(Fixtures.WireGuardConf, Fixtures.Settings(), [], Self));
    }

    [Fact]
    public void A_file_without_a_peer_is_refused()
    {
        Assert.Throws<FormatException>(() =>
            WireSockConfig.Build("[Interface]\nPrivateKey = x\n", Fixtures.Settings(RouteMode.FullTunnel), [], Self));
    }
}
