using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace RouteShield.Tunnels;

/// <summary>Turns user-supplied share links, WireGuard INI, or sing-box JSON into a single node.</summary>
public static class TunnelParser
{
    public const string ProxyTag = "proxy";

    /// <summary>Outbound types RouteShield can lift out of a pasted sing-box configuration.</summary>
    private static readonly HashSet<string> SupportedJsonOutbounds = new(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "vmess", "trojan", "shadowsocks", "socks", "http", "hysteria2", "tuic", "anytls"
    };

    public static ParsedTunnel Parse(string input)
    {
        var text = input.Trim().TrimStart('\uFEFF');
        if (text.Length == 0)
        {
            throw new FormatException("Configuration text is empty.");
        }

        if (text.StartsWith("[Interface]", StringComparison.OrdinalIgnoreCase))
        {
            return ParseWireGuard(text);
        }

        if (text.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseVless(text);
        }

        if (text.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseVmess(text);
        }

        if (text.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseTrojan(text);
        }

        if (text.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            return ParseShadowsocks(text);
        }

        if (text.StartsWith('{'))
        {
            return ParseSingBoxJson(text);
        }

        throw new NotSupportedException(
            "Unknown format. Paste a VLESS, VMess, Trojan or Shadowsocks link, a WireGuard .conf, or sing-box JSON.");
    }

    public static string DetectFormat(string input)
    {
        try
        {
            return Parse(input).FormatName;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException)
        {
            return "Unknown";
        }
    }

    private static ParsedTunnel ParseVless(string text)
    {
        var uri = ParseUri(text, "VLESS");
        var query = ParseQuery(uri.Query);

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = ProxyTag,
            ["server"] = uri.Host,
            ["server_port"] = ParsePort(uri),
            ["uuid"] = Uri.UnescapeDataString(uri.UserInfo)
        };

        SetIfPresent(outbound, "flow", Lookup(query, "flow"));
        ApplyTls(outbound, query, uri.Host, tlsByDefault: false);
        ApplyTransport(outbound, query);

