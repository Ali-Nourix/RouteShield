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
        AppPaths.Ensure();
        var scriptPath = Path.Combine(AppPaths.Root, "firewall-task.ps1");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false));

        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("PowerShell could not be started.");

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var output = await standardOutput;
            var error = await standardError;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Windows Firewall: " + (error + output).Trim());
            }

            return output;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
