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
/// Two rules shape everything here. Every dial has to name a resolver — sing-box 1.14 refuses
/// to start otherwise — and the resolver that reaches the proxy server itself must live outside
/// the tunnel, or the tunnel can never come up. So <see cref="LocalResolverTag"/> answers on the
/// physical adapter and is the default for all dialing, while <see cref="TunnelResolverTag"/>
/// carries the queries of routed applications over DNS-over-HTTPS inside the tunnel.
/// </summary>
public static class RuntimeConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string LocalResolverTag = "dns-local";
    public const string TunnelResolverTag = "dns-tunnel";
    public const string TunnelInterfaceName = "RouteShield";

    private const string SecureDnsServer = "1.1.1.1";
    private const string SecureDnsServerName = "cloudflare-dns.com";
    private const int TunnelMtu = 9000;

    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = true };

    public static RuntimeConfig Build(ParsedTunnel parsed, AppSettings settings, IEnumerable<AppTarget> apps)
    {
        var proxyPort = ReserveLoopbackPort();
        var controlPort = ReserveLoopbackPort();
        var controlSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        return Build(parsed, settings, apps, proxyPort, controlPort, controlSecret);
    }

    public static RuntimeConfig Build(
        ParsedTunnel parsed,
        AppSettings settings,
        IEnumerable<AppTarget> apps,
        int proxyPort,
        int controlPort,
        string controlSecret)
    {
        var executables = NormalizeExecutablePaths(apps);
        if (settings.RouteMode != RouteMode.FullTunnel && executables.Length == 0)
        {
            throw new InvalidOperationException(
                "Pick at least one application, or switch to the full system tunnel.");
        }

        var node = TunnelParser.Clone(parsed.Node);
        node["tag"] = ProxyTag;
        node["domain_resolver"] = LocalResolverTag;

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = false
            },
            ["dns"] = BuildDns(settings, executables),
            ["inbounds"] = BuildInbounds(settings, proxyPort),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag }),
            ["route"] = BuildRoute(settings, executables),
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{controlPort}",
                    ["secret"] = controlSecret
                }
            }
        };

        if (parsed.Endpoint is not null)
        {
            root["endpoints"] = new JsonArray(node);
        }
        else
        {
            root["outbounds"]!.AsArray().Insert(0, node);
        }

        return new RuntimeConfig(root.ToJsonString(WriteOptions), proxyPort, controlPort, controlSecret);
    }

    private static JsonObject BuildDns(AppSettings settings, string[] executables)
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
            ["cache_capacity"] = 4096
        };

        if (!settings.DnsProtection)
        {
            dns["final"] = LocalResolverTag;
            return dns;
        }

        servers.Add(new JsonObject
        {
            ["type"] = "https",
            ["tag"] = TunnelResolverTag,
            ["server"] = SecureDnsServer,
            ["server_port"] = 443,
            ["path"] = "/dns-query",
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = SecureDnsServerName
            },
            ["detour"] = ProxyTag,
            ["domain_resolver"] = LocalResolverTag
        });

        // A query is answered by whichever resolver matches how its process is routed,
        // so a tunneled application never resolves on the physical adapter and vice versa.
        switch (settings.RouteMode)
        {
            case RouteMode.SelectedAppsOnly:
                dns["rules"] = new JsonArray(ProcessRule(executables, ("server", TunnelResolverTag)));
                dns["final"] = LocalResolverTag;
                break;

            case RouteMode.AllExceptSelected:
                dns["rules"] = new JsonArray(ProcessRule(executables, ("server", LocalResolverTag)));
                dns["final"] = TunnelResolverTag;
                break;

            default:
                dns["final"] = TunnelResolverTag;
                break;
        }

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

        var probe = new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "probe-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = proxyPort
        };

        return new JsonArray(tun, probe);
    }

    private static JsonObject BuildRoute(AppSettings settings, string[] executables)
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
            ["inbound"] = "probe-in",
            ["action"] = "route",
            ["outbound"] = ProxyTag
        });

        if (settings.AllowLan)
        {
            rules.Add(new JsonObject
            {
                ["ip_is_private"] = true,
                ["action"] = "route",
                ["outbound"] = DirectTag
            });
        }

        switch (settings.RouteMode)
        {
            case RouteMode.SelectedAppsOnly:
                rules.Add(ProcessRule(executables, ("action", "route"), ("outbound", ProxyTag)));
                break;

            case RouteMode.AllExceptSelected:
                rules.Add(ProcessRule(executables, ("action", "route"), ("outbound", DirectTag)));
                break;
        }

        return new JsonObject
        {
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = new JsonObject { ["server"] = LocalResolverTag },
            ["rules"] = rules,
            ["final"] = settings.RouteMode == RouteMode.SelectedAppsOnly ? DirectTag : ProxyTag
        };
    }

    /// <summary>
    /// Matches executables by regex rather than by literal path: Windows hands the same
    /// program back with different casing depending on where the path came from, and an
    /// exact match would silently drop those connections out of the policy.
    /// </summary>
    private static JsonObject ProcessRule(IEnumerable<string> executables, params (string Key, string Value)[] fields)
    {
        var rule = new JsonObject
        {
            ["process_path_regex"] = TunnelParser.ToJsonArray(
                executables.Select(path => $"(?i)^{EscapeRegex(path)}$"))
        };

        foreach (var (key, value) in fields)
        {
            rule[key] = value;
        }

        return rule;
    }

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

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
