using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
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

    /// <summary>Adapters settle noisily — a VPN connecting fires several changes — so the rebind waits them out.</summary>
    private static readonly TimeSpan RebindSettleDelay = TimeSpan.FromSeconds(4);

    private readonly CoreProcessService _core = new();
    private readonly FirewallService _firewall = new();
    private readonly NetworkProbe _probe = new();
    private readonly TrafficMeter _traffic = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly HttpClient _control = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>The port each bridge route listened on last time, asked for again on the next start.</summary>
    private readonly Dictionary<(BridgeKind Kind, Guid? ProfileId), int> _lastBridgePorts = [];

    private TunnelSession? _session;
    private CancellationTokenSource? _reconnect;
    private CancellationTokenSource? _probing;
    private CancellationTokenSource? _rebind;
    private string? _boundInterface;

    public TunnelController()
    {
        _core.OutputReceived += line => AppLog.Write(LogCategory.Core, line);
        _core.Exited += OnCoreExited;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _traffic.SampleReceived += sample =>
        {
            ThroughputMegabytesPerSecond = sample.TotalMegabytesPerSecond;
            Changed?.Invoke();
        };
    }

    private sealed record TunnelSession(
        VpnProfile Profile,
        ConnectionTarget Target,
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

    /// <summary>The core's own loopback proxy while the tunnel is up, for work that has to go through it.</summary>
    public Uri? ProxyUri { get; private set; }

    /// <summary>The adapter the tunnel is leaving on, or null while it follows the system default route.</summary>
    public string? BoundInterface => _boundInterface;

    public VpnProfile? ActiveProfile => _session?.Profile;

    /// <summary>The node an automatic group is carrying traffic over right now; null for a single node.</summary>
    public string? GroupSelection { get; private set; }

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
    public async Task<string> ValidateAsync(ConnectionTarget target, AppSettings settings, IReadOnlyList<AppTarget> apps)
    {
        var runtime = BuildRuntime(target, settings, apps, []);
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
        ConnectionTarget target,
        AppSettings settings,
        IReadOnlyList<AppTarget> apps,
        IReadOnlyList<VpnProfile> pinned)
    {
        await _transition.WaitAsync();
        try
        {
            CancelReconnect();
            _session = new TunnelSession(profile, target, settings, apps, pinned);
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

        var runtime = BuildRuntime(session.Target, session.Settings, session.Apps, BridgeRoutesFor(session));

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
        ProxyUri = runtime.ProxyUri;
        GroupSelection = runtime.IsAutomatic ? "choosing…" : null;

        foreach (var binding in runtime.Bridges)
        {
            _lastBridgePorts[(binding.Kind, binding.ProfileId)] = binding.Port;
        }

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
        if (runtime.IsAutomatic)
        {
            await RefreshGroupSelectionAsync(runtime, cancellationToken);
        }

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

    /// <summary>Asks the Clash API which member the automatic group is using, so the dashboard can name it.</summary>
    private async Task RefreshGroupSelectionAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(runtime.ControlUri, $"proxies/{RuntimeConfigBuilder.ProxyTag}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", runtime.ControlSecret);

            using var response = await _control.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("now", out var now) && now.GetString() is { Length: > 0 } tag)
            {
                var name = runtime.MemberName(tag);
                if (name != GroupSelection)
                {
                    GroupSelection = name;
                    AppLog.Write(LogCategory.Network, $"Automatic selection is using \"{name}\"");
                    Changed?.Invoke();
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          && !cancellationToken.IsCancellationRequested)
        {
            // The selection is a courtesy on the dashboard; a missed read is not a tunnel problem.
        }
    }

    /// <summary>
    /// Another VPN connecting or dropping rewrites the routing table under us. The tunnel is
    /// rebuilt on the adapter that is right now, rather than left dialling through an interface
    /// that has stopped carrying traffic.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs args)
    {
        if (_session is null || State != TunnelState.Connected)
        {
            return;
        }

        _rebind?.Cancel();
        _rebind?.Dispose();

        var cancellation = new CancellationTokenSource();
        _rebind = cancellation;
        _ = RebindAsync(cancellation.Token);
    }

    private async Task RebindAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RebindSettleDelay, cancellationToken);

            var session = _session;
            if (session is null || State != TunnelState.Connected)
            {
                return;
            }

            var desired = NetworkAdapters.Resolve(session.Settings)?.InterfaceName;
            if (string.Equals(desired, _boundInterface, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await _transition.WaitAsync(cancellationToken);
            try
            {
                if (!ReferenceEquals(_session, session) || State != TunnelState.Connected)
                {
                    return;
                }

                AppLog.Write(
                    LogCategory.Network,
                    $"The network changed; moving the tunnel from \"{_boundInterface ?? "the system default"}\" to \"{desired ?? "the system default"}\".");

                Report(TunnelState.Connecting, "Moving the tunnel to the adapter that is up now");

                // The leak guard stays armed across the restart: nothing should slip out while
                // the core is down, least of all during a network change.
                await TearDownAsync(keepKillSwitch: true);
                await StartAsync(session);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                AppLog.Write(LogCategory.Network, $"The tunnel could not be moved: {exception.Message}");
                Fail(exception.Message);
            }
            finally
            {
                _transition.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A later change took over, or the tunnel went down while we waited.
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is InvalidOperationException or IOException or TimeoutException or HttpRequestException;

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
        ProxyUri = null;
    }

    private void ClearMeasurements()
    {
        ConnectedAt = null;
        ExitIp = null;
        LatencyMilliseconds = null;
        ProbeFailure = null;
        GroupSelection = null;
        ThroughputMegabytesPerSecond = 0;
    }

    /// <summary>
    /// Turns a profile into what the core connects to. A node profile is parsed as itself; an
    /// automatic profile gathers every node of its subscription that parses, so one broken
    /// link in a subscription costs one member, not the whole group.
    /// </summary>
    public static ConnectionTarget ResolveTarget(VpnProfile profile, IEnumerable<VpnProfile> library)
    {
        if (!profile.IsAutomatic)
        {
            if (string.IsNullOrWhiteSpace(profile.ConfigText))
            {
                throw new InvalidOperationException($"Profile \"{profile.Name}\" has no configuration body.");
            }

            return ConnectionTarget.Single(TunnelParser.Parse(profile.ConfigText));
        }

        var members = new List<(string Name, ParsedTunnel Tunnel)>();
        foreach (var candidate in library.Where(candidate => !candidate.IsAutomatic && candidate.SubscriptionId == profile.SubscriptionId))
        {
            try
            {
                members.Add((candidate.Name, TunnelParser.Parse(candidate.ConfigText)));
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidOperationException)
            {
                AppLog.Write(LogCategory.Config, $"\"{candidate.Name}\" left out of the automatic group: {exception.Message}");
            }
        }

        if (members.Count == 0)
        {
            throw new InvalidOperationException($"\"{profile.Name}\" has no usable node. Refresh the subscription first.");
        }

        return ConnectionTarget.Automatic(profile.Name, members);
    }

    private RuntimeConfig BuildRuntime(
        ConnectionTarget target,
        AppSettings settings,
        IReadOnlyList<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges)
    {
        foreach (var warning in target.Tunnels.SelectMany(tunnel => tunnel.Warnings))
        {
            AppLog.Write(LogCategory.Config, warning);
        }

        var preferred = bridges
            .Select(route => _lastBridgePorts.TryGetValue((route.Kind, route.ProfileId), out var port) ? port : (int?)null)
            .ToArray();

        var binding = NetworkAdapters.Resolve(settings);
        _boundInterface = binding?.InterfaceName;

        AppLog.Write(
            LogCategory.Network,
            binding is null
                ? "Leaving on whichever adapter holds the default route."
                : $"Leaving on \"{binding.InterfaceName}\"" +
                  (binding.DnsAddresses.Count > 0 ? $", resolving through {binding.DnsAddresses[0]}" : string.Empty));

        var runtime = RuntimeConfigBuilder.Build(
            target, settings, apps, bridges, PortPlan.Reserve(bridges.Count, preferred), binding);

        AppLog.Write(
            LogCategory.Config,
            target.IsAutomatic
                ? $"\"{target.Name}\" built as an automatic group of {target.Tunnels.Count} nodes"
                : $"Profile \"{target.Name}\" built as {target.Tunnels[0].FormatName}");

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
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _rebind?.Cancel();
        _rebind?.Dispose();
        CancelReconnect();
        StopProbing();
        _traffic.Dispose();
        _control.Dispose();
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
