using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteShield.Tunnels;

/// <summary>
/// A throwaway core that carries every saved profile as its own node and nothing else — no
/// TUN, no inbounds — so the Clash API can time a request through each one. Delay tests are
/// how every sing-box front end measures a node, and running them in one instance means one
/// process start for a whole library rather than one per profile.
/// </summary>
public static class LatencyProbeConfigBuilder
{
    public const string TestUrl = "https://www.gstatic.com/generate_204";

    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = true };

    public static string TagFor(Guid profileId) => "p-" + profileId.ToString("N");

    public static string Build(IReadOnlyList<(Guid Id, ParsedTunnel Tunnel)> candidates, int controlPort, string controlSecret)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one profile is needed.", nameof(candidates));
        }

        var outbounds = new JsonArray();
        var endpoints = new JsonArray();

        foreach (var (id, tunnel) in candidates)
        {
            var node = TunnelParser.Clone(tunnel.Node);
            node["tag"] = TagFor(id);
            node["domain_resolver"] = RuntimeConfigBuilder.LocalResolverTag;

            if (tunnel.Endpoint is not null)
            {
                endpoints.Add(node);
            }
            else
            {
                outbounds.Add(node);
            }
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = RuntimeConfigBuilder.DirectTag });

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = false },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = RuntimeConfigBuilder.LocalResolverTag }),
                ["final"] = RuntimeConfigBuilder.LocalResolverTag
            },
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = new JsonObject { ["server"] = RuntimeConfigBuilder.LocalResolverTag },
                ["final"] = RuntimeConfigBuilder.DirectTag
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{controlPort}",
                    ["secret"] = controlSecret
                }
            }
        };

        if (endpoints.Count > 0)
        {
            root["endpoints"] = endpoints;
        }

        return root.ToJsonString(WriteOptions);
    }
}
