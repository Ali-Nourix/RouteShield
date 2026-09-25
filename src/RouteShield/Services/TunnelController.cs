using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
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

    /// <summary>How many members of an automatic group are tested at once, and how long each may take.</summary>
    private const int GroupCheckParallelism = 8;
    private static readonly TimeSpan GroupMemberTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A failing probe tests the whole group again, but not more often than this.</summary>
    private static readonly TimeSpan GroupRecheckInterval = TimeSpan.FromMinutes(2);

    /// <summary>How often the node an automatic group is using is tested on its own.</summary>
    private static readonly TimeSpan SelectionCheckInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// When the chosen node fails and no other member has a recent pass, every member is tested,
    /// first after this long and then at doubling gaps while none answers, up to the ceiling.
    /// </summary>
    private static readonly TimeSpan GroupRetestFloor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GroupRetestCeiling = TimeSpan.FromMinutes(5);

    /// <summary>How long connections on a node that failed its test are watched for traffic before they are closed.</summary>
    private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(2);

    private readonly CoreProcessService _core = new();
    private readonly WireSockEngine _wireSock = new();
    private readonly FirewallService _firewall = new();
    private readonly NetworkProbe _probe = new();
    private readonly TrafficMeter _traffic = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly HttpClient _control = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>For the group tests, which set their own deadline per member.</summary>
    private readonly HttpClient _groupControl = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The port each bridge route listened on last time, asked for again on the next start.</summary>
    private readonly Dictionary<(BridgeKind Kind, Guid? ProfileId), int> _lastBridgePorts = [];

    private TunnelSession? _session;
    private CancellationTokenSource? _reconnect;
    private CancellationTokenSource? _probing;
    private CancellationTokenSource? _rebind;
    private string? _boundInterface;
    private DateTimeOffset _lastGroupCheck;
    private DateTimeOffset? _lastProbeSuccess;
    private TimeSpan _groupRetestGap = GroupRetestFloor;
    private readonly SemaphoreSlim _groupCheckGate = new(1, 1);

    public TunnelController()
    {
        _core.OutputReceived += line => AppLog.Write(LogCategory.Core, line, persist: false);
        _core.Exited += OnCoreExited;
        _wireSock.OutputReceived += line => AppLog.Write(LogCategory.Core, $"[wiresock] {line}");
        _wireSock.Exited += OnCoreExited;
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

    /// <summary>Why the adapter is not the one that was asked for; null when the choice held.</summary>
    public string? BindingWarning { get; private set; }

    /// <summary>The program carrying the tunnel right now: "sing-box" or "WireSock".</summary>
    public string? ActiveEngine { get; private set; }

    /// <summary>Why the engine is the one it is, when that was not the obvious choice.</summary>
    public string? EngineNote { get; private set; }

    public VpnProfile? ActiveProfile => _session?.Profile;

    /// <summary>The node an automatic group is carrying traffic over right now; null for a single node.</summary>
    public string? GroupSelection { get; private set; }

    /// <summary>The last test of every member of the automatic group; null for a single node or before the first.</summary>
    public GroupHealth? GroupHealth { get; private set; }

    /// <summary>Set when no member of the automatic group answered, with what can be done about it.</summary>
    public string? GroupWarning { get; private set; }

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
        var runtime = BuildRuntime(target, settings, apps, [], NetworkAdapters.Resolve(settings));
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

    private sealed record EngineDecision(bool UseWireSock, string? Note, bool IsWarning);

    /// <summary>
    /// Picks the program for this session. sing-box carries everything and is the default;
    /// WireSock is chosen for a WireGuard profile when it is the only thing that can work — a
    /// corporate VPN holding the default route, which WireSock works underneath — or when the
    /// profile is AmneziaWG, which sing-box cannot speak.
    /// </summary>
    private static EngineDecision ChooseEngine(TunnelSession session)
    {
        var settings = session.Settings;
        var tunnel = session.Target.IsAutomatic ? null : session.Target.Tunnels[0];
        var isWireGuardConf = tunnel?.WireGuardConf is not null;

        if (settings.Engine == TunnelEngine.SingBox)
        {
            return tunnel?.IsAmneziaWg == true
                ? new EngineDecision(false, "This AmneziaWG profile runs as plain WireGuard on sing-box, which a DPI filter may block. The WireSock engine speaks it.", true)
                : new EngineDecision(false, null, false);
        }

        if (!isWireGuardConf)
        {
            return settings.Engine == TunnelEngine.WireSock
                ? new EngineDecision(false, "WireSock carries WireGuard profiles only, so this profile runs on sing-box.", true)
                : new EngineDecision(false, null, false);
        }

        var otherVpn = NetworkAdapters.OtherVpnHoldingDefaultRoute();
        var wanted = settings.Engine == TunnelEngine.WireSock || tunnel!.IsAmneziaWg || otherVpn is not null;

        if (!WireSockEngine.IsInstalled)
        {
            return wanted
                ? new EngineDecision(
                    false,
                    (otherVpn is null ? string.Empty : $"\"{otherVpn.Name}\" ({otherVpn.Description}) holds the default route. ") +
                    "WireSock would carry this WireGuard profile underneath it, but it is not installed — install WireSock Secure Connect (TunnlTo installs it too).",
                    true)
                : new EngineDecision(false, null, false);
        }

        if (settings.Engine == TunnelEngine.WireSock)
        {
            return new EngineDecision(true, null, false);
        }

        if (tunnel!.IsAmneziaWg)
        {
            return new EngineDecision(true, "AmneziaWG profile — running on WireSock, which speaks its obfuscation.", false);
        }

        return otherVpn is not null
            ? new EngineDecision(true, $"\"{otherVpn.Name}\" ({otherVpn.Description}) holds the default route, so this profile runs on WireSock, underneath it.", false)
            : new EngineDecision(false, null, false);
    }

    private async Task StartAsync(TunnelSession session)
    {
        var engine = ChooseEngine(session);
        EngineNote = engine.IsWarning ? null : engine.Note;
        BindingWarning = engine.IsWarning ? engine.Note : null;

        if (engine.UseWireSock)
        {
            await StartWireSockAsync(session);
            return;
        }

        Report(TunnelState.Connecting, "Starting sing-box and attaching the TUN adapter");

        // Verified before the core starts: an adapter the operating system cannot route on
        // would fail every dial, and the reason would only show up as a timeout much later.
        var choice = await NetworkAdapters.ResolveAsync(session.Settings);
        BindingWarning = string.Join(" ", new[] { BindingWarning, choice.Warning }.Where(text => text is not null)) is { Length: > 0 } joined
            ? joined
            : null;

        var runtime = BuildRuntime(
            session.Target, session.Settings, session.Apps, BridgeRoutesFor(session), choice.Binding,
            NetworkAdapters.CarryingMtu(choice.Binding));

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
        GroupHealth = null;
        GroupWarning = null;

        foreach (var bridge in runtime.Bridges)
        {
            _lastBridgePorts[(bridge.Kind, bridge.ProfileId)] = bridge.Port;
        }

        Report(TunnelState.Connected, DescribeRoute(session.Settings, session.Apps.Count));

        if (runtime.Bridges.Count > 0)
        {
            AppLog.Write(LogCategory.Network, $"Browser bridge: {runtime.Bridges.Count} route(s) on loopback");
        }

        ActiveEngine = "sing-box";
        _traffic.Start(runtime.ControlUri, runtime.ControlSecret);
        StartProbing(runtime.ProxyUri, runtime);
    }

    /// <summary>
    /// Hands a WireGuard profile to WireSock. Nothing of sing-box runs: no TUN adapter, no
    /// routes, so no browser bridge and no secure DNS either — WireSock resolves with the
    /// profile's own DNS line. The firewall kill switch stays off, because its rules would stop
    /// the applications before their packets ever reached WireSock underneath.
    /// </summary>
    private async Task StartWireSockAsync(TunnelSession session)
    {
        Report(TunnelState.Connecting, "Starting WireSock underneath the routing table");

        var tunnel = session.Target.Tunnels[0];
        var conf = WireSockConfig.Build(tunnel.WireGuardConf!, session.Settings, session.Apps, Environment.ProcessPath);

        AppPaths.Ensure();
        await File.WriteAllTextAsync(AppPaths.WireSockConfig, conf, new UTF8Encoding(false));
        await _wireSock.StartAsync(AppPaths.WireSockConfig);

        // Rules left armed by a sing-box run this session moved away from would now block the
        // very applications WireSock is about to carry.
        if (KillSwitchArmed)
        {
            await _firewall.RemoveAsync();
            KillSwitchArmed = false;
        }

        if (session.Settings.AppKillSwitch && session.Settings.RouteMode == RouteMode.SelectedAppsOnly)
        {
            AppLog.Write(
                LogCategory.Firewall,
                "The kill switch is not armed under WireSock: its firewall rules would block the applications before WireSock could carry them.");
        }

        _boundInterface = null;
        ConnectedAt = DateTimeOffset.Now;
        ReconnectAttempt = 0;
        Bridges = [];
        ProxyUri = null;
        GroupSelection = null;
        ActiveEngine = "WireSock";

        AppLog.Write(LogCategory.Network, EngineNote ?? "Running on WireSock.");
        Report(TunnelState.Connected, DescribeRoute(session.Settings, session.Apps.Count) + " · WireSock");

        // RouteShield itself is one of the tunnelled applications, so the probe needs no proxy.
        StartProbing(null, null);
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

    /// <param name="proxy">Where the probe sends its request; null when RouteShield's own traffic is already tunnelled.</param>
    /// <param name="runtime">The sing-box run, when there is one, for reading an automatic group's choice.</param>
    private void StartProbing(Uri? proxy, RuntimeConfig? runtime)
    {
        StopProbing();
        var cancellation = new CancellationTokenSource();
        _probing = cancellation;
        _groupRetestGap = GroupRetestFloor;
        _ = ProbeLoopAsync(proxy, runtime, cancellation.Token);

        if (runtime is { IsAutomatic: true })
        {
            _ = WatchSelectionAsync(runtime, cancellation.Token);
        }
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
    private async Task ProbeLoopAsync(Uri? proxy, RuntimeConfig? runtime, CancellationToken cancellationToken)
    {
        try
        {
            // An automatic group is tested member by member alongside the first probe, not
            // before it. Every answer moves the group onto the fastest node there and then; the
            // core's own round only decides once its slowest member has timed out.
            var automatic = runtime is { IsAutomatic: true } ? runtime : null;
            var groupCheck = automatic is null ? Task.CompletedTask : CheckGroupAsync(automatic, cancellationToken);

            var healthy = false;
            for (var attempt = 1; attempt <= ProbeAttempts && !healthy; attempt++)
            {
                healthy = await MeasureOnceAsync(proxy, runtime, cancellationToken);
                if (!healthy && attempt < ProbeAttempts)
                {
                    await Task.Delay(ProbeRetryDelay, cancellationToken);
                }
            }

            await groupCheck;
            if (!healthy && GroupHealth is { Answering: > 0 })
            {
                // The probe went out over a member the group has since moved off.
                await MeasureOnceAsync(proxy, runtime, cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ProbeInterval, cancellationToken);
                if (await MeasureOnceAsync(proxy, runtime, cancellationToken) || automatic is null
                    || DateTimeOffset.Now - _lastGroupCheck < GroupRecheckInterval)
                {
                    continue;
                }

                await CheckGroupAsync(automatic, cancellationToken);
                if (GroupHealth is { Answering: > 0 })
                {
                    await MeasureOnceAsync(proxy, runtime, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Tests every member of the automatic group through the core's Clash API. A member that
    /// answers is stored with its delay and makes the group choose again, so the group is on
    /// the fastest node as soon as that answer is in. When none answers, the dashboard says so,
    /// and says what is most likely in the way.
    /// </summary>
    private async Task CheckGroupAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        // A test already running answers for every caller that arrives while it runs.
        if (!await _groupCheckGate.WaitAsync(0, cancellationToken))
        {
            await _groupCheckGate.WaitAsync(cancellationToken);
            _groupCheckGate.Release();
            return;
        }

        try
        {
            await RunGroupCheckAsync(runtime, cancellationToken);
        }
        finally
        {
            _groupCheckGate.Release();
        }
    }

    private async Task RunGroupCheckAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        _lastGroupCheck = started;

        using var slots = new SemaphoreSlim(GroupCheckParallelism);
        var checks = await Task.WhenAll(runtime.Members.Select(async member =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                return await CheckMemberAsync(runtime, member, cancellationToken);
            }
            finally
            {
                slots.Release();
            }
        }));

        var health = new GroupHealth(checks);
        GroupHealth = health;
        AppLog.Write(LogCategory.Network, $"Group test: {health.Summary}");
        AppLog.Write(LogCategory.Network, $"Group test by node: {health.Details}");

        // A probe that got through while the test ran outranks it: traffic is flowing.
        GroupWarning = health.NoneAnswer && !(_lastProbeSuccess >= started) ? ExplainSilentGroup(health) : null;
        if (GroupWarning is not null)
        {
            AppLog.Write(LogCategory.Network, GroupWarning);
        }

        Changed?.Invoke();
        await RefreshGroupSelectionAsync(runtime, cancellationToken);
    }

    private async Task<MemberCheck> CheckMemberAsync(RuntimeConfig runtime, GroupMember member, CancellationToken cancellationToken)
    {
        var query = $"proxies/{Uri.EscapeDataString(member.Tag)}/delay" +
                    $"?url={Uri.EscapeDataString(RuntimeConfigBuilder.GroupTestUrl)}&timeout={(int)GroupMemberTimeout.TotalMilliseconds}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(runtime.ControlUri, query));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runtime.ControlSecret);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(GroupMemberTimeout + TimeSpan.FromSeconds(3));

            using var response = await _groupControl.SendAsync(request, deadline.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));

            if (response.IsSuccessStatusCode
                && document.RootElement.TryGetProperty("delay", out var delay)
                && delay.TryGetInt32(out var milliseconds)
                && milliseconds > 0)
            {
                return new MemberCheck(member.Tag, member.Name, milliseconds, null);
            }

            return new MemberCheck(
                member.Tag,
                member.Name,
                null,
                response.StatusCode == HttpStatusCode.GatewayTimeout ? MemberCheck.TimeoutFailure : MemberCheck.UnreachableFailure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MemberCheck(member.Tag, member.Name, null, MemberCheck.TimeoutFailure);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return new MemberCheck(member.Tag, member.Name, null, MemberCheck.UnreachableFailure);
        }
    }

    /// <summary>What most likely keeps every node of the group from answering, and what to do.</summary>
    private string ExplainSilentGroup(GroupHealth health)
    {
        var opening = $"None of the {health.Members.Count} nodes answered a test, so nothing gets through.";
        var holder = NetworkAdapters.OtherVpnHoldingDefaultRoute();

        if (holder is not null && _boundInterface is null)
        {
            return opening +
                   $" Every connection has to go through \"{holder.Name}\" ({holder.Description}), and the network behind it is refusing these servers." +
                   " VLESS, VMess, Trojan and the rest cannot get underneath that VPN; WireGuard can, on WireSock" +
                   (WireSockEngine.IsInstalled ? "." : " (install WireSock Secure Connect; TunnlTo installs it too).") +
                   " Disconnect it, or connect with a WireGuard profile.";
        }

        if (holder is not null)
        {
            return opening +
                   $" \"{holder.Name}\" ({holder.Description}) is connected and may be filtering the other adapters. Disconnect it and connect again.";
        }

        return opening + " The network may be blocking these servers, or the subscription may have run out. Refresh it, turn on TLS fragment under Security, or try another subscription.";
    }

    private async Task<bool> MeasureOnceAsync(Uri? proxy, RuntimeConfig? runtime, CancellationToken cancellationToken)
    {
        if (runtime is { IsAutomatic: true })
        {
            await RefreshGroupSelectionAsync(runtime, cancellationToken);
        }

        try
        {
            var result = await _probe.RunAsync(proxy, cancellationToken);
            ExitIp = result.ExitIp;
            LatencyMilliseconds = result.LatencyMs;
            ProbeFailure = null;
            GroupWarning = null;
            _lastProbeSuccess = DateTimeOffset.Now;
            AppLog.Write(LogCategory.Network, $"Probe ok — exit {result.ExitIp}, rtt {result.LatencyMs} ms");
            Changed?.Invoke();
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException
                                          && !cancellationToken.IsCancellationRequested)
        {
            // "The SSL connection could not be established" says nothing on its own; every
            // useful detail — the reset, the certificate, the closed pipe — is an inner cause.
            ProbeFailure = Explain(exception);
            AppLog.Write(LogCategory.Network, $"Probe failed: {Explain(exception)}");
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>Asks the Clash API which member the automatic group is using, so the dashboard can name it.</summary>
    private async Task RefreshGroupSelectionAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        if (await ReadSelectionAsync(runtime, cancellationToken) is not { } tag)
        {
            return;
        }

        var name = runtime.MemberName(tag);
        if (name != GroupSelection)
        {
            GroupSelection = name;
            AppLog.Write(LogCategory.Network, $"Automatic selection is using \"{name}\"");
            Changed?.Invoke();
        }
    }

    /// <summary>The tag of the member the group is carrying traffic over; null before it has chosen, or when the API does not answer.</summary>
    private async Task<string?> ReadSelectionAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        try
        {
            using var request = Authorized(runtime, HttpMethod.Get, $"proxies/{RuntimeConfigBuilder.ProxyTag}");
            using var response = await _control.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("now", out var now) && now.GetString() is { Length: > 0 } tag ? tag : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException
                                          && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps an automatic group on a node that answers. The core re-tests its members every few
    /// minutes, and until then keeps dialling the node it chose even after that node has died,
    /// so every page fails in the meantime. Testing the chosen node on its own every few seconds
    /// closes that gap: a failed test makes the core choose again at once, and connections still
    /// held open through the dead node are closed, so applications reconnect instead of hanging.
    /// </summary>
    private async Task WatchSelectionAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(SelectionCheckInterval, cancellationToken);
                await CheckSelectionAsync(runtime, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckSelectionAsync(RuntimeConfig runtime, CancellationToken cancellationToken)
    {
        if (await ReadSelectionAsync(runtime, cancellationToken) is not { } selected)
        {
            return;
        }

        var check = await CheckMemberAsync(runtime, new GroupMember(selected, runtime.MemberName(selected)), cancellationToken);
        if (check.Answered)
        {
            return;
        }

        AppLog.Write(LogCategory.Network, $"\"{check.Name}\" stopped answering ({(check.Failure ?? "no answer").ToLowerInvariant()}); choosing another node.");

        // The failed test has already made the core choose again among the members that passed
        // their last test. When none has, every member is tested now, less often while none answers.
        var next = await ReadSelectionAsync(runtime, cancellationToken);
        if ((next is null || next == selected) && DateTimeOffset.Now - _lastGroupCheck >= _groupRetestGap)
        {
            await CheckGroupAsync(runtime, cancellationToken);
            _groupRetestGap = GroupHealth is { Answering: > 0 }
                ? GroupRetestFloor
                : TimeSpan.FromTicks(Math.Min(_groupRetestGap.Ticks * 2, GroupRetestCeiling.Ticks));
            next = await ReadSelectionAsync(runtime, cancellationToken);
        }

        if (next is null || next == selected)
        {
            return;
        }

        var closed = await CloseStalledConnectionsAsync(runtime, selected, cancellationToken);
        AppLog.Write(
            LogCategory.Network,
            closed switch
            {
                null => $"Moved to \"{runtime.MemberName(next)}\"; \"{check.Name}\" is still carrying traffic, so its connections stay open.",
                0 => $"Moved to \"{runtime.MemberName(next)}\".",
                _ => $"Moved to \"{runtime.MemberName(next)}\" and closed {closed} connection(s) left hanging on \"{check.Name}\"."
            });

        await RefreshGroupSelectionAsync(runtime, cancellationToken);
        await MeasureOnceAsync(runtime.ProxyUri, runtime, cancellationToken);
    }

    /// <summary>
    /// Closes the connections still carried by a member that failed its test, so the applications
    /// holding them reconnect through the member the group moved to. A test can fail on a node
    /// that works — a saturated link times the test out — so the connections are watched for a
    /// moment first; if any of them is still receiving data the node is alive and nothing is
    /// closed, and null is returned.
    /// </summary>
    private async Task<int?> CloseStalledConnectionsAsync(RuntimeConfig runtime, string memberTag, CancellationToken cancellationToken)
    {
        var before = await ReadConnectionsAsync(runtime, memberTag, cancellationToken);
        if (before.Count == 0)
        {
            return 0;
        }

        await Task.Delay(LivenessWindow, cancellationToken);
        var after = await ReadConnectionsAsync(runtime, memberTag, cancellationToken);

        if (after.Any(pair => before.TryGetValue(pair.Key, out var earlier) && pair.Value > earlier))
        {
            return null;
        }

        var closed = 0;
        foreach (var id in after.Keys)
        {
            try
            {
                using var request = Authorized(runtime, HttpMethod.Delete, $"connections/{Uri.EscapeDataString(id)}");
                using var response = await _control.SendAsync(request, cancellationToken);
                closed += response.IsSuccessStatusCode ? 1 : 0;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException
                                              && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        return closed;
    }

    /// <summary>The open connections whose chain runs through the member, with the bytes each has received so far.</summary>
    private async Task<Dictionary<string, long>> ReadConnectionsAsync(RuntimeConfig runtime, string memberTag, CancellationToken cancellationToken)
    {
        var connections = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            using var request = Authorized(runtime, HttpMethod.Get, "connections");
            using var response = await _control.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return connections;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return connections;
            }

            foreach (var connection in list.EnumerateArray())
            {
                var throughMember = connection.TryGetProperty("chains", out var chains)
                                    && chains.ValueKind == JsonValueKind.Array
                                    && chains.EnumerateArray().Any(hop => hop.GetString() == memberTag);

                if (throughMember && connection.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } key)
                {
                    connections[key] = connection.TryGetProperty("download", out var download) && download.TryGetInt64(out var bytes) ? bytes : 0;
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException
                                          && !cancellationToken.IsCancellationRequested)
        {
        }

        return connections;
    }

    private static HttpRequestMessage Authorized(RuntimeConfig runtime, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(runtime.ControlUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runtime.ControlSecret);
        return request;
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

            // Another VPN connecting or dropping can change which engine should carry the
            // session as well as which adapter it leaves on.
            var engineChanged = ChooseEngine(session).UseWireSock != (ActiveEngine == "WireSock");
            var desired = engineChanged || ActiveEngine == "WireSock"
                ? _boundInterface
                : (await NetworkAdapters.ResolveAsync(session.Settings, cancellationToken)).Binding?.InterfaceName;

            if (!engineChanged && string.Equals(desired, _boundInterface, StringComparison.OrdinalIgnoreCase))
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
        await _wireSock.StopAsync();

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
        BindingWarning = null;
        ActiveEngine = null;
        EngineNote = null;
    }

    private void ClearMeasurements()
    {
        ConnectedAt = null;
        ExitIp = null;
        LatencyMilliseconds = null;
        ProbeFailure = null;
        GroupSelection = null;
        GroupHealth = null;
        GroupWarning = null;
        _lastProbeSuccess = null;
        ThroughputMegabytesPerSecond = 0;
    }

    private RuntimeConfig BuildRuntime(
        ConnectionTarget target,
        AppSettings settings,
        IReadOnlyList<AppTarget> apps,
        IReadOnlyList<BridgeRoute> bridges,
        NetworkBinding? binding,
        int? carryingMtu = null)
    {
        foreach (var warning in target.Tunnels.SelectMany(tunnel => tunnel.Warnings))
        {
            AppLog.Write(LogCategory.Config, warning);
        }

        var preferred = bridges
            .Select(route => _lastBridgePorts.TryGetValue((route.Kind, route.ProfileId), out var port) ? port : (int?)null)
            .ToArray();

        _boundInterface = binding?.InterfaceName;

        AppLog.Write(
            LogCategory.Network,
            binding is null
                ? "Leaving on whichever adapter holds the default route."
                : $"Leaving on \"{binding.InterfaceName}\".");

        if (carryingMtu is < 1500)
        {
            AppLog.Write(LogCategory.Network, $"The carrying adapter's MTU is {carryingMtu}; WireGuard is sized to fit inside it.");
        }

        var runtime = RuntimeConfigBuilder.Build(
            target, settings, apps, bridges, PortPlan.Reserve(bridges.Count, preferred), binding, carryingMtu);

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

    /// <summary>The whole chain of causes on one line, innermost last.</summary>
    private static string Explain(Exception exception)
    {
        var causes = new List<string>();

        for (var current = exception; current is not null && causes.Count < 4; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !causes.Contains(message, StringComparer.Ordinal))
            {
                causes.Add(message);
            }
        }

        return string.Join(" · ", causes);
    }

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
        _groupControl.Dispose();
        await _core.DisposeAsync();
        await _wireSock.DisposeAsync();

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
