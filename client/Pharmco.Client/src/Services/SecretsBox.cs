namespace Pharmco.Client.Services;

using System.IO;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Cross-platform secret envelope for the credential cache.
///
/// Windows (the deployment target, net8.0-windows): tokens are wrapped
/// with the Windows Data Protection API (DPAPI) via ProtectedData — the
/// same user + machine who encrypted can decrypt, OS-managed keying.
///
/// Non-Windows / CI: transparent AES-256-GCM fallback keyed by
/// PHARMCO_DEV_ENCRYPTION_KEY (or a per-machine key file) so the auth
/// flow stays runnable in development. Production builds must use DPAPI.
/// </summary>
public static class SecretsBox
{
    public static readonly SecretCipher Cipher = new DpapiCipher();
    public static readonly string DevContext = "pharmco.local.credential-cache.v1";

    public static byte[] Protect(string plaintext)
        => Protect(Encoding.UTF8.GetBytes(plaintext));

    public static byte[] Protect(byte[] plaintext)
        => Cipher.Protect(plaintext, DevContext);

    public static byte[] Unprotect(byte[] blob)
        => Cipher.Unprotect(blob, DevContext);

    public static string UnprotectString(byte[] blob)
        => Encoding.UTF8.GetString(Unprotect(blob));

    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public static string RandomHex(int byteCount)
        => Convert.ToHexString(RandomBytes(byteCount)).ToLowerInvariant();
}

/// <summary>encrypt/decrypt primitive — swappable so tests never touch real DPAPI.</summary>
public interface SecretCipher
{
    byte[] Protect(byte[] plaintext, string context);
    byte[] Unprotect(byte[] blob, string context);
}

/// <summary>Windows-only DPAPI implementation (default on the deployment target).</summary>
public sealed class DpapiCipher : SecretCipher
{
    public byte[] Protect(byte[] plaintext, string context)
        => ProtectedData.Protect(plaintext, Encoding.UTF8.GetBytes(context), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] blob, string context)
        => ProtectedData.Unprotect(blob, Encoding.UTF8.GetBytes(context), DataProtectionScope.CurrentUser);
}

/// <summary>
/// Dev/CI-only AES-256-GCM fallback. Key: PHARMCO_DEV_ENCRYPTION_KEY, else a
/// per-machine key file under %APPDATA%/Pharmco/dev.key. Never ship to pilots.
/// Blob layout: 12-byte nonce | ciphertext | 16-byte GCM tag.
/// </summary>
public sealed class AesGcmDevCipher : SecretCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmDevCipher(byte[] keyMaterial) => _key = keyMaterial;

    public static AesGcmDevCipher Open()
    {
        var env = Environment.GetEnvironmentVariable("PHARMCO_DEV_ENCRYPTION_KEY");
        if (!string.IsNullOrEmpty(env))
            return new AesGcmDevCipher(SHA256.HashData(Encoding.UTF8.GetBytes(env)));

        var path = ConfigDir.DataPath("dev.key");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, SecretsBox.RandomHex(32));
        }
        return new AesGcmDevCipher(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path))));
    }

    public byte[] Protect(byte[] plaintext, string context)
    {
        var nonce = SecretsBox.RandomBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(context));

        var output = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, output, NonceSize + cipher.Length, TagSize);
        return output;
    }

    public byte[] Unprotect(byte[] blob, string context)
    {
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("bad blob");

        var nonce = blob[..NonceSize];
        var tag = blob[^TagSize..];
        var cipher = blob[NonceSize..^TagSize];
        var plaintext = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plaintext, Encoding.UTF8.GetBytes(context));
        return plaintext;
    }
}

/// <summary>%APPDATA%/Pharmco path helper.</summary>
public static class ConfigDir
{
    public static string DataPath(string fileName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, "Pharmco", fileName);
    }
}
