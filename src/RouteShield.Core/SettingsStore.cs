using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteShield;

/// <summary>Reads and writes settings.json, sealing every secret on the way out and unsealing it on the way in.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<AppSettings> LoadAsync()
    {
        AppPaths.Ensure();

        if (!File.Exists(AppPaths.SettingsFile))
        {
            return new AppSettings();
        }

        AppSettings settings;
        try
        {
            var json = await File.ReadAllTextAsync(AppPaths.SettingsFile, Encoding.UTF8);
            settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            QuarantineBrokenFile();
            AppLog.Write(LogCategory.Settings, $"settings.json could not be read ({exception.Message}); starting from defaults");
            return new AppSettings();
        }

        Unseal(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings)
    {
        AppPaths.Ensure();

        foreach (var profile in settings.Profiles)
        {
            profile.EncryptedConfig = Secrets.Protect(profile.ConfigText);
        }

        foreach (var subscription in settings.Subscriptions)
        {
            subscription.EncryptedUrl = Secrets.Protect(subscription.Url);
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        var temporary = AppPaths.SettingsFile + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
        File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
    }

    private static void Unseal(AppSettings settings)
    {
        foreach (var profile in settings.Profiles)
        {
            try
            {
                profile.ConfigText = Secrets.Unprotect(profile.EncryptedConfig);
            }
            catch (Exception exception) when (exception is FormatException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                profile.ConfigText = string.Empty;
                AppLog.Write(LogCategory.Settings, $"Profile \"{profile.Name}\" could not be decrypted: {exception.Message}");
            }
        }

        foreach (var subscription in settings.Subscriptions)
        {
            try
            {
                subscription.Url = Secrets.Unprotect(subscription.EncryptedUrl);
            }
            catch (Exception exception) when (exception is FormatException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                subscription.Url = string.Empty;
                AppLog.Write(LogCategory.Settings, $"Subscription \"{subscription.Name}\" could not be decrypted: {exception.Message}");
            }
        }
    }

    private static void QuarantineBrokenFile()
    {
        try
        {
            var backup = $"{AppPaths.SettingsFile}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(AppPaths.SettingsFile, backup, overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
