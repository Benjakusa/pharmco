namespace Pharmco.Client.Services;

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Cross-platform secret envelope for the credential cache.
///
/// Windows (the deployment target, <c>net8.0-windows</c>): tokens are wrapped
/// with the Windows Data Protection API (DPAPI, <c>CryptProtectData</c>) —
/// the same user + machine who encrypted can decrypt, OS-managed keying.
///
/// Non-Windows / CI (Linux sandbox): transparent AES-256-GCM fallback keyed by
/// <c>PHARMCO_DEV_ENCRYPTION_KEY</c> (or a per-machine key file) so the auth
/// flow stays runnable in development. Production builds must use DPAPI.
/// </summary>
public static class SecretsBox
{
    private static SecretCipher? _cipher;

    public static readonly string DevContext = "pharmco.local.credential-cache.v1";

    /// <summary>OS-selected primitive (lazy so a bad dev key file fails on first use, not at type load).</summary>
    public static SecretCipher Cipher
        => _cipher ??= OperatingSystem.IsWindows()
            ? new DpapiCipher()
            : AesGcmDevCipher.Open();

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
    {
        var sb = new StringBuilder(byteCount * 2);
        foreach (var b in RandomBytes(byteCount))
            sb.Append(b.ToString("x2"));
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
public sealed class DpapiCipher : SecretCipher
{
    public byte[] Protect(byte[] plaintext, string context)
    {
        RequireWindows();
        return Transform(plaintext, Encoding.UTF8.GetBytes(context), protect: true);
    }

    public byte[] Unprotect(byte[] blob, string context)
    {
        RequireWindows();
        return Transform(blob, Encoding.UTF8.GetBytes(context), protect: false);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is Windows-only; use AesGcmDevCipher elsewhere");
    }

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        var inputBlob = ToBlob(input);
        var entropyBlob = ToBlob(entropy);
        var outBlob = default(DataBlob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob);
            if (!ok)
                throw new CryptographicException("DPAPI operation failed (win32 error " + Marshal.GetLastWin32Error() + ")");

            var result = new byte[outBlob.CbData];
            Marshal.Copy(outBlob.PbData, result, 0, outBlob.CbData);
            return result;
        }
        finally
        {
            Free(ref inputBlob);
            Free(ref entropyBlob);
            Free(ref outBlob);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { CbData = data.Length, PbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.PbData, data.Length);
        return blob;
    }

    private static void Free(ref DataBlob blob)
    {
        if (blob.PbData != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.PbData);
        blob.PbData = IntPtr.Zero;
        blob.CbData = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);
}


/// <summary>
/// Dev/CI-only AES-256-GCM fallback. Key: PHARMCO_DEV_ENCRYPTION_KEY, else a
/// per-machine key file under %APPDATA%/Pharmco/dev.key. Never ship to pilots.
/// Blob layout: 12-byte IV ‖ ciphertext ‖ 16-byte tag, AAD = context.
/// </summary>
public sealed class AesGcmDevCipher : SecretCipher
{
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
        return new AesGcmDevCipher(SHA256.HashData(File.ReadAllBytes(path)));
    }

    public byte[] Protect(byte[] plaintext, string context)
    {
        var iv = SecretsBox.RandomBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16))
            aes.Encrypt(iv, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));

        var blob = new byte[12 + ciphertext.Length + tag.Length];
        iv.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, 12);
        tag.CopyTo(blob, 12 + ciphertext.Length);
        return blob;
    }

    public byte[] Unprotect(byte[] blob, string context)
    {
        if (blob.Length < 12 + 16)
            throw new CryptographicException("bad blob");

        var iv = blob.AsSpan(0, 12);
        var ciphertext = blob.AsSpan(12, blob.Length - 12 - 16);
        var tag = blob.AsSpan(blob.Length - 16, 16);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(_key, 16))
            aes.Decrypt(iv, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(context));
        return plaintext;
    }
}

/// <summary>%APPDATA%/Pharmco path helper.</summary>
public static class ConfigDir
{
    public static string DataPath(string fileName)
    {
        var root = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(root))
            root = Environment.GetEnvironmentVariable("HOME") ?? ".";
        return Path.Combine(root, "Pharmco", fileName);
    }
}