        return FromOutbound("VLESS", FragmentOrDefault(uri, "VLESS"), outbound);
    }

    private static ParsedTunnel ParseTrojan(string text)
    {
        var uri = ParseUri(text, "Trojan");
        var query = ParseQuery(uri.Query);

        var outbound = new JsonObject
        {
            ["type"] = "trojan",
            ["tag"] = ProxyTag,
            ["server"] = uri.Host,
            ["server_port"] = ParsePort(uri),
            ["password"] = Uri.UnescapeDataString(uri.UserInfo)
        };

        ApplyTls(outbound, query, uri.Host, tlsByDefault: true);
        ApplyTransport(outbound, query);

        return FromOutbound("Trojan", FragmentOrDefault(uri, "Trojan"), outbound);
    }

    private static ParsedTunnel ParseVmess(string text)
    {
        var payload = text[8..].Split('#', 2)[0].Trim();
        var source = JsonNode.Parse(Encoding.UTF8.GetString(DecodeBase64(payload)))?.AsObject()
                     ?? throw new FormatException("The VMess payload is not a JSON object.");

        var server = ReadString(source, "add") ?? throw new FormatException("The VMess 'add' field is missing.");
        var port = ReadInt(source, "port") ?? throw new FormatException("The VMess port is invalid.");
        var id = ReadString(source, "id") ?? throw new FormatException("The VMess 'id' field is missing.");

        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = ProxyTag,
            ["server"] = server,
            ["server_port"] = port,
            ["uuid"] = id,
            ["security"] = ReadString(source, "scy") ?? "auto",
            ["alter_id"] = ReadInt(source, "aid") ?? 0
        };

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = ReadString(source, "net") ?? "tcp",
            ["host"] = ReadString(source, "host") ?? string.Empty,
            ["path"] = ReadString(source, "path") ?? string.Empty,
            ["security"] = ReadString(source, "tls") ?? string.Empty,
            ["sni"] = ReadString(source, "sni") ?? string.Empty,
            ["fp"] = ReadString(source, "fp") ?? string.Empty,
            ["alpn"] = ReadString(source, "alpn") ?? string.Empty,
            ["serviceName"] = ReadString(source, "path") ?? string.Empty
        };

        ApplyTls(outbound, query, server, tlsByDefault: false);
        ApplyTransport(outbound, query);

        return FromOutbound("VMess", ReadString(source, "ps") ?? "VMess", outbound);
    }

    private static ParsedTunnel ParseShadowsocks(string text)
    {
        var body = text[5..];

        var hashIndex = body.IndexOf('#');
        var name = hashIndex >= 0 ? Uri.UnescapeDataString(body[(hashIndex + 1)..]) : "Shadowsocks";
        if (hashIndex >= 0)
        {
            body = body[..hashIndex];
        }

        var queryIndex = body.IndexOf('?');
        if (queryIndex >= 0)
        {
            body = body[..queryIndex];
        }

        string credentials;
        string endpoint;
        var atIndex = body.LastIndexOf('@');

        if (atIndex >= 0)
        {
            credentials = Uri.UnescapeDataString(body[..atIndex]);
            if (!credentials.Contains(':'))
            {
                credentials = Encoding.UTF8.GetString(DecodeBase64(credentials));
            }

            endpoint = body[(atIndex + 1)..];
        }
        else
        {
            var decoded = Encoding.UTF8.GetString(DecodeBase64(body));
            atIndex = decoded.LastIndexOf('@');
            if (atIndex < 0)
            {
                throw new FormatException("The Shadowsocks link is not in method:password@host:port form.");
            }

            credentials = decoded[..atIndex];
            endpoint = decoded[(atIndex + 1)..];
        }

        var separator = credentials.IndexOf(':');
        if (separator <= 0)
        {
            throw new FormatException("The Shadowsocks method or password is missing.");
        }

        var (host, port) = SplitHostPort(endpoint);

        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["tag"] = ProxyTag,
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = credentials[..separator],
            ["password"] = credentials[(separator + 1)..]
        };

        return FromOutbound("Shadowsocks", name, outbound);
    }

    private static ParsedTunnel ParseWireGuard(string text)
    {
        var sections = ParseIni(text);

        var iface = sections.FirstOrDefault(section => section.Name.Equals("Interface", StringComparison.OrdinalIgnoreCase))
                    ?? throw new FormatException("The [Interface] section is missing.");

        var peers = sections.Where(section => section.Name.Equals("Peer", StringComparison.OrdinalIgnoreCase)).ToList();
        if (peers.Count == 0)
        {
            throw new FormatException("No [Peer] section was found.");
        }

        var peerNodes = new JsonArray();
        foreach (var peer in peers)
        {
            var (host, port) = SplitHostPort(Required(peer.Values, "Endpoint"));

            var node = new JsonObject
            {
                ["address"] = host,
                ["port"] = port,
                ["public_key"] = Required(peer.Values, "PublicKey"),
                ["allowed_ips"] = ToJsonArray(SplitCsv(Required(peer.Values, "AllowedIPs")))
            };

            SetIfPresent(node, "pre_shared_key", Optional(peer.Values, "PresharedKey"));
            if (int.TryParse(Optional(peer.Values, "PersistentKeepalive"), out var keepalive))
            {
                node["persistent_keepalive_interval"] = keepalive;
            }

            peerNodes.Add(node);
        }

        var endpoint = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = ProxyTag,
            ["system"] = false,
            ["name"] = "RouteShield-WG",
            ["address"] = ToJsonArray(SplitCsv(Required(iface.Values, "Address"))),
            ["private_key"] = Required(iface.Values, "PrivateKey"),
            ["peers"] = peerNodes
        };

        if (int.TryParse(Optional(iface.Values, "MTU"), out var mtu))
        {
            endpoint["mtu"] = mtu;
        }

        var parsed = new ParsedTunnel
        {
            FormatName = "WireGuard",
            DisplayName = "WireGuard",
            Endpoint = endpoint
        };

        var carriesDefaultRoute = peers.Any(peer =>
            SplitCsv(Optional(peer.Values, "AllowedIPs")).Any(range => range is "0.0.0.0/0" or "::/0"));

        if (!carriesDefaultRoute)
        {
            parsed.Warnings.Add("AllowedIPs has no default route, so only the listed destinations will be tunneled.");
        }

        return parsed;
    }

    private static ParsedTunnel ParseSingBoxJson(string text)
    {
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new FormatException("Invalid JSON.");

        var candidate = ExtractOutbound(root);
        if (candidate is not null)
        {
            candidate["tag"] = ProxyTag;
            return FromOutbound("sing-box JSON", ReadString(candidate, "server") ?? "sing-box", candidate);
        }

        if (root["endpoints"] is JsonArray endpoints)
        {
            var wireGuard = endpoints.OfType<JsonObject>()
                .FirstOrDefault(node => string.Equals(ReadString(node, "type"), "wireguard", StringComparison.OrdinalIgnoreCase));

            if (wireGuard is not null)
            {
                var endpoint = Clone(wireGuard);
                endpoint["tag"] = ProxyTag;
                endpoint["system"] = false;

                return new ParsedTunnel
                {
                    FormatName = "sing-box WireGuard",
                    DisplayName = "WireGuard",
                    Endpoint = endpoint
                };
            }
        }

        throw new NotSupportedException("No supported outbound or WireGuard endpoint was found in the JSON.");
    }

    private static JsonObject? ExtractOutbound(JsonObject root)
    {
        if (!string.IsNullOrWhiteSpace(ReadString(root, "type")))
        {
            return Clone(root);
        }

        if (root["outbounds"] is not JsonArray outbounds)
        {
            return null;
        }

        var match = outbounds.OfType<JsonObject>()
            .FirstOrDefault(node => SupportedJsonOutbounds.Contains(ReadString(node, "type") ?? string.Empty));

        return match is null ? null : Clone(match);
    }

    /// <summary>
    /// Builds the TLS block. Reality is refused by sing-box unless uTLS is also on, so a link that
    /// asks for Reality without a fingerprint gets the Chrome profile rather than a failed start.
    /// </summary>
    private static void ApplyTls(JsonObject outbound, Dictionary<string, string> query, string server, bool tlsByDefault)
    {
        var security = Lookup(query, "security");
        var usesReality = security.Equals("reality", StringComparison.OrdinalIgnoreCase);
        var enabled = tlsByDefault || usesReality || security.Equals("tls", StringComparison.OrdinalIgnoreCase);

        if (!enabled)
        {
            return;
        }

        var tls = new JsonObject { ["enabled"] = true };

        var serverName = FirstNonEmpty(Lookup(query, "sni"), Lookup(query, "peer"), Lookup(query, "host"), server);
        if (serverName.Length > 0)
        {
            tls["server_name"] = serverName.Split(',')[0];
        }

        if (IsTrue(Lookup(query, "allowInsecure")) || IsTrue(Lookup(query, "insecure")))
        {
            tls["insecure"] = true;
        }

        var alpn = SplitCsv(Lookup(query, "alpn"));
        if (alpn.Length > 0)
        {
            tls["alpn"] = ToJsonArray(alpn);
        }

        var fingerprint = Lookup(query, "fp");
        var wantsUtls = fingerprint.Length > 0 && !fingerprint.Equals("none", StringComparison.OrdinalIgnoreCase);

        if (wantsUtls || usesReality)
        {
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = wantsUtls ? fingerprint : "chrome"
            };
        }

        if (usesReality)
        {
            var publicKey = FirstNonEmpty(Lookup(query, "pbk"), Lookup(query, "publicKey"));
            if (publicKey.Length == 0)
            {
                throw new FormatException("The Reality public key (pbk) is missing.");
            }

            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = publicKey,
                ["short_id"] = Lookup(query, "sid")
            };
        }

        outbound["tls"] = tls;
    }

    private static void ApplyTransport(JsonObject outbound, Dictionary<string, string> query)
    {
        var type = FirstNonEmpty(Lookup(query, "type"), Lookup(query, "net"), "tcp").ToLowerInvariant();
        if (type is "tcp" or "none" or "raw")
        {
            return;
        }

        JsonObject transport;
        switch (type)
        {
            case "ws":
            case "websocket":
                transport = new JsonObject
                {
                    ["type"] = "ws",
                    ["path"] = FirstNonEmpty(Lookup(query, "path"), "/")
                };

                var wsHost = Lookup(query, "host");
                if (wsHost.Length > 0)
                {
                    transport["headers"] = new JsonObject { ["Host"] = wsHost.Split(',')[0] };
                }

                if (int.TryParse(Lookup(query, "ed"), out var earlyData) && earlyData > 0)
                {
                    transport["max_early_data"] = earlyData;
                    transport["early_data_header_name"] = "Sec-WebSocket-Protocol";
                }

                break;

            case "grpc":
                transport = new JsonObject
                {
                    ["type"] = "grpc",
                    ["service_name"] = FirstNonEmpty(Lookup(query, "serviceName"), Lookup(query, "service_name"))
                };
                break;

            case "h2":
            case "http":
                transport = new JsonObject
                {
                    ["type"] = "http",
                    ["path"] = FirstNonEmpty(Lookup(query, "path"), "/")
                };

                var httpHosts = SplitCsv(Lookup(query, "host"));
                if (httpHosts.Length > 0)
                {
                    transport["host"] = ToJsonArray(httpHosts);
                }

                break;

            case "httpupgrade":
                transport = new JsonObject
                {
                    ["type"] = "httpupgrade",
                    ["path"] = FirstNonEmpty(Lookup(query, "path"), "/"),
                    ["host"] = Lookup(query, "host")
                };
                break;

            case "quic":
                transport = new JsonObject { ["type"] = "quic" };
                break;

            case "kcp":
                throw new NotSupportedException("mKCP is not supported by the sing-box core.");

            default:
                throw new NotSupportedException($"Transport '{type}' is not supported.");
        }

        outbound["transport"] = transport;
    }

    private static ParsedTunnel FromOutbound(string format, string name, JsonObject outbound) => new()
    {
        FormatName = format,
        DisplayName = string.IsNullOrWhiteSpace(name) ? format : name,
        Outbound = outbound
    };

    private static Uri ParseUri(string text, string kind)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new FormatException($"The {kind} link is malformed.");
        }

        return uri;
    }

    private static int ParsePort(Uri uri) => uri.Port is > 0 and <= 65535
        ? uri.Port
        : throw new FormatException("The port is missing or out of range.");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            values[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return values;
    }

    internal static byte[] DecodeBase64(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - (normalized.Length % 4)) % 4);

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            throw new FormatException("The Base64 payload is invalid.");
        }
    }

    private static (string Host, int Port) SplitHostPort(string endpoint)
    {
        endpoint = endpoint.Trim();

        string host;
        string port;

        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']');
            if (close < 0 || close + 2 > endpoint.Length || endpoint[close + 1] != ':')
            {
                throw new FormatException("The IPv6 endpoint must be written as [address]:port.");
            }

            host = endpoint[1..close];
            port = endpoint[(close + 2)..];
        }
        else
        {
            var colon = endpoint.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new FormatException("The endpoint must be written as host:port.");
            }

            host = endpoint[..colon];
            port = endpoint[(colon + 1)..];
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) ||
            parsedPort is <= 0 or > 65535)
        {
            throw new FormatException("The endpoint port is out of range.");
        }

        return (host, parsedPort);
    }

    private sealed record IniSection(string Name, Dictionary<string, string> Values);

    private static List<IniSection> ParseIni(string text)
    {
        var sections = new List<IniSection>();
        IniSection? current = null;

        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new IniSection(line[1..^1].Trim(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                sections.Add(current);
                continue;
            }

            var separator = line.IndexOf('=');
            if (current is not null && separator > 0)
            {
                current.Values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return sections;
    }

    private static string Required(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"{key} is missing from the WireGuard configuration.");

    private static string Optional(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Lookup(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;

    private static string[] SplitCsv(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    internal static JsonObject Clone(JsonObject source) => JsonNode.Parse(source.ToJsonString())!.AsObject();

    private static void SetIfPresent(JsonObject node, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            node[key] = value;
        }
    }

    private static bool IsTrue(string value) =>
        value == "1" ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FragmentOrDefault(Uri uri, string fallback) =>
        string.IsNullOrWhiteSpace(uri.Fragment) ? fallback : Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));

    internal static string? ReadString(JsonObject node, string key)
    {
        if (node[key] is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString().Trim('"');
    }

    private static int? ReadInt(JsonObject node, string key)
    {
        if (node[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return int.TryParse(ReadString(node, key), out number) ? number : null;
    }
}
