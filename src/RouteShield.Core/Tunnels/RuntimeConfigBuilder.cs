using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteShield.Tunnels;

/// <summary>
/// Assembles the sing-box configuration that enforces the current routing policy.
///
/// Two facts shape the DNS design. Every dial has to name a resolver — sing-box 1.14 refuses
/// to start otherwise — and the resolver that reaches the proxy server itself must live outside
/// the tunnel, or the tunnel can never come up; that is <see cref="LocalResolverTag"/>.
///
/// The second fact is Windows-specific: applications never send their own DNS queries. The
/// DNS Client service does, so a query can never be attributed to the program that asked, and a
/// per-process DNS rule would never see the browser. Instead, with secure DNS on, every A/AAAA
/// query is answered at once from a fake range. The application connects to the fake address,
/// the core maps it back to the name, and the name — not a locally resolved address — is what
/// travels to the proxy. Traffic that stays direct is resolved at connect time on the physical
/// adapter, exactly as it would have been without RouteShield.
/// </summary>
public static class RuntimeConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string LocalResolverTag = "dns-local";
    public const string FakeResolverTag = "dns-fake";
    public const string TunnelInterfaceName = "RouteShield";
    public const string ProbeInboundTag = "probe-in";

    private const string FakeIpv4Range = "198.18.0.0/15";
    private const string FakeIpv6Range = "fc00::/18";
    private const int TunnelMtu = 9000;

    /// <summary>How an automatic group measures its members: the same 204 endpoint the latency test uses.</summary>
    public const string GroupTestUrl = LatencyProbeConfigBuilder.TestUrl;
    public const string GroupTestInterval = "3m";
    public const string GroupIdleTimeout = "30m";
    public const int GroupToleranceMilliseconds = 50;

    /// <summary>The ports a browser speaks QUIC on; HTTP/3 is always one of these.</summary>
    private static readonly int[] QuicPorts = [443, 80];

    /// <summary>Protocols that run over QUIC, where a TCP-level TLS fragment has nothing to split.</summary>
    private static readonly HashSet<string> QuicProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "hysteria", "hysteria2", "tuic", "wireguard"
    };

    /// <summary>Names that only ever mean something on the local network.</summary>
    private static readonly string[] LocalNameSuffixes =
        [".local", ".lan", ".home", ".internal", ".home.arpa", ".localdomain"];

    /// <summary>
    /// Sites served from inside Iran. Sending them abroad and back costs a round trip for
    /// nothing, and many of them refuse foreign addresses outright — banks and government
    /// services in particular. The .ir zone covers most of it; these are the large services
    /// that sit on a generic TLD. Matching is by name, which is what the connection carries
    /// once secure DNS is answering, so no address list is needed.
    /// </summary>
    private static readonly string[] DomesticNameSuffixes =
    [
        ".ir",
        "digikala.com", "digikalajet.com", "basalam.com", "torob.com", "emalls.ir", "sheypoor.com",
        "aparat.com", "filimo.com", "telewebion.com", "namava.com", "tamashakhoneh.ir",
        "varzesh3.com", "zoomit.ir", "mehrnews.com", "irna.ir", "yjc.ir", "khabaronline.ir",
        "alibaba.ir", "snapptrip.com", "flytoday.ir", "eligasht.com", "tapsi.ir",
        "zarinpal.com", "behpardakht.com", "shaparak.ir", "sadad.ir",
        "arvancloud.com", "arvancloud.ir", "parspack.com", "iranserver.com", "abrarvan.com",
        "blogfa.com", "virgool.io", "quera.org", "sokanacademy.com",
        "cafebazaar.ir", "myket.ir", "divar.ir", "balad.ir", "neshan.org",
        "eitaa.com", "rubika.ir", "splus.ir", "igap.net"
    ];

    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = true };

    public static RuntimeConfig Build(ParsedTunnel parsed, AppSettings settings, IEnumerable<AppTarget> apps) =>
        Build(parsed, settings, apps, []);

    public static RuntimeConfig Build(
        ParsedTunnel parsed,
        AppSettings settings,
        IEnumerable<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges) =>
        Build(ConnectionTarget.Single(parsed), settings, apps, bridges, PortPlan.Reserve(bridges.Count));

    public static RuntimeConfig Build(
        ParsedTunnel parsed,
        AppSettings settings,
        IEnumerable<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges,
        PortPlan ports) =>
        Build(ConnectionTarget.Single(parsed), settings, apps, bridges, ports);

    public static RuntimeConfig Build(
        ConnectionTarget target,
        AppSettings settings,
        IEnumerable<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges,
        PortPlan ports,
        NetworkBinding? binding = null)
    {
        if (ports.BridgePorts.Count != bridges.Count)
        {
            throw new ArgumentException("One port is needed per bridge route.", nameof(ports));
        }

        var executables = NormalizeExecutablePaths(apps);
        if (settings.RouteMode != RouteMode.FullTunnel && executables.Length == 0)
        {
            throw new InvalidOperationException(
                "Pick at least one application, or switch to the full system tunnel.");
        }

        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        var members = PlaceTarget(target, settings, outbounds, endpoints);

        // A node that cannot carry IPv6 — a WireGuard peer with no v6 address — must not be
        // handed v6 destinations, or every one of them fails with "missing IPv6 local address".
        var carriesIpv6 = settings.Ipv6Protection && target.Tunnels.All(tunnel => tunnel.CarriesIpv6);

        var inbounds = BuildInbounds(settings, ports.ProxyPort, carriesIpv6, TunnelMtuFor(target));
        var rules = BuildLeadingRules(settings);
        var bindings = new List<BridgeBinding>(bridges.Count);

        for (var index = 0; index < bridges.Count; index++)
        {
            var route = bridges[index];
            var inboundTag = $"bridge-in-{index}";
            var outboundTag = route.Kind switch
            {
                BridgeKind.Active => ProxyTag,
                BridgeKind.Bypass => DirectTag,
                _ => $"bridge-{index}"
            };

            if (route.Kind == BridgeKind.Profile)
            {
                Place(
                    route.Tunnel ?? throw new ArgumentException($"Bridge route \"{route.Name}\" has no tunnel.", nameof(bridges)),
                    outboundTag,
                    settings,
                    outbounds,
                    endpoints);
            }

            inbounds.Add(LoopbackInbound(inboundTag, ports.BridgePorts[index]));
            rules.Add(new JsonObject
            {
                ["inbound"] = inboundTag,
                ["action"] = "route",
                ["outbound"] = outboundTag
            });

            bindings.Add(new BridgeBinding(route.Kind, route.ProfileId, route.Name, ports.BridgePorts[index]));
        }

        AddPolicyRules(rules, settings, executables);
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = false
            },
            ["dns"] = BuildDns(settings, carriesIpv6),
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = BuildRoute(settings, rules, binding),
            ["experimental"] = BuildExperimental(settings, ports)
        };

        if (endpoints.Count > 0)
        {
            root["endpoints"] = endpoints;
        }

        return new RuntimeConfig(
            root.ToJsonString(WriteOptions),
            ports.ProxyPort,
            ports.ControlPort,
            ports.ControlSecret,
            bindings,
            members);
    }

    /// <summary>
    /// Places what the tunnel connects to under <see cref="ProxyTag"/>. One node is placed as
    /// itself. Several become an automatic group: each node under its own member tag, and a
    /// <c>urltest</c> outbound under the proxy tag that keeps measuring them and carries traffic
    /// over the fastest. It moves on when the chosen node stops answering, and only switches
    /// for a faster one when the gap is real, so connections are not shuffled for a few ms.
    /// </summary>
    private static List<GroupMember> PlaceTarget(ConnectionTarget target, AppSettings settings, JsonArray outbounds, JsonArray endpoints)
    {
        if (!target.IsAutomatic)
        {
            Place(target.Tunnels[0], ProxyTag, settings, outbounds, endpoints);
            return [];
        }

        var members = new List<GroupMember>(target.Tunnels.Count);
        var tags = new JsonArray();

        for (var index = 0; index < target.Tunnels.Count; index++)
        {
            var tag = ConnectionTarget.MemberTag(index);
            Place(target.Tunnels[index], tag, settings, outbounds, endpoints);
            members.Add(new GroupMember(tag, target.MemberNames[index]));
            tags.Add(tag);
        }

        outbounds.Insert(0, new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = ProxyTag,
            ["outbounds"] = tags,
            ["url"] = GroupTestUrl,
            ["interval"] = GroupTestInterval,
            ["tolerance"] = GroupToleranceMilliseconds,
            ["idle_timeout"] = GroupIdleTimeout,
            ["interrupt_exist_connections"] = false
        });

        return members;
    }

    /// <summary>Adds a node under a tag, in the section its type belongs to; WireGuard is an endpoint, everything else an outbound.</summary>
    private static void Place(ParsedTunnel tunnel, string tag, AppSettings settings, JsonArray outbounds, JsonArray endpoints)
    {
        var node = TunnelParser.Clone(tunnel.Node);
        node["tag"] = tag;
        node["domain_resolver"] = LocalResolverTag;

        if (settings.TlsFragment)
        {
            ApplyTlsFragment(node);
        }

        if (tunnel.Endpoint is not null)
        {
            endpoints.Add(node);
        }
        else
        {
            outbounds.Add(node);
        }
    }

    /// <summary>
    /// Splits the TLS handshake to the server over several TCP segments and several TLS records.
    /// A firewall that matches the server name in the first packet of a connection then never
    /// sees it whole; a server sees an ordinary, if slightly slower, handshake. Only TCP carries
    /// a handshake this can split: QUIC-based protocols are left alone.
    /// </summary>
    private static void ApplyTlsFragment(JsonObject node)
    {
        if (node["tls"] is not JsonObject tls || tls["enabled"]?.GetValue<bool>() != true)
        {
            return;
        }

        var type = TunnelParser.ReadString(node, "type") ?? string.Empty;
        if (QuicProtocols.Contains(type) || node["transport"] is JsonObject transport && TunnelParser.ReadString(transport, "type") == "quic")
        {
            return;
        }

        tls["fragment"] = true;
        tls["record_fragment"] = true;
    }

    /// <summary>
    /// Names the adapter the core dials on, or lets it follow the system default.
    ///
    /// The two are alternatives: <c>auto_detect_interface</c> follows whatever holds the default
    /// route, which is exactly what another VPN takes over, so a bound run states the adapter
    /// instead and never asks.
    /// </summary>
    private static JsonObject BuildRoute(AppSettings settings, JsonArray rules, NetworkBinding? binding)
    {
        var route = new JsonObject();

        if (binding is null)
        {
            route["auto_detect_interface"] = true;
        }
        else
        {
            route["default_interface"] = binding.InterfaceName;
        }

        route["default_domain_resolver"] = new JsonObject { ["server"] = LocalResolverTag };
        route["rules"] = rules;
        route["final"] = settings.RouteMode == RouteMode.SelectedAppsOnly ? DirectTag : ProxyTag;

        return route;
    }

    /// <summary>
    /// The tunnel interface never carries more than the node underneath it can. A WireGuard peer
    /// wraps each packet in one UDP datagram, so a 9000-byte frame from the interface becomes a
    /// datagram the socket refuses to send; the interface takes the peer's own MTU instead.
    /// </summary>
    private static int TunnelMtuFor(ConnectionTarget target) =>
        target.Tunnels.Select(tunnel => tunnel.LinkMtu ?? TunnelMtu).Append(TunnelMtu).Min();

    private static JsonObject BuildDns(AppSettings settings, bool carriesIpv6)
    {
        var servers = new JsonArray(new JsonObject
        {
            ["type"] = "local",
            ["tag"] = LocalResolverTag
        });

        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["strategy"] = carriesIpv6 ? "prefer_ipv4" : "ipv4_only",
            ["cache_capacity"] = 4096,
            ["final"] = LocalResolverTag
        };

        if (!settings.DnsProtection)
        {
            return dns;
        }

        var fake = new JsonObject
        {
            ["type"] = "fakeip",
            ["tag"] = FakeResolverTag,
            ["inet4_range"] = FakeIpv4Range
        };

        var rules = new JsonArray();

        if (carriesIpv6)
        {
            fake["inet6_range"] = FakeIpv6Range;
            rules.Add(new JsonObject
            {
                ["query_type"] = new JsonArray("A", "AAAA"),
                ["server"] = FakeResolverTag
            });
        }
        else
        {
            // Without IPv6 in the tunnel a real AAAA answer would send traffic around it, so the
            // question gets an empty, successful reply and applications fall back to IPv4.
            rules.Add(new JsonObject
            {
                ["query_type"] = new JsonArray("AAAA"),
                ["action"] = "predefined",
                ["rcode"] = "NOERROR"
            });
            rules.Add(new JsonObject
            {
                ["query_type"] = new JsonArray("A"),
                ["server"] = FakeResolverTag
            });
        }

        servers.Add(fake);
        dns["rules"] = rules;
        return dns;
    }

    private static JsonArray BuildInbounds(AppSettings settings, int proxyPort, bool carriesIpv6, int mtu)
    {
        var addresses = new JsonArray("172.31.255.1/30");
        if (carriesIpv6)
        {
            addresses.Add("fdfe:dcba:9876::1/126");
        }

        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = TunnelInterfaceName,
            ["address"] = addresses,
            ["mtu"] = mtu,
            ["auto_route"] = true,
            ["strict_route"] = true
        };

        return new JsonArray(tun, LoopbackInbound(ProbeInboundTag, proxyPort));
    }

    private static JsonObject LoopbackInbound(string tag, int port) => new()
    {
        ["type"] = "mixed",
        ["tag"] = tag,
        ["listen"] = "127.0.0.1",
        ["listen_port"] = port
    };

    private static JsonArray BuildLeadingRules(AppSettings settings)
    {
        var rules = new JsonArray(new JsonObject { ["action"] = "sniff" });

        if (settings.DnsProtection)
        {
            rules.Add(new JsonObject
            {
                ["protocol"] = "dns",
                ["action"] = "hijack-dns"
            });
        }

        // The probe inbound exists to measure the tunnel, so it is pinned to the proxy
        // regardless of the routing policy the user picked.
        rules.Add(new JsonObject
        {
            ["inbound"] = ProbeInboundTag,
            ["action"] = "route",
            ["outbound"] = ProxyTag
        });

        return rules;
    }

    private static void AddPolicyRules(JsonArray rules, AppSettings settings, string[] executables)
    {
        if (settings.AllowLan)
        {
            // IP rules never match a destination that is still a name, so this only catches
            // literal addresses; the names that only mean something locally get their own rule.
            rules.Add(new JsonObject
            {
                ["ip_is_private"] = true,
                ["action"] = "route",
                ["outbound"] = DirectTag
            });
            rules.Add(new JsonObject
            {
                ["domain_suffix"] = TunnelParser.ToJsonArray(LocalNameSuffixes),
                ["action"] = "route",
                ["outbound"] = DirectTag
            });
        }

        // Domestic names leave before anything else can claim them, so they keep their own
        // route whatever the policy is and whether or not QUIC is being refused.
        if (settings.DirectDomesticSites)
        {
            rules.Add(new JsonObject
            {
                ["domain_suffix"] = TunnelParser.ToJsonArray(DomesticNameSuffixes),
                ["action"] = "route",
                ["outbound"] = DirectTag
            });
        }

        switch (settings.RouteMode)
        {
            case RouteMode.SelectedAppsOnly:
                // Refused before the rule that would route it, and only for the applications in
                // the policy: nothing outside the tunnel loses QUIC.
                if (settings.BlockQuic)
                {
                    rules.Add(QuicRejectRule(executables));
                }

                rules.Add(ProcessRule(executables, ProxyTag));
                break;

            case RouteMode.AllExceptSelected:
                // The excluded applications leave first, so they keep QUIC on the open connection.
                rules.Add(ProcessRule(executables, DirectTag));

                if (settings.BlockQuic)
                {
                    rules.Add(QuicRejectRule([]));
                }

                break;

            default:
                if (settings.BlockQuic)
                {
                    rules.Add(QuicRejectRule([]));
                }

                break;
        }
    }

    /// <summary>
    /// Refuses QUIC so a browser falls back to HTTP/2 over TCP.
    ///
    /// QUIC is UDP, and UDP through a proxy is a second-class citizen: every packet is wrapped,
    /// there is no congestion control shared with the tunnel underneath, and loss on the path to
    /// the server is not recovered the way a TCP stream's is. A browser that reaches YouTube over
    /// QUIC through a tunnel usually loads the page and then stalls on the video, because the
    /// media stream is the part that needs sustained throughput. Refused with an ICMP
    /// unreachable rather than dropped, so the browser gives up on QUIC immediately instead of
    /// waiting out a timeout on every connection.
    /// </summary>
    private static JsonObject QuicRejectRule(IEnumerable<string> executables)
    {
        var rule = new JsonObject
        {
            ["network"] = "udp",
            ["port"] = new JsonArray(QuicPorts.Select(port => (JsonNode)JsonValue.Create(port)).ToArray()),
            ["action"] = "reject",
            ["method"] = "default"
        };

        var paths = executables.ToArray();
        if (paths.Length > 0)
        {
            rule["process_path_regex"] = ExecutableRegexes(paths);
        }

        return rule;
    }

    private static JsonObject BuildExperimental(AppSettings settings, PortPlan ports)
    {
        var experimental = new JsonObject
        {
            ["clash_api"] = new JsonObject
            {
                ["external_controller"] = $"127.0.0.1:{ports.ControlPort}",
                ["secret"] = ports.ControlSecret
            }
        };

        if (settings.DnsProtection)
        {
            // Fake addresses an application cached before a restart must still map to their
            // names afterwards, so the mapping is persisted.
            experimental["cache_file"] = new JsonObject
            {
                ["enabled"] = true,
                ["path"] = AppPaths.CacheFile,
                ["store_fakeip"] = true
            };
        }

        return experimental;
    }

    /// <summary>
    /// Matches executables by regex rather than by literal path: Windows hands the same
    /// program back with different casing depending on where the path came from, and an
    /// exact match would silently drop those connections out of the policy.
    /// </summary>
    private static JsonObject ProcessRule(IEnumerable<string> executables, string outbound) => new()
    {
        ["process_path_regex"] = ExecutableRegexes(executables),
        ["action"] = "route",
        ["outbound"] = outbound
    };

    private static JsonArray ExecutableRegexes(IEnumerable<string> executables) =>
        TunnelParser.ToJsonArray(executables.Select(path => $"(?i)^{EscapeRegex(path)}$"));

    /// <summary>
    /// Escapes the metacharacters of RE2, the engine the core uses. The framework's own
    /// <c>Regex.Escape</c> also escapes spaces, which RE2 rejects as an unknown escape.
    /// </summary>
    internal static string EscapeRegex(string value)
    {
        const string Metacharacters = @"\.+*?()|[]{}^$";
        var builder = new StringBuilder(value.Length + 8);

        foreach (var character in value)
        {
            if (Metacharacters.Contains(character))
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string[] NormalizeExecutablePaths(IEnumerable<AppTarget> apps) => apps
        .Select(app => app.Path.Trim().Trim('"'))
        .Where(path => path.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Holds every listener open until all ports are known, so no two can be handed the same one.</summary>
    public static int[] ReserveLoopbackPorts(int count) => ReserveLoopbackPorts(new int?[count]);

    /// <summary>
    /// Like <see cref="ReserveLoopbackPorts(int)"/>, but each slot may ask for a particular port
    /// first. A preferred port that is taken, or out of range, falls back to any free one.
    /// </summary>
    public static int[] ReserveLoopbackPorts(IReadOnlyList<int?> preferred)
    {
        var listeners = new List<TcpListener>(preferred.Count);
        try
        {
            foreach (var wanted in preferred)
            {
                listeners.Add(Listen(wanted is > 0 and <= 65535 ? wanted.Value : 0) ?? Listen(0)!);
            }

            return listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    private static TcpListener? Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException) when (port != 0)
        {
            return null;
        }
    }
}
