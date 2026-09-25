using System.Diagnostics;
using System.IO;
using System.Text;

namespace RouteShield.Services;

/// <summary>
/// Owns one sing-box child process. The core is told to log to stdout rather than to a file
/// so its output can reach the Diagnostics view live; this class mirrors it to disk.
/// The tunnel and the latency probe each hold their own instance.
/// </summary>
public sealed class CoreProcessService : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _label;
    private Process? _process;
    private string? _configPath;
    private bool _stopRequested;

    public CoreProcessService(string label = "sing-box")
    {
        _label = label;
    }

    public event Action<string>? OutputReceived;

    public event Action<int>? Exited;

    public static string CorePath => Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");

    public static bool IsCoreInstalled => File.Exists(CorePath);

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
            _configPath = configPath;

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
                throw new InvalidOperationException($"The {_label} core could not be started.");
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            StartedAt = DateTimeOffset.Now;

            // The core validates its adapter and dial settings as it comes up; a failure
            // here shows as an immediate exit rather than as a start error.
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"The {_label} core stopped immediately (exit code {_process.ExitCode}).");
            }

            AppLog.Write(LogCategory.Core, $"{_label} started");
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
                    AppLog.Write(LogCategory.Core, $"The {_label} core did not stop cleanly: {exception.Message}");
                }

                AppLog.Write(LogCategory.Core, $"{_label} stopped");
            }

            _process?.Dispose();
            _process = null;
            StartedAt = null;

            TryDelete(_configPath);
            _configPath = null;
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

        // This runs on the thread that drains the core's output pipe, and nothing on it may wait
        // for the disk: the core blocks on a full pipe, and every connection it is opening with it.
        var clean = AppLog.Redact(line.Trim());
        AppLog.AppendCoreLog(clean);
        OutputReceived?.Invoke(clean);
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RequireCore()
    {
        if (!IsCoreInstalled)
        {
            throw new FileNotFoundException(
                "sing-box.exe is missing from the core folder. Reinstall RouteShield or run build.cmd.", CorePath);
        }
    }

    private static async Task<(int ExitCode, string Output)> CaptureAsync(TimeSpan timeout, params string[] arguments)
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
