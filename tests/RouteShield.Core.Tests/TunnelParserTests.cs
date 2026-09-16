using System.Text.Json.Nodes;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

public class TunnelParserTests
{
    [Fact]
    public void Vless_reality_link_enables_utls()
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessReality);
        var tls = parsed.Outbound!["tls"]!.AsObject();

        Assert.Equal("VLESS", parsed.FormatName);
        Assert.Equal("Frankfurt", parsed.DisplayName);
        Assert.Equal("fra-01.example.net", parsed.Outbound!["server"]!.GetValue<string>());
        Assert.Equal(443, parsed.Outbound!["server_port"]!.GetValue<int>());
        Assert.Equal("xtls-rprx-vision", parsed.Outbound!["flow"]!.GetValue<string>());
        Assert.Equal("cdn.example.net", tls["server_name"]!.GetValue<string>());
        Assert.Equal(Fixtures.RealityPublicKey, tls["reality"]!["public_key"]!.GetValue<string>());

        // sing-box refuses a Reality client that has no uTLS profile.
        Assert.True(tls["utls"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("chrome", tls["utls"]!["fingerprint"]!.GetValue<string>());
    }

    [Fact]
    public void Vless_websocket_link_keeps_the_declared_fingerprint()
    {
        var parsed = TunnelParser.Parse(Fixtures.VlessWebSocket);
        var transport = parsed.Outbound!["transport"]!.AsObject();

        Assert.Equal("ws", transport["type"]!.GetValue<string>());
        Assert.Equal("/ray", transport["path"]!.GetValue<string>());
        Assert.Equal("cdn.example.net", transport["headers"]!["Host"]!.GetValue<string>());
        Assert.Equal("chrome", parsed.Outbound!["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());
    }

    [Fact]
    public void Tls_links_without_a_fingerprint_imitate_chrome()
    {
        // The core's own client hello is recognisably Go; every current client imitates a browser.
        var trojan = TunnelParser.Parse(Fixtures.Trojan).Outbound!["tls"]!.AsObject();

        Assert.True(trojan["utls"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("chrome", trojan["utls"]!["fingerprint"]!.GetValue<string>());
    }

    [Fact]
    public void Http2_transports_are_not_fingerprinted_unless_asked()
    {
        var grpc = TunnelParser.Parse(Fixtures.VlessGrpc).Outbound!;

        Assert.Equal("grpc", grpc["transport"]!["type"]!.GetValue<string>());
        Assert.Null(grpc["tls"]!["utls"]);

        var asked = TunnelParser.Parse(Fixtures.VlessGrpc.Replace("#Grpc", "&fp=firefox#Grpc")).Outbound!;
        Assert.Equal("firefox", asked["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());
    }

    [Fact]
    public void A_link_can_refuse_the_fingerprint()
    {
        var parsed = TunnelParser.Parse(Fixtures.Trojan.Replace("#Tokyo", "&fp=none#Tokyo"));

        Assert.Null(parsed.Outbound!["tls"]!["utls"]);
    }

    [Fact]
    public void Hysteria2_link_carries_obfuscation_bandwidth_and_port_hopping()
    {
        var parsed = TunnelParser.Parse(Fixtures.Hysteria2);
        var outbound = parsed.Outbound!;

        Assert.Equal("Hysteria2", parsed.FormatName);
        Assert.Equal("Hop", parsed.DisplayName);
        Assert.Equal("hysteria2", outbound["type"]!.GetValue<string>());
        Assert.Equal("letmein", outbound["password"]!.GetValue<string>());
        Assert.Equal("salamander", outbound["obfs"]!["type"]!.GetValue<string>());
        Assert.Equal("salt", outbound["obfs"]!["password"]!.GetValue<string>());
        Assert.Equal(50, outbound["up_mbps"]!.GetValue<int>());
        Assert.Equal(200, outbound["down_mbps"]!.GetValue<int>());
        Assert.True(outbound["tls"]!["insecure"]!.GetValue<bool>());
        Assert.Equal("hy.example.net", outbound["tls"]!["server_name"]!.GetValue<string>());

        // Port hopping replaces the single port with ranges the core understands.
        Assert.Null(outbound["server_port"]);
        Assert.Equal(["20000:30000", "443:443"], outbound["server_ports"]!.AsArray().Select(port => port!.GetValue<string>()));
    }

    [Fact]
    public void Hy2_is_an_alias_and_defaults_to_port_443()
    {
        var parsed = TunnelParser.Parse("hy2://pw@hy.example.net#Short");

        Assert.Equal(443, parsed.Outbound!["server_port"]!.GetValue<int>());
        Assert.Null(parsed.Outbound!["obfs"]);
        Assert.Null(parsed.Outbound!["tls"]!["insecure"]);
    }

    [Fact]
    public void Tuic_link_splits_uuid_and_password()
    {
        var parsed = TunnelParser.Parse(Fixtures.Tuic);
        var outbound = parsed.Outbound!;

        Assert.Equal("TUIC", parsed.FormatName);
        Assert.Equal("b1f0e4c2-0000-4000-8000-000000009ac4", outbound["uuid"]!.GetValue<string>());
        Assert.Equal("pw", outbound["password"]!.GetValue<string>());
        Assert.Equal("bbr", outbound["congestion_control"]!.GetValue<string>());
        Assert.Equal("native", outbound["udp_relay_mode"]!.GetValue<string>());
        Assert.Equal(["h3"], outbound["tls"]!["alpn"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.True(outbound["tls"]!["insecure"]!.GetValue<bool>());
    }

    [Fact]
    public void AnyTls_link_is_tls_with_a_browser_fingerprint()
    {
        var parsed = TunnelParser.Parse(Fixtures.AnyTls);
        var outbound = parsed.Outbound!;

        Assert.Equal("anytls", outbound["type"]!.GetValue<string>());
        Assert.Equal("pw", outbound["password"]!.GetValue<string>());
        Assert.Equal("chrome", outbound["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());
    }

    [Fact]
    public void Trojan_link_turns_tls_on_without_being_asked()
    {
        var parsed = TunnelParser.Parse(Fixtures.Trojan);

        Assert.Equal("Tokyo relay", parsed.DisplayName);
        Assert.Equal("s3cret", parsed.Outbound!["password"]!.GetValue<string>());
        Assert.True(parsed.Outbound!["tls"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Shadowsocks_link_decodes_base64_credentials()
    {
        var parsed = TunnelParser.Parse(Fixtures.Shadowsocks);

        Assert.Equal("Shadow", parsed.DisplayName);
        Assert.Equal("aes-256-gcm", parsed.Outbound!["method"]!.GetValue<string>());
        Assert.Equal("password", parsed.Outbound!["password"]!.GetValue<string>());
        Assert.Equal(8388, parsed.Outbound!["server_port"]!.GetValue<int>());
    }

    [Fact]
    public void Vmess_link_carries_transport_and_name()
    {
        var payload = """
            {"v":"2","ps":"Berlin","add":"ber.example.net","port":"443","id":"b1f0e4c2-0000-4000-8000-000000009ac4",
             "aid":"0","net":"ws","path":"/mess","host":"cdn.example.net","tls":"tls"}
            """;
        var link = "vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        var parsed = TunnelParser.Parse(link);

        Assert.Equal("Berlin", parsed.DisplayName);
        Assert.Equal(443, parsed.Outbound!["server_port"]!.GetValue<int>());
        Assert.Equal("ws", parsed.Outbound!["transport"]!["type"]!.GetValue<string>());
        Assert.True(parsed.Outbound!["tls"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void WireGuard_conf_becomes_an_endpoint()
    {
        var parsed = TunnelParser.Parse(Fixtures.WireGuardConf);

        Assert.Null(parsed.Outbound);
        Assert.NotNull(parsed.Endpoint);
        Assert.Equal(1420, parsed.Endpoint!["mtu"]!.GetValue<int>());
        Assert.Equal(2, parsed.Endpoint!["address"]!.AsArray().Count);

        var peer = parsed.Endpoint!["peers"]!.AsArray()[0]!.AsObject();
        Assert.Equal("wg.example.net", peer["address"]!.GetValue<string>());
        Assert.Equal(51820, peer["port"]!.GetValue<int>());
        Assert.Equal(25, peer["persistent_keepalive_interval"]!.GetValue<int>());
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void WireGuard_without_a_default_route_is_flagged()
    {
        var conf = Fixtures.WireGuardConf.Replace("0.0.0.0/0, ::/0", "10.8.0.0/24");

        Assert.Single(TunnelParser.Parse(conf).Warnings);
    }

    [Fact]
    public void SingBox_json_is_retagged_as_the_proxy()
    {
        var parsed = TunnelParser.Parse(Fixtures.SingBoxJson);

        Assert.Equal("sing-box JSON", parsed.FormatName);
        Assert.Equal(TunnelParser.ProxyTag, parsed.Outbound!["tag"]!.GetValue<string>());
        Assert.Equal("json.example.net", parsed.Outbound!["server"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a config")]
    [InlineData("vless://")]
    public void Unusable_input_is_reported_as_unknown(string input) =>
        Assert.Equal("Unknown", TunnelParser.DetectFormat(input));

    [Fact]
    public void Reality_without_a_public_key_is_rejected()
    {
        var link = Fixtures.VlessReality.Replace("&pbk=" + Fixtures.RealityPublicKey, string.Empty);

        Assert.Throws<FormatException>(() => TunnelParser.Parse(link));
    }

    [Fact]
    public void Unsupported_transport_is_rejected()
    {
        var link = Fixtures.VlessWebSocket.Replace("type=ws", "type=kcp");

        Assert.Throws<NotSupportedException>(() => TunnelParser.Parse(link));
    }
}
