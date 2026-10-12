using System.Diagnostics;
using System.IO;
using System.Text;

namespace RouteShield.Services;

/// <summary>
/// The application kill switch. While the tunnel is up, the routed executables are blocked from
/// reaching the internet over any physical adapter, so a core crash drops their traffic instead
/// of leaking it. The rules are scoped to one firewall group and torn down with the tunnel.
/// </summary>
public sealed class FirewallService
{
    public const string RuleGroup = "RouteShield Kill Switch";

    public async Task ApplyAsync(IEnumerable<string> executables)
    {
        var programs = executables
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (programs.Length == 0)
        {
            throw new InvalidOperationException("None of the selected applications could be found on disk.");
        }

        var literals = string.Join(",", programs.Select(path => "'" + path.Replace("'", "''") + "'"));

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $group = '{{RuleGroup}}'
            Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            $adapters = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' } | Select-Object -ExpandProperty Name)
            if ($adapters.Count -eq 0) { throw 'No active physical adapter was found.' }
            $programs = @({{literals}})
            $index = 0
            foreach ($program in $programs) {
                $index++
                New-NetFirewallRule -DisplayName "RouteShield Leak Guard $index" -Group $group -Direction Outbound `
                    -Action Block -Program $program -Profile Any -InterfaceAlias $adapters -RemoteAddress Internet `
                    -Enabled True | Out-Null
            }
            """;

        await RunPowerShellAsync(script);
        AppLog.Write(LogCategory.Firewall, $"Leak guard applied to {programs.Length} executable(s)");
    }

    public async Task RemoveAsync()
    {
        await RunPowerShellAsync(
            $"$ErrorActionPreference = 'Stop'; Get-NetFirewallRule -Group '{RuleGroup}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue");

        AppLog.Write(LogCategory.Firewall, "Leak guard removed");
    }

    public async Task<bool> HasRulesAsync()
    {
        var output = await RunPowerShellAsync(
            $"$rules = @(Get-NetFirewallRule -Group '{RuleGroup}' -ErrorAction SilentlyContinue); if ($rules.Count -gt 0) {{ 'yes' }} else {{ 'no' }}");

        return output.Contains("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RunPowerShellAsync(string script)
    {
        // No script file in LocalAppData: an elevated child must never execute code that a
        // non-elevated process can replace between writing it and PowerShell reading it.
        using var process = Process.Start(CreateStartInfo(script,
            Environment.GetFolderPath(Environment.SpecialFolder.System)))
            ?? throw new InvalidOperationException("PowerShell could not be started.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            throw new InvalidOperationException("Windows Firewall: PowerShell did not respond within 30 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows Firewall: " + (error + output).Trim());
        }

        return output;
    }

    internal static ProcessStartInfo CreateStartInfo(string script, string systemDirectory)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
