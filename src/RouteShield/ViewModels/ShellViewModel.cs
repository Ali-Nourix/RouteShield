using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Microsoft.Win32;
using RouteShield.Services;
using RouteShield.Subscriptions;
using RouteShield.Tunnels;

namespace RouteShield.ViewModels;

public enum ShellPage
{
    Dashboard,
    Profiles,
    Applications,
    Security,
    Diagnostics
}

/// <summary>Window-level services the view model needs but should not construct itself.</summary>
public interface IUiHost
{
    Task AlertAsync(string kicker, string title, string body);

    Task<bool> ConfirmAsync(string kicker, string title, string body, string confirmLabel);

    IReadOnlyList<RunningProcessItem>? PickRunningProcesses();
}

public sealed class ShellViewModel : Observable
{
    private readonly SettingsStore _store = new();
    private readonly SubscriptionService _subscriptions = new();
    private readonly TunnelController _tunnel = new();
    private readonly DispatcherTimer _clock;
    private readonly IUiHost _host;

    private AppSettings _settings = new();
    private bool _loaded;

    private ShellPage _page = ShellPage.Dashboard;
    private VpnProfile? _selectedProfile;
    private VpnSubscription? _selectedSubscription;
    private string _editorName = string.Empty;
    private string _editorConfig = string.Empty;
    private string _editorFormat = "NO PROFILE";
    private string _subscriptionName = string.Empty;
    private string _subscriptionUrl = string.Empty;
    private string _logFilter = string.Empty;
    private string _logCategory = "All";
    private string _processFilter = string.Empty;
    private string _busyMessage = string.Empty;

    public ShellViewModel(IUiHost host)
    {
        _host = host;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => SelectedProfile is not null);
        ReleaseKillSwitchCommand = new AsyncRelayCommand(ReleaseKillSwitchAsync);
        StopReconnectCommand = new AsyncRelayCommand(() => _tunnel.StopReconnectingAsync());

