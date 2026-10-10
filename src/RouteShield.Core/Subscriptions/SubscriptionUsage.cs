using System.Globalization;

namespace RouteShield.Subscriptions;

/// <summary>
/// What a provider reports about the account behind a subscription, from the
/// <c>subscription-userinfo</c> response header every panel sends —
/// <c>upload=455727941; download=6174315083; total=1073741824000; expire=1671815872</c>.
/// Byte counts, and a Unix time for the expiry; 0 or a missing field means "not stated".
/// </summary>
public sealed record SubscriptionUsage(long? UploadBytes, long? DownloadBytes, long? TotalBytes, DateTimeOffset? ExpiresAt)
{
    public const string HeaderName = "subscription-userinfo";

    public long? UsedBytes => UploadBytes is null && DownloadBytes is null ? null : (UploadBytes ?? 0) + (DownloadBytes ?? 0);

    public long? RemainingBytes => TotalBytes is > 0 && UsedBytes is { } used ? Math.Max(0, TotalBytes.Value - used) : null;

    public static SubscriptionUsage? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0)
            {
                values[pair[0]] = number;
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        long? Field(string key) => values.TryGetValue(key, out var value) ? value : null;

        var expire = values.TryGetValue("expire", out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253402300799))
            : (DateTimeOffset?)null;

        return new SubscriptionUsage(Field("upload"), Field("download"), values.TryGetValue("total", out var total) && total > 0 ? total : null, expire);
    }

    /// <summary>"7.75 GB"; binary units, as panels count them.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }
}

/// <summary>A fetched subscription: its entries, and what the provider said about the account.</summary>
public sealed record SubscriptionFetch(IReadOnlyList<SubscriptionEntry> Entries, SubscriptionUsage? Usage);
