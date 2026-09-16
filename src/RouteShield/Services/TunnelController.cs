using System.IO;
using System.Net.Http;
using System.Text;
using RouteShield.Tunnels;

namespace RouteShield.Services;

/// <summary>
/// Drives one tunnel from end to end: build the configuration, let the core check it, start it,
/// arm the leak guard, then measure the result. Failures leave the guard in place so nothing
/// escapes onto the physical adapter while a reconnect is in flight.
/// </summary>
public sealed class TunnelController : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    private const int MaxReconnectAttempts = 8;
    private const int ProbeAttempts = 3;
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(60);

    private readonly CoreProcessService _core = new();
    private readonly FirewallService _firewall = new();
    private readonly NetworkProbe _probe = new();
    private readonly TrafficMeter _traffic = new();
    private readonly SemaphoreSlim _transition = new(1, 1);

    private TunnelSession? _session;
    private CancellationTokenSource? _reconnect;
    private CancellationTokenSource? _probing;

    public TunnelController()
    {
        _core.OutputReceived += line => AppLog.Write(LogCategory.Core, line);
        _core.Exited += OnCoreExited;
        _traffic.SampleReceived += sample =>
        {
            ThroughputMegabytesPerSecond = sample.TotalMegabytesPerSecond;
            Changed?.Invoke();
        };
    }

    private sealed record TunnelSession(
        VpnProfile Profile,
        AppSettings Settings,
        IReadOnlyList<AppTarget> Apps,
        IReadOnlyList<VpnProfile> Pinned);

    public event Action? Changed;

    public TunnelState State { get; private set; } = TunnelState.Disconnected;

    public string StatusDetail { get; private set; } = "Traffic is on your local adapter";

    public string? ExitIp { get; private set; }

    public long? LatencyMilliseconds { get; private set; }

    public string? ProbeFailure { get; private set; }

    public double ThroughputMegabytesPerSecond { get; private set; }

    public DateTimeOffset? ConnectedAt { get; private set; }

    public string CoreVersion { get; private set; } = "sing-box";

    public int ReconnectAttempt { get; private set; }

    public bool KillSwitchArmed { get; private set; }

    /// <summary>The browser-facing proxies of the running core; empty while disconnected.</summary>
    public IReadOnlyList<BridgeBinding> Bridges { get; private set; } = [];

    public VpnProfile? ActiveProfile => _session?.Profile;

    public async Task LoadCoreVersionAsync()
    {
        try
        {
            CoreVersion = await _core.GetVersionAsync();
        }
        catch (Exception exception) when (exception is FileNotFoundException or TimeoutException or InvalidOperationException)
        {
            CoreVersion = "sing-box (not installed)";
        }

        Changed?.Invoke();
    }

    /// <summary>Builds the configuration and asks the core to check it, without starting anything.</summary>
    public async Task<string> ValidateAsync(VpnProfile profile, AppSettings settings, IReadOnlyList<AppTarget> apps)
    {
        var runtime = BuildRuntime(profile, settings, apps, []);
        var scratch = Path.Combine(AppPaths.Root, "validate.json");
        AppPaths.Ensure();
        await File.WriteAllTextAsync(scratch, runtime.Json, new UTF8Encoding(false));

        try
        {
            var (ok, output) = await _core.ValidateAsync(scratch);
            if (!ok)
            {
                throw new InvalidOperationException(FirstMeaningfulLine(output));
            }

            return string.IsNullOrWhiteSpace(output) ? "The configuration is valid." : output;
        }
        finally
        {
            try
            {
                File.Delete(scratch);
            }
            catch (IOException)
            {
            }
        }
    }

    public async Task ConnectAsync(
        VpnProfile profile,
        AppSettings settings,
        IReadOnlyList<AppTarget> apps,
        IReadOnlyList<VpnProfile> pinned)
    {
        await _transition.WaitAsync();
        try
        {
            CancelReconnect();
            _session = new TunnelSession(profile, settings, apps, pinned);
            ReconnectAttempt = 0;
            await StartAsync(_session);
        }
        catch (Exception exception)
        {
            // Auto-reconnect is for a tunnel that was up and dropped. An initial connect that
            // failed is reported to the user instead of retried behind their back.
            _session = null;
            CancelReconnect();
            await TearDownAsync(keepKillSwitch: false);
            Fail(exception.Message);
            throw;
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _transition.WaitAsync();
        try
        {
            CancelReconnect();
            _session = null;

            Report(TunnelState.Disconnecting, "Stopping the core and releasing the adapter");
            await TearDownAsync(keepKillSwitch: false);
            Report(TunnelState.Disconnected, "Traffic is on your local adapter");
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>Drops the firewall rules that outlive a failed tunnel, at the user's request.</summary>
    public async Task ReleaseKillSwitchAsync()
    {
        await _firewall.RemoveAsync();
        KillSwitchArmed = false;
        Changed?.Invoke();
    }

    public async Task StopReconnectingAsync()
    {
        CancelReconnect();
        _session = null;
        await TearDownAsync(keepKillSwitch: false);
        Report(TunnelState.Disconnected, "Traffic is on your local adapter");
    }

    private async Task StartAsync(TunnelSession session)
    {
        Report(TunnelState.Connecting, "Starting sing-box and attaching the TUN adapter");

        var runtime = BuildRuntime(session.Profile, session.Settings, session.Apps, BridgeRoutesFor(session));

        AppPaths.Ensure();
        await File.WriteAllTextAsync(AppPaths.RuntimeConfig, runtime.Json, new UTF8Encoding(false));

        var (ok, output) = await _core.ValidateAsync(AppPaths.RuntimeConfig);
        if (!ok)
        {
            throw new InvalidOperationException("sing-box rejected the configuration: " + FirstMeaningfulLine(output));
        }

        await _core.StartAsync(AppPaths.RuntimeConfig);

        if (session.Settings.AppKillSwitch && session.Settings.RouteMode == RouteMode.SelectedAppsOnly)
        {
            await _firewall.ApplyAsync(session.Apps.Select(app => app.Path));
            KillSwitchArmed = true;
        }

        ConnectedAt = DateTimeOffset.Now;
        ReconnectAttempt = 0;
        Bridges = runtime.Bridges;
        Report(TunnelState.Connected, DescribeRoute(session.Settings, session.Apps.Count));

        if (runtime.Bridges.Count > 0)
        {
            AppLog.Write(LogCategory.Network, $"Browser bridge: {runtime.Bridges.Count} route(s) on loopback");
        }

        _traffic.Start(runtime.ControlUri, runtime.ControlSecret);
        StartProbing(runtime);
    }

    /// <summary>
    /// The routes the browser extension can pick from: the tunnel's own profile, every pinned
    /// profile that parses, and a way out that uses no VPN at all.
    /// </summary>
    private static List<BridgeRoute> BridgeRoutesFor(TunnelSession session)
    {
        if (!session.Settings.BrowserBridgeEnabled)
        {
            return [];
        }

        var routes = new List<BridgeRoute> { BridgeRoute.Active(session.Profile) };

        foreach (var profile in session.Pinned.Where(candidate => candidate.Id != session.Profile.Id))
        {
            try
            {
                routes.Add(BridgeRoute.Profile(profile, TunnelParser.Parse(profile.ConfigText)));
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException)
            {
                AppLog.Write(LogCategory.Config, $"Pinned profile \"{profile.Name}\" left out of the browser bridge: {exception.Message}");
            }
        }

        routes.Add(BridgeRoute.Bypass());
        return routes;
    }

    private void StartProbing(RuntimeConfig runtime)
    {
        StopProbing();
        var cancellation = new CancellationTokenSource();
        _probing = cancellation;
        _ = ProbeLoopAsync(runtime, cancellation.Token);
    }

    private void StopProbing()
    {
        _probing?.Cancel();
        _probing?.Dispose();
        _probing = null;
    }

    /// <summary>
    /// Measures right after start — with a few retries, because a WireGuard handshake or a
    /// slow first TLS connection can outlast the first attempt — and then keeps the latency
    /// figure fresh for as long as the tunnel is up.
    /// </summary>
    private async Task ProbeLoopAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= ProbeAttempts; attempt++)
            {
                if (await MeasureOnceAsync(runtime, cancellationToken) || attempt == ProbeAttempts)
                {
                    break;
                }

                await Task.Delay(ProbeRetryDelay, cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ProbeInterval, cancellationToken);
                await MeasureOnceAsync(runtime, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> MeasureOnceAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _probe.RunAsync(runtime.ProxyUri, cancellationToken);
            ExitIp = result.ExitIp;
            LatencyMilliseconds = result.LatencyMs;
            ProbeFailure = null;
            AppLog.Write(LogCategory.Network, $"Probe ok — exit {result.ExitIp}, rtt {result.LatencyMs} ms");
            Changed?.Invoke();
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException
                                          && !cancellationToken.IsCancellationRequested)
        {
            ProbeFailure = exception.Message;
            AppLog.Write(LogCategory.Network, $"Probe failed: {exception.Message}");
            Changed?.Invoke();
            return false;
        }
    }

    private void OnCoreExited(int exitCode)
    {
        AppLog.Write(LogCategory.Core, $"The core exited unexpectedly (code {exitCode})");

        _traffic.Stop();
        StopProbing();
        ClearMeasurements();

        var session = _session;
        if (session is null || !session.Settings.AutoReconnect)
        {
            Fail($"The core stopped unexpectedly (exit code {exitCode})");
            return;
        }

        _reconnect = new CancellationTokenSource();
        _ = ReconnectAsync(session, _reconnect.Token);
    }

    private async Task ReconnectAsync(TunnelSession session, CancellationToken cancellationToken)
    {
        try
        {
            await RunReconnectLoopAsync(session, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The user disconnected, or a fresh connect took over.
        }
    }

    private async Task RunReconnectLoopAsync(TunnelSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && ReconnectAttempt < MaxReconnectAttempts)
        {
            ReconnectAttempt++;
            var wait = ReconnectBackoff[Math.Min(ReconnectAttempt - 1, ReconnectBackoff.Length - 1)];

            Report(
                TunnelState.Reconnecting,
                $"Attempt {ReconnectAttempt} of {MaxReconnectAttempts} · retrying in {wait.TotalSeconds:0}s" +
                (KillSwitchArmed ? " · kill switch still holding" : string.Empty));

            await Task.Delay(wait, cancellationToken);
            await _transition.WaitAsync(cancellationToken);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await StartAsync(session);
                return;
            }
            catch (Exception exception)
            {
                AppLog.Write(LogCategory.Core, $"Reconnect attempt {ReconnectAttempt} failed: {exception.Message}");
            }
            finally
            {
                _transition.Release();
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            Fail($"The tunnel could not be restored after {MaxReconnectAttempts} attempts");
        }
    }

    private void CancelReconnect()
    {
        _reconnect?.Cancel();
        _reconnect?.Dispose();
        _reconnect = null;
        ReconnectAttempt = 0;
    }

    private async Task TearDownAsync(bool keepKillSwitch)
    {
        _traffic.Stop();
        StopProbing();
        await _core.StopAsync();

        if (!keepKillSwitch && KillSwitchArmed)
        {
            try
            {
                await _firewall.RemoveAsync();
                KillSwitchArmed = false;
            }
            catch (InvalidOperationException exception)
            {
                AppLog.Write(LogCategory.Firewall, $"The leak guard could not be removed: {exception.Message}");
            }
        }

        ClearMeasurements();
        Bridges = [];
    }

    private void ClearMeasurements()
    {
        ConnectedAt = null;
        ExitIp = null;
        LatencyMilliseconds = null;
        ProbeFailure = null;
        ThroughputMegabytesPerSecond = 0;
    }

    private static RuntimeConfig BuildRuntime(
        VpnProfile profile,
        AppSettings settings,
        IReadOnlyList<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges)
    {
        if (string.IsNullOrWhiteSpace(profile.ConfigText))
        {
            throw new InvalidOperationException($"Profile \"{profile.Name}\" has no configuration body.");
        }

        var parsed = TunnelParser.Parse(profile.ConfigText);

        foreach (var warning in parsed.Warnings)
        {
            AppLog.Write(LogCategory.Config, warning);
        }

        var runtime = RuntimeConfigBuilder.Build(parsed, settings, apps, bridges);
        AppLog.Write(LogCategory.Config, $"Profile \"{profile.Name}\" built as {parsed.FormatName}");

        return runtime;
    }

    private static string DescribeRoute(AppSettings settings, int appCount) => settings.RouteMode switch
    {
        RouteMode.SelectedAppsOnly => $"Selected applications · {appCount} routed",
        RouteMode.AllExceptSelected => $"All except {appCount} application(s)",
        _ => "Full system tunnel"
    };

    private static string FirstMeaningfulLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var line = lines.LastOrDefault(candidate => candidate.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                                                    || candidate.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                   ?? lines.LastOrDefault();

        return string.IsNullOrWhiteSpace(line) ? "The core reported an unknown error." : StripAnsi(line);
    }

    private static string StripAnsi(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\x1B\[[0-9;]*m", string.Empty);

    private void Report(TunnelState state, string detail)
    {
        State = state;
        StatusDetail = detail;
        Changed?.Invoke();
    }

    private void Fail(string detail) => Report(TunnelState.Failed, detail);

    public async ValueTask DisposeAsync()
    {
        CancelReconnect();
        StopProbing();
        _traffic.Dispose();
        await _core.DisposeAsync();

        if (KillSwitchArmed)
        {
            try
            {
                await _firewall.RemoveAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        _transition.Dispose();
    }
}
