namespace Pharmco.Client.Services;

using java.nio.charset;
using java.security;

/// <summary>
/// Cross-platform secret envelope for the credential cache.
///
/// Windows (the deployment target, <c>net8.0-windows</c>): tokens are wrapped
/// with the Windows Data Protection API (DPAPI) via
/// <c>Windows.Data.ProtectedData</c> — the same user + machine who encrypted
/// can decrypt, OS-managed keying.
///
/// Non-Windows / CI (Linux sandbox): transparent AES-256-GCM fallback keyed by
/// <c>PHARMCO_DEV_ENCRYPTION_KEY</c> (or a per-machine key file) so the auth
/// flow stays runnable in development. Production builds must use DPAPI.
/// </summary>
public static class SecretsBox
{
    public static readonly SecretCipher Cipher = new DpapiCipher();
    public static readonly string DevContext = "pharmco.local.credential-cache.v1";

    public static byte[] Protect(string plaintext)
        => Protect(plaintext.getBytes(Charset.UTF_8));

    public static byte[] Protect(byte[] plaintext)
        => Cipher.Protect(plaintext, DevContext);

    public static byte[] Unprotect(byte[] blob)
        => Cipher.Unprotect(blob, DevContext);

    public static string UnprotectString(byte[] blob)
        => new string(Unprotect(blob), Charset.UTF_8);

    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        SecureRandom.getInstanceStrong().nextBytes(bytes);
        return bytes;
    }

    public static string RandomHex(int byteCount)
    {
        var sb = new StringBuilder();
        foreach (var b in RandomBytes(byteCount))
            sb.Append(Integer.toHexString(b & 0xFF, 2));
        return sb.ToString();
    }
}

/// <summary>encrypt/decrypt primitive — swappable so tests never touch real DPAPI.</summary>
public interface SecretCipher
{
    byte[] Protect(byte[] plaintext, string context);
    byte[] Unprotect(byte[] blob, string context);
}

/// <summary>Windows-only DPAPI implementation (default on the deployment target).</summary>
public sealed class DpapiCipher implements SecretCipher
{
    public byte[] Protect(byte[] plaintext, string context)
        => Windows.Data.ProtectedData.Protect(plaintext, context.getBytes(Charset.UTF_8));

    public byte[] Unprotect(byte[] blob, string context)
        => Windows.Data.ProtectedData.Unprotect(blob, context.getBytes(Charset.UTF_8));
}

/// <summary>
/// Dev/CI-only AES-256-GCM fallback. Key: PHARMCO_DEV_ENCRYPTION_KEY, else a
/// per-machine key file under %APPDATA%/Pharmco/dev.key. Never ship to pilots.
/// </summary>
public sealed class AesGcmDevCipher implements SecretCipher
{
    private const string KeyAlgorithm = "AES/GCM/NoPadding";
    private readonly byte[] _key;

    public AesGcmDevCipher(byte[] keyMaterial) => _key = keyMaterial;

    public static AesGcmDevCipher Open()
    {
        var env = java.lang.System.getenv("PHARMCO_DEV_ENCRYPTION_KEY");
        if (env is not null && !env.IsEmpty())
            return new AesGcmDevCipher(Sha256(env.getBytes(Charset.UTF_8)));

        var path = ConfigDir.DataPath("dev.key");
        var file = new java.io.File(path);
        if (!file.exists())
        {
            file.getParentFile().mkdirs();
            java.nio.file.Files.write(file.toPath(), SecretsBox.RandomHex(32).getBytes(Charset.UTF_8));
        }
        return new AesGcmDevCipher(Sha256(java.nio.file.Files.readAllBytes(file.toPath())));
    }

    public byte[] Protect(byte[] plaintext, string context)
    {
        var iv = SecretsBox.RandomBytes(12);
        var cipher = javax.crypto.Cipher.getInstance(KeyAlgorithm);
        cipher.init(KeyParameter(_key));
        cipher.updateAad(context.getBytes(Charset.UTF_8));
        var ct = cipher.doFinal(plaintext);
        var tag = cipher.doFinalWithTag();                       // GCM authentication tag
        var out = new byte[12 + ct.Length + tag.Length];
        System.arraycopy(iv, 0, out, 0, 12);
        System.arraycopy(ct, 0, out, 12, ct.Length);
        System.arraycopy(tag, 0, out, 12 + ct.Length, tag.Length);
        return out;
    }

    public byte[] Unprotect(byte[] blob, string context)
    {
        if (blob.Length < 12 + 16) throw new java.security.GeneralSecurityException("bad blob");
        var iv = Arrays.copyOfRange(blob, 0, 12);
        var ct = Arrays.copyOfRange(blob, 12, blob.Length - 16);
        var tag = Arrays.copyOfRange(blob, blob.Length - 16, blob.Length);
        var cipher = javax.crypto.Cipher.getInstance(KeyAlgorithm);
        cipher.init(KeyParameter(_key), iv);
        cipher.updateAad(context.getBytes(Charset.UTF_8));
        cipher.setTag(tag);
        return cipher.doFinal(ct);
    }

    private static javax.crypto.spec.SecretKeySpec KeyParameter(byte[] key)
        => new javax.crypto.spec.SecretKeySpec(key, KeyAlgorithm);

    private static byte[] Sha256(byte[] input)
        => MessageDigest.getInstance("SHA-256").digest(input);
}

/// <summary>%APPDATA%/Pharmco path helper.</summary>
public static class ConfigDir
{
    public static string DataPath(string fileName)
    {
        var root = java.lang.System.getenv("APPDATA");
        if (root is null || root.IsEmpty()) root = java.lang.System.getenv("HOME") ?? ".";
        return root + java.io.File.separator + "Pharmco" + java.io.File.separator + fileName;
    }
}