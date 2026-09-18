using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pharmco.Core.Licensing;

namespace Pharmco.Client.Services;

/// <summary>
/// Validates RSA-signed license tokens from the server.
/// Uses embedded public key for offline verification.
/// </summary>
public sealed class LicenseValidator
{
    private readonly RSA _publicKey;
    private const string EmbeddedPublicKey = @"-----BEGIN PUBLIC KEY-----
MFwwDQYJKoZIhvcNAQEBBQADSwAwSAJBAN...PlACEHOLDER_KEEP_RSA_FORMAT...ewIDAQAB
-----END PUBLIC KEY-----";

    public LicenseValidator()
    {
        // In production, embed the real public key here
        // For now, create a test key that can be replaced
        _publicKey = RSA.Create(2048);
    }

    public LicenseValidator(RSA publicKey)
    {
        _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
    }

    /// <summary>
    /// Verifies a license token signature and returns the claims.
    /// </summary>
    public VerificationResult Verify(string licenseToken)
    {
        if (string.IsNullOrWhiteSpace(licenseToken))
        {
            return new VerificationResult
            {
                IsValid = false,
                Error = "License token is empty"
            };
        }

        try
        {
            var parts = licenseToken.Split('.');
            if (parts.Length != 3)
            {
                return new VerificationResult
                {
                    IsValid = false,
                    Error = "Malformed license token"
                };
            }

            // Verify signature
            var signed = $"{parts[0]}.{parts[1]}";
            var signature = Base64UrlDecode(parts[2]);

            var isValid = _publicKey.VerifyData(
                Encoding.UTF8.GetBytes(signed),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            if (!isValid)
            {
                return new VerificationResult
                {
                    IsValid = false,
                    Error = "License signature invalid — tampered or wrong key"
                };
            }

            // Parse claims
            var payloadJson = Base64UrlDecode(parts[1]);
            var claims = JsonSerializer.Deserialize<LicenseClaims>(payloadJson);

            if (claims == null)
            {
                return new VerificationResult
                {
                    IsValid = false,
                    Error = "License payload unreadable"
                };
            }

            // Check expiry
            var time = DateTimeOffset.UtcNow;
            var timeDiff = claims.ExpiresAt - time;

            return new VerificationResult
            {
                IsValid = true,
                Claims = claims,
                TimeToExpiry = timeDiff,
                IsExpired = timeDiff < TimeSpan.Zero,
                DaysToExpiry = (int)timeDiff.TotalDays
            };
        }
        catch (Exception ex)
        {
            return new VerificationResult
            {
                IsValid = false,
                Error = $"Verification error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Checks for clock tampering by comparing local time with cached sync time.
    /// If local time is before last_sync_at, it's likely tampered.
    /// </summary>
    public bool DetectClockTamper(DateTimeOffset lastSyncAt)
    {
        if (lastSyncAt == default)
            return false;

        var now = DateTimeOffset.UtcNow;
        // If current time is more than 5 minutes before last sync, flag it
        return now < lastSyncAt.AddMinutes(-5);
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        return Convert.FromBase64String(b64);
    }
}

public sealed class VerificationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public LicenseClaims? Claims { get; init; }
    public TimeSpan TimeToExpiry { get; init; }
    public bool IsExpired { get; init; }
    public int DaysToExpiry { get; init; }
}
