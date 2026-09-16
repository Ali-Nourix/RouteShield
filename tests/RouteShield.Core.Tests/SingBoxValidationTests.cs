using System.Diagnostics;
using RouteShield.Tunnels;
using Xunit;

namespace RouteShield.Tests;

/// <summary>
/// Runs every generated configuration through the core itself. Set ROUTESHIELD_SINGBOX to the
/// sing-box executable to enable these; without it the suite reports the checks as skipped
/// rather than pretending the configuration was verified.
/// </summary>
public class SingBoxValidationTests
{
    /// <summary>
    /// Null when the variable is unset, so the checks report as skipped. A variable that points
    /// at nothing throws instead — a misconfigured pipeline must not look like a clean run.
    /// </summary>
    private static string? CorePath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ROUTESHIELD_SINGBOX") is not { Length: > 0 } path)
            {
                return null;
            }

            return File.Exists(path)
                ? path
                : throw new FileNotFoundException($"ROUTESHIELD_SINGBOX points at {path}, which does not exist.", path);
        }
    }

    public static TheoryData<RouteMode, bool, bool, bool, string> Policies()
    {
        var data = new TheoryData<RouteMode, bool, bool, bool, string>();

        foreach (var mode in Enum.GetValues<RouteMode>())
        {
            foreach (var dns in new[] { true, false })
            {
                foreach (var ipv6 in new[] { true, false })
                {
                    foreach (var lan in new[] { true, false })
                    {
                        foreach (var profile in new[]
                                 {
                                     Fixtures.VlessReality, Fixtures.VlessWebSocket, Fixtures.VlessGrpc, Fixtures.Trojan,
                                     Fixtures.Shadowsocks, Fixtures.WireGuardConf, Fixtures.SingBoxJson,
                                     Fixtures.Hysteria2, Fixtures.Tuic, Fixtures.AnyTls
                                 })
                        {
                            data.Add(mode, dns, ipv6, lan, profile);
                        }
                    }
                }
            }
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Policies))]
    public void Generated_configurations_are_accepted_by_the_core(
        RouteMode mode, bool dnsProtection, bool ipv6, bool allowLan, string profile)
    {
        var core = CorePath;
        Skip.If(core is null, "Set ROUTESHIELD_SINGBOX to a sing-box executable to run core validation.");

        var settings = Fixtures.Settings(mode, dnsProtection, ipv6, allowLan);
        var runtime = RuntimeConfigBuilder.Build(TunnelParser.Parse(profile), settings, Fixtures.Apps);

        var configPath = Path.Combine(Path.GetTempPath(), $"routeshield-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, runtime.Json);

        try
        {
            var (exitCode, output) = Run(core!, "check", "-c", configPath);

            Assert.True(exitCode == 0, $"sing-box rejected the configuration:\n{output}\n\n{runtime.Json}");
            Assert.DoesNotContain("deprecated", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [SkippableFact]
    public void A_bridged_configuration_is_accepted_by_the_core()
    {
        var core = CorePath;
        Skip.If(core is null, "Set ROUTESHIELD_SINGBOX to a sing-box executable to run core validation.");

        BridgeRoute[] bridges =
        [
            BridgeRoute.Active(new VpnProfile { Name = "Frankfurt" }),
            BridgeRoute.Profile(new VpnProfile { Name = "Tokyo" }, TunnelParser.Parse(Fixtures.Trojan)),
            BridgeRoute.Profile(new VpnProfile { Name = "Home" }, TunnelParser.Parse(Fixtures.WireGuardConf)),
            BridgeRoute.Bypass()
        ];

        var runtime = RuntimeConfigBuilder.Build(
            TunnelParser.Parse(Fixtures.VlessReality),
            Fixtures.Settings(allowLan: true),
            Fixtures.Apps,
            bridges,
            new PortPlan(21080, 29090, "s3cret", [23000, 23001, 23002, 23003]));

        AssertAccepted(core!, runtime.Json);
    }

    [SkippableFact]
    public void An_automatic_group_is_accepted_by_the_core()
    {
        var core = CorePath;
        Skip.If(core is null, "Set ROUTESHIELD_SINGBOX to a sing-box executable to run core validation.");

        var runtime = RuntimeConfigBuilder.Build(
            Fixtures.AutomaticGroup(),
            Fixtures.Settings(),
            Fixtures.Apps,
            [BridgeRoute.Active(new VpnProfile { Name = "Auto" }), BridgeRoute.Bypass()],
            new PortPlan(21080, 29090, "s3cret", [23000, 23001]));

        AssertAccepted(core!, runtime.Json);
    }

    [SkippableTheory]
    [InlineData(Fixtures.VlessReality)]
    [InlineData(Fixtures.VlessWebSocket)]
    [InlineData(Fixtures.Trojan)]
    [InlineData(Fixtures.AnyTls)]
    [InlineData(Fixtures.Hysteria2)]
    public void Tls_fragmenting_is_accepted_by_the_core(string profile)
    {
        var core = CorePath;
        Skip.If(core is null, "Set ROUTESHIELD_SINGBOX to a sing-box executable to run core validation.");

        var runtime = RuntimeConfigBuilder.Build(
            TunnelParser.Parse(profile), Fixtures.Settings(tlsFragment: true), Fixtures.Apps, [], new PortPlan(21080, 29090, "s3cret", []));

        AssertAccepted(core!, runtime.Json);
    }

    [SkippableFact]
    public void The_latency_probe_configuration_is_accepted_by_the_core()
    {
        var core = CorePath;
        Skip.If(core is null, "Set ROUTESHIELD_SINGBOX to a sing-box executable to run core validation.");

        var json = LatencyProbeConfigBuilder.Build(
            [
                (Guid.NewGuid(), TunnelParser.Parse(Fixtures.VlessReality)),
                (Guid.NewGuid(), TunnelParser.Parse(Fixtures.VlessWebSocket)),
                (Guid.NewGuid(), TunnelParser.Parse(Fixtures.Shadowsocks)),
                (Guid.NewGuid(), TunnelParser.Parse(Fixtures.WireGuardConf))
            ],
            29191,
            "probe");

        AssertAccepted(core!, json);
    }

    private static void AssertAccepted(string core, string json)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"routeshield-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, json);

        try
        {
            var (exitCode, output) = Run(core, "check", "-c", configPath);

            Assert.True(exitCode == 0, $"sing-box rejected the configuration:\n{output}\n\n{json}");
            Assert.DoesNotContain("deprecated", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [SkippableFact]
    public void A_config_without_a_default_resolver_is_still_rejected_by_the_core()
    {
        var core = CorePath;
        Skip.If(core is null, "Set ROUTESHIELD_SINGBOX to a sing-box executable to run core validation.");

        var runtime = RuntimeConfigBuilder.Build(
            TunnelParser.Parse(Fixtures.VlessReality), Fixtures.Settings(), Fixtures.Apps);

        // Strips exactly what the 1.14 core complains about, to prove the guard is what keeps us green.
        var broken = System.Text.Json.Nodes.JsonNode.Parse(runtime.Json)!.AsObject();
        broken["route"]!.AsObject().Remove("default_domain_resolver");
        foreach (var outbound in broken["outbounds"]!.AsArray())
        {
            outbound!.AsObject().Remove("domain_resolver");
        }

        var configPath = Path.Combine(Path.GetTempPath(), $"routeshield-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, broken.ToJsonString());

        try
        {
            var (exitCode, output) = Run(core!, "check", "-c", configPath);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("default_domain_resolver", output);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return (process.ExitCode, output);
    }
}
