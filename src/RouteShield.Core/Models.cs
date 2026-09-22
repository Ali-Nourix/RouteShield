using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using RouteShield.Subscriptions;

namespace RouteShield;

public enum RouteMode
{
    SelectedAppsOnly,
    AllExceptSelected,
    FullTunnel
}

/// <summary>
/// Which network adapter the tunnel's own traffic leaves on.
///
/// This matters because another VPN — a corporate client such as Cisco AnyConnect, say —
/// takes over the default route while it is connected. The core then dials the proxy server
/// through that VPN, where a corporate firewall usually refuses it, and RouteShield reports
/// itself connected while nothing can actually reach the outside.
/// </summary>
public enum OutboundBinding
{
    /// <summary>Pick a physical adapter and ignore adapters belonging to other VPNs. The default.</summary>
    AvoidOtherVpns,

    /// <summary>Whatever Windows says the default route is, other VPNs included.</summary>
    FollowWindows,

    /// <summary>One named adapter, whatever else happens.</summary>
    Fixed
}

/// <summary>Which program carries the tunnel.</summary>
public enum TunnelEngine
{
    /// <summary>
    /// sing-box, except for a WireGuard profile that needs WireSock: an AmneziaWG profile, or
    /// any WireGuard profile while another VPN holds the default route.
    /// </summary>
    Automatic,

    /// <summary>sing-box for everything: every protocol, the browser bridge, secure DNS.</summary>
    SingBox,

    /// <summary>WireSock for WireGuard profiles; other profiles still use sing-box.</summary>
    WireSock
}

