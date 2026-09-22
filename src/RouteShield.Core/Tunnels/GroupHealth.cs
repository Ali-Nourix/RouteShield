namespace RouteShield.Tunnels;

/// <summary>How one member of an automatic group answered a test.</summary>
/// <param name="DelayMilliseconds">The round trip of the test request; null when it did not answer.</param>
/// <param name="Failure">"Timeout" or "Unreachable" when it did not answer.</param>
public sealed record MemberCheck(string Tag, string Name, int? DelayMilliseconds, string? Failure)
{
    public const string TimeoutFailure = "Timeout";
    public const string UnreachableFailure = "Unreachable";

    public bool Answered => DelayMilliseconds is not null;
}

/// <summary>
/// One test of every member of an automatic group, made right after the tunnel starts. It is
/// what tells "the tunnel is up" apart from "the tunnel is up and something behind it answers":
/// with no member answering, the core carries traffic over its first member regardless, and
/// the only other sign would be every page failing.
/// </summary>
public sealed record GroupHealth(IReadOnlyList<MemberCheck> Members)
{
    public int Answering => Members.Count(member => member.Answered);

    public bool NoneAnswer => Members.Count > 0 && Answering == 0;

    public MemberCheck? Fastest => Members.Where(member => member.Answered).MinBy(member => member.DelayMilliseconds);

    /// <summary>"3 of 23 nodes answer · fastest DE-1 at 180 ms", or how the silent ones failed when none does.</summary>
    public string Summary
    {
        get
        {
            if (Fastest is { } fastest)
            {
                return $"{Answering} of {Members.Count} nodes answer · fastest \"{fastest.Name}\" at {fastest.DelayMilliseconds} ms";
            }

            var timedOut = Members.Count(member => member.Failure == MemberCheck.TimeoutFailure);
            var refused = Members.Count - timedOut;
            var how = (timedOut, refused) switch
            {
                (> 0, 0) => "every one timed out",
                (0, > 0) => "every one was refused at once",
                _ => $"{timedOut} timed out, {refused} refused"
            };

            return $"None of {Members.Count} nodes answers · {how}";
        }
    }

    /// <summary>Every member and its result, for the log.</summary>
    public string Details => string.Join(
        "; ",
        Members.Select(member => $"\"{member.Name}\" {(member.DelayMilliseconds is { } delay ? $"{delay} ms" : (member.Failure ?? "failed").ToLowerInvariant())}"));
}
