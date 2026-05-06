using System;
using System.IO;
using RemoteFlattener.Settings;
using Xunit;

namespace RemoteFlattener.Tests.Settings;

public class SettingsStoreTests
{
    // ── Encrypt / Decrypt ─────────────────────────────────────────────────

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_NormalPassword()
    {
        const string plaintext = "my-secret-password-123!";
        var encrypted = SettingsStore.Encrypt(plaintext);
        var decrypted = SettingsStore.Decrypt(encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_EmptyPassword()
    {
        var encrypted = SettingsStore.Encrypt(string.Empty);
        var decrypted = SettingsStore.Decrypt(encrypted);
        Assert.Equal(string.Empty, decrypted);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_UnicodePassword()
    {
        const string plaintext = "pässwörð-Ω-🔒";
        var encrypted = SettingsStore.Encrypt(plaintext);
        var decrypted = SettingsStore.Decrypt(encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesBase64String()
    {
        var encrypted = SettingsStore.Encrypt("password");
        // Should not throw
        var bytes = Convert.FromBase64String(encrypted);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Encrypt_TwoCalls_ProduceDifferentCiphertext()
    {
        // DPAPI adds entropy; same plaintext must not produce deterministic output.
        var a = SettingsStore.Encrypt("password");
        var b = SettingsStore.Encrypt("password");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decrypt_InvalidBase64_ReturnsNull()
    {
        var result = SettingsStore.Decrypt("!!! not base64 at all !!!");
        Assert.Null(result);
    }

    [Fact]
    public void Decrypt_ValidBase64ButRandomBytes_ReturnsNull()
    {
        // Random bytes are not valid DPAPI data.
        var garbage = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var result  = SettingsStore.Decrypt(garbage);
        Assert.Null(result);
    }

    // ── Load / Save ───────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsDefaultSettings()
    {
        // SettingsStore always falls back gracefully; a missing file returns defaults.
        // We can't control the path, but we can verify Load() never throws.
        var settings = SettingsStore.Load();
        Assert.NotNull(settings);
    }

    [Fact]
    public void Save_Load_RoundTrip_Machines()
    {
        // NOTE: This test writes to the real settings file. It restores the original after.
        var original = SettingsStore.Load();
        try
        {
            var toSave = new SettingsStore.AppSettings { Machines = "PC1,PC2,PC3" };
            SettingsStore.Save(toSave);

            var loaded = SettingsStore.Load();
            Assert.Equal("PC1,PC2,PC3", loaded.Machines);
        }
        finally
        {
            // Restore original to avoid polluting real settings.
            SettingsStore.Save(original);
        }
    }
}
