using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteFlattener.Settings;

/// <summary>
/// Persists user settings to %LOCALAPPDATA%\RemoteFlattener\settings.json.
/// The password is encrypted at rest using DPAPI (Windows Data Protection API)
/// scoped to the current user, so only this Windows account can decrypt it.
/// </summary>
public static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(AppPaths.DataDirectory, "settings.json");

    public sealed class AppSettings
    {
        public string EncryptedPassword { get; set; } = string.Empty;
        public string Machines { get; set; } = string.Empty;
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json, Encoding.UTF8);
        }
        catch { /* don't let settings save failure crash the app */ }
    }

    /// <summary>Encrypts a plaintext password using DPAPI (current-user scope).</summary>
    public static string Encrypt(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Decrypts a DPAPI-encrypted password. Returns null if decryption fails.</summary>
    public static string? Decrypt(string base64Ciphertext)
    {
        try
        {
            var encrypted = Convert.FromBase64String(base64Ciphertext);
            var data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }
}
