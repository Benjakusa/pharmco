using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pharmco.Core.Licensing;

public sealed class LicenseException : Exception
{
    public LicenseException(string message) : base(message) { }
}

/// <summary>
/// RSA-2048 signed license tokens: base64url(header).base64url(payload).base64url(sig).
/// Header: {alg:RS256, typ:JWT, kid:pharmco-v1}. Payload: <see cref="LicenseClaims"/>.
/// Keys: PKCS#8/PKCS#1 PEM. The private key lives on the Pharmco server; the
/// public key is embedded in the desktop client for offline verification.
/// </summary>
public sealed class LicenseService
{
    public const string ClaimTenantId = "tenant_id";
    private const string HeaderJson = "{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"pharmco-v1\"}";

    private const int KeySizeBits = 2048;
    private readonly RSA _key;

    public LicenseService(RSA key)
        => _key = key ?? throw new ArgumentNullException(nameof(key));

    public static RSA LoadPrivateKey(string pem)
    {
        var rsa = RSA.Create(KeySizeBits);
        rsa.ImportFromPem(pem);
        return rsa;
    }

    public static RSA LoadPublicKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }

    public string Sign(LicenseClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        var signingInput = $"{header}.{payload}";
        var signature = _key.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    /// <summary>Verifies signature and expiry against a public key.</summary>
    public LicenseClaims Verify(string token, RSA publicKey)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new LicenseException("malformed license token");

        using var headerDoc = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        if (!headerDoc.RootElement.TryGetProperty("alg", out var alg) || alg.GetString() != "RS256")
            throw new LicenseException("unsupported license header");

        var signed = $"{parts[0]}.{parts[1]}";
        var valid = publicKey.VerifyData(
            Encoding.UTF8.GetBytes(signed),
            Base64UrlDecode(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!valid)
            throw new LicenseException("license signature invalid");

        var claims = JsonSerializer.Deserialize<LicenseClaims>(Base64UrlDecode(parts[1]))
            ?? throw new LicenseException("license payload unreadable");

        if (claims.ExpiresAt < DateTimeOffset.UtcNow)
            throw new LicenseException("license expired");

        return claims;
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        b64 = (input.Length % 4) switch
        {
            2 => b64 + "==",
            3 => b64 + "=",
            _ => b64,
        };
        return Convert.FromBase64String(b64);
    }
}