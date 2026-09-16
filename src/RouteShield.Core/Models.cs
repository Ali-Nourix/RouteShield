using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RouteShield;

public enum RouteMode
{
    SelectedAppsOnly,
    AllExceptSelected,
    FullTunnel
}

public enum TunnelState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Failed
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        OnPropertyChanged(propertyName);
    }

    /// <summary>Hook for properties whose value is derived from the one that just changed.</summary>
    protected virtual void OnPropertyChanged(string? propertyName)
    {
    }
}

public sealed class VpnProfile : Observable
{
    private string _name = "New profile";
    private string _format = "Unknown";
    private string _configText = string.Empty;
    private bool _browserPinned;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SubscriptionId { get; set; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Format
    {
        get => _format;
        set => Set(ref _format, value);
    }

    /// <summary>DPAPI-sealed <see cref="ConfigText"/>; this is the only form that reaches disk.</summary>
    public string EncryptedConfig { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Offered to the browser extension as a per-tab choice while the tunnel is up.</summary>
    public bool BrowserPinned
    {
        get => _browserPinned;
        set => Set(ref _browserPinned, value);
    }

    [JsonIgnore]
    public string ConfigText
    {
        get => _configText;
        set => Set(ref _configText, value);
    }
}

public sealed class AppTarget : Observable
{
    private string _displayName = string.Empty;
    private string _path = string.Empty;
    private bool _isRunning;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string Path
    {
        get => _path;
        set => Set(ref _path, value);
    }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    [JsonIgnore]
    public string StateLabel => IsRunning ? "RUNNING" : "CLOSED";

    protected override void OnPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(IsRunning))
        {
            Raise(nameof(StateLabel));
        }
    }
}

public sealed class VpnSubscription : Observable
{
    private string _name = "Subscription";
    private string _url = string.Empty;
    private string _lastError = string.Empty;
    private DateTimeOffset? _lastUpdated;
    private int _lastCount;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>DPAPI-sealed <see cref="Url"/>; subscription URLs usually embed an account token.</summary>
    public string EncryptedUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    public DateTimeOffset? LastUpdated
    {
        get => _lastUpdated;
        set => Set(ref _lastUpdated, value);
    }

    public int LastCount
    {
        get => _lastCount;
        set => Set(ref _lastCount, value);
    }

    public string LastError
    {
        get => _lastError;
        set => Set(ref _lastError, value);
    }

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LastError))
            {
                return $"Failed · {LastError}";
            }

            return LastUpdated is null
                ? "Never updated"
                : $"{LastCount} nodes · updated {Humanize(DateTimeOffset.Now - LastUpdated.Value)}";
        }
    }

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    protected override void OnPropertyChanged(string? propertyName)
    {
        if (propertyName is not (nameof(StatusText) or nameof(HasError)))
        {
            Raise(nameof(StatusText));
            Raise(nameof(HasError));
        }
    }

    private static string Humanize(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} min ago",
        { TotalHours: < 24 } => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago"
    };
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<VpnProfile> Profiles { get; set; } = [];

    public List<VpnSubscription> Subscriptions { get; set; } = [];

    public List<AppTarget> Apps { get; set; } = [];

    public Guid? SelectedProfileId { get; set; }

    public RouteMode RouteMode { get; set; } = RouteMode.SelectedAppsOnly;

    public bool AppKillSwitch { get; set; } = true;

    public bool DnsProtection { get; set; } = true;

    public bool Ipv6Protection { get; set; } = true;

    public bool AutoReconnect { get; set; } = true;

    public bool AllowLan { get; set; }

    public bool StartWithWindows { get; set; }

    public bool AutoConnect { get; set; }

    public bool CloseToTray { get; set; } = true;

    public bool BrowserBridgeEnabled { get; set; } = true;

    /// <summary>Where the browser extension finds the running app. Fixed so the extension needs no pairing step.</summary>
    public int BrowserBridgePort { get; set; } = 47831;

    public bool FirstRun { get; set; } = true;
}
