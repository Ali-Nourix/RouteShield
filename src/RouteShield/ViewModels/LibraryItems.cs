using RouteShield.Services;

namespace RouteShield.ViewModels;

/// <summary>One heading in the library: the manual profiles, or one subscription and what it delivered.</summary>
public sealed class LibrarySection
{
    public const string ManualTitle = "Manual profiles";

    private LibrarySection(string title, VpnSubscription? subscription)
    {
        Title = title;
        Subscription = subscription;
    }

    public static LibrarySection Manual { get; } = new(ManualTitle, null);

    public static LibrarySection For(VpnSubscription subscription) => new(subscription.Name, subscription);

    public string Title { get; }

    public VpnSubscription? Subscription { get; }

    public bool IsSubscription => Subscription is not null;

    /// <summary>Manual profiles come first; subscriptions follow in name order.</summary>
    public int Order => IsSubscription ? 1 : 0;
}

public enum LatencyState
{
    Untested,
    Testing,
    Measured,
    Failed
}

/// <summary>A profile as the library shows it: the profile itself, where it came from, and how it last measured.</summary>
public sealed class ProfileItem : Observable
{
    private const int FailedSortKey = 1_000_000;
    private const int UntestedSortKey = 2_000_000;

    private LatencyState _latencyState;
    private int? _latencyMilliseconds;
    private string? _failure;

    public ProfileItem(VpnProfile profile, LibrarySection section)
    {
        Profile = profile;
        Section = section;
    }

    public VpnProfile Profile { get; }

    public LibrarySection Section { get; }

    public LatencyState LatencyState
    {
        get => _latencyState;
        private set => Set(ref _latencyState, value);
    }

    public int? LatencyMilliseconds
    {
        get => _latencyMilliseconds;
        private set => Set(ref _latencyMilliseconds, value);
    }

    public string LatencyText => LatencyState switch
    {
        LatencyState.Testing => "TESTING",
        LatencyState.Measured => $"{LatencyMilliseconds} MS",
        LatencyState.Failed => (_failure ?? "FAILED").ToUpperInvariant(),
        _ => string.Empty
    };

    public bool HasLatency => LatencyState != LatencyState.Untested;

    /// <summary>Measured nodes first and fastest on top; failures after them; untested last.</summary>
    public int SortKey => LatencyState switch
    {
        LatencyState.Measured => LatencyMilliseconds ?? FailedSortKey,
        LatencyState.Failed => FailedSortKey,
        _ => UntestedSortKey
    };

    public void BeginTest()
    {
        LatencyState = LatencyState.Testing;
        _failure = null;
        RaiseLatency();
    }

    public void Apply(LatencyResult result)
    {
        LatencyMilliseconds = result.DelayMilliseconds;
        _failure = result.Failure;
        LatencyState = result.DelayMilliseconds is null ? LatencyState.Failed : LatencyState.Measured;
        RaiseLatency();
    }

    public void Restore(LatencyState state, int? milliseconds, string? failure)
    {
        LatencyState = state == LatencyState.Testing ? LatencyState.Untested : state;
        LatencyMilliseconds = milliseconds;
        _failure = failure;
        RaiseLatency();
    }

    public (LatencyState State, int? Milliseconds, string? Failure) Snapshot() => (LatencyState, LatencyMilliseconds, _failure);

    private void RaiseLatency()
    {
        Raise(nameof(LatencyText));
        Raise(nameof(HasLatency));
        Raise(nameof(SortKey));
    }
}
