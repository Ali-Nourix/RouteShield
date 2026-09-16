using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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

    /// <summary>Names that only ever mean something on the local network.</summary>
    private static readonly string[] LocalNameSuffixes =
        [".local", ".lan", ".home", ".internal", ".home.arpa", ".localdomain"];

    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = true };

    public static RuntimeConfig Build(ParsedTunnel parsed, AppSettings settings, IEnumerable<AppTarget> apps) =>
        Build(parsed, settings, apps, []);

    public static RuntimeConfig Build(
        ParsedTunnel parsed,
        AppSettings settings,
        IEnumerable<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges)
    {
        var ports = ReserveLoopbackPorts(2 + bridges.Count);
        var plan = new PortPlan(
            ports[0],
            ports[1],
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ports.Skip(2).ToArray());

        return Build(parsed, settings, apps, bridges, plan);
    }

    public static RuntimeConfig Build(
        ParsedTunnel parsed,
        AppSettings settings,
        IEnumerable<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges,
        PortPlan ports)
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
        Place(parsed, ProxyTag, outbounds, endpoints);

        var inbounds = BuildInbounds(settings, ports.ProxyPort);
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
            ["dns"] = BuildDns(settings),
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = new JsonObject { ["server"] = LocalResolverTag },
                ["rules"] = rules,
                ["final"] = settings.RouteMode == RouteMode.SelectedAppsOnly ? DirectTag : ProxyTag
            },
            ["experimental"] = BuildExperimental(settings, ports)
        };

        if (endpoints.Count > 0)
        {
            root["endpoints"] = endpoints;
        }

        return new RuntimeConfig(root.ToJsonString(WriteOptions), ports.ProxyPort, ports.ControlPort, ports.ControlSecret, bindings);
    }

    /// <summary>Adds a node under a tag, in the section its type belongs to; WireGuard is an endpoint, everything else an outbound.</summary>
    private static void Place(ParsedTunnel tunnel, string tag, JsonArray outbounds, JsonArray endpoints)
    {
        var node = TunnelParser.Clone(tunnel.Node);
        node["tag"] = tag;
        node["domain_resolver"] = LocalResolverTag;

        if (tunnel.Endpoint is not null)
        {
            endpoints.Add(node);
        }
        else
        {
            outbounds.Add(node);
        }
    }

    private static JsonObject BuildDns(AppSettings settings)
    {
        var servers = new JsonArray(new JsonObject
        {
            ["type"] = "local",
            ["tag"] = LocalResolverTag
        });

        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["strategy"] = settings.Ipv6Protection ? "prefer_ipv4" : "ipv4_only",
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

        if (settings.Ipv6Protection)
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

    private static JsonArray BuildInbounds(AppSettings settings, int proxyPort)
    {
        var addresses = new JsonArray("172.31.255.1/30");
        if (settings.Ipv6Protection)
        {
            addresses.Add("fdfe:dcba:9876::1/126");
        }

        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = TunnelInterfaceName,
            ["address"] = addresses,
            ["mtu"] = TunnelMtu,
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

        switch (settings.RouteMode)
        {
            case RouteMode.SelectedAppsOnly:
                rules.Add(ProcessRule(executables, ProxyTag));
                break;

            case RouteMode.AllExceptSelected:
                rules.Add(ProcessRule(executables, DirectTag));
                break;
        }
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
        ["process_path_regex"] = TunnelParser.ToJsonArray(executables.Select(path => $"(?i)^{EscapeRegex(path)}$")),
        ["action"] = "route",
        ["outbound"] = outbound
    };

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
    public static int[] ReserveLoopbackPorts(int count)
    {
        var listeners = new List<TcpListener>(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
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
}
