using System.Text.RegularExpressions;

namespace RouteShield;

public enum LogCategory
{
    Core,
    Config,
    Firewall,
    Subscription,
    Network,
    Settings
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogCategory Category, string Message)
{
    public string Line => $"{Timestamp:HH:mm:ss.fff}  {Category.ToString().ToUpperInvariant(),-13}{Message}";
}

/// <summary>
/// Append-only session log. Everything written here is redacted first, so the buffer
/// shown in Diagnostics and the exported bundle can never carry profile secrets.
/// </summary>
public static partial class AppLog
{
    public const int BufferLimit = 2000;

    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> Buffer = new();

    public static event Action<LogEntry>? EntryWritten;

    public static void Write(LogCategory category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, category, Redact(Flatten(message)));

        lock (Gate)
        {
            Buffer.Enqueue(entry);
            while (Buffer.Count > BufferLimit)
            {
                Buffer.Dequeue();
            }

            TryAppendToFile(entry);
        }

        EntryWritten?.Invoke(entry);
    }

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate)
        {
            return Buffer.ToArray();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Buffer.Clear();
        }
    }

    /// <summary>
    /// Strips credentials so a line is safe to display, export, or paste into an issue.
    /// URL hosts survive because they are what makes a failure diagnosable; the path and
    /// query, which is where subscription tokens live, do not.
    /// </summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = SecretFieldPattern().Replace(value, "$1$2<redacted>");
        value = ShareLinkPattern().Replace(value, "$1://<redacted>");
        return UrlPathPattern().Replace(value, "$1/<redacted>");
    }

    private static string Flatten(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void TryAppendToFile(LogEntry entry)
    {
        try
        {
            AppPaths.Ensure();
            var stamp = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{entry.Category}] {entry.Message}";
            File.AppendAllText(AppPaths.AppLog, stamp + Environment.NewLine);
        }
        catch (IOException)
        {
            // The in-memory buffer is what the UI reads; a locked log file must not break logging.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"(?i)\b(private[_-]?key|pre[_-]?shared[_-]?key|public[_-]?key|password|uuid|token|secret|auth)(\s*[:=]\s*""?)[^,;""\s]+")]
    private static partial Regex SecretFieldPattern();

    [GeneratedRegex(@"(?i)\b(vless|vmess|trojan|ss|ssr|hysteria2?|hy2|tuic)://\S+")]
    private static partial Regex ShareLinkPattern();

    [GeneratedRegex(@"(?i)\b(https?://[^/\s""]+)/\S+")]
    private static partial Regex UrlPathPattern();
}
