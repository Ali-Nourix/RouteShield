using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using RouteShield.Services;
using RouteShield.Subscriptions;
using RouteShield.Tunnels;
using RouteShield.Ui;

namespace RouteShield.ViewModels;

public enum ShellPage
{
    Dashboard,
    Profiles,
    Applications,
    Security,
    Diagnostics
}

public sealed record SubscriptionEdit(string Name, string Url, bool Remove);

/// <summary>Window-level services the view model needs but should not construct itself.</summary>
public interface IUiHost
{
    Task AlertAsync(string kicker, string title, string body);

    Task<bool> ConfirmAsync(string kicker, string title, string body, string confirmLabel);

    IReadOnlyList<RunningProcessItem>? PickRunningProcesses();

    /// <summary>Opens the subscription dialog; null when the user backed out.</summary>
    SubscriptionEdit? EditSubscription(VpnSubscription? existing);
}

public sealed class ShellViewModel : Observable
{
    private readonly SettingsStore _store = new();
    private readonly SubscriptionService _subscriptions = new();
    private readonly TunnelController _tunnel = new();
    private readonly LatencyTester _latency = new();
    private readonly BrowserBridgeServer _bridge;
    private readonly DispatcherTimer _clock;
    private readonly IUiHost _host;
    private readonly Dictionary<Guid, LibrarySection> _sections = [];
    private readonly Dictionary<Guid, (LatencyState State, int? Milliseconds, string? Failure)> _latencyMemory = [];

    /// <summary>The "fastest of this subscription" entry per subscription; synthesised, never saved.</summary>
    private readonly Dictionary<Guid, VpnProfile> _autoProfiles = [];

    private AppSettings _settings = new();
    private bool _loaded;
    private bool _rebuildingLibrary;
    private CancellationTokenSource? _latencyRun;

    private ShellPage _page = ShellPage.Dashboard;
    private VpnProfile? _selectedProfile;
    private ProfileItem? _selectedItem;
    private string _editorName = string.Empty;
    private string _editorConfig = string.Empty;
    private string _editorFormat = "NO PROFILE";
    private string _logFilter = string.Empty;
    private string _logCategory = "All";
    private string _busyMessage = string.Empty;
    private bool _sortByLatency;
    private bool _isTestingLatency;
    private bool _isEditorOpen;

    public ShellViewModel(IUiHost host)
    {
        _host = host;
        _bridge = new BrowserBridgeServer(() => new BridgeSnapshot(IsConnected, _tunnel.ActiveProfile?.Name, _tunnel.Bridges));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => SelectedProfile is not null);
        ReleaseKillSwitchCommand = new AsyncRelayCommand(ReleaseKillSwitchAsync);
        StopReconnectCommand = new AsyncRelayCommand(() => _tunnel.StopReconnectingAsync());

