using RouteShield.Tunnels;

namespace RouteShield.Tests;

/// <summary>Sample inputs shared by the parser and config tests. The keys are throwaway values.</summary>
internal static class Fixtures
{
    public const string RealityPublicKey = "_TjUAUe2m107-7gwTN9jNfWz_FEUrEacrL37qj64_D0";
    public const string WireGuardKey = "/TjUAUe2m107+7gwTN9jNfWz/FEUrEacrL37qj64/D0=";

    public const string VlessReality =
        "vless://b1f0e4c2-0000-4000-8000-000000009ac4@fra-01.example.net:443" +
        "?type=tcp&security=reality&flow=xtls-rprx-vision&sni=cdn.example.net" +
        "&pbk=" + RealityPublicKey + "&sid=ab12#Frankfurt";

    public const string VlessWebSocket =
        "vless://b1f0e4c2-0000-4000-8000-000000009ac4@ams-02.example.net:443" +
        "?type=ws&security=tls&path=%2Fray&host=cdn.example.net&fp=chrome#Amsterdam";

    public const string Trojan =
        "trojan://s3cret@tok-03.example.net:443?sni=tok-03.example.net#Tokyo%20relay";

    public const string Shadowsocks =
        "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@ss.example.net:8388#Shadow";

    public const string WireGuardConf = """
        [Interface]
        PrivateKey = /TjUAUe2m107+7gwTN9jNfWz/FEUrEacrL37qj64/D0=
        Address = 10.8.0.2/32, fd00::2/128
        MTU = 1420

        [Peer]
        PublicKey = /TjUAUe2m107+7gwTN9jNfWz/FEUrEacrL37qj64/D0=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = wg.example.net:51820
        PersistentKeepalive = 25
        """;

    public const string SingBoxJson = """
        {
          "outbounds": [
            {
              "type": "shadowsocks",
              "tag": "old-tag",
              "server": "json.example.net",
              "server_port": 8388,
              "method": "aes-256-gcm",
              "password": "password"
            }
          ]
        }
        """;

    public const string Hysteria2 =
        "hysteria2://letmein@hy.example.net:8443/?sni=hy.example.net&obfs=salamander&obfs-password=salt" +
        "&insecure=1&up=50&down=200&mport=20000-30000,443#Hop";

    public const string Tuic =
        "tuic://b1f0e4c2-0000-4000-8000-000000009ac4:pw@tuic.example.net:443" +
        "?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=tuic.example.net&allow_insecure=1#Quic";

    public const string AnyTls =
        "anytls://pw@any.example.net:443?sni=any.example.net#Any";

    public const string VlessGrpc =
        "vless://b1f0e4c2-0000-4000-8000-000000009ac4@grpc.example.net:443" +
        "?type=grpc&security=tls&serviceName=svc&sni=grpc.example.net#Grpc";

    public static AppTarget[] Apps =>
    [
        new() { DisplayName = "Firefox", Path = @"C:\Program Files\Mozilla Firefox\firefox.exe" },
        new() { DisplayName = "Telegram Desktop", Path = @"C:\Users\amir\AppData\Roaming\Telegram\Telegram.exe" }
    ];

    public static AppSettings Settings(
        RouteMode mode = RouteMode.SelectedAppsOnly,
        bool dnsProtection = true,
        bool ipv6 = true,
        bool allowLan = false,
        bool tlsFragment = false) => new()
        {
            RouteMode = mode,
            DnsProtection = dnsProtection,
            Ipv6Protection = ipv6,
            AllowLan = allowLan,
            TlsFragment = tlsFragment
        };

    /// <summary>Three nodes of one subscription, the way the app hands them to an automatic group.</summary>
    public static ConnectionTarget AutomaticGroup() => ConnectionTarget.Automatic(
        "Auto · Provider",
        [
            ("Frankfurt", TunnelParser.Parse(VlessReality)),
            ("Tokyo relay", TunnelParser.Parse(Trojan)),
            ("Home", TunnelParser.Parse(WireGuardConf))
        ]);
}
