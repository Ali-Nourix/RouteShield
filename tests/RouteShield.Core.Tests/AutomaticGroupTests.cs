using System.Text.Json.Nodes;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class AutomaticGroupTests
{
    private const string Uuid = "b1f0e4c2-0000-4000-8000-000000009ac4";

    private static string Vless(string hostAndPort, string name) =>
        $"vless://{Uuid}@{hostAndPort}?type=tcp&security=none#{Uri.EscapeDataString(name)}";

    [Theory]
    [InlineData("1.1.1.1:53966")]
    [InlineData("8.8.8.8:443")]
    [InlineData("127.0.0.1:1")]
    [InlineData("127.0.0.1:443")]
    [InlineData("0.0.0.0:443")]
    [InlineData("[::1]:443")]
    [InlineData("169.254.10.1:443")]
    [InlineData("224.0.0.1:443")]
    [InlineData("255.255.255.255:443")]
    [InlineData("localhost:443")]
    [InlineData("de-1.example.net:1")]
    public void Addresses_that_run_no_server_are_placeholders(string hostAndPort) =>
        Assert.True(NodeInspector.IsPlaceholder(TunnelParser.Parse(Vless(hostAndPort, "note"))));

    [Theory]
    [InlineData("104.16.132.229:443")]
    [InlineData("1.1.1.2:443")]
    [InlineData("162.159.192.1:2408")]
    [InlineData("10.0.0.5:443")]
    [InlineData("de-1.example.net:443")]
    [InlineData("[2606:4700::6810:84e5]:443")]
    public void Real_servers_are_not_placeholders(string hostAndPort) =>
        Assert.False(NodeInspector.IsPlaceholder(TunnelParser.Parse(Vless(hostAndPort, "📊 7.75 GB left"))));

    [Fact]
    public void A_port_range_stands_in_for_the_port()
    {
        var hopping = TunnelParser.Parse(Fixtures.Hysteria2);

        Assert.NotNull(hopping.Outbound!["server_ports"]);
        Assert.False(NodeInspector.IsPlaceholder(hopping));
    }

    [Fact]
    public void WireGuard_is_judged_by_its_peer()
    {
        Assert.False(NodeInspector.IsPlaceholder(TunnelParser.Parse(Fixtures.WireGuardConf)));
        Assert.True(NodeInspector.IsPlaceholder(TunnelParser.Parse(Fixtures.WireGuardConf.Replace("wg.example.net:51820", "127.0.0.1:51820"))));
    }

    [Fact]
    public void The_address_of_a_node_is_shown_the_way_it_is_dialled()
    {
        Assert.Equal("1.1.1.1:53966", NodeInspector.AddressOf(TunnelParser.Parse(Vless("1.1.1.1:53966", "note"))));
        Assert.Equal("[::1]:443", NodeInspector.AddressOf(TunnelParser.Parse(Vless("[::1]:443", "note"))));
        Assert.Equal("wg.example.net:51820", NodeInspector.AddressOf(TunnelParser.Parse(Fixtures.WireGuardConf)));
    }

    private static (VpnProfile Automatic, List<VpnProfile> Library) Subscription(params (string Name, string Config)[] nodes)
    {
        var subscription = new VpnSubscription { Name = "Provider" };
        var library = nodes
            .Select(node => new VpnProfile { Name = node.Name, Format = "VLESS", ConfigText = node.Config, SubscriptionId = subscription.Id })
            .ToList();

        // A manual profile of the same format must never be pulled into the group.
        library.Add(new VpnProfile { Name = "Manual", Format = "Trojan", ConfigText = Fixtures.Trojan });

        return (VpnProfile.AutomaticFor(subscription), library);
    }

    [Fact]
    public void An_automatic_group_leaves_the_providers_notes_out()
    {
        var (automatic, library) = Subscription(
            ("📊 7.75 GB left", Vless("1.1.1.1:53966", "📊 7.75 GB left")),
            ("Frankfurt", Fixtures.VlessReality),
            ("Amsterdam", Fixtures.VlessWebSocket));

        var resolved = TargetResolver.Resolve(automatic, library);

        Assert.Equal(["Frankfurt", "Amsterdam"], resolved.Target.MemberNames);
        var line = Assert.Single(resolved.LeftOut);
        Assert.Contains("7.75 GB left", line);
        Assert.Contains("1.1.1.1:53966", line);
    }

    [Fact]
    public void An_automatic_group_puts_the_last_tests_best_node_first()
    {
        var (automatic, library) = Subscription(
            ("Slow", Vless("slow.example.net:443", "Slow")),
            ("Dead", Vless("dead.example.net:443", "Dead")),
            ("Untested", Vless("new.example.net:443", "Untested")),
            ("Fast", Vless("fast.example.net:443", "Fast")),
            ("Also untested", Vless("new2.example.net:443", "Also untested")));

        var delays = new Dictionary<Guid, int?>
        {
            [library[0].Id] = 900,
            [library[1].Id] = null,
            [library[3].Id] = 120
        };

        var resolved = TargetResolver.Resolve(automatic, library, delays);

        // The core carries traffic over the first member until its own test is in, and over it
        // for good when no test passes: the order is the last test's, untested before failed.
        Assert.Equal(["Fast", "Slow", "Untested", "Also untested", "Dead"], resolved.Target.MemberNames);
    }

    [Fact]
    public void An_automatic_group_keeps_library_order_without_a_test()
    {
        var (automatic, library) = Subscription(
            ("Frankfurt", Fixtures.VlessReality),
            ("Amsterdam", Fixtures.VlessWebSocket));

        Assert.Equal(["Frankfurt", "Amsterdam"], TargetResolver.Resolve(automatic, library).Target.MemberNames);
    }

    [Fact]
    public void An_automatic_group_of_nothing_but_notes_refuses_to_start()
    {
        var (automatic, library) = Subscription(
            ("📊 7.75 GB left", Vless("1.1.1.1:53966", "📊 7.75 GB left")),
            ("Expires 2026-10-01", Vless("127.0.0.1:1", "Expires 2026-10-01")));

        var failure = Assert.Throws<InvalidOperationException>(() => TargetResolver.Resolve(automatic, library));
        Assert.Contains("no usable node", failure.Message);
    }

    [Fact]
    public void An_unreadable_member_is_left_out_with_its_reason()
    {
        var (automatic, library) = Subscription(
            ("Broken", "vmess://!!!not-base64"),
            ("Frankfurt", Fixtures.VlessReality));

        var resolved = TargetResolver.Resolve(automatic, library);

        Assert.Equal(["Frankfurt"], resolved.Target.MemberNames);
        Assert.StartsWith("\"Broken\" left out of the automatic group:", Assert.Single(resolved.LeftOut));
    }

    [Fact]
    public void A_single_profile_is_connected_as_itself()
    {
        var profile = new VpnProfile { Name = "Frankfurt", Format = "VLESS", ConfigText = Fixtures.VlessReality };

        var resolved = TargetResolver.Resolve(profile, [profile]);

        Assert.False(resolved.Target.IsAutomatic);
        Assert.Equal("Frankfurt", resolved.Target.Name);
        Assert.Empty(resolved.LeftOut);
    }

    [Fact]
    public void The_placeholder_free_group_is_what_the_runtime_places_first()
    {
        var (automatic, library) = Subscription(
            ("📊 7.75 GB left", Vless("1.1.1.1:53966", "📊 7.75 GB left")),
            ("Frankfurt", Fixtures.VlessReality),
            ("Amsterdam", Fixtures.VlessWebSocket));

        var target = TargetResolver.Resolve(automatic, library).Target;
        var runtime = RuntimeConfigBuilder.Build(target, Fixtures.Settings(), Fixtures.Apps, [], new PortPlan(21080, 29090, "s3cret", []));
        var outbounds = JsonNode.Parse(runtime.Json)!["outbounds"]!.AsArray();

        var group = outbounds.Single(node => node!["type"]!.GetValue<string>() == "urltest")!;
        var first = outbounds.Single(node => node!["tag"]!.GetValue<string>() == group["outbounds"]![0]!.GetValue<string>())!;

        Assert.Equal("fra-01.example.net", first["server"]!.GetValue<string>());
        Assert.DoesNotContain(outbounds, node => node!["server"]?.GetValue<string>() == "1.1.1.1");
    }

    [Fact]
    public void Group_health_names_the_fastest_node()
    {
        var health = new GroupHealth(
        [
            new MemberCheck("auto-0", "Frankfurt", 180, null),
            new MemberCheck("auto-1", "Amsterdam", null, MemberCheck.TimeoutFailure),
            new MemberCheck("auto-2", "Tokyo", 95, null)
        ]);

        Assert.Equal(2, health.Answering);
        Assert.False(health.NoneAnswer);
        Assert.Equal("2 of 3 nodes answer · fastest \"Tokyo\" at 95 ms", health.Summary);
        Assert.Equal("\"Frankfurt\" 180 ms; \"Amsterdam\" timeout; \"Tokyo\" 95 ms", health.Details);
    }

    [Theory]
    [InlineData(3, 0, "None of 3 nodes answers · every one timed out")]
    [InlineData(0, 2, "None of 2 nodes answers · every one was refused at once")]
    [InlineData(1, 2, "None of 3 nodes answers · 1 timed out, 2 refused")]
    public void Group_health_says_how_a_silent_group_failed(int timedOut, int refused, string expected)
    {
        var members = Enumerable.Range(0, timedOut)
            .Select(index => new MemberCheck($"auto-{index}", $"T{index}", null, MemberCheck.TimeoutFailure))
            .Concat(Enumerable.Range(0, refused)
                .Select(index => new MemberCheck($"auto-{timedOut + index}", $"R{index}", null, MemberCheck.UnreachableFailure)))
            .ToArray();

        var health = new GroupHealth(members);

        Assert.True(health.NoneAnswer);
        Assert.Null(health.Fastest);
        Assert.Equal(expected, health.Summary);
    }
}
