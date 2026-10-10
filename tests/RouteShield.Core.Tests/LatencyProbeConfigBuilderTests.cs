using System.Text.Json.Nodes;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class LatencyProbeConfigBuilderTests
{
    [Fact]
    public void Every_profile_becomes_a_node_the_clash_api_can_time()
    {
        var vless = Guid.NewGuid();
        var wireGuard = Guid.NewGuid();

        var json = LatencyProbeConfigBuilder.Build(
            [(vless, TunnelParser.Parse(Fixtures.VlessReality)), (wireGuard, TunnelParser.Parse(Fixtures.WireGuardConf))],
            29191,
            "probe");
        var root = JsonNode.Parse(json)!.AsObject();

        var outboundTags = root["outbounds"]!.AsArray().Select(node => node!["tag"]!.GetValue<string>()).ToList();
        Assert.Contains(LatencyProbeConfigBuilder.TagFor(vless), outboundTags);
        Assert.Contains(RuntimeConfigBuilder.DirectTag, outboundTags);
        Assert.Equal(LatencyProbeConfigBuilder.TagFor(wireGuard), root["endpoints"]![0]!["tag"]!.GetValue<string>());

        Assert.Null(root["inbounds"]);
        Assert.Equal("127.0.0.1:29191", root["experimental"]!["clash_api"]!["external_controller"]!.GetValue<string>());
        Assert.Equal(RuntimeConfigBuilder.LocalResolverTag, root["route"]!["default_domain_resolver"]!["server"]!.GetValue<string>());
    }

    [Fact]
    public void Tags_are_stable_and_safe_for_a_url()
    {
        var id = Guid.Parse("b1f0e4c2-0000-4000-8000-000000009ac4");

        Assert.Equal("p-b1f0e4c2000040008000000000009ac4", LatencyProbeConfigBuilder.TagFor(id));
    }

    [Fact]
    public void An_empty_library_is_refused()
    {
        Assert.Throws<ArgumentException>(() => LatencyProbeConfigBuilder.Build([], 29191, "probe"));
    }
}
