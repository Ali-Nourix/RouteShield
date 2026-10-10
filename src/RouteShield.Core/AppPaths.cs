namespace RouteShield;

/// <summary>Every location RouteShield reads from or writes to at runtime.</summary>
public static class AppPaths
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteShield");

    public static readonly string SettingsFile = Path.Combine(Root, "settings.json");
    public static readonly string RuntimeConfig = Path.Combine(Root, "runtime.json");
    public static readonly string ProbeConfig = Path.Combine(Root, "latency-probe.json");
    public static readonly string WireSockConfig = Path.Combine(Root, "wiresock.conf");
    public static readonly string CacheFile = Path.Combine(Root, "cache.db");
    public static readonly string Logs = Path.Combine(Root, "logs");
    public static readonly string CoreLog = Path.Combine(Logs, "sing-box.log");
    public static readonly string AppLog = Path.Combine(Logs, "app.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
