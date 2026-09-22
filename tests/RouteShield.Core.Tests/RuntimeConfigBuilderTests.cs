using System.Text.Json.Nodes;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class RuntimeConfigBuilderTests
{
    private static readonly PortPlan FixedPorts = new(21080, 29090, "s3cret", []);

    private static JsonObject Build(AppSettings settings, params AppTarget[] apps) =>
        Build(settings, [], apps);

    private static JsonObject Build(AppSettings settings, IReadOnlyList<BridgeRoute> bridges, params AppTarget[] apps)
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessReality);
        var ports = new PortPlan(21080, 29090, "s3cret", Enumerable.Range(23000, bridges.Count).ToArray());
        var runtime = RuntimeConfigBuilder.Build(parsed, settings, apps.Length == 0 ? Fixtures.Apps : apps, bridges, ports);
        return JsonNode.Parse(runtime.Json)!.AsObject();
    }

    private static JsonArray RulesOf(JsonObject root) => root["route"]!["rules"]!.AsArray();

    /// <summary>The rule that routes the policy's executables; the QUIC rule names them too.</summary>
    private static JsonObject ProcessRuleOf(JsonObject root) => RulesOf(root)
        .OfType<JsonObject>()
        .Single(rule => rule.ContainsKey("process_path_regex") && rule["action"]?.GetValue<string>() == "route");

    private static JsonObject? QuicRuleOf(JsonObject root) => RulesOf(root)
        .OfType<JsonObject>()
        .SingleOrDefault(rule => rule["action"]?.GetValue<string>() == "reject");

    private static int IndexOf(JsonObject root, Func<JsonObject, bool> match) => RulesOf(root)
        .Select((rule, index) => (Rule: (JsonObject)rule!, Index: index))
        .First(entry => match(entry.Rule))
        .Index;

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

            var referenced = new List<string>
            {
                dns["final"]!.GetValue<string>(),
                root["route"]!["default_domain_resolver"]!["server"]!.GetValue<string>()
            };

            if (dns["rules"] is JsonArray rules)
            {
                referenced.AddRange(rules
                    .OfType<JsonObject>()
                    .Where(rule => rule.ContainsKey("server"))
                    .Select(rule => rule["server"]!.GetValue<string>()));
            }

            Assert.All(referenced, tag => Assert.Contains(tag, declared));
        }
    }

    [Fact]
    public void Secure_dns_answers_names_from_the_fake_range()
    {
        // Windows sends every DNS query from the DNS Client service, never from the application
        // that asked, so no per-process rule can carry a routed application's DNS through the
        // tunnel. Fake answers sidestep the question: the name itself travels to the proxy.
        var dns = Build(Fixtures.Settings())["dns"]!.AsObject();
        var fake = dns["servers"]!.AsArray().OfType<JsonObject>()
            .Single(server => server["tag"]!.GetValue<string>() == RuntimeConfigBuilder.FakeResolverTag);

        Assert.Equal("fakeip", fake["type"]!.GetValue<string>());
        Assert.Equal("198.18.0.0/15", fake["inet4_range"]!.GetValue<string>());
        Assert.Equal("fc00::/18", fake["inet6_range"]!.GetValue<string>());

        var rule = dns["rules"]![0]!.AsObject();
        Assert.Equal(RuntimeConfigBuilder.FakeResolverTag, rule["server"]!.GetValue<string>());
        Assert.Equal(["A", "AAAA"], rule["query_type"]!.AsArray().Select(type => type!.GetValue<string>()));
        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, dns["final"]!.GetValue<string>());
    }

    [Fact]
    public void Fake_addresses_survive_a_restart()
    {
        var cache = Build(Fixtures.Settings())["experimental"]!["cache_file"]!.AsObject();

        Assert.True(cache["enabled"]!.GetValue<bool>());
        Assert.True(cache["store_fakeip"]!.GetValue<bool>());
        Assert.Equal(AppPaths.CacheFile, cache["path"]!.GetValue<string>());
    }

    [Fact]
    public void Ipv6_off_answers_aaaa_with_nothing_instead_of_a_real_address()
    {
        var dns = Build(Fixtures.Settings(ipv6: false))["dns"]!.AsObject();
        var rules = dns["rules"]!.AsArray().OfType<JsonObject>().ToList();

        var aaaa = rules.Single(rule => rule["query_type"]!.AsArray().Any(type => type!.GetValue<string>() == "AAAA"));
        Assert.Equal("predefined", aaaa["action"]!.GetValue<string>());
        Assert.Equal("NOERROR", aaaa["rcode"]!.GetValue<string>());

        var fake = dns["servers"]!.AsArray().OfType<JsonObject>()
            .Single(server => server["tag"]!.GetValue<string>() == RuntimeConfigBuilder.FakeResolverTag);
        Assert.Null(fake["inet6_range"]);
        Assert.Equal("ipv4_only", dns["strategy"]!.GetValue<string>());
    }

    [Fact]
    public void Secure_dns_off_leaves_a_single_local_resolver()
    {
        var root = Build(Fixtures.Settings(dnsProtection: false));
        var dns = root["dns"]!.AsObject();

        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, dns["final"]!.GetValue<string>());
        Assert.Single(dns["servers"]!.AsArray());
        Assert.Null(dns["rules"]);
        Assert.Null(root["experimental"]!["cache_file"]);
        Assert.DoesNotContain(root["route"]!["rules"]!.AsArray(), rule => rule!["action"]!.GetValue<string>() == "hijack-dns");
    }

    [Theory]
    [InlineData(RouteMode.SelectedAppsOnly, "direct", RuntimeConfigBuilder.ProxyTag)]
    [InlineData(RouteMode.AllExceptSelected, "proxy", RuntimeConfigBuilder.DirectTag)]
    public void Selected_applications_are_routed_against_the_default(RouteMode mode, string expectedFinal, string expectedOutbound)
    {
        var root = Build(Fixtures.Settings(mode));
        var processRule = ProcessRuleOf(root);

        Assert.Equal(expectedFinal, root["route"]!["final"]!.GetValue<string>());
        Assert.Equal(expectedOutbound, processRule["outbound"]!.GetValue<string>());
        Assert.Equal(2, processRule["process_path_regex"]!.AsArray().Count);
    }

    [Fact]
    public void Full_tunnel_needs_no_application_list()
    {
        var route = Build(Fixtures.Settings(RouteMode.FullTunnel), [], [])["route"]!.AsObject();

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
    public void Ipv6_protection_controls_the_tunnel_address()
    {
        Assert.Equal(2, Build(Fixtures.Settings(ipv6: true))["inbounds"]![0]!["address"]!.AsArray().Count);
        Assert.Single(Build(Fixtures.Settings(ipv6: false))["inbounds"]![0]!["address"]!.AsArray());
    }

    [Fact]
    public void Local_network_access_covers_addresses_and_local_names()
    {
        static bool CoversLocalNames(JsonObject rule) =>
            rule["domain_suffix"]?.AsArray().Any(suffix => suffix!.GetValue<string>() == ".local") == true;

        var withoutLan = Build(Fixtures.Settings(allowLan: false))["route"]!["rules"]!.AsArray();
        Assert.DoesNotContain(withoutLan, rule => rule!.AsObject().ContainsKey("ip_is_private"));
        Assert.DoesNotContain(withoutLan, rule => CoversLocalNames(rule!.AsObject()));

        var withLan = Build(Fixtures.Settings(allowLan: true))["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        var byAddress = withLan.Single(rule => rule.ContainsKey("ip_is_private"));
        var byName = withLan.Single(CoversLocalNames);

        Assert.Equal(RuntimeConfigBuilder.DirectTag, byAddress["outbound"]!.GetValue<string>());
        Assert.Equal(RuntimeConfigBuilder.DirectTag, byName["outbound"]!.GetValue<string>());

        // Both must be decided before the process rule, or a routed application loses its printer.
        var processIndex = withLan.FindIndex(rule =>
            rule.ContainsKey("process_path_regex") && rule["action"]?.GetValue<string>() == "route");

        Assert.True(withLan.IndexOf(byAddress) < processIndex);
        Assert.True(withLan.IndexOf(byName) < processIndex);
    }

    [Fact]
    public void WireGuard_profiles_are_emitted_as_endpoints()
    {
        var parsed = TunnelParser.Parse(Fixtures.WireGuardConf);
        var runtime = RuntimeConfigBuilder.Build(parsed, Fixtures.Settings(), Fixtures.Apps, [], FixedPorts);
        var root = JsonNode.Parse(runtime.Json)!.AsObject();

        Assert.Equal(RuntimeConfigBuilder.ProxyTag, root["endpoints"]![0]!["tag"]!.GetValue<string>());
        Assert.Single(root["outbounds"]!.AsArray());
    }

    [Fact]
    public void Probe_and_control_ports_are_wired_into_the_config()
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessReality);
        var runtime = RuntimeConfigBuilder.Build(parsed, Fixtures.Settings(), Fixtures.Apps, [], FixedPorts);
        var root = JsonNode.Parse(runtime.Json)!.AsObject();

        Assert.Equal(21080, root["inbounds"]![1]!["listen_port"]!.GetValue<int>());
        Assert.Equal("127.0.0.1:29090", root["experimental"]!["clash_api"]!["external_controller"]!.GetValue<string>());
        Assert.Equal("s3cret", root["experimental"]!["clash_api"]!["secret"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:21080/", runtime.ProxyUri.ToString());
        Assert.Empty(runtime.Bridges);
    }

    [Fact]
    public void Bridge_routes_get_their_own_loopback_inbounds()
    {
        var pinned = new VpnProfile { Name = "Tokyo relay" };
        var active = new VpnProfile { Name = "Frankfurt" };

        BridgeRoute[] bridges =
        [
            BridgeRoute.Active(active),
            BridgeRoute.Profile(pinned, TunnelParser.Parse(Fixtures.Trojan)),
            BridgeRoute.Bypass()
        ];

        var parsed = TunnelParser.Parse(Fixtures.VlessReality);
        var ports = new PortPlan(21080, 29090, "s3cret", [23000, 23001, 23002]);
        var runtime = RuntimeConfigBuilder.Build(parsed, Fixtures.Settings(), Fixtures.Apps, bridges, ports);
        var root = JsonNode.Parse(runtime.Json)!.AsObject();

        var inbounds = root["inbounds"]!.AsArray().OfType<JsonObject>()
            .Where(inbound => inbound["tag"]!.GetValue<string>().StartsWith("bridge-in-"))
            .ToList();
        Assert.Equal([23000, 23001, 23002], inbounds.Select(inbound => inbound["listen_port"]!.GetValue<int>()));
        Assert.All(inbounds, inbound => Assert.Equal("127.0.0.1", inbound["listen"]!.GetValue<string>()));

        var rules = root["route"]!["rules"]!.AsArray().OfType<JsonObject>()
            .Where(rule => rule["inbound"]?.GetValue<string>().StartsWith("bridge-in-") == true)
            .ToDictionary(rule => rule["inbound"]!.GetValue<string>(), rule => rule["outbound"]!.GetValue<string>());

        // The active route reuses the tunnel's outbound, the pinned one gets its own, bypass goes direct.
        Assert.Equal(RuntimeConfigBuilder.ProxyTag, rules["bridge-in-0"]);
        Assert.Equal("bridge-1", rules["bridge-in-1"]);
        Assert.Equal(RuntimeConfigBuilder.DirectTag, rules["bridge-in-2"]);

        var pinnedOutbound = root["outbounds"]!.AsArray().OfType<JsonObject>()
            .Single(node => node["tag"]!.GetValue<string>() == "bridge-1");
        Assert.Equal("trojan", pinnedOutbound["type"]!.GetValue<string>());
        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, pinnedOutbound["domain_resolver"]!.GetValue<string>());

        Assert.Equal(
            [BridgeKind.Active, BridgeKind.Profile, BridgeKind.Bypass],
            runtime.Bridges.Select(binding => binding.Kind));
        Assert.Equal(pinned.Id, runtime.Bridges[1].ProfileId);
        Assert.Equal(23002, runtime.Bridges[2].Port);
    }

    [Fact]
    public void Bridge_routes_come_before_the_routing_policy()
    {
        var root = Build(Fixtures.Settings(RouteMode.FullTunnel), [BridgeRoute.Bypass()]);
        var rules = root["route"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();

        // A tab sent to "No VPN" has to win over "everything goes through the tunnel".
        Assert.Contains(rules, rule => rule["inbound"]?.GetValue<string>() == "bridge-in-0");
        Assert.Equal(RuntimeConfigBuilder.ProxyTag, root["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void A_port_is_required_per_bridge_route()
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessReality);

        Assert.Throws<ArgumentException>(() =>
            RuntimeConfigBuilder.Build(parsed, Fixtures.Settings(), Fixtures.Apps, [BridgeRoute.Bypass()], FixedPorts));
    }

    [Fact]
    public void An_unbound_run_follows_whatever_holds_the_default_route()
    {
        var route = Build(Fixtures.Settings())["route"]!.AsObject();

        Assert.True(route["auto_detect_interface"]!.GetValue<bool>());
        Assert.Null(route["default_interface"]);
    }

    [Fact]
    public void A_bound_run_names_its_adapter_and_never_asks_for_the_default_route()
    {
        // Another VPN owns the default route while it is connected; detecting it is the bug.
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.VlessReality)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts,
            new NetworkBinding("Wi-Fi"));

        var route = JsonNode.Parse(runtime.Json)!["route"]!.AsObject();

        Assert.Equal("Wi-Fi", route["default_interface"]!.GetValue<string>());
        Assert.Null(route["auto_detect_interface"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Names_are_always_answered_by_the_system_resolver(bool bound)
    {
        // Pinning the adapter must not pin the resolver with it: an ISP's own server refuses
        // the names the tunnel exists to reach, including the proxy provider's own address,
        // where the resolver Windows is configured with answers them.
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.VlessReality)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts,
            bound ? new NetworkBinding("Wi-Fi") : null);

        var resolver = JsonNode.Parse(runtime.Json)!["dns"]!["servers"]!.AsArray().OfType<JsonObject>()
            .Single(server => server["tag"]!.GetValue<string>() == RuntimeConfigBuilder.LocalResolverTag);

        Assert.Equal("local", resolver["type"]!.GetValue<string>());
        Assert.Null(resolver["server"]);
    }

    [Fact]
    public void A_node_without_ipv6_of_its_own_is_never_handed_an_ipv6_destination()
    {
        // "missing IPv6 local address" on every v6 connection is what this prevents.
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.WireGuardIpv4Only)),
            Fixtures.Settings(ipv6: true), Fixtures.Apps, [], FixedPorts);

        var root = JsonNode.Parse(runtime.Json)!.AsObject();
        var addresses = root["inbounds"]!.AsArray().OfType<JsonObject>()
            .Single(inbound => inbound["type"]!.GetValue<string>() == "tun")["address"]!.AsArray();

        Assert.Single(addresses);
        Assert.Equal("ipv4_only", root["dns"]!["strategy"]!.GetValue<string>());

        var answered = root["dns"]!["rules"]!.AsArray().OfType<JsonObject>()
            .Single(rule => rule["action"]?.GetValue<string>() == "predefined");

        Assert.Equal("NOERROR", answered["rcode"]!.GetValue<string>());
    }

    [Fact]
    public void A_node_with_ipv6_keeps_it()
    {
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.WireGuardConf)),
            Fixtures.Settings(ipv6: true), Fixtures.Apps, [], FixedPorts);

        var addresses = JsonNode.Parse(runtime.Json)!["inbounds"]!.AsArray().OfType<JsonObject>()
            .Single(inbound => inbound["type"]!.GetValue<string>() == "tun")["address"]!.AsArray();

        Assert.Equal(2, addresses.Count);
    }

    [Fact]
    public void The_tunnel_interface_never_offers_more_than_the_node_can_carry()
    {
        static int MtuOf(JsonObject root) => root["inbounds"]!.AsArray().OfType<JsonObject>()
            .Single(inbound => inbound["type"]!.GetValue<string>() == "tun")["mtu"]!.GetValue<int>();

        // A WireGuard peer wraps each packet in one datagram; a 9000-byte frame cannot be sent.
        var wireGuard = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.WireGuardConf)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts);

        Assert.Equal(1420, MtuOf(JsonNode.Parse(wireGuard.Json)!.AsObject()));

        // A peer that declares no MTU gets the core's own default rather than the full frame.
        var silent = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.WireGuardIpv4Only)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts);

        Assert.Equal(TunnelParser.DefaultWireGuardMtu, MtuOf(JsonNode.Parse(silent.Json)!.AsObject()));

        // Everything else keeps the large frame.
        Assert.Equal(9000, MtuOf(Build(Fixtures.Settings())));
    }

    [Fact]
    public void Wireguard_inside_another_vpn_is_sized_to_fit_the_adapter_carrying_it()
    {
        // Cisco's adapter at 1300: a peer sized for 1500 produced datagrams the socket refused.
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.WireGuardConf)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts, null, underlayMtu: 1300);

        var root = JsonNode.Parse(runtime.Json)!.AsObject();
        var endpointMtu = root["endpoints"]!.AsArray().Single()!["mtu"]!.GetValue<int>();
        var tunMtu = root["inbounds"]!.AsArray().OfType<JsonObject>()
            .Single(inbound => inbound["type"]!.GetValue<string>() == "tun")["mtu"]!.GetValue<int>();

        Assert.Equal(1280, endpointMtu);
        Assert.Equal(1280, tunMtu);
    }

    [Fact]
    public void A_roomier_underlay_only_trims_what_does_not_fit()
    {
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.WireGuardConf)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts, null, underlayMtu: 1406);

        var endpointMtu = JsonNode.Parse(runtime.Json)!["endpoints"]!.AsArray().Single()!["mtu"]!.GetValue<int>();

        Assert.Equal(1406 - 80, endpointMtu);
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(null)]
    public void A_plain_link_leaves_the_peer_as_declared(int? underlay)
    {
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.WireGuardConf)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts, null, underlay);

        Assert.Equal(1420, JsonNode.Parse(runtime.Json)!["endpoints"]!.AsArray().Single()!["mtu"]!.GetValue<int>());
    }

    [Fact]
    public void A_tcp_proxy_keeps_the_large_frame_inside_another_vpn()
    {
        // A stream-based proxy re-segments everything; the interface size is not its problem.
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.VlessReality)),
            Fixtures.Settings(), Fixtures.Apps, [], FixedPorts, null, underlayMtu: 1300);

        var tunMtu = JsonNode.Parse(runtime.Json)!["inbounds"]!.AsArray().OfType<JsonObject>()
            .Single(inbound => inbound["type"]!.GetValue<string>() == "tun")["mtu"]!.GetValue<int>();

        Assert.Equal(9000, tunMtu);
    }

    [Fact]
    public void Quic_is_refused_only_for_the_applications_the_policy_routes()
    {
        var rule = QuicRuleOf(Build(Fixtures.Settings()))!;

        Assert.Equal("udp", rule["network"]!.GetValue<string>());
        Assert.Equal([443, 80], rule["port"]!.AsArray().Select(port => port!.GetValue<int>()));
        Assert.Equal("default", rule["method"]!.GetValue<string>());

        // Scoped to the routed executables, so nothing outside the tunnel loses QUIC...
        Assert.Equal(2, rule["process_path_regex"]!.AsArray().Count);

        // ...and refused before the rule that would otherwise send it to the proxy.
        var root = Build(Fixtures.Settings());
        Assert.True(
            IndexOf(root, candidate => candidate["action"]?.GetValue<string>() == "reject")
            < IndexOf(root, candidate => candidate.ContainsKey("process_path_regex") && candidate["action"]?.GetValue<string>() == "route"));
    }

    [Fact]
    public void Excluded_applications_keep_quic_on_their_own_connection()
    {
        var root = Build(Fixtures.Settings(RouteMode.AllExceptSelected));

        // The rule that sends them out directly has to win over the refusal that follows it.
        Assert.True(
            IndexOf(root, candidate => candidate.ContainsKey("process_path_regex") && candidate["action"]?.GetValue<string>() == "route")
            < IndexOf(root, candidate => candidate["action"]?.GetValue<string>() == "reject"));

        Assert.Null(QuicRuleOf(root)!["process_path_regex"]);
    }

    [Fact]
    public void A_full_tunnel_refuses_quic_everywhere()
    {
        var rule = QuicRuleOf(Build(Fixtures.Settings(RouteMode.FullTunnel), [], []))!;

        Assert.Null(rule["process_path_regex"]);
    }

    [Fact]
    public void Quic_can_be_left_alone()
    {
        var settings = Fixtures.Settings();
        settings.BlockQuic = false;

        Assert.Null(QuicRuleOf(Build(settings)));
    }

    [Fact]
    public void Domestic_names_leave_on_the_local_connection_before_anything_else_claims_them()
    {
        var root = Build(Fixtures.Settings());
        var domestic = RulesOf(root).OfType<JsonObject>()
            .Single(rule => rule["domain_suffix"]?.AsArray().Any(suffix => suffix!.GetValue<string>() == ".ir") == true);

        Assert.Equal(RuntimeConfigBuilder.DirectTag, domestic["outbound"]!.GetValue<string>());

        var suffixes = domestic["domain_suffix"]!.AsArray().Select(suffix => suffix!.GetValue<string>()).ToArray();
        Assert.Contains("digikala.com", suffixes);
        Assert.Contains("zarinpal.com", suffixes);

        // Before the refusal, so a domestic site is never denied QUIC it can actually use.
        Assert.True(
            IndexOf(root, candidate => candidate["domain_suffix"]?.AsArray().Any(suffix => suffix!.GetValue<string>() == ".ir") == true)
            < IndexOf(root, candidate => candidate["action"]?.GetValue<string>() == "reject"));
    }

    [Fact]
    public void Domestic_routing_can_be_turned_off()
    {
        var settings = Fixtures.Settings();
        settings.DirectDomesticSites = false;

        Assert.DoesNotContain(
            RulesOf(Build(settings)).OfType<JsonObject>(),
            rule => rule["domain_suffix"]?.AsArray().Any(suffix => suffix!.GetValue<string>() == ".ir") == true);
    }

    [Fact]
    public void An_automatic_group_wraps_every_node_in_a_urltest_outbound()
    {
        var runtime = RuntimeConfigBuilder.Build(Fixtures.AutomaticGroup(), Fixtures.Settings(), Fixtures.Apps, [], FixedPorts);
        var root = JsonNode.Parse(runtime.Json)!.AsObject();

        var group = root["outbounds"]!.AsArray().OfType<JsonObject>()
            .Single(node => node["tag"]!.GetValue<string>() == RuntimeConfigBuilder.ProxyTag);

        Assert.Equal("urltest", group["type"]!.GetValue<string>());
        Assert.Equal(["auto-0", "auto-1", "auto-2"], group["outbounds"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.Equal(RuntimeConfigBuilder.GroupTestUrl, group["url"]!.GetValue<string>());
        Assert.False(group["interrupt_exist_connections"]!.GetValue<bool>());

        // Every member is placed where its type belongs and resolves its server locally.
        var members = root["outbounds"]!.AsArray().Concat(root["endpoints"]!.AsArray()).OfType<JsonObject>()
            .Where(node => node["tag"]!.GetValue<string>().StartsWith("auto-", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, members.Count);
        Assert.All(members, node => Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, node["domain_resolver"]!.GetValue<string>()));
        Assert.Equal("wireguard", root["endpoints"]!.AsArray().Single()!["type"]!.GetValue<string>());

        // The app can name the node the core chose.
        Assert.True(runtime.IsAutomatic);
        Assert.Equal("Tokyo relay", runtime.MemberName("auto-1"));
        Assert.Equal("auto-9", runtime.MemberName("auto-9"));
    }

    [Fact]
    public void A_single_node_is_not_wrapped()
    {
        var runtime = RuntimeConfigBuilder.Build(
            ConnectionTarget.Single(TunnelParser.Parse(Fixtures.Trojan)), Fixtures.Settings(), Fixtures.Apps, [], FixedPorts);
        var root = JsonNode.Parse(runtime.Json)!.AsObject();

        Assert.False(runtime.IsAutomatic);
        Assert.DoesNotContain(root["outbounds"]!.AsArray().OfType<JsonObject>(), node => node["type"]!.GetValue<string>() == "urltest");
    }

    [Fact]
    public void Tls_fragmenting_splits_tcp_handshakes_but_leaves_quic_alone()
    {
        var target = ConnectionTarget.Automatic(
            "Mixed",
            [
                ("Reality", TunnelParser.Parse(Fixtures.VlessReality)),
                ("Hysteria", TunnelParser.Parse(Fixtures.Hysteria2)),
                ("Shadow", TunnelParser.Parse(Fixtures.Shadowsocks))
            ]);

        var runtime = RuntimeConfigBuilder.Build(target, Fixtures.Settings(tlsFragment: true), Fixtures.Apps, [], FixedPorts);
        var outbounds = JsonNode.Parse(runtime.Json)!["outbounds"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(node => node["tag"]!.GetValue<string>());

        Assert.True(outbounds["auto-0"]["tls"]!["fragment"]!.GetValue<bool>());
        Assert.True(outbounds["auto-0"]["tls"]!["record_fragment"]!.GetValue<bool>());
        Assert.Null(outbounds["auto-1"]["tls"]!["fragment"]);
        Assert.Null(outbounds["auto-2"]["tls"]);
    }

    [Fact]
    public void Tls_fragmenting_is_off_unless_asked()
    {
        var proxy = Build(Fixtures.Settings())["outbounds"]!.AsArray().OfType<JsonObject>()
            .Single(node => node["tag"]!.GetValue<string>() == RuntimeConfigBuilder.ProxyTag);

        Assert.Null(proxy["tls"]!["fragment"]);
    }

    [Fact]
    public void Preferred_ports_are_honoured_when_free()
    {
        var first = RuntimeConfigBuilder.ReserveLoopbackPorts(3);
        var again = RuntimeConfigBuilder.ReserveLoopbackPorts([first[0], null, first[2]]);

        Assert.Equal(first[0], again[0]);
        Assert.Equal(first[2], again[2]);
        Assert.Equal(3, again.Distinct().Count());
    }

    [Fact]
    public void A_taken_preferred_port_falls_back_to_a_free_one()
    {
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var taken = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
            var ports = RuntimeConfigBuilder.ReserveLoopbackPorts([taken, 70000]);

            Assert.NotEqual(taken, ports[0]);
            Assert.All(ports, port => Assert.InRange(port, 1, 65535));
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void A_port_plan_keeps_last_run_bridge_ports()
    {
        var previous = PortPlan.Reserve(2);
        var next = PortPlan.Reserve(2, previous.BridgePorts.Select(port => (int?)port).ToArray());

        Assert.Equal(previous.BridgePorts, next.BridgePorts);
        Assert.NotEqual(previous.ControlSecret, next.ControlSecret);
    }

    [Fact]
    public void Reserved_ports_are_distinct()
    {
        var ports = RuntimeConfigBuilder.ReserveLoopbackPorts(6);

        Assert.Equal(6, ports.Distinct().Count());
        Assert.All(ports, port => Assert.InRange(port, 1024, 65535));
    }

    [Fact]
    public void Executable_paths_are_matched_case_insensitively()
    {
        var pattern = ProcessRuleOf(Build(Fixtures.Settings()))["process_path_regex"]![0]!.GetValue<string>();

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

        Assert.Single(ProcessRuleOf(Build(Fixtures.Settings(), apps))["process_path_regex"]!.AsArray());
    }
}