/// <summary>Which palette the window draws with; System follows the Windows app colour.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
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
    /// <summary>The format name of a synthesised profile that stands for a whole subscription.</summary>
    public const string AutomaticFormat = "Automatic";

    private string _name = "New profile";
    private string _format = "Unknown";
    private string _configText = string.Empty;
    private bool _browserPinned;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SubscriptionId { get; set; }

    /// <summary>
    /// True for the "fastest of this subscription" entry. It has no configuration of its own:
    /// the core is handed every node of the subscription and picks among them.
    /// </summary>
    [JsonIgnore]
    public bool IsAutomatic => Format == AutomaticFormat;

    /// <summary>
    /// Builds the automatic entry for a subscription. The id is derived from the subscription's,
    /// so the selection survives a restart although the entry itself is never saved.
    /// </summary>
    public static VpnProfile AutomaticFor(VpnSubscription subscription) => new()
    {
        Id = AutomaticId(subscription.Id),
        SubscriptionId = subscription.Id,
        Name = $"Fastest of {subscription.Name}",
        Format = AutomaticFormat
    };

    public static Guid AutomaticId(Guid subscriptionId)
    {
        var bytes = subscriptionId.ToByteArray();
        bytes[0] ^= 0xA5;
        bytes[15] ^= 0x5A;
        return new Guid(bytes);
    }

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
    private List<string> _notes = [];
    private long? _uploadBytes;
    private long? _downloadBytes;
    private long? _totalBytes;
    private DateTimeOffset? _expiresAt;

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

    /// <summary>
    /// What the provider wrote among the nodes instead of a server — "7.75 GB left",
    /// "Expires 2026-10-01" — kept as text rather than as profiles that point nowhere.
    /// </summary>
    public List<string> Notes
    {
        get => _notes;
        set => Set(ref _notes, value ?? []);
    }

    /// <summary>From the provider's <c>subscription-userinfo</c> header; null when it did not say.</summary>
    public long? UploadBytes
    {
        get => _uploadBytes;
        set => Set(ref _uploadBytes, value);
    }

    public long? DownloadBytes
    {
        get => _downloadBytes;
        set => Set(ref _downloadBytes, value);
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        set => Set(ref _totalBytes, value);
    }

    public DateTimeOffset? ExpiresAt
    {
        get => _expiresAt;
        set => Set(ref _expiresAt, value);
    }

    [JsonIgnore]
    public SubscriptionUsage? Usage => UploadBytes is null && DownloadBytes is null && TotalBytes is null && ExpiresAt is null
        ? null
        : new SubscriptionUsage(UploadBytes, DownloadBytes, TotalBytes, ExpiresAt);

    /// <summary>True when the provider says the account is used up or past its date, which no node can work around.</summary>
    [JsonIgnore]
    public bool IsExhausted => Usage is { } usage && (usage.RemainingBytes == 0 || usage.ExpiresAt <= DateTimeOffset.Now);

    public void ApplyUsage(SubscriptionUsage? usage)
    {
        UploadBytes = usage?.UploadBytes;
        DownloadBytes = usage?.DownloadBytes;
        TotalBytes = usage?.TotalBytes;
        ExpiresAt = usage?.ExpiresAt;
    }

    /// <summary>"7.75 GB left of 50 GB · expires in 12 d"; the provider's notes when it sent no header; empty when neither.</summary>
    [JsonIgnore]
    public string UsageText
    {
        get
        {
            if (Usage is not { } usage)
            {
                return string.Join(" · ", Notes.Take(2));
            }

            var parts = new List<string>();
            if (usage.RemainingBytes is { } remaining)
            {
                parts.Add(remaining == 0
                    ? "no traffic left"
                    : $"{SubscriptionUsage.FormatBytes(remaining)} left of {SubscriptionUsage.FormatBytes(usage.TotalBytes!.Value)}");
            }
            else if (usage.UsedBytes is { } used)
            {
                parts.Add($"{SubscriptionUsage.FormatBytes(used)} used");
            }

            if (usage.ExpiresAt is { } expires)
            {
                var left = expires - DateTimeOffset.Now;
                parts.Add(left <= TimeSpan.Zero ? "expired" : $"expires in {Humanize(left)}");
            }

            return string.Join(" · ", parts);
        }
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

            if (LastUpdated is null)
            {
                return "Never updated";
            }

            var age = DateTimeOffset.Now - LastUpdated.Value;
            var status = $"{LastCount} nodes · updated {(age.TotalMinutes < 1 ? "just now" : Humanize(age) + " ago")}";
            return UsageText is { Length: > 0 } usage ? $"{status} · {usage}" : status;
        }
    }

    /// <summary>The status with every note the provider sent, for where the one-line status is cut short.</summary>
    [JsonIgnore]
    public string StatusDetails => Notes.Count == 0
        ? StatusText
        : $"{StatusText}{Environment.NewLine}{Environment.NewLine}From the provider:{Environment.NewLine}{string.Join(Environment.NewLine, Notes)}";

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    private static readonly string[] DerivedProperties =
        [nameof(StatusText), nameof(StatusDetails), nameof(HasError), nameof(UsageText), nameof(Usage), nameof(IsExhausted)];

    protected override void OnPropertyChanged(string? propertyName)
    {
        if (!DerivedProperties.Contains(propertyName))
        {
            foreach (var derived in DerivedProperties)
            {
                Raise(derived);
            }
        }
    }

    private static string Humanize(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "under a minute",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min",
        { TotalHours: < 24 } => $"{(int)span.TotalHours} h",
        _ => $"{(int)span.TotalDays} d"
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

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Splits every TLS handshake to the proxy server across several TCP segments and TLS
    /// records, so a firewall that reads the server name from the first packet does not see
    /// it whole. Off by default: it costs a little on every new connection.
    /// </summary>
    public bool TlsFragment { get; set; }

    /// <summary>
    /// Refuses QUIC from routed applications so browsers fall back to HTTP/2 over TCP. On by
    /// default, and the single biggest difference to how a proxied browser feels.
    /// </summary>
    public bool BlockQuic { get; set; } = true;

    /// <summary>Keeps Iranian sites on the local connection instead of sending them abroad and back.</summary>
    public bool DirectDomesticSites { get; set; } = true;

    public OutboundBinding OutboundBinding { get; set; } = OutboundBinding.AvoidOtherVpns;

    public TunnelEngine Engine { get; set; } = TunnelEngine.Automatic;

    /// <summary>The adapter name used when <see cref="OutboundBinding"/> is Fixed; the Windows connection name.</summary>
    public string OutboundAdapter { get; set; } = string.Empty;
}
