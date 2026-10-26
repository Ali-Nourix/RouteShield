using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RouteShield.Tunnels;

namespace RouteShield.Subscriptions;

public sealed record BackupProfile(Guid? SubscriptionId, string Name, string Config, bool Favorite, bool BrowserPinned);
public sealed record BackupSubscription(Guid Id, string Name, string Url);
public sealed record LibraryBackupData(int Version, BackupProfile[] Profiles, BackupSubscription[] Subscriptions,
    string[] DirectDomains, string[] ProxyDomains, string[] BlockedDomains);

/// <summary>Password-encrypted portable library; does not export autostart, firewall or app settings.</summary>
public static class LibraryBackup
{
    public const int MaxBytes = 12 * 1024 * 1024;
    private const int Iterations = 210_000;
    private const string AssociatedData = "RouteShield library backup v1";
    private sealed record Envelope(int Version, string Salt, string Nonce, string Ciphertext, string Tag);

    public static byte[] Export(AppSettings settings, string password)
    {
        if (password.Length < 12) throw new ArgumentException("Use a backup password of at least 12 characters.");
        var document = new LibraryBackupData(1,
            settings.Profiles.Where(p => !p.IsAutomatic && p.SubscriptionId != FreeConfigService.PoolId)
                .Select(p => new BackupProfile(p.SubscriptionId, p.Name, p.ConfigText, p.IsFavorite, p.BrowserPinned)).ToArray(),
            settings.Subscriptions.Where(s => s.Id != FreeConfigService.PoolId)
                .Select(s => new BackupSubscription(s.Id, s.Name, s.Url)).ToArray(),
            [.. settings.DirectDomains], [.. settings.ProxyDomains], [.. settings.BlockedDomains]);
        Validate(document);
        var clear = JsonSerializer.SerializeToUtf8Bytes(document);
        if (clear.Length > MaxBytes / 2) { CryptographicOperations.ZeroMemory(clear); throw new InvalidOperationException("The library exceeds the backup size limit."); }
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var ciphertext = new byte[clear.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, clear, ciphertext, tag, Encoding.UTF8.GetBytes(AssociatedData));
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    public static LibraryBackupData Read(byte[] encrypted, string password)
    {
        if (encrypted.Length > MaxBytes) throw new FormatException("The backup is too large.");
        var envelope = JsonSerializer.Deserialize<Envelope>(encrypted) ?? throw new FormatException("The backup is empty.");
        if (envelope.Version != 1) throw new FormatException("This backup version is not supported.");
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16) throw new FormatException("Invalid backup encryption fields.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var clear = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, clear, Encoding.UTF8.GetBytes(AssociatedData));
            var data = JsonSerializer.Deserialize<LibraryBackupData>(clear) ?? throw new FormatException("Invalid library backup.");
            Validate(data);
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    private static void Validate(LibraryBackupData data)
    {
        if (data.Version != 1 || data.Profiles is null || data.Subscriptions is null || data.DirectDomains is null
            || data.ProxyDomains is null || data.BlockedDomains is null || data.Profiles.Length > 10_000 || data.Subscriptions.Length > 1000)
            throw new FormatException("Invalid or unsupported library backup.");
        if (data.Subscriptions.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Url)) ||
            data.Subscriptions.Select(s => s.Id).Distinct().Count() != data.Subscriptions.Length)
            throw new FormatException("Duplicate or invalid subscription identifiers in backup.");
        var ids = data.Subscriptions.Select(s => s.Id).ToHashSet();
        foreach (var sub in data.Subscriptions) _ = SubscriptionLibrary.Upsert(new List<VpnSubscription>(), sub.Name, sub.Url);
        foreach (var profile in data.Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Config) || profile.SubscriptionId is { } id && !ids.Contains(id))
                throw new FormatException("A backup profile refers to an unknown subscription.");
            _ = TunnelParser.Parse(profile.Config);
        }
        _ = DomainPolicy.Parse(string.Join('\n', data.DirectDomains));
        _ = DomainPolicy.Parse(string.Join('\n', data.ProxyDomains));
        _ = DomainPolicy.Parse(string.Join('\n', data.BlockedDomains));
    }

    /// <summary>Merge only after full validation; do not replace the user's current library.</summary>
    public static int Merge(LibraryBackupData data, ICollection<VpnProfile> profiles, ICollection<VpnSubscription> subscriptions)
    {
        Validate(data);
        var map = new Dictionary<Guid, Guid>();
        foreach (var item in data.Subscriptions)
        {
            // Keep the existing provider's name if its URL already exists.
            var current = subscriptions.FirstOrDefault(s => s.Url == item.Url);
            current ??= SubscriptionLibrary.Upsert(subscriptions, item.Name, item.Url);
            map[item.Id] = current.Id;
        }
        var added = 0;
        foreach (var item in data.Profiles)
        {
            Guid? owner = item.SubscriptionId is { } id ? map[id] : null;
            var current = profiles.FirstOrDefault(p => p.SubscriptionId == owner && p.ConfigText == item.Config);
            if (current is not null) { current.IsFavorite |= item.Favorite; current.BrowserPinned |= item.BrowserPinned; continue; }
            var parsed = TunnelParser.Parse(item.Config);
            profiles.Add(new VpnProfile { Name = item.Name, Format = parsed.FormatName, ConfigText = item.Config,
                SubscriptionId = owner, IsFavorite = item.Favorite, BrowserPinned = item.BrowserPinned });
            added++;
        }
        return added;
    }
}
