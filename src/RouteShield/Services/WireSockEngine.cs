using System.Diagnostics;
using System.IO;
using System.Text;

namespace RouteShield.Services;

/// <summary>
/// Runs a WireGuard profile through WireSock instead of sing-box — the engine TunnlTo drives.
///
/// The difference that matters: sing-box captures traffic with a TUN adapter and routes, so it
/// competes for the routing table with any other VPN, and a corporate client in full-tunnel mode
/// always wins that. WireSock takes the chosen applications' packets at the network-driver level
/// and sends them on the network card directly, underneath the routing table, so the other VPN's
/// routes are never consulted. It carries WireGuard only, and AmneziaWG, which sing-box cannot.
///
/// WireSock is not shipped with RouteShield. It is found where its own installer — or TunnlTo's —
/// put it, and run in transparent mode: no virtual adapter, no routes.
/// </summary>
public sealed class WireSockEngine : IAsyncDisposable
{
    private const string ExecutableName = "wiresock-client.exe";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _configPath;
    private bool _stopRequested;

    public event Action<string>? OutputReceived;

    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The installed client, or null when WireSock is not on this machine.</summary>
    public static string? ExecutablePath => Candidates().FirstOrDefault(File.Exists);

    public static bool IsInstalled => ExecutablePath is not null;

    /// <summary>
    /// Another WireSock already holds the driver — TunnlTo, or WireSock's own service. Two
    /// instances filtering the same adapter fight over every packet, so a second is not started.
    /// </summary>
    public static bool AnotherInstanceIsRunning(int? ownProcessId = null) =>
        Process.GetProcessesByName("wiresock-client").Any(process => process.Id != ownProcessId);

    public async Task StartAsync(string configPath)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
            {
                return;
            }

            var executable = ExecutablePath
                             ?? throw new InvalidOperationException(
                                 "WireSock is not installed. Install WireSock Secure Connect (TunnlTo installs it too), or switch the engine to sing-box.");

            if (AnotherInstanceIsRunning())
            {
                throw new InvalidOperationException(
                    "WireSock is already running — probably TunnlTo, or WireSock's own service. Disconnect it first; two copies cannot share the driver.");
            }

            _process?.Dispose();
            _stopRequested = false;
            _configPath = configPath;

            // Transparent mode: no -lac, so no virtual adapter and no routes for another VPN to override.
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };

            foreach (var argument in new[] { "run", "-config", configPath, "-log-level", "info" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => Forward(args.Data);
            _process.ErrorDataReceived += (_, args) => Forward(args.Data);
            _process.Exited += OnExited;

            if (!_process.Start())
            {
                throw new InvalidOperationException("WireSock could not be started.");
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // A bad key or an unreachable driver shows as an immediate exit, not as an error code.
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"WireSock stopped immediately (exit code {_process.ExitCode}). The Diagnostics log shows what it reported.");
            }

            AppLog.Write(LogCategory.Core, $"WireSock started ({executable})");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _stopRequested = true;

            if (_process is { HasExited: false } running)
            {
                try
                {
                    running.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await running.WaitForExitAsync(timeout.Token);
                }
                catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    AppLog.Write(LogCategory.Core, $"WireSock did not stop cleanly: {exception.Message}");
                }

                AppLog.Write(LogCategory.Core, "WireSock stopped");
            }

            _process?.Dispose();
            _process = null;

            // The file holds the private key; it lives exactly as long as the tunnel does.
            if (_configPath is not null)
            {
                try
                {
                    File.Delete(_configPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                _configPath = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Where WireSock's installers put the client: its own product folders, and TunnlTo's.</summary>
    private static IEnumerable<string> Candidates()
    {
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in programFiles)
        {
            yield return Path.Combine(root, "WireSock Secure Connect", "bin", ExecutableName);
            yield return Path.Combine(root, "WireSock VPN Client", "bin", ExecutableName);
            yield return Path.Combine(root, "WireSock Secure Connect", ExecutableName);
            yield return Path.Combine(root, "WireSock VPN Client", ExecutableName);
            yield return Path.Combine(root, "TunnlTo", "bin", ExecutableName);
            yield return Path.Combine(root, "TunnlTo", ExecutableName);
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(folder, ExecutableName);
        }
    }

    private void OnExited(object? sender, EventArgs args)
    {
        var exitCode = 1;
        try
        {
            exitCode = _process?.ExitCode ?? 1;
        }
        catch (InvalidOperationException)
        {
        }

        if (!_stopRequested)
        {
            Exited?.Invoke(exitCode);
        }
    }

    private void Forward(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            OutputReceived?.Invoke(AppLog.Redact(line.Trim()));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
