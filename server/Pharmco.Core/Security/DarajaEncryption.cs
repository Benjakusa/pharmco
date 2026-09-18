namespace Pharmco.Core.Security;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// AES-256-GCM encryption for Daraja credentials.
/// Format stored in DB: [12-byte IV | ciphertext | 16-byte auth tag]
/// Key is read from Daraja__EncryptionKey env (base64-encoded 32 bytes).
/// Key rotation: when the env var changes, new encryptions use the new key;
/// old values can still be decrypted if we track key versions — for simplicity
/// this implementation uses a single active key. For production key rotation,
/// extend with a key version prefix in the stored bytea.
/// </summary>
public sealed class DarajaEncryption
{
    private const int AesKeySizeBytes = 32;     // 256 bits
    private const int AesNonceSizeBytes = 12;   // 96 bits (GCM standard)
    private const int AesTagSizeBytes = 16;     // 128 bits

    private readonly byte[] _key;

    public DarajaEncryption(string base64Key)
    {
        _key = DecodeKey(base64Key);
    }

    public DarajaEncryption(byte[] key)
    {
        if (key.Length != AesKeySizeBytes)
            throw new ArgumentException($"Key must be {AesKeySizeBytes} bytes", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <summary>Encrypt plaintext → [IV | ciphertext | tag] as bytea-friendly byte[].</summary>
    public byte[] Encrypt(string plainText)
    {
        if (plainText is null) throw new ArgumentNullException(nameof(plainText));

        var nonce = new byte[AesNonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[AesTagSizeBytes];

        using var aes = new AesGcm(_key, AesTagSizeBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Concatenate: IV (12) + ciphertext (N) + tag (16)
        var result = new byte[AesNonceSizeBytes + cipherBytes.Length + AesTagSizeBytes];
        Buffer.BlockCopy(nonce, 0, result, 0, AesNonceSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, result, AesNonceSizeBytes, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, result, AesNonceSizeBytes + cipherBytes.Length, AesTagSizeBytes);
        return result;
    }

    /// <summary>Decrypt [IV | ciphertext | tag] → plaintext string.</summary>
    public string Decrypt(byte[] encrypted)
    {
        if (encrypted is null || encrypted.Length < AesNonceSizeBytes + AesTagSizeBytes)
            throw new ArgumentException("Invalid encrypted data: too short", nameof(encrypted));

        var nonce = new byte[AesNonceSizeBytes];
        Buffer.BlockCopy(encrypted, 0, nonce, 0, AesNonceSizeBytes);

        var cipherLength = encrypted.Length - AesNonceSizeBytes - AesTagSizeBytes;
        if (cipherLength <= 0)
            throw new ArgumentException("Invalid encrypted data: no ciphertext", nameof(encrypted));

        var cipherBytes = new byte[cipherLength];
        Buffer.BlockCopy(encrypted, AesNonceSizeBytes, cipherBytes, 0, cipherLength);

        var tag = new byte[AesTagSizeBytes];
        Buffer.BlockCopy(encrypted, AesNonceSizeBytes + cipherLength, tag, 0, AesTagSizeBytes);

        var plainBytes = new byte[cipherLength];
        using var aes = new AesGcm(_key, AesTagSizeBytes);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>Mask a shortcode for display: "****1234".</summary>
    public static string MaskShortcode(string shortcode)
    {
        if (string.IsNullOrWhiteSpace(shortcode))
            return "****";

        var maskLen = Math.Min(4, shortcode.Length);
        var visible = shortcode[^maskLen..];
        var masked = new string('*', Math.Max(0, shortcode.Length - maskLen));
        return masked + visible;
    }

    private static byte[] DecodeKey(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new InvalidOperationException("Daraja__EncryptionKey environment variable is not set");

        try
        {
            var key = Convert.FromBase64String(base64Key);
            if (key.Length != AesKeySizeBytes)
                throw new InvalidOperationException(
                    $"Daraja__EncryptionKey must be base64-encoded {AesKeySizeBytes}-byte key (got {key.Length} bytes)");
            return key;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Daraja__EncryptionKey is not valid base64. Generate with: " +
                "openssl rand -base64 32");
        }
    }

    /// <summary>Generate a new random encryption key (for initial setup).</summary>
    public static string GenerateNewKey()
    {
        var key = new byte[AesKeySizeBytes];
        RandomNumberGenerator.Fill(key);
        return Convert.ToBase64String(key);
    }
}
