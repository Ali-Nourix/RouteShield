using System.Text.Json.Nodes;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class DomainPolicyTests
{
    [Fact]
    public void Domains_are_deduplicated_and_idn_wildcard_suffixes_normalized() => Assert.Equal(
        new[] { "example.com", "xn--bcher-kva.example" }, DomainPolicy.Parse("# comment\n*.Example.COM\n.example.com\nbücher.example."));
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("example.com:443")]
    [InlineData("127.0.0.1")]
    [InlineData("a..example.com")]
    [InlineData("*.com/")]
    [InlineData("a.*.example.com")]
    [InlineData("-a.example.com")]
    [InlineData("example.com?x=1")]
    [InlineData("localhost")]
    public void Non_domain_input_is_rejected(string value) => Assert.Throws<FormatException>(() => DomainPolicy.Parse(value));
    [Fact]
    public void Domain_lists_are_bounded() => Assert.Throws<FormatException>(() =>
        DomainPolicy.Parse(string.Join('\n', Enumerable.Range(0, 201).Select(i => $"node{i}.example.com"))));
    [Fact]
    public void Custom_domain_priority_is_explicit_and_does_not_override_probe_or_pinned_browser_routes()
    {
        var settings = Fixtures.Settings(RouteMode.FullTunnel);
        settings.DirectDomains = ["direct.example.com"];
        settings.ProxyDomains = ["proxy.example.com"];
        settings.BlockedDomains = ["block.example.com"];
        var runtime = RuntimeConfigBuilder.Build(TunnelParser.Parse(Fixtures.Trojan), settings, Fixtures.Apps, [], new PortPlan(21080, 29090, "secret", []));
        var rules = JsonNode.Parse(runtime.Json)!["route"]!["rules"]!.AsArray();
        int Index(string domain) => rules.Select((rule, index) => (rule, index)).First(x => x.rule?["domain_suffix"]?.AsArray().Any(d => d?.GetValue<string>() == domain) == true).index;
        Assert.True(Index("block.example.com") < Index("proxy.example.com"));
        Assert.True(Index("proxy.example.com") < Index("direct.example.com"));
        var block = rules[Index("block.example.com")]!;
        Assert.Equal("reject", block["action"]!.GetValue<string>());
        Assert.Equal("proxy", rules[Index("proxy.example.com")]!["outbound"]!.GetValue<string>());
        Assert.Equal("direct", rules[Index("direct.example.com")]!["outbound"]!.GetValue<string>());
        Assert.Contains(rules.Take(Index("block.example.com")), rule => rule?["inbound"]?.GetValue<string>() == RuntimeConfigBuilder.ProbeInboundTag);
    }
    [Fact]
    public void WireSock_refuses_domain_rules_rather_than_ignoring_them()
    {
        var settings = new AppSettings { BlockedDomains = ["block.example.com"] };
        Assert.Throws<NotSupportedException>(() => WireSockConfig.Build(Fixtures.WireGuardConf, settings, [], null));
    }
}
