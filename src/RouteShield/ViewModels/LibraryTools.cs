using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using RouteShield.Services;
using RouteShield.Subscriptions;
using RouteShield.Tunnels;

namespace RouteShield.ViewModels;

public sealed partial class ShellViewModel
{
    private string _libraryQuery = "";
    private string _protocolFilter = "All";
    private string _countryFilter = "All";
    private bool _favoritesOnly;
    private bool _filterRefreshing;
    private bool _workingOnly;
    private bool _quickConnecting;
    private bool _lastProbeCancelled;
    private bool _autoRefreshing;
    private bool _shuttingDown;
    private readonly SemaphoreSlim _subscriptionRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _libraryShutdown = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastAutoAttempts = [];
    private DateTimeOffset _nextRefreshCheck = DateTimeOffset.MinValue;
    private VpnProfile? _globalAutomatic;
    private string _directDomainText = "";
    private string _proxyDomainText = "";
    private string _blockedDomainText = "";

    public AsyncRelayCommand ImportClipboardCommand { get; private set; } = null!;
    public AsyncRelayCommand CopyProfileCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportProfileCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveDuplicatesCommand { get; private set; } = null!;
    public AsyncRelayCommand TestSelectedCommand { get; private set; } = null!;
    public AsyncRelayCommand QuickConnectCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportBackupCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreBackupCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyDomainsCommand { get; private set; } = null!;
    public RelayCommand ResetLibraryFiltersCommand { get; private set; } = null!;
    public ObservableCollection<string> ProtocolOptions { get; } = ["All"];
    public ObservableCollection<string> CountryOptions { get; } = ["All"];
    public IReadOnlyList<int> RefreshIntervalOptions { get; } = [0, 15, 30, 60, 180, 360, 720, 1440];