        NewProfileCommand = new RelayCommand(NewProfile);
        ImportProfileCommand = new AsyncRelayCommand(ImportProfileAsync);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => SelectedProfile is null || CanEditSelectedProfile);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, () => CanEditSelectedProfile);
        DetectFormatCommand = new RelayCommand(DetectFormat);
        ToggleEditorCommand = new RelayCommand(() => IsEditorOpen = !IsEditorOpen);
        RefreshAdaptersCommand = new RelayCommand(RefreshAdapters);

        AddSubscriptionCommand = new AsyncRelayCommand(AddSubscriptionAsync);
        EditSubscriptionCommand = new AsyncRelayCommand(EditSubscriptionAsync);
        RefreshSubscriptionCommand = new AsyncRelayCommand(RefreshOneSubscriptionAsync);
        RefreshSubscriptionsCommand = new AsyncRelayCommand(RefreshSubscriptionsAsync);

        TestLatencyCommand = new AsyncRelayCommand(TestLatencyAsync, () => !IsTestingLatency && Library.Count > 0);
        CancelLatencyCommand = new RelayCommand(() => _latencyRun?.Cancel(), () => IsTestingLatency);

        AddRunningProcessCommand = new AsyncRelayCommand(AddRunningProcessesAsync);
        BrowseExecutableCommand = new AsyncRelayCommand(BrowseExecutableAsync);
        RemoveAppCommand = new AsyncRelayCommand(RemoveAppAsync);

        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenExtensionFolderCommand = new RelayCommand(OpenExtensionFolder);
        ClearLogCommand = new RelayCommand(ClearLog);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync);
        FinishFirstRunCommand = new AsyncRelayCommand(FinishFirstRunAsync);
        SetLogCategoryCommand = new RelayCommand(parameter => LogCategoryFilter = parameter?.ToString() ?? "All");

        NavigateCommand = new RelayCommand(
            parameter =>
            {
                if (Enum.TryParse<ShellPage>(parameter?.ToString(), out var page))
                {
                    Page = page;
                }
            });

        LibraryView = (ListCollectionView)CollectionViewSource.GetDefaultView(Library);
        LibraryView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileItem.Section)));
        LibraryView.IsLiveSorting = true;
        LibraryView.LiveSortingProperties.Add(nameof(ProfileItem.SortKey));
        ApplyLibrarySort();

        _tunnel.Changed += OnTunnelChanged;
        AppLog.EntryWritten += OnLogEntry;

        _clock = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Raise(nameof(UptimeText));
        _clock.Start();
    }

    // ══ Navigation ══

    public ShellPage Page
    {
        get => _page;
        set => Set(ref _page, value);
    }

    // ══ Collections ══

    public ObservableCollection<VpnProfile> Profiles { get; } = [];

    public ObservableCollection<VpnSubscription> Subscriptions { get; } = [];

    public ObservableCollection<AppTarget> Apps { get; } = [];

    public ObservableCollection<ProfileItem> Library { get; } = [];

    /// <summary>Everything the connect button can be pointed at: automatic entries first, then every node.</summary>
    public ObservableCollection<VpnProfile> ConnectTargets { get; } = [];

    /// <summary>The adapters the tunnel could leave on, refreshed on demand.</summary>
    public ObservableCollection<NetworkAdapterInfo> OutboundAdapters { get; } = [];

    public ListCollectionView LibraryView { get; }

    public ObservableCollection<LogEntry> VisibleLog { get; } = [];

    public IReadOnlyList<string> LogCategories { get; } = ["All", .. Enum.GetNames<LogCategory>()];

    public IReadOnlyList<RouteMode> RouteModes { get; } = Enum.GetValues<RouteMode>();

    // ══ Tunnel state ══

    public TunnelState State => _tunnel.State;

    public string StateWord => State switch
    {
        TunnelState.Connected => "CONNECTED",
        TunnelState.Connecting => "CONNECTING",
        TunnelState.Reconnecting => "RECONNECTING",
        TunnelState.Disconnecting => "STOPPING",
        TunnelState.Failed => "FAILED",
        _ => Profiles.Count == 0 ? "NO PROFILE" : "DISCONNECTED"
    };

    public string StatusDetail => State == TunnelState.Disconnected && Profiles.Count == 0
        ? "Add a profile or a subscription to enable the connect button."
        : _tunnel.StatusDetail;

    public bool IsConnected => State == TunnelState.Connected;

    public bool IsTransitioning => State is TunnelState.Connecting or TunnelState.Disconnecting or TunnelState.Reconnecting;

    public bool IsKillSwitchHolding => _tunnel.KillSwitchArmed && State != TunnelState.Connected;

    public bool CanConnect => SelectedProfile is not null && State is TunnelState.Disconnected or TunnelState.Failed;

    public string UptimeText => _tunnel.ConnectedAt is { } start
        ? (DateTimeOffset.Now - start).ToString(@"hh\:mm\:ss")
        : "00:00:00";

    public string LatencyText => _tunnel.LatencyMilliseconds?.ToString() ?? "—";

    public string ThroughputText => IsConnected ? _tunnel.ThroughputMegabytesPerSecond.ToString("0.0") : "—";

    public string ExitIpText => _tunnel.ExitIp ?? "—";

    public string? ProbeFailure => _tunnel.ProbeFailure is { } failure ? $"Probe failed · {failure}" : null;

    /// <summary>Shown on the dashboard when the tunnel could not leave on the adapter it was told to.</summary>
    public string? OutboundWarning => _tunnel.BindingWarning;

    /// <summary>The program carrying the tunnel, and why, while connected.</summary>
    public string? EngineSummary => _tunnel.ActiveEngine is { } engine
        ? $"Engine · {engine}" + (_tunnel.EngineNote is { } note ? $" — {note}" : string.Empty)
        : null;

    public string CoreVersionText => _tunnel.CoreVersion;

    public string KillSwitchStateText => _tunnel.KillSwitchArmed ? "Kill switch armed" : "Kill switch idle";

    public bool IsAdministrator { get; } = Elevation.IsAdministrator;

    public string AppVersion => DiagnosticsExporter.AppVersion;

    public string BusyMessage
    {
        get => _busyMessage;
        private set
        {
            if (Set(ref _busyMessage, value))
            {
                Raise(nameof(IsBusy));
            }
        }
    }

    public bool IsBusy => BusyMessage.Length > 0;

    public bool ShowFirstRun => _settings.FirstRun;

    // ══ Browser bridge ══

    public string BridgeSummary
    {
        get
        {
            if (!BrowserBridgeEnabled)
            {
                return "Browser bridge is off";
            }

            if (_bridge.LastError is { } error)
            {
                return error;
            }

            var routes = _tunnel.Bridges.Count;
            return IsConnected
                ? $"Browser bridge on 127.0.0.1:{_settings.BrowserBridgePort} · {routes} route(s) for the extension"
                : $"Browser bridge on 127.0.0.1:{_settings.BrowserBridgePort} · routes appear when the tunnel is up";
        }
    }

    public string PinnedSummary
    {
        get
        {
            var pinned = Profiles.Count(profile => profile.BrowserPinned);
            return pinned == 0
                ? "Only the active profile and \"No VPN\" are offered to the extension."
                : $"{pinned} pinned profile(s) are offered to the extension beside the active one.";
        }
    }

    // ══ Settings ══

    public RouteMode RouteMode
    {
        get => _settings.RouteMode;
        set => ApplySetting(settings => settings.RouteMode = value, _settings.RouteMode == value);
    }

    public string RouteModeText => RouteMode switch
    {
        RouteMode.SelectedAppsOnly => "Selected applications",
        RouteMode.AllExceptSelected => "All except selected",
        _ => "Full system tunnel"
    };

    public bool AppKillSwitch
    {
        get => _settings.AppKillSwitch;
        set => ApplySetting(settings => settings.AppKillSwitch = value, _settings.AppKillSwitch == value);
    }

    public bool DnsProtection
    {
        get => _settings.DnsProtection;
        set => ApplySetting(settings => settings.DnsProtection = value, _settings.DnsProtection == value);
    }

    public bool Ipv6Protection
    {
        get => _settings.Ipv6Protection;
        set => ApplySetting(settings => settings.Ipv6Protection = value, _settings.Ipv6Protection == value);
    }

    public bool AllowLan
    {
        get => _settings.AllowLan;
        set => ApplySetting(settings => settings.AllowLan = value, _settings.AllowLan == value);
    }

    public bool AutoReconnect
    {
        get => _settings.AutoReconnect;
        set => ApplySetting(settings => settings.AutoReconnect = value, _settings.AutoReconnect == value);
    }

    public bool AutoConnect
    {
        get => _settings.AutoConnect;
        set => ApplySetting(settings => settings.AutoConnect = value, _settings.AutoConnect == value);
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set => ApplySetting(settings => settings.CloseToTray = value, _settings.CloseToTray == value);
    }

    public bool TlsFragment
    {
        get => _settings.TlsFragment;
        set => ApplySetting(settings => settings.TlsFragment = value, _settings.TlsFragment == value);
    }

    public bool BlockQuic
    {
        get => _settings.BlockQuic;
        set => ApplySetting(settings => settings.BlockQuic = value, _settings.BlockQuic == value);
    }

    public bool DirectDomesticSites
    {
        get => _settings.DirectDomesticSites;
        set => ApplySetting(settings => settings.DirectDomesticSites = value, _settings.DirectDomesticSites == value);
    }

    // ══ Engine ══

    public TunnelEngine Engine
    {
        get => _settings.Engine;
        set
        {
            if (_settings.Engine == value)
            {
                return;
            }

            _settings.Engine = value;
            Raise(nameof(Engine));
            Raise(nameof(EngineAvailability));
            QueueSave();
        }
    }

    /// <summary>Whether WireSock is on this machine, which decides what the engine choice can do.</summary>
    public string EngineAvailability
    {
        get
        {
            if (WireSockEngine.ExecutablePath is not { } path)
            {
                return "WireSock is not installed, so every profile runs on sing-box. Install WireSock Secure Connect — TunnlTo installs it too — to carry WireGuard profiles alongside a corporate VPN.";
            }

            var holder = NetworkAdapters.OtherVpnHoldingDefaultRoute();
            return holder is null
                ? $"WireSock found at {path}."
                : $"WireSock found at {path}. \"{holder.Name}\" ({holder.Description}) holds the default route right now, so Automatic runs WireGuard profiles on WireSock.";
        }
    }

    // ══ Outbound adapter ══

    public OutboundBinding OutboundBinding
    {
        get => _settings.OutboundBinding;
        set
        {
            if (_settings.OutboundBinding == value)
            {
                return;
            }

            _settings.OutboundBinding = value;

            // Picking "one adapter" with nothing chosen yet lands on the one the automatic
            // choice would have made, so the setting is never left pointing at nothing.
            if (value == OutboundBinding.Fixed && _settings.OutboundAdapter.Length == 0)
            {
                _settings.OutboundAdapter = OutboundAdapters.FirstOrDefault(adapter => !adapter.IsVirtual)?.Name
                                            ?? OutboundAdapters.FirstOrDefault()?.Name
                                            ?? string.Empty;
            }

            Raise(nameof(OutboundBinding));
            Raise(nameof(SelectedOutboundAdapter));
            Raise(nameof(CanChooseAdapter));
            Raise(nameof(OutboundSummary));
            QueueSave();
        }
    }

    public bool CanChooseAdapter => OutboundBinding == OutboundBinding.Fixed;

    public NetworkAdapterInfo? SelectedOutboundAdapter
    {
        get => OutboundAdapters.FirstOrDefault(adapter =>
            string.Equals(adapter.Name, _settings.OutboundAdapter, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || string.Equals(_settings.OutboundAdapter, value.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.OutboundAdapter = value.Name;
            Raise(nameof(SelectedOutboundAdapter));
            Raise(nameof(OutboundSummary));
            QueueSave();
        }
    }

    /// <summary>What the current choice resolves to right now, so the effect is visible before connecting.</summary>
    public string OutboundSummary
    {
        get
        {
            if (IsConnected && _tunnel.BoundInterface is { } bound)
            {
                return $"The tunnel is leaving on \"{bound}\".";
            }

            if (OutboundBinding == OutboundBinding.FollowWindows)
            {
                return "Following the default route. While another VPN is connected, the tunnel goes through it.";
            }

            var resolved = NetworkAdapters.Resolve(_settings);
            return resolved is null
                ? "No physical adapter could be identified; the default route will be followed."
                : $"Would leave on \"{resolved.InterfaceName}\", if it can still reach the internet when you connect.";
        }
    }

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value)
            {
                return;
            }

            _settings.Theme = value;
            Raise(nameof(Theme));
            ThemeManager.Apply(value);
            QueueSave();
        }
    }

    public bool BrowserBridgeEnabled
    {
        get => _settings.BrowserBridgeEnabled;
        set
        {
            if (_settings.BrowserBridgeEnabled == value)
            {
                return;
            }

            _settings.BrowserBridgeEnabled = value;
            Raise(nameof(BrowserBridgeEnabled));

            if (value)
            {
                _bridge.Start(_settings.BrowserBridgePort);
            }
            else
            {
                _bridge.Stop();
            }

            Raise(nameof(BridgeSummary));
            QueueSave();
        }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            _settings.StartWithWindows = value;
            Raise(nameof(StartWithWindows));
            _ = ApplyStartupAsync(value);
        }
    }

    // ══ Library ══

    public ProfileItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            // Clearing the library makes the list push null through this binding; that is
            // a side effect of the rebuild, not a choice, and must not unset the profile.
            if (_rebuildingLibrary)
            {
                return;
            }

            if (Set(ref _selectedItem, value))
            {
                SelectedProfile = value?.Profile;
            }
        }
    }

    public VpnProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_rebuildingLibrary && value is null)
            {
                return;
            }

            if (!Set(ref _selectedProfile, value))
            {
                return;
            }

            _settings.SelectedProfileId = value?.Id;
            EditorName = value?.Name ?? string.Empty;
            EditorConfig = value switch
            {
                null => string.Empty,
                { IsAutomatic: true } => AutomaticDescription(value),
                _ => value.ConfigText
            };
            EditorFormat = value?.Format.ToUpperInvariant() ?? "NO PROFILE";

            var item = Library.FirstOrDefault(candidate => candidate.Profile == value);
            if (!ReferenceEquals(item, _selectedItem))
            {
                _selectedItem = item;
                Raise(nameof(SelectedItem));
            }

            Raise(nameof(CanConnect));
            Raise(nameof(SelectedProfileLabel));
            Raise(nameof(SelectedProfilePinned));
            Raise(nameof(HasSelectedProfile));
            Raise(nameof(CanEditSelectedProfile));
            ConnectCommand.RaiseCanExecuteChanged();
            ValidateCommand.RaiseCanExecuteChanged();
            SaveProfileCommand.RaiseCanExecuteChanged();
            DeleteProfileCommand.RaiseCanExecuteChanged();
            QueueSave();
        }
    }

    public bool HasSelectedProfile => SelectedProfile is not null;

    /// <summary>Automatic entries have nothing to edit, pin or delete; they follow their subscription.</summary>
    public bool CanEditSelectedProfile => SelectedProfile is { IsAutomatic: false };

    public string SelectedProfileLabel => SelectedProfile switch
    {
        null => "No profile selected",
        { IsAutomatic: true } when IsConnected && _tunnel.GroupSelection is { } node => $"{SelectedProfile.Name} · via {node}",
        { IsAutomatic: true } => $"{SelectedProfile.Name} · the core picks the node",
        _ => $"{SelectedProfile.Name} · {SelectedProfile.Format}"
    };

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        set => Set(ref _isEditorOpen, value);
    }

    public bool SelectedProfilePinned
    {
        get => SelectedProfile?.BrowserPinned ?? false;
        set
        {
            if (SelectedProfile is null or { IsAutomatic: true } || SelectedProfile.BrowserPinned == value)
            {
                return;
            }

            SelectedProfile.BrowserPinned = value;
            Raise(nameof(SelectedProfilePinned));
            Raise(nameof(PinnedSummary));
            QueueSave();
        }
    }

    public bool SortByLatency
    {
        get => _sortByLatency;
        set
        {
            if (Set(ref _sortByLatency, value))
            {
                ApplyLibrarySort();
            }
        }
    }

    public bool IsTestingLatency
    {
        get => _isTestingLatency;
        private set
        {
            if (Set(ref _isTestingLatency, value))
            {
                TestLatencyCommand.RaiseCanExecuteChanged();
                CancelLatencyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LibrarySummary
    {
        get
        {
            var subscriptions = Subscriptions.Count switch
            {
                0 => "no subscriptions",
                1 => "1 subscription",
                var count => $"{count} subscriptions"
            };

            return $"{Profiles.Count} profile(s) · {subscriptions}";
        }
    }

    public string EditorName
    {
        get => _editorName;
        set => Set(ref _editorName, value);
    }

    public string EditorConfig
    {
        get => _editorConfig;
        set => Set(ref _editorConfig, value);
    }

    public string EditorFormat
    {
        get => _editorFormat;
        private set => Set(ref _editorFormat, value);
    }

    // ══ Applications ══

    public string AppSummary
    {
        get
        {
            var routed = RouteMode == RouteMode.AllExceptSelected ? "excluded" : "routed";
            return $"{Apps.Count} application(s) · {routed}";
        }
    }

    public string AppStateWord => RouteMode switch
    {
        RouteMode.SelectedAppsOnly => IsConnected ? "ROUTED" : "PENDING",
        RouteMode.AllExceptSelected => "EXCLUDED",
        _ => "ALL TRAFFIC"
    };

    // ══ Diagnostics ══

    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (Set(ref _logFilter, value))
            {
                RebuildLog();
            }
        }
    }

    public string LogCategoryFilter
    {
        get => _logCategory;
        set
        {
            if (Set(ref _logCategory, value))
            {
                RebuildLog();
            }
        }
    }

    public string LogBufferText => $"Buffer {VisibleLog.Count} / {AppLog.BufferLimit} lines";

    // ══ Commands ══

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand ValidateCommand { get; }

    public AsyncRelayCommand ReleaseKillSwitchCommand { get; }

    public AsyncRelayCommand StopReconnectCommand { get; }

    public RelayCommand NewProfileCommand { get; }

    public AsyncRelayCommand ImportProfileCommand { get; }

    public AsyncRelayCommand SaveProfileCommand { get; }

    public AsyncRelayCommand DeleteProfileCommand { get; }

    public RelayCommand DetectFormatCommand { get; }

    public RelayCommand ToggleEditorCommand { get; }

    public RelayCommand RefreshAdaptersCommand { get; }

    public AsyncRelayCommand AddSubscriptionCommand { get; }

    public AsyncRelayCommand EditSubscriptionCommand { get; }

    public AsyncRelayCommand RefreshSubscriptionCommand { get; }

    public AsyncRelayCommand RefreshSubscriptionsCommand { get; }

    public AsyncRelayCommand TestLatencyCommand { get; }

    public RelayCommand CancelLatencyCommand { get; }

    public AsyncRelayCommand AddRunningProcessCommand { get; }

    public AsyncRelayCommand BrowseExecutableCommand { get; }

    public AsyncRelayCommand RemoveAppCommand { get; }

    public AsyncRelayCommand ExportDiagnosticsCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand OpenExtensionFolderCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public AsyncRelayCommand ResetSettingsCommand { get; }

    public AsyncRelayCommand FinishFirstRunCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand SetLogCategoryCommand { get; }

    // ══ Lifecycle ══

    public async Task InitializeAsync()
    {
        _settings = await _store.LoadAsync();
        _loaded = true;

        foreach (var profile in _settings.Profiles)
        {
            Profiles.Add(profile);
        }

        foreach (var subscription in _settings.Subscriptions)
        {
            Subscriptions.Add(subscription);
        }

        foreach (var app in _settings.Apps)
        {
            Apps.Add(app);
        }

        RebuildLibrary();
        RefreshAdapters();
        ThemeManager.Apply(_settings.Theme);

        SelectedProfile = FindSelectable(_settings.SelectedProfileId) ?? Profiles.FirstOrDefault();

        ProcessCatalog.RefreshStates(Apps);
        RaiseAllSettings();
        RebuildLog();

        if (_settings.BrowserBridgeEnabled)
        {
            _bridge.Start(_settings.BrowserBridgePort);
            Raise(nameof(BridgeSummary));
        }

        await _tunnel.LoadCoreVersionAsync();

        if (!CoreProcessService.IsCoreInstalled)
        {
            AppLog.Write(LogCategory.Core, $"sing-box.exe was not found at {CoreProcessService.CorePath}");
        }

        if (_settings.AutoConnect && CanConnect)
        {
            await ConnectAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        _clock.Stop();
        _latencyRun?.Cancel();
        _bridge.Dispose();
        await _tunnel.DisposeAsync();
        _subscriptions.Dispose();
        await SaveAsync();
    }

    // ══ Tunnel actions ══

    private async Task ConnectAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (!IsAdministrator)
        {
            await _host.AlertAsync(
                "Permission required",
                "RouteShield needs to restart as administrator",
                "Creating the TUN adapter and the kill-switch firewall rules requires elevation. Close RouteShield and start it again with \"Run as administrator\".");
            return;
        }

        try
        {
            BusyMessage = "Starting the tunnel";
            var target = TunnelController.ResolveTarget(SelectedProfile, Profiles);
            var pinned = Profiles.Where(profile => profile.BrowserPinned).ToList();
            await _tunnel.ConnectAsync(SelectedProfile, target, _settings, [.. Apps], pinned);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            await _host.AlertAsync(
                "Connection failed",
                "The core stopped before the tunnel came up",
                exception.Message);
        }
        finally
        {
            BusyMessage = string.Empty;
        }
    }

    private async Task DisconnectAsync()
    {
        BusyMessage = "Stopping the tunnel";
        try
        {
            await _tunnel.DisconnectAsync();
        }
        finally
        {
            BusyMessage = string.Empty;
        }
    }

    private async Task ValidateAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            BusyMessage = "Checking the configuration";
            var output = await _tunnel.ValidateAsync(TunnelController.ResolveTarget(SelectedProfile, Profiles), _settings, [.. Apps]);
            await _host.AlertAsync("Configuration", "The core accepted this profile", output);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            await _host.AlertAsync("Configuration rejected", "sing-box refused this profile", exception.Message);
        }
        finally
        {
            BusyMessage = string.Empty;
        }
    }

    private async Task ReleaseKillSwitchAsync()
    {
        try
        {
            await _tunnel.ReleaseKillSwitchAsync();
        }
        catch (InvalidOperationException exception)
        {
            await _host.AlertAsync("Windows Firewall", "The leak guard could not be released", exception.Message);
        }
    }

    // ══ Profile actions ══

    private void NewProfile()
    {
        var profile = new VpnProfile { Name = "New profile" };
        Profiles.Add(profile);
        RebuildLibrary();
        SelectedProfile = profile;
        IsEditorOpen = true;
        Page = ShellPage.Profiles;
    }

    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a configuration",
            Filter = "Configurations (*.json;*.conf;*.txt)|*.json;*.conf;*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(dialog.FileName);
            var parsed = TunnelParser.Parse(text);

            var profile = new VpnProfile
            {
                Name = Path.GetFileNameWithoutExtension(dialog.FileName),
                Format = parsed.FormatName,
                ConfigText = text
            };

            Profiles.Add(profile);
            RebuildLibrary();
            SelectedProfile = profile;
            IsEditorOpen = true;
            await SaveAsync();

            AppLog.Write(LogCategory.Config, $"Imported profile \"{profile.Name}\" ({parsed.FormatName})");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            await _host.AlertAsync("Import failed", "That file is not a usable configuration", exception.Message);
        }
    }

    private async Task SaveProfileAsync()
    {
        if (SelectedProfile is null or { IsAutomatic: true })
        {
            NewProfile();
        }

        var profile = SelectedProfile!;

        try
        {
            var parsed = TunnelParser.Parse(EditorConfig);
            profile.Format = parsed.FormatName;
            EditorFormat = parsed.FormatName.ToUpperInvariant();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            await _host.AlertAsync("Profile not saved", "The configuration could not be read", exception.Message);
            return;
        }

        profile.Name = string.IsNullOrWhiteSpace(EditorName) ? "Untitled profile" : EditorName.Trim();
        profile.ConfigText = EditorConfig;
        profile.UpdatedAt = DateTimeOffset.Now;

        Raise(nameof(SelectedProfileLabel));
        await SaveAsync();

        AppLog.Write(LogCategory.Config, $"Profile \"{profile.Name}\" saved as {profile.Format}");
    }

    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { IsAutomatic: false } profile)
        {
            return;
        }

        var confirmed = await _host.ConfirmAsync(
            "Delete profile",
            $"Remove \"{profile.Name}\"?",
            "The configuration is deleted from this machine. A profile that came from a subscription returns on the next refresh.",
            "Delete");

        if (!confirmed)
        {
            return;
        }

        Profiles.Remove(profile);
        RebuildLibrary();
        SelectedProfile = Profiles.FirstOrDefault();
        await SaveAsync();
    }

    private void DetectFormat() => EditorFormat = TunnelParser.DetectFormat(EditorConfig).ToUpperInvariant();

    // ══ Subscription actions ══

    private async Task AddSubscriptionAsync()
    {
        if (_host.EditSubscription(null) is not { Remove: false } edit)
        {
            return;
        }

        var subscription = Subscriptions.FirstOrDefault(item => string.Equals(item.Url, edit.Url, StringComparison.Ordinal))
                           ?? new VpnSubscription();

        if (!Subscriptions.Contains(subscription))
        {
            Subscriptions.Add(subscription);
        }

        subscription.Name = edit.Name;
        subscription.Url = edit.Url;

        await RefreshSubscriptionAsync(subscription);
        RebuildLibrary();
        await SaveAsync();
    }

    private async Task EditSubscriptionAsync(object? parameter)
    {
        if (parameter is not VpnSubscription subscription || _host.EditSubscription(subscription) is not { } edit)
        {
            return;
        }

        if (edit.Remove)
        {
            var confirmed = await _host.ConfirmAsync(
                "Remove subscription",
                $"Remove \"{subscription.Name}\"?",
                "Profiles that came from this subscription are removed with it.",
                "Remove");

            if (!confirmed)
            {
                return;
            }

            foreach (var profile in Profiles.Where(item => item.SubscriptionId == subscription.Id).ToList())
            {
                Profiles.Remove(profile);
            }

            Subscriptions.Remove(subscription);
            _sections.Remove(subscription.Id);
            _autoProfiles.Remove(subscription.Id);
            RebuildLibrary();

            if (SelectedProfile is null || !IsSelectable(SelectedProfile))
            {
                SelectedProfile = Profiles.FirstOrDefault();
            }

            await SaveAsync();
            return;
        }

        var urlChanged = !string.Equals(subscription.Url, edit.Url, StringComparison.Ordinal);
        subscription.Name = edit.Name;
        subscription.Url = edit.Url;
        _sections.Remove(subscription.Id);

        if (urlChanged)
        {
            await RefreshSubscriptionAsync(subscription);
        }

        RebuildLibrary();
        await SaveAsync();
    }

    private async Task RefreshOneSubscriptionAsync(object? parameter)
    {
        if (parameter is not VpnSubscription subscription)
        {
            return;
        }

        BusyMessage = $"Refreshing {subscription.Name}";
        try
        {
            await RefreshSubscriptionAsync(subscription);
            RebuildLibrary();
            await SaveAsync();
        }
        finally
        {
            BusyMessage = string.Empty;
        }
    }

    private async Task RefreshSubscriptionsAsync()
    {
        if (Subscriptions.Count == 0)
        {
            return;
        }

        BusyMessage = "Refreshing subscriptions";
        try
        {
            foreach (var subscription in Subscriptions.ToList())
            {
                await RefreshSubscriptionAsync(subscription);
            }

            RebuildLibrary();
            await SaveAsync();
        }
        finally
        {
            BusyMessage = string.Empty;
        }
    }

    private async Task RefreshSubscriptionAsync(VpnSubscription subscription)
    {
        try
        {
            // The address a subscription lives at is often blocked by the network the tunnel is
            // there to get around, so it is fetched through the tunnel whenever one is up.
            var entries = await _subscriptions.FetchAsync(subscription.Url, _tunnel.ProxyUri);
            var replaced = ReplaceSubscriptionProfiles(subscription, entries);

            subscription.LastError = string.Empty;
            subscription.LastCount = replaced;
            subscription.LastUpdated = DateTimeOffset.Now;

            AppLog.Write(LogCategory.Subscription, $"\"{subscription.Name}\" refreshed — {replaced} profile(s)");
        }
        catch (Exception exception) when (IsExpected(exception) || exception is HttpRequestException)
        {
            subscription.LastError = exception.Message;
            AppLog.Write(LogCategory.Subscription, $"\"{subscription.Name}\" refresh failed — {exception.Message}");
        }
    }

    /// <summary>
    /// Swaps a subscription's profiles for the freshly fetched list while keeping what the user
    /// attached to them: the selection, and which ones were pinned for the browser.
    /// </summary>
    private int ReplaceSubscriptionProfiles(VpnSubscription subscription, IReadOnlyList<SubscriptionEntry> entries)
    {
        var selectedName = SelectedProfile?.Name;
        var owned = Profiles.Where(profile => profile.SubscriptionId == subscription.Id).ToList();
        var pinnedNames = owned.Where(profile => profile.BrowserPinned).Select(profile => profile.Name).ToHashSet();

        foreach (var profile in owned)
        {
            Profiles.Remove(profile);
        }

        var added = 0;
        foreach (var entry in entries)
        {
            string format;
            try
            {
                format = TunnelParser.Parse(entry.Config).FormatName;
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                continue;
            }

            Profiles.Add(new VpnProfile
            {
                Name = entry.Name,
                Format = format,
                ConfigText = entry.Config,
                SubscriptionId = subscription.Id,
                BrowserPinned = pinnedNames.Contains(entry.Name)
            });
            added++;
        }

        if (SelectedProfile is null || !IsSelectable(SelectedProfile))
        {
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Name == selectedName) ?? Profiles.FirstOrDefault();
        }

        return added;
    }

    private void RefreshAdapters()
    {
        OutboundAdapters.Clear();
        foreach (var adapter in NetworkAdapters.List())
        {
            OutboundAdapters.Add(adapter);
        }

        Raise(nameof(SelectedOutboundAdapter));
        Raise(nameof(OutboundSummary));
        Raise(nameof(EngineAvailability));
    }

    private bool IsSelectable(VpnProfile profile) => Profiles.Contains(profile) || _autoProfiles.ContainsValue(profile);

    private VpnProfile? FindSelectable(Guid? id) => id is null
        ? null
        : Profiles.FirstOrDefault(profile => profile.Id == id) ?? _autoProfiles.Values.FirstOrDefault(profile => profile.Id == id);

    private string AutomaticDescription(VpnProfile automatic)
    {
        var nodes = Profiles.Count(profile => profile.SubscriptionId == automatic.SubscriptionId);
        return $"""
            Automatic selection has no configuration of its own.

            RouteShield hands the core every node of this subscription ({nodes} at the moment).
            The core measures all of them, carries traffic over the fastest, re-tests every
            {RuntimeConfigBuilder.GroupTestInterval.TrimEnd('m')} minutes, and moves to the next node when the
            chosen one stops answering — without dropping the tunnel.

            Refresh the subscription to change the set of nodes.
            """;
    }

    // ══ Library ══

    private void RebuildLibrary()
    {
        foreach (var item in Library)
        {
            _latencyMemory[item.Profile.Id] = item.Snapshot();
        }

        _rebuildingLibrary = true;
        try
        {
            Library.Clear();
            ConnectTargets.Clear();

            foreach (var profile in Profiles)
            {
                var item = new ProfileItem(profile, SectionFor(profile));
                if (_latencyMemory.TryGetValue(profile.Id, out var remembered))
                {
                    item.Restore(remembered.State, remembered.Milliseconds, remembered.Failure);
                }

                Library.Add(item);
            }

            // A subscription with two or more nodes gets a "fastest of" entry at the top of its section.
            foreach (var stale in _autoProfiles.Keys.Where(id => Subscriptions.All(subscription => subscription.Id != id)).ToList())
            {
                _autoProfiles.Remove(stale);
            }

            foreach (var subscription in Subscriptions.OrderBy(subscription => subscription.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var nodes = Profiles.Count(profile => profile.SubscriptionId == subscription.Id);
                if (nodes < 2)
                {
                    _autoProfiles.Remove(subscription.Id);
                    continue;
                }

                if (!_autoProfiles.TryGetValue(subscription.Id, out var automatic))
                {
                    automatic = VpnProfile.AutomaticFor(subscription);
                    _autoProfiles[subscription.Id] = automatic;
                }

                automatic.Name = $"Fastest of {subscription.Name}";
                Library.Add(new ProfileItem(automatic, SectionFor(automatic), nodes));
                ConnectTargets.Add(automatic);
            }

            foreach (var profile in Profiles)
            {
                ConnectTargets.Add(profile);
            }
        }
        finally
        {
            _rebuildingLibrary = false;
        }

        if (SelectedProfile is { IsAutomatic: true } selected && !_autoProfiles.ContainsValue(selected))
        {
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.SubscriptionId == selected.SubscriptionId) ?? Profiles.FirstOrDefault();
        }

        _selectedItem = Library.FirstOrDefault(item => item.Profile == SelectedProfile);
        Raise(nameof(SelectedItem));
        // The connect list was refilled; the combo bound to it re-reads the selection.
        Raise(nameof(SelectedProfile));
        Raise(nameof(LibrarySummary));
        Raise(nameof(PinnedSummary));
        Raise(nameof(StateWord));
        Raise(nameof(StatusDetail));
        TestLatencyCommand.RaiseCanExecuteChanged();
    }

    private LibrarySection SectionFor(VpnProfile profile)
    {
        if (profile.SubscriptionId is not { } id)
        {
            return LibrarySection.Manual;
        }

        var subscription = Subscriptions.FirstOrDefault(candidate => candidate.Id == id);
        if (subscription is null)
        {
            return LibrarySection.Manual;
        }

        if (!_sections.TryGetValue(id, out var section))
        {
            section = LibrarySection.For(subscription);
            _sections[id] = section;
        }

        return section;
    }

    private void ApplyLibrarySort()
    {
        using (LibraryView.DeferRefresh())
        {
            LibraryView.SortDescriptions.Clear();
            LibraryView.SortDescriptions.Add(new SortDescription("Section.Order", ListSortDirection.Ascending));
            LibraryView.SortDescriptions.Add(new SortDescription("Section.Title", ListSortDirection.Ascending));
            LibraryView.SortDescriptions.Add(new SortDescription(nameof(ProfileItem.Rank), ListSortDirection.Ascending));

            if (SortByLatency)
            {
                LibraryView.SortDescriptions.Add(new SortDescription(nameof(ProfileItem.SortKey), ListSortDirection.Ascending));
            }

            LibraryView.SortDescriptions.Add(new SortDescription("Profile.Name", ListSortDirection.Ascending));
        }
    }

    private async Task TestLatencyAsync()
    {
        var items = Library.Where(item => !item.Profile.IsAutomatic).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var candidates = new List<(Guid Id, ParsedTunnel Tunnel)>(items.Count);
        var byId = new Dictionary<Guid, ProfileItem>(items.Count);

        foreach (var item in items)
        {
            try
            {
                candidates.Add((item.Profile.Id, TunnelParser.Parse(item.Profile.ConfigText)));
                byId[item.Profile.Id] = item;
                item.BeginTest();
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                item.Apply(new LatencyResult(item.Profile.Id, null, "Invalid"));
            }
        }

        var run = new CancellationTokenSource();
        _latencyRun = run;
        IsTestingLatency = true;

        try
        {
            await _latency.RunAsync(candidates, result => Dispatch(() => byId[result.ProfileId].Apply(result)), run.Token);
        }
        catch (OperationCanceledException)
        {
            foreach (var item in byId.Values.Where(item => item.LatencyState == LatencyState.Testing))
            {
                item.Apply(new LatencyResult(item.Profile.Id, null, "Cancelled"));
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            foreach (var item in byId.Values.Where(item => item.LatencyState == LatencyState.Testing))
            {
                item.Apply(new LatencyResult(item.Profile.Id, null, "Failed"));
            }

            await _host.AlertAsync("Latency test", "The probe core could not run", exception.Message);
        }
        finally
        {
            IsTestingLatency = false;
            if (ReferenceEquals(_latencyRun, run))
            {
                _latencyRun = null;
            }

            run.Dispose();
        }
    }

    // ══ Application actions ══

    private async Task AddRunningProcessesAsync()
    {
        var picked = _host.PickRunningProcesses();
        if (picked is null || picked.Count == 0)
        {
            return;
        }

        foreach (var process in picked)
        {
            AddApp(process.Name, process.Path);
        }

        await SaveAsync();
    }

    private async Task BrowseExecutableAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an application",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AddApp(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
        await SaveAsync();
    }

    private async Task RemoveAppAsync(object? parameter)
    {
        if (parameter is not AppTarget app)
        {
            return;
        }

        Apps.Remove(app);
        Raise(nameof(AppSummary));
        await SaveAsync();
    }

    private void AddApp(string name, string path)
    {
        if (Apps.Any(app => string.Equals(app.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Apps.Add(new AppTarget { DisplayName = name, Path = path });
        ProcessCatalog.RefreshStates(Apps);
        Raise(nameof(AppSummary));
    }

    // ══ Diagnostics actions ══

    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            var target = DiagnosticsExporter.Export();
            await _host.AlertAsync("Diagnostics", "Bundle written to your desktop", target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await _host.AlertAsync("Diagnostics", "The bundle could not be written", exception.Message);
        }
    }

    private void OpenLogFolder()
    {
        AppPaths.Ensure();
        Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true });
    }

    private void OpenExtensionFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "extensions");
        if (!Directory.Exists(folder))
        {
            _ = _host.AlertAsync(
                "Browser extension",
                "The extension folder is not part of this build",
                "Release packages carry an \"extensions\" folder with the Firefox and Chrome add-ons. Download the package from GitHub, or build with build.cmd.");
            return;
        }

        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void ClearLog()
    {
        AppLog.Clear();
        RebuildLog();
    }

    private async Task ResetSettingsAsync()
    {
        var confirmed = await _host.ConfirmAsync(
            "Reset preferences",
            "Restore the default settings?",
            "Profiles, subscriptions and the application list are kept. Only the security and behaviour switches return to their defaults.",
            "Reset");

        if (!confirmed)
        {
            return;
        }

        var defaults = new AppSettings();
        _settings.RouteMode = defaults.RouteMode;
        _settings.AppKillSwitch = defaults.AppKillSwitch;
        _settings.DnsProtection = defaults.DnsProtection;
        _settings.Ipv6Protection = defaults.Ipv6Protection;
        _settings.AllowLan = defaults.AllowLan;
        _settings.AutoReconnect = defaults.AutoReconnect;
        _settings.AutoConnect = defaults.AutoConnect;
        _settings.CloseToTray = defaults.CloseToTray;
        _settings.TlsFragment = defaults.TlsFragment;
        _settings.BlockQuic = defaults.BlockQuic;
        _settings.DirectDomesticSites = defaults.DirectDomesticSites;
        _settings.OutboundBinding = defaults.OutboundBinding;
        _settings.OutboundAdapter = defaults.OutboundAdapter;
        _settings.Engine = defaults.Engine;
        BrowserBridgeEnabled = defaults.BrowserBridgeEnabled;
        Theme = defaults.Theme;

        RaiseAllSettings();
        await SaveAsync();
    }

    private async Task FinishFirstRunAsync()
    {
        _settings.FirstRun = false;
        Raise(nameof(ShowFirstRun));
        await SaveAsync();
    }

    // ══ Plumbing ══

    private void OnTunnelChanged()
    {
        Dispatch(() =>
        {
            foreach (var property in new[]
                     {
                         nameof(State), nameof(StateWord), nameof(StatusDetail), nameof(IsConnected),
                         nameof(IsTransitioning), nameof(IsKillSwitchHolding), nameof(CanConnect),
                         nameof(UptimeText), nameof(LatencyText), nameof(ThroughputText), nameof(ExitIpText),
                         nameof(ProbeFailure), nameof(CoreVersionText), nameof(KillSwitchStateText),
                         nameof(AppStateWord), nameof(BridgeSummary), nameof(SelectedProfileLabel),
                         nameof(OutboundSummary), nameof(OutboundWarning), nameof(EngineSummary)
                     })
            {
                Raise(property);
            }

            ConnectCommand.RaiseCanExecuteChanged();
        });
    }

    private void OnLogEntry(LogEntry entry)
    {
        Dispatch(() =>
        {
            if (!Matches(entry))
            {
                return;
            }

            VisibleLog.Add(entry);
            while (VisibleLog.Count > AppLog.BufferLimit)
            {
                VisibleLog.RemoveAt(0);
            }

            Raise(nameof(LogBufferText));
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void RebuildLog()
    {
        VisibleLog.Clear();

        foreach (var entry in AppLog.Snapshot().Where(Matches))
        {
            VisibleLog.Add(entry);
        }

        Raise(nameof(LogBufferText));
    }

    private bool Matches(LogEntry entry)
    {
        if (LogCategoryFilter != "All" && entry.Category.ToString() != LogCategoryFilter)
        {
            return false;
        }

        return LogFilter.Length == 0 || entry.Message.Contains(LogFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySetting(Action<AppSettings> apply, bool unchanged, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (unchanged)
        {
            return;
        }

        apply(_settings);
        Raise(propertyName);

        if (propertyName == nameof(RouteMode))
        {
            Raise(nameof(RouteModeText));
            Raise(nameof(AppStateWord));
            Raise(nameof(AppSummary));
        }

        QueueSave();
    }

    private async Task ApplyStartupAsync(bool enabled)
    {
        try
        {
            await new StartupService().SetEnabledAsync(enabled);
            await SaveAsync();
        }
        catch (InvalidOperationException exception)
        {
            _settings.StartWithWindows = !enabled;
            Raise(nameof(StartWithWindows));
            await _host.AlertAsync("Windows integration", "The logon task could not be updated", exception.Message);
        }
    }

    private void RaiseAllSettings()
    {
        foreach (var property in new[]
                 {
                     nameof(RouteMode), nameof(RouteModeText), nameof(AppKillSwitch), nameof(DnsProtection),
                     nameof(Ipv6Protection), nameof(AllowLan), nameof(AutoReconnect), nameof(AutoConnect),
                     nameof(CloseToTray), nameof(StartWithWindows), nameof(BrowserBridgeEnabled), nameof(BridgeSummary),
                     nameof(TlsFragment), nameof(BlockQuic), nameof(DirectDomesticSites), nameof(Theme),
                     nameof(OutboundBinding), nameof(SelectedOutboundAdapter), nameof(CanChooseAdapter), nameof(OutboundSummary),
                     nameof(Engine), nameof(EngineAvailability),
                     nameof(ShowFirstRun), nameof(AppSummary), nameof(StateWord), nameof(StatusDetail)
                 })
        {
            Raise(property);
        }
    }

    private void QueueSave() => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (!_loaded)
        {
            return;
        }

        _settings.Profiles = [.. Profiles];
        _settings.Subscriptions = [.. Subscriptions];
        _settings.Apps = [.. Apps];

        try
        {
            await _store.SaveAsync(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(LogCategory.Settings, $"Settings could not be saved: {exception.Message}");
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is FormatException or NotSupportedException or InvalidOperationException
            or ArgumentException or IOException or TimeoutException or UnauthorizedAccessException;
}
