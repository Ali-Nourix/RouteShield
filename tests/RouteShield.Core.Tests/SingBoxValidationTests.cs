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
    private static string? CorePath =>
        Environment.GetEnvironmentVariable("ROUTESHIELD_SINGBOX") is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;

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
                        foreach (var profile in new[] { Fixtures.VlessReality, Fixtures.VlessWebSocket, Fixtures.Trojan, Fixtures.Shadowsocks, Fixtures.WireGuardConf, Fixtures.SingBoxJson })
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
