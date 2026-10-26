using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouteShield.Tunnels;

namespace RouteShield.Subscriptions;

public sealed record FreeConfigBatch(IReadOnlyList<ImportedNode> Nodes, IReadOnlyList<string> SourceErrors);

/// <summary>Opt-in public share links only; never imports a repository's full runtime JSON.</summary>
public sealed class FreeConfigService
{
    public const int MaxCandidates = 100;
    public static IReadOnlyList<string> Sources { get; } = Array.AsReadOnly(new[]
    {
        "https://raw.githubusercontent.com/Epodonios/v2ray-configs/main/All_Configs_Sub.txt",
        "https://raw.githubusercontent.com/mahdibland/V2RayAggregator/master/sub/sub_merge.txt"
    });

    public static readonly Guid PoolId = new("b3eaf076-6ae7-4c80-b3ee-1fafcbf063ba");
    private readonly Func<string, Uri?, CancellationToken, Task<SubscriptionFetch>> _fetch;

    public FreeConfigService(SubscriptionService subscriptions) : this(subscriptions.FetchAsync) { }

    internal FreeConfigService(Func<string, Uri?, CancellationToken, Task<SubscriptionFetch>> fetch) => _fetch = fetch;

    public async Task<FreeConfigBatch> FetchAsync(Uri? proxy, CancellationToken cancellationToken)
    {
        var nodes = new List<ImportedNode>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _fetch(source, proxy, cancellationToken);
                var accepted = 0;
                // Sample each feed instead of always favouring its first server/country.
                foreach (var entry in response.Entries.OrderBy(_ => Random.Shared.Next()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (accepted >= MaxCandidates / Sources.Count) break;
                    var node = ReadPublicNode(entry);
                    if (node is null) continue;
                    var normalized = TunnelParser.Clone(TunnelParser.Parse(node.Config).Node);
                    normalized.Remove("tag");
                    if (!seen.Add(normalized.ToJsonString())) continue;
                    nodes.Add(node);
                    accepted++;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException or FormatException
                or InvalidOperationException or ArgumentException or JsonException)
            {
                // Name the public source, not a response body or node credentials.
                errors.Add($"{new Uri(source).AbsolutePath}: {exception.GetType().Name}");
            }
        }
        return new FreeConfigBatch(nodes, errors);
    }

    internal static ImportedNode? ReadPublicNode(SubscriptionEntry entry)
    {
        var config = entry.Config.Trim();
        // TunnelParser's JSON/WireGuard support is intentionally not available to untrusted feeds.
        if (!new[] { "vless://", "vmess://", "trojan://", "ss://", "hy2://", "hysteria2://", "tuic://", "anytls://" }
            .Any(scheme => config.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            var tunnel = TunnelParser.Parse(config);
            if (NodeInspector.IsPlaceholder(tunnel) || tunnel.Outbound is not { } outbound) return null;
            if (outbound["tls"] is JsonObject tls && tls["insecure"]?.GetValue<bool>() == true) return null;
            var host = outbound["server"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return null;
            if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublic(ip)) return null;
            return new ImportedNode(entry.Name, tunnel.FormatName, config);
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException
            or InvalidOperationException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xe0) == 0x20; // global unicast only
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    public static Guid? Fastest(IEnumerable<(Guid Id, int? Delay)> results) => results
        .Where(result => result.Delay is >= 0)
        .OrderBy(result => result.Delay).ThenBy(result => result.Id)
        .Select(result => (Guid?)result.Id).FirstOrDefault();
}
