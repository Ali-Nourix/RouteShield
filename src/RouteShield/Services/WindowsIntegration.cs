using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace RouteShield.Services;

/// <summary>
/// Registers a logon task rather than a Run key, because RouteShield needs the elevated token
/// that only Task Scheduler can grant without a UAC prompt on every sign-in.
/// </summary>
public sealed class StartupService
{
    private const string TaskName = "RouteShield AutoStart";

    public async Task SetEnabledAsync(bool enabled)
    {
        if (!enabled)
        {
            await RunAsync(ignoreFailure: true, "/Delete", "/TN", TaskName, "/F");
            return;
        }

        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The RouteShield executable path is unavailable.");

        await RunAsync(
            ignoreFailure: false,
            "/Create", "/TN", TaskName, "/TR", $"\"{executable}\" --autostart",
            "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
    }

    private static async Task RunAsync(bool ignoreFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Task Scheduler could not be started.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !ignoreFailure)
        {
            throw new InvalidOperationException(((await standardError) + (await standardOutput)).Trim());
        }
    }
}

public sealed record RunningProcessItem(string Name, string Path, int Pid, long WorkingSetBytes)
{
    public string MemoryText => WorkingSetBytes >= 1024 * 1024
        ? $"{WorkingSetBytes / (1024 * 1024)} MB"
        : $"{Math.Max(1, WorkingSetBytes / 1024)} KB";
}

public static class ProcessCatalog
{
    /// <summary>One entry per executable, so a browser with twenty child processes shows up once.</summary>
    public static IReadOnlyList<RunningProcessItem> GetRunning()
    {
        var byPath = new Dictionary<string, RunningProcessItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    var name = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = Path.GetFileNameWithoutExtension(path);
                    }

                    if (byPath.TryGetValue(path, out var existing))
                    {
                        byPath[path] = existing with { WorkingSetBytes = existing.WorkingSetBytes + process.WorkingSet64 };
                        continue;
                    }

                    byPath[path] = new RunningProcessItem(name, path, process.Id, process.WorkingSet64);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Protected and exited processes cannot be inspected; they are simply not offered.
                }
            }
        }

        return byPath.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static void RefreshStates(IEnumerable<AppTarget> apps)
    {
        var running = GetRunning().Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            app.IsRunning = running.Contains(app.Path);
        }
    }
}

public static class Elevation
{
    public static bool IsAdministrator
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Relaunches through the shell so Windows raises the consent prompt.</summary>
    public static bool TryRestartElevated()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
