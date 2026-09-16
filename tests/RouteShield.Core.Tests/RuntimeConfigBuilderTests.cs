using System.Text.Json.Nodes;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class RuntimeConfigBuilderTests
{
    private static JsonObject Build(AppSettings settings, params AppTarget[] apps)
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessReality);
        var runtime = RuntimeConfigBuilder.Build(parsed, settings, apps.Length == 0 ? Fixtures.Apps : apps);
        return JsonNode.Parse(runtime.Json)!.AsObject();
    }

    [Fact]
    public void Route_always_names_a_default_domain_resolver()
    {
        // sing-box 1.12 deprecated dialing without a resolver and 1.14 made it fatal:
        // "missing `route.default_domain_resolver` or `domain_resolver` in dial fields".
        var route = Build(Fixtures.Settings())["route"]!.AsObject();

        Assert.Equal(
            RuntimeConfigBuilder.LocalResolverTag,
            route["default_domain_resolver"]!["server"]!.GetValue<string>());
    }

    [Fact]
    public void Proxy_resolves_its_own_server_outside_the_tunnel()
    {
        var root = Build(Fixtures.Settings());
        var proxy = root["outbounds"]!.AsArray()
            .OfType<JsonObject>()
            .Single(node => node["tag"]!.GetValue<string>() == RuntimeConfigBuilder.ProxyTag);

        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, proxy["domain_resolver"]!.GetValue<string>());
    }

    [Fact]
    public void Every_named_resolver_exists()
    {
        foreach (var mode in Enum.GetValues<RouteMode>())
        {
            var root = Build(Fixtures.Settings(mode));
            var dns = root["dns"]!.AsObject();

            var declared = dns["servers"]!.AsArray()
                .Select(server => server!["tag"]!.GetValue<string>())
                .ToHashSet();

            var referenced = new List<string> { dns["final"]!.GetValue<string>() };
            referenced.Add(root["route"]!["default_domain_resolver"]!["server"]!.GetValue<string>());

            if (dns["rules"] is JsonArray rules)
            {
                referenced.AddRange(rules.Select(rule => rule!["server"]!.GetValue<string>()));
            }

            Assert.All(referenced, tag => Assert.Contains(tag, declared));
        }
    }

    [Fact]
    public void Secure_dns_off_leaves_a_single_local_resolver()
    {
        var dns = Build(Fixtures.Settings(dnsProtection: false))["dns"]!.AsObject();

        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, dns["final"]!.GetValue<string>());
        Assert.Single(dns["servers"]!.AsArray());
        Assert.Null(dns["rules"]);
    }

    [Fact]
    public void Secure_dns_off_stops_hijacking_queries()
    {
        var rules = Build(Fixtures.Settings(dnsProtection: false))["route"]!["rules"]!.AsArray();

        Assert.DoesNotContain(rules, rule => rule!["action"]!.GetValue<string>() == "hijack-dns");
    }

    [Theory]
    [InlineData(RouteMode.SelectedAppsOnly, "direct", RuntimeConfigBuilder.ProxyTag)]
    [InlineData(RouteMode.AllExceptSelected, "proxy", RuntimeConfigBuilder.DirectTag)]
    public void Selected_applications_are_routed_against_the_default(RouteMode mode, string expectedFinal, string expectedOutbound)
    {
        var route = Build(Fixtures.Settings(mode))["route"]!.AsObject();
        var processRule = route["rules"]!.AsArray()
            .OfType<JsonObject>()
            .Single(rule => rule.ContainsKey("process_path_regex"));

        Assert.Equal(expectedFinal, route["final"]!.GetValue<string>());
        Assert.Equal(expectedOutbound, processRule["outbound"]!.GetValue<string>());
        Assert.Equal(2, processRule["process_path_regex"]!.AsArray().Count);
    }

    [Fact]
    public void Full_tunnel_needs_no_application_list()
    {
        var route = Build(Fixtures.Settings(RouteMode.FullTunnel), [])["route"]!.AsObject();

        Assert.Equal(RuntimeConfigBuilder.ProxyTag, route["final"]!.GetValue<string>());
        Assert.DoesNotContain(route["rules"]!.AsArray(), rule => rule!.AsObject().ContainsKey("process_path_regex"));
    }

    [Fact]
    public void Split_tunnel_without_applications_is_refused()
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessReality);

        Assert.Throws<InvalidOperationException>(
            () => RuntimeConfigBuilder.Build(parsed, Fixtures.Settings(), []));
    }

    [Fact]
    public void Dns_queries_follow_the_routing_policy()
    {
        var selected = Build(Fixtures.Settings(RouteMode.SelectedAppsOnly))["dns"]!.AsObject();
        Assert.Equal(RuntimeConfigBuilder.TunnelResolverTag, selected["rules"]![0]!["server"]!.GetValue<string>());
        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, selected["final"]!.GetValue<string>());

        var excluded = Build(Fixtures.Settings(RouteMode.AllExceptSelected))["dns"]!.AsObject();
        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, excluded["rules"]![0]!["server"]!.GetValue<string>());
        Assert.Equal(RuntimeConfigBuilder.TunnelResolverTag, excluded["final"]!.GetValue<string>());
    }

    [Fact]
    public void Ipv6_protection_controls_the_tunnel_address_and_strategy()
    {
        var withIpv6 = Build(Fixtures.Settings(ipv6: true));
        Assert.Equal(2, withIpv6["inbounds"]![0]!["address"]!.AsArray().Count);
        Assert.Equal("prefer_ipv4", withIpv6["dns"]!["strategy"]!.GetValue<string>());

        var withoutIpv6 = Build(Fixtures.Settings(ipv6: false));
        Assert.Single(withoutIpv6["inbounds"]![0]!["address"]!.AsArray());
        Assert.Equal("ipv4_only", withoutIpv6["dns"]!["strategy"]!.GetValue<string>());
    }

    [Fact]
    public void Local_network_access_is_opt_in()
    {
        Assert.DoesNotContain(
            Build(Fixtures.Settings(allowLan: false))["route"]!["rules"]!.AsArray(),
            rule => rule!.AsObject().ContainsKey("ip_is_private"));

        Assert.Contains(
            Build(Fixtures.Settings(allowLan: true))["route"]!["rules"]!.AsArray(),
            rule => rule!.AsObject().ContainsKey("ip_is_private"));
    }

    [Fact]
    public void WireGuard_profiles_are_emitted_as_endpoints()
    {
        var parsed = TunnelParser.Parse(Fixtures.WireGuardConf);
        var runtime = RuntimeConfigBuilder.Build(parsed, Fixtures.Settings(), Fixtures.Apps);
        var root = JsonNode.Parse(runtime.Json)!.AsObject();

        Assert.Equal(RuntimeConfigBuilder.ProxyTag, root["endpoints"]![0]!["tag"]!.GetValue<string>());
        Assert.Single(root["outbounds"]!.AsArray());
    }

    [Fact]
    public void Probe_and_control_ports_are_wired_into_the_config()
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessReality);
        var runtime = RuntimeConfigBuilder.Build(parsed, Fixtures.Settings(), Fixtures.Apps, 21080, 29090, "s3cret");
        var root = JsonNode.Parse(runtime.Json)!.AsObject();

        Assert.Equal(21080, root["inbounds"]![1]!["listen_port"]!.GetValue<int>());
        Assert.Equal("127.0.0.1:29090", root["experimental"]!["clash_api"]!["external_controller"]!.GetValue<string>());
        Assert.Equal("s3cret", root["experimental"]!["clash_api"]!["secret"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:21080/", runtime.ProxyUri.ToString());
    }

    [Fact]
    public void Executable_paths_are_matched_case_insensitively()
    {
        var route = Build(Fixtures.Settings())["route"]!.AsObject();
        var pattern = route["rules"]!.AsArray()
            .OfType<JsonObject>()
            .Single(rule => rule.ContainsKey("process_path_regex"))["process_path_regex"]![0]!
            .GetValue<string>();

        Assert.StartsWith("(?i)^", pattern);
        Assert.EndsWith("$", pattern);
        Assert.Contains(@"C:\\Program Files\\Mozilla Firefox\\firefox\.exe", pattern);
    }

    [Fact]
    public void Regex_escaping_leaves_spaces_alone()
    {
        // RE2, the engine the core uses, rejects "\ " as an unknown escape sequence.
        Assert.DoesNotContain(@"\ ", RuntimeConfigBuilder.EscapeRegex(@"C:\Program Files\app.exe"));
    }

    [Fact]
    public void Duplicate_applications_are_collapsed()
    {
        AppTarget[] apps =
        [
            new() { DisplayName = "Firefox", Path = @"C:\Program Files\Mozilla Firefox\firefox.exe" },
            new() { DisplayName = "Firefox again", Path = @"C:\PROGRAM FILES\MOZILLA FIREFOX\FIREFOX.EXE" }
        ];

        var route = Build(Fixtures.Settings(), apps)["route"]!.AsObject();
        var patterns = route["rules"]!.AsArray()
            .OfType<JsonObject>()
            .Single(rule => rule.ContainsKey("process_path_regex"))["process_path_regex"]!.AsArray();

        Assert.Single(patterns);
    }
}