    private void InitializeLibraryTools()
    {
        ImportClipboardCommand = new AsyncRelayCommand(ImportClipboardAsync, () => !IsTestingLatency);
        CopyProfileCommand = new AsyncRelayCommand(CopyProfileAsync, () => CanEditSelectedProfile);
        ExportProfileCommand = new AsyncRelayCommand(ExportProfileAsync, () => CanEditSelectedProfile);
        RemoveDuplicatesCommand = new AsyncRelayCommand(RemoveDuplicatesAsync, () => !IsConnected && !IsTransitioning && !IsTestingLatency);
        TestSelectedCommand = new AsyncRelayCommand(() => TestItemsAsync(Library.Where(item => item.Profile == SelectedProfile && !item.IsAutomatic).ToList()),
            () => CanEditSelectedProfile && !IsTestingLatency);
        QuickConnectCommand = new AsyncRelayCommand(QuickConnectAsync,
            () => Profiles.Count > 0 && !_quickConnecting && !_isFreeSearching && !IsTestingLatency && State is TunnelState.Disconnected or TunnelState.Failed);
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync, () => !IsTestingLatency);
        ApplyDomainsCommand = new AsyncRelayCommand(ApplyDomainsAsync);
        ResetLibraryFiltersCommand = new RelayCommand(() =>
        {
            LibraryQuery = ""; ProtocolFilter = "All"; CountryFilter = "All"; FavoritesOnly = false; WorkingOnly = false;
        });
        LibraryView.Filter = item => item is ProfileItem row && MatchesLibraryFilter(row);
    }

    public string LibraryQuery { get => _libraryQuery; set { if (Set(ref _libraryQuery, value)) RefreshLibraryFilter(); } }
    public string ProtocolFilter { get => _protocolFilter; set { if (Set(ref _protocolFilter, value ?? "All")) RefreshLibraryFilter(); } }
    public string CountryFilter { get => _countryFilter; set { if (Set(ref _countryFilter, value ?? "All")) RefreshLibraryFilter(); } }
    public bool FavoritesOnly { get => _favoritesOnly; set { if (Set(ref _favoritesOnly, value)) RefreshLibraryFilter(); } }
    public bool WorkingOnly { get => _workingOnly; set { if (Set(ref _workingOnly, value)) RefreshLibraryFilter(); } }
    public bool SelectedProfileFavorite
    {
        get => SelectedProfile?.IsFavorite ?? false;
        set
        {
            if (SelectedProfile is null || SelectedProfile.IsAutomatic) return;
            SelectedProfile.IsFavorite = value;
            Raise(nameof(SelectedProfileFavorite));
            RefreshLibraryFilter();
            QueueSave();
        }
    }
    public string FilterSummary => $"{Library.Count(MatchesLibraryFilter)} of {Library.Count} entries shown";
    public bool NoFilterMatches => Library.Count > 0 && !Library.Any(MatchesLibraryFilter);
    private bool MatchesLibraryFilter(ProfileItem row) =>
        ProfileTools.Matches(row.Profile, LibraryQuery, ProtocolFilter, CountryFilter, FavoritesOnly)
        && (!WorkingOnly || row.LatencyState == LatencyState.Measured && row.LatencyMilliseconds is >= 0);
    private void RefreshLibraryFilter()
    {
        if (_rebuildingLibrary) return;
        _filterRefreshing = true;
        try { LibraryView.Refresh(); }
        finally { _filterRefreshing = false; }
        Raise(nameof(FilterSummary)); Raise(nameof(NoFilterMatches));
    }
    private void RefreshLibraryTools()
    {
        var protocol = ProtocolFilter;
        var country = CountryFilter;
        ProtocolOptions.Clear(); ProtocolOptions.Add("All");
        foreach (var item in Profiles.Select(p => p.Format).Distinct().Order()) ProtocolOptions.Add(item);
        CountryOptions.Clear(); CountryOptions.Add("All");
        foreach (var item in Profiles.Select(p => ProfileTools.ClaimedCountry(p.Name)).Distinct().Order()) CountryOptions.Add(item);
        ProtocolFilter = ProtocolOptions.Contains(protocol) ? protocol : "All";
        CountryFilter = CountryOptions.Contains(country) ? country : "All";
        RefreshLibraryFilter(); RaiseLibraryCommands();
    }
    private void RaiseLibraryCommands()
    {
        ImportClipboardCommand?.RaiseCanExecuteChanged(); RestoreBackupCommand?.RaiseCanExecuteChanged();
        Raise(nameof(CanEditSelectedProfile)); NewProfileCommand.RaiseCanExecuteChanged();
        AddSubscriptionCommand.RaiseCanExecuteChanged(); EditSubscriptionCommand.RaiseCanExecuteChanged();
        RefreshSubscriptionCommand.RaiseCanExecuteChanged(); RefreshSubscriptionsCommand.RaiseCanExecuteChanged();
        ImportProfileCommand.RaiseCanExecuteChanged(); SaveProfileCommand.RaiseCanExecuteChanged(); DeleteProfileCommand.RaiseCanExecuteChanged();
        CopyProfileCommand?.RaiseCanExecuteChanged(); ExportProfileCommand?.RaiseCanExecuteChanged();
        RemoveDuplicatesCommand?.RaiseCanExecuteChanged(); TestSelectedCommand?.RaiseCanExecuteChanged();
        QuickConnectCommand?.RaiseCanExecuteChanged(); Raise(nameof(SelectedProfileFavorite));
    }

    private async Task ImportClipboardAsync()
    {
        try
        {
            if (!Clipboard.ContainsText()) { await _host.AlertAsync("Import", "The clipboard has no text", "Copy share links or a Base64 subscription body first."); return; }
            var text = Clipboard.GetText();
            if (text.Length > 5 * 1024 * 1024) throw new FormatException("Clipboard import is limited to 5 MB.");
            // Clipboard import is configuration text only. URLs needing a download go through Add subscription.
            var import = SubscriptionImport.From(SubscriptionParser.Parse(text));
            if (import.Nodes.Count == 0) throw new FormatException("No supported server was found. To download a subscription URL, use Add subscription.");
            if (!await _host.ConfirmAsync("Clipboard import", $"Import {import.Nodes.Count} server configurations?",
                "Only the text currently on the clipboard is read. No subscription URL is fetched. Exact manual duplicates will be skipped.", "Import")) return;
            var seen = Profiles.Where(p => p.SubscriptionId is null).Select(p => p.ConfigText).ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var node in import.Nodes)
            {
                if (!seen.Add(node.Config)) continue;
                Profiles.Add(new VpnProfile { Name = node.Name, Format = node.Format, ConfigText = node.Config });
                added++;
            }
            RebuildLibrary(); await SaveAsync();
            await _host.AlertAsync("Clipboard import", $"Added {added} profiles", $"Skipped {import.Unreadable} unreadable entries; provider notes were not imported as servers.");
        }
        catch (Exception e) when (IsExpected(e) || e is System.Runtime.InteropServices.ExternalException or JsonException)
        { await _host.AlertAsync("Import", "Clipboard import failed", e.Message); }
    }

    private async Task CopyProfileAsync()
    {
        var profile = SelectedProfile;
        if (profile is null || profile.IsAutomatic) return;
        if (!await _host.ConfirmAsync("Share configuration", "Copy this profile, including its credentials?",
            "Other apps, clipboard managers and clipboard sync may read the contents. Share it only with someone you trust.", "Copy")) return;
        try { Clipboard.SetText(profile.ConfigText); }
        catch (System.Runtime.InteropServices.ExternalException) { await _host.AlertAsync("Clipboard", "The clipboard is busy", "Try copying again."); }
    }
    private async Task ExportProfileAsync()
    {
        var profile = SelectedProfile;
        if (profile is null || profile.IsAutomatic) return;
        if (!await _host.ConfirmAsync("Plaintext export", "Export this profile without encryption?",
            "The file contains server credentials. Use encrypted library backup for routine backups.", "Export")) return;
        var dialog = new SaveFileDialog { Title = "Export selected configuration", Filter = "Configuration text|*.txt", FileName = "RouteShield-profile.txt" };
        if (dialog.ShowDialog() != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, profile.ConfigText); }
        catch (Exception e) when (IsExpected(e)) { await _host.AlertAsync("Export", "Could not export the profile", e.Message); }
    }
    private async Task RemoveDuplicatesAsync()
    {
        if (!await _host.ConfirmAsync("Remove duplicates", "Remove duplicate nodes within each provider?",
            "Separate subscriptions are never merged. Browser pins and favorites move to the retained node.", "Remove duplicates")) return;
        var removed = ProfileTools.RemoveDuplicates(Profiles);
        if (SelectedProfile is { } selected && removed.TryGetValue(selected.Id, out var retained)) SelectedProfile = Profiles.First(p => p.Id == retained);
        RebuildLibrary(); await SaveAsync();
        await _host.AlertAsync("Library", $"Removed {removed.Count} duplicates", "Private subscriptions and manual profiles remain separate.");
    }
    private async Task QuickConnectAsync()
    {
        if (!IsAdministrator || !CoreProcessService.IsCoreInstalled)
        {
            await _host.AlertAsync("Quick connect", "Administrator access and sing-box are required", "Run the packaged Windows app as administrator before testing and connecting.");
            return;
        }
        var filter = (LibraryQuery, ProtocolFilter, CountryFilter, FavoritesOnly, WorkingOnly);
        var matching = Library.Where(item => !item.IsAutomatic && MatchesLibraryFilter(item)).ToList();
        _quickConnecting = true; Raise(nameof(CanConnect)); ConnectCommand.RaiseCanExecuteChanged(); RaiseLibraryCommands();
        try
        {
            await TestItemsAsync(matching);
            if (_lastProbeCancelled) return;
            if (filter != (LibraryQuery, ProtocolFilter, CountryFilter, FavoritesOnly, WorkingOnly))
            {
                await _host.AlertAsync("Quick connect", "Filters changed during the test", "Run Quick connect again to test the new selection. No connection was started.");
                return;
            }
            var tested = matching.Select(item => item.Profile.Id).ToHashSet();
            var fastest = Library.Where(item => tested.Contains(item.Profile.Id) && !item.IsAutomatic && MatchesLibraryFilter(item) && item.LatencyState == LatencyState.Measured && item.LatencyMilliseconds is >= 0)
                .OrderBy(item => item.LatencyMilliseconds).FirstOrDefault();
            if (fastest is null) { await _host.AlertAsync("Quick connect", "No matching node passed the HTTPS test", "No untested fallback was connected. Clear filters or refresh your subscriptions and try again."); return; }
            SelectedProfile = fastest.Profile;
            _quickConnecting = false;
            await ConnectAsync();
        }
        finally { _quickConnecting = false; Raise(nameof(CanConnect)); ConnectCommand.RaiseCanExecuteChanged(); RaiseLibraryCommands(); }
    }

    public bool RefreshSubscriptionsOnStartup
    {
        get => _settings.RefreshSubscriptionsOnStartup;
        set => ApplySetting(s => s.RefreshSubscriptionsOnStartup = value, _settings.RefreshSubscriptionsOnStartup == value);
    }
    public int SubscriptionRefreshMinutes
    {
        get => _settings.SubscriptionRefreshMinutes;
        set
        {
            if (!RefreshIntervalOptions.Contains(value)) return;
            ApplySetting(s => s.SubscriptionRefreshMinutes = value, _settings.SubscriptionRefreshMinutes == value);
        }
    }
    private async Task RefreshDueSubscriptionsAsync(bool startup = false)
    {
        if (!_loaded || _shuttingDown || _autoRefreshing || IsTestingLatency || IsTransitioning) return;
        var now = DateTimeOffset.UtcNow;
        if (!startup && now < _nextRefreshCheck) return;
        _nextRefreshCheck = now.AddMinutes(1);
        foreach (var subscription in Subscriptions) subscription.RefreshTimeSensitiveState();
        var due = Subscriptions.Where(sub => sub.Id != FreeConfigService.PoolId &&
            (startup || SubscriptionSchedule.IsDue(sub, now, SubscriptionRefreshMinutes,
                _lastAutoAttempts.TryGetValue(sub.Id, out var last) ? last : null))).ToArray();
        if (due.Length == 0) return;
        _autoRefreshing = true;
        try
        {
            foreach (var sub in due)
            {
                if (_shuttingDown) return;
                _lastAutoAttempts[sub.Id] = DateTimeOffset.UtcNow;
                await RefreshSubscriptionAsync(sub);
            }
            RebuildLibrary(); await SaveAsync();
        }
        catch (Exception e) when (IsExpected(e) || e is JsonException)
        { AppLog.Write(LogCategory.Subscription, "Scheduled subscription refresh failed; saved profiles were retained."); }
        finally { _autoRefreshing = false; }
    }

    public string DirectDomainText { get => _directDomainText; set => Set(ref _directDomainText, value); }
    public string ProxyDomainText { get => _proxyDomainText; set => Set(ref _proxyDomainText, value); }
    public string BlockedDomainText { get => _blockedDomainText; set => Set(ref _blockedDomainText, value); }
    private async Task ApplyDomainsAsync()
    {
        try
        {
            var direct = DomainPolicy.Parse(DirectDomainText);
            var proxy = DomainPolicy.Parse(ProxyDomainText);
            var blocked = DomainPolicy.Parse(BlockedDomainText);
            _settings.DirectDomains = [.. direct]; _settings.ProxyDomains = [.. proxy]; _settings.BlockedDomains = [.. blocked];
            await SaveAsync();
            await _host.AlertAsync("Domain policy", "Domain rules saved", "Rules apply on the next connection. Priority: block, proxy, direct, then built-in policy. Browser-pinned routes and the tunnel health probe remain authoritative. Direct rules explicitly bypass the VPN; disconnect and reconnect when ready.");
        }
        catch (Exception e) when (IsExpected(e)) { await _host.AlertAsync("Domain policy", "The routing list was not saved", e.Message); }
    }
    private void LoadLibrarySettings()
    {
        DirectDomainText = string.Join('\n', _settings.DirectDomains ?? []);
        ProxyDomainText = string.Join('\n', _settings.ProxyDomains ?? []);
        BlockedDomainText = string.Join('\n', _settings.BlockedDomains ?? []);
        Raise(nameof(RefreshSubscriptionsOnStartup)); Raise(nameof(SubscriptionRefreshMinutes));
    }
    private async Task ExportBackupAsync()
    {
        var dialog = new SaveFileDialog { Title = "Encrypted library backup", Filter = "Encrypted RouteShield library|*.rsbackup", FileName = $"RouteShield-{DateTime.Now:yyyyMMdd}.rsbackup" };
        if (dialog.ShowDialog() != true || _host.BackupPassword(true) is not { } password) return;
        try
        {
            _settings.Profiles = [.. Profiles]; _settings.Subscriptions = [.. Subscriptions];
            var bytes = LibraryBackup.Export(_settings, password);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            await _host.AlertAsync("Backup", "Encrypted library exported", "Keep the password separately. This backup can be restored on another computer; the free pool and Windows security settings are not included.");
        }
        catch (Exception e) when (IsExpected(e) || e is CryptographicException or JsonException)
        { await _host.AlertAsync("Backup", "Backup could not be exported", e.Message); }
    }
    private async Task RestoreBackupAsync()
    {
        var dialog = new OpenFileDialog { Title = "Restore encrypted library", Filter = "Encrypted RouteShield library|*.rsbackup", CheckFileExists = true };
        if (dialog.ShowDialog() != true || _host.BackupPassword(false) is not { } password) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > LibraryBackup.MaxBytes) throw new FormatException("The backup is too large.");
            var data = LibraryBackup.Read(await File.ReadAllBytesAsync(dialog.FileName), password);
            if (!await _host.ConfirmAsync("Restore", $"Merge {data.Profiles.Length} saved profiles?", "Current profiles are not deleted. Restoring does not change Windows/firewall/autostart settings. Domain rules can be restored separately after reviewing them.", "Merge")) return;
            var count = LibraryBackup.Merge(data, Profiles, Subscriptions);
            RebuildLibrary(); await SaveAsync();
            DirectDomainText = string.Join('\n', data.DirectDomains); ProxyDomainText = string.Join('\n', data.ProxyDomains); BlockedDomainText = string.Join('\n', data.BlockedDomains);
            await _host.AlertAsync("Restore", $"Restored {count} new profiles", "Domain lists were loaded into the editor only. Review them and click Save domain rules to apply them for a future connection.");
        }
        catch (CryptographicException) { await _host.AlertAsync("Restore", "Wrong password or damaged backup", "The current library was not changed."); }
        catch (Exception e) when (IsExpected(e) || e is JsonException)
        { await _host.AlertAsync("Restore", "The backup could not be restored", "The file is invalid or unavailable. " + e.GetType().Name); }
    }
}
