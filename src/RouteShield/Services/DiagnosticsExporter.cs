using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace RouteShield.Services;

/// <summary>
/// Packs the session logs for a bug report. Everything written into the bundle has already
/// been through <see cref="AppLog.Redact"/>, so profiles and subscription URLs stay behind.
/// </summary>
public static class DiagnosticsExporter
{
    public static string Export()
    {
        AppPaths.Ensure();

        var staging = Path.Combine(AppPaths.Root, "diagnostics");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);

        File.WriteAllText(Path.Combine(staging, "system.txt"), DescribeEnvironment(), new UTF8Encoding(false));

        foreach (var source in new[] { AppPaths.AppLog, AppPaths.CoreLog })
        {
            if (File.Exists(source))
            {
                File.WriteAllText(
                    Path.Combine(staging, Path.GetFileName(source)),
                    AppLog.Redact(File.ReadAllText(source)),
                    new UTF8Encoding(false));
            }
        }

        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"RouteShield-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        ZipFile.CreateFromDirectory(staging, target, CompressionLevel.Optimal, includeBaseDirectory: false);
        Directory.Delete(staging, recursive: true);

        AppLog.Write(LogCategory.Settings, "Diagnostics bundle exported to the desktop");
        return target;
    }

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.0";

    private static string DescribeEnvironment() => string.Join(
        Environment.NewLine,
        $"RouteShield: {AppVersion}",
        $"OS: {Environment.OSVersion}",
        $"Runtime: {Environment.Version}",
        $"Processors: {Environment.ProcessorCount}",
        $"Elevated: {Elevation.IsAdministrator}",
        $"Captured: {DateTimeOffset.Now:O}");
}
