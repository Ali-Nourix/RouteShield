using System.Diagnostics;
using System.IO;
using System.Text;

namespace RouteShield.Services;

/// <summary>
/// Owns the sing-box child process. The core is told to log to stdout rather than to a file
/// so its output can reach the Diagnostics view live; this class mirrors it to disk.
/// </summary>
public sealed class CoreProcessService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private bool _stopRequested;

    public event Action<string>? OutputReceived;

    public event Action<int>? Exited;

    public string CorePath => Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");

    public bool IsRunning => _process is { HasExited: false };

    public DateTimeOffset? StartedAt { get; private set; }

    public async Task<string> GetVersionAsync()
    {
        RequireCore();
        var result = await CaptureAsync(TimeSpan.FromSeconds(8), "version");

        var firstLine = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim();

        return string.IsNullOrWhiteSpace(firstLine) ? "sing-box" : firstLine;
    }

    public async Task<(bool Ok, string Output)> ValidateAsync(string configPath)
    {
        RequireCore();
        var result = await CaptureAsync(TimeSpan.FromSeconds(20), "check", "-c", configPath);
        return (result.ExitCode == 0, result.Output.Trim());
    }

    public async Task StartAsync(string configPath)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning)
            {
                return;
            }

            RequireCore();
            _process?.Dispose();
            _stopRequested = false;

            var startInfo = new ProcessStartInfo(CorePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppPaths.Root
            };

            foreach (var argument in new[] { "run", "-c", configPath })
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => Forward(args.Data);
            _process.ErrorDataReceived += (_, args) => Forward(args.Data);
            _process.Exited += OnExited;

            if (!_process.Start())
            {
                throw new InvalidOperationException("The sing-box core could not be started.");
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            StartedAt = DateTimeOffset.Now;

            // The core validates its adapter and dial settings as it comes up; a failure
            // here shows as an immediate exit rather than as a start error.
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"The core stopped immediately (exit code {_process.ExitCode}).");
            }

            AppLog.Write(LogCategory.Core, "sing-box started");
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
                    AppLog.Write(LogCategory.Core, $"The core did not stop cleanly: {exception.Message}");
                }
            }

            _process?.Dispose();
            _process = null;
            StartedAt = null;

            TryDeleteRuntimeConfig();
            AppLog.Write(LogCategory.Core, "sing-box stopped");
        }
        finally
        {
            _gate.Release();
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

        StartedAt = null;

        if (!_stopRequested)
        {
            Exited?.Invoke(exitCode);
        }
    }

    private void Forward(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var clean = AppLog.Redact(line.Trim());
        MirrorToCoreLog(clean);
        OutputReceived?.Invoke(clean);
    }

    private static void MirrorToCoreLog(string line)
    {
        try
        {
            AppPaths.Ensure();
            File.AppendAllText(AppPaths.CoreLog, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteRuntimeConfig()
    {
        try
        {
            if (File.Exists(AppPaths.RuntimeConfig))
            {
                File.Delete(AppPaths.RuntimeConfig);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RequireCore()
    {
        if (!File.Exists(CorePath))
        {
            throw new FileNotFoundException(
                "sing-box.exe is missing from the core folder. Reinstall RouteShield or run build.cmd.", CorePath);
        }
    }

    private async Task<(int ExitCode, string Output)> CaptureAsync(TimeSpan timeout, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(CorePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.Root
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("The sing-box core could not be launched.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("The sing-box core did not respond in time.");
        }

        var combined = string.Join(Environment.NewLine, new[] { await standardOutput, await standardError }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        return (process.ExitCode, AppLog.Redact(combined));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