        NewProfileCommand = new RelayCommand(NewProfile);
        ImportProfileCommand = new AsyncRelayCommand(ImportProfileAsync);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, () => SelectedProfile is not null);
        DetectFormatCommand = new RelayCommand(DetectFormat);

        SaveSubscriptionCommand = new AsyncRelayCommand(SaveSubscriptionAsync);
        RemoveSubscriptionCommand = new AsyncRelayCommand(RemoveSubscriptionAsync, () => SelectedSubscription is not null);
        RefreshSubscriptionsCommand = new AsyncRelayCommand(RefreshSubscriptionsAsync);

        AddRunningProcessCommand = new AsyncRelayCommand(AddRunningProcessesAsync);
        BrowseExecutableCommand = new AsyncRelayCommand(BrowseExecutableAsync);
        RemoveAppCommand = new AsyncRelayCommand(RemoveAppAsync);

        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
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
        set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(PageKicker));
                Raise(nameof(PageTitle));
            }
        }
    }

    public string PageKicker => Page switch
    {
        ShellPage.Dashboard => "Control centre",
        ShellPage.Profiles => "Library",
        ShellPage.Applications => "Split tunnel",
        ShellPage.Security => "Preferences",
        _ => "Support"
    };

    public string PageTitle => Page switch
    {
        ShellPage.Dashboard => "Connection",
        ShellPage.Profiles => "Profiles & subscriptions",
        ShellPage.Applications => "Application routing",
        ShellPage.Security => "Security & behaviour",
        _ => "Diagnostics"
    };

    // ══ Collections ══

    public ObservableCollection<VpnProfile> Profiles { get; } = [];

    public ObservableCollection<VpnSubscription> Subscriptions { get; } = [];

    public ObservableCollection<AppTarget> Apps { get; } = [];

    public ObservableCollection<LogEntry> VisibleLog { get; } = [];

    public IReadOnlyList<string> LogCategories { get; } =
        ["All", .. Enum.GetNames<LogCategory>()];

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

    public bool IsReconnecting => State == TunnelState.Reconnecting;

    public bool IsKillSwitchHolding => _tunnel.KillSwitchArmed && State != TunnelState.Connected;

    public bool CanConnect => SelectedProfile is not null && State is TunnelState.Disconnected or TunnelState.Failed;

    public string UptimeText => _tunnel.ConnectedAt is { } start
        ? (DateTimeOffset.Now - start).ToString(@"hh\:mm\:ss")
        : "00:00:00";

    public string LatencyText => _tunnel.LatencyMilliseconds?.ToString() ?? "—";

    public string ThroughputText => IsConnected
        ? _tunnel.ThroughputMegabytesPerSecond.ToString("0.0")
        : "—";

    public string ExitIpText => _tunnel.ExitIp ?? "—";

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

    // ══ Profiles ══

    public VpnProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value))
            {
                return;
            }

            _settings.SelectedProfileId = value?.Id;
            EditorName = value?.Name ?? string.Empty;
            EditorConfig = value?.ConfigText ?? string.Empty;
            EditorFormat = value?.Format.ToUpperInvariant() ?? "NO PROFILE";

            Raise(nameof(CanConnect));
            Raise(nameof(SelectedProfileLabel));
            ConnectCommand.RaiseCanExecuteChanged();
            ValidateCommand.RaiseCanExecuteChanged();
            DeleteProfileCommand.RaiseCanExecuteChanged();
            QueueSave();
        }
    }

    public string SelectedProfileLabel => SelectedProfile is null
        ? "No profile selected"
        : $"{SelectedProfile.Name} · {SelectedProfile.Format}";

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

    // ══ Subscriptions ══

    public VpnSubscription? SelectedSubscription
    {
        get => _selectedSubscription;
        set
        {
            if (!Set(ref _selectedSubscription, value))
            {
                return;
            }

            SubscriptionName = value?.Name ?? string.Empty;
            SubscriptionUrl = value?.Url ?? string.Empty;
            RemoveSubscriptionCommand.RaiseCanExecuteChanged();
        }
    }

    public string SubscriptionName
    {
        get => _subscriptionName;
        set => Set(ref _subscriptionName, value);
    }

    public string SubscriptionUrl
    {
        get => _subscriptionUrl;
        set => Set(ref _subscriptionUrl, value);
    }

    // ══ Applications ══

    public string ProcessFilter
    {
        get => _processFilter;
        set => Set(ref _processFilter, value);
    }

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

    public AsyncRelayCommand SaveSubscriptionCommand { get; }

    public AsyncRelayCommand RemoveSubscriptionCommand { get; }

    public AsyncRelayCommand RefreshSubscriptionsCommand { get; }

    public AsyncRelayCommand AddRunningProcessCommand { get; }

    public AsyncRelayCommand BrowseExecutableCommand { get; }

    public AsyncRelayCommand RemoveAppCommand { get; }

    public AsyncRelayCommand ExportDiagnosticsCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

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

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == _settings.SelectedProfileId)
                          ?? Profiles.FirstOrDefault();

        ProcessCatalog.RefreshStates(Apps);
        RaiseAllSettings();
        RebuildLog();

        await _tunnel.LoadCoreVersionAsync();

        if (!_tunnel.IsCoreInstalled)
        {
            AppLog.Write(LogCategory.Core, $"sing-box.exe was not found at {_tunnel.CorePath}");
        }

        if (_settings.AutoConnect && CanConnect)
        {
            await ConnectAsync();
        }
    }

    public async Task ShutdownAsync()
    {
        _clock.Stop();
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
            await _tunnel.ConnectAsync(SelectedProfile, _settings, [.. Apps]);
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
            var output = await _tunnel.ValidateAsync(SelectedProfile, _settings, [.. Apps]);
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
        SelectedProfile = profile;
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
            SelectedProfile = profile;
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
        if (SelectedProfile is null)
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
        if (SelectedProfile is not { } profile)
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
        SelectedProfile = Profiles.FirstOrDefault();
        await SaveAsync();
    }

    private void DetectFormat() => EditorFormat = TunnelParser.DetectFormat(EditorConfig).ToUpperInvariant();

    // ══ Subscription actions ══

    private async Task SaveSubscriptionAsync()
    {
        var url = SubscriptionUrl.Trim();
        if (url.Length == 0)
        {
            await _host.AlertAsync("Subscription", "Enter an HTTPS address", "A subscription URL must start with https://.");
            return;
        }

        var subscription = SelectedSubscription;
        if (subscription is null || !string.Equals(subscription.Url, url, StringComparison.Ordinal))
        {
            subscription = Subscriptions.FirstOrDefault(item => string.Equals(item.Url, url, StringComparison.Ordinal));
        }

        if (subscription is null)
        {
            subscription = new VpnSubscription();
            Subscriptions.Add(subscription);
        }

        subscription.Name = string.IsNullOrWhiteSpace(SubscriptionName) ? "Subscription" : SubscriptionName.Trim();
        subscription.Url = url;
        SelectedSubscription = subscription;

        await RefreshSubscriptionAsync(subscription);
        await SaveAsync();
    }

    private async Task RemoveSubscriptionAsync()
    {
        if (SelectedSubscription is not { } subscription)
        {
            return;
        }

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
        SelectedSubscription = null;

        if (SelectedProfile is null || !Profiles.Contains(SelectedProfile))
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }

        await SaveAsync();
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
            var entries = await _subscriptions.FetchAsync(subscription.Url);
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

    private int ReplaceSubscriptionProfiles(VpnSubscription subscription, IReadOnlyList<SubscriptionEntry> entries)
    {
        var selectedName = SelectedProfile?.Name;
        var owned = Profiles.Where(profile => profile.SubscriptionId == subscription.Id).ToList();

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

            var profile = new VpnProfile
            {
                Name = entry.Name,
                Format = format,
                ConfigText = entry.Config,
                SubscriptionId = subscription.Id
            };

            Profiles.Add(profile);
            added++;
        }

        if (SelectedProfile is null || !Profiles.Contains(SelectedProfile))
        {
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Name == selectedName) ?? Profiles.FirstOrDefault();
        }

        return added;
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
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(OnTunnelChanged);
            return;
        }

        foreach (var property in new[]
                 {
                     nameof(State), nameof(StateWord), nameof(StatusDetail), nameof(IsConnected),
                     nameof(IsTransitioning), nameof(IsReconnecting), nameof(IsKillSwitchHolding),
                     nameof(CanConnect), nameof(UptimeText), nameof(LatencyText), nameof(ThroughputText),
                     nameof(ExitIpText), nameof(CoreVersionText), nameof(KillSwitchStateText), nameof(AppStateWord)
                 })
        {
            Raise(property);
        }

        ConnectCommand.RaiseCanExecuteChanged();
    }

    private void OnLogEntry(LogEntry entry)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnLogEntry(entry));
            return;
        }

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
                     nameof(CloseToTray), nameof(StartWithWindows), nameof(ShowFirstRun), nameof(AppSummary),
                     nameof(StateWord), nameof(StatusDetail)
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
