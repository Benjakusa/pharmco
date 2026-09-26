namespace Pharmco.Api.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pharmco.Core;
using Pharmco.Core.Auth;
using Pharmco.Core.Tenants;

/// <summary>Tuning knobs — env-driven, defaults match the auth spec (15 min / 30 days).</summary>
public sealed class JwtConfig
{
    public string Issuer = "pharmco";
    public string Audience = "pharmco-client";
    public long AccessTtlSeconds = 15 * 60;              // access 15 min
    public long RefreshTtlSeconds = 30L * 24 * 60 * 60;  // refresh 30 days
}

public sealed class JwtException : Exception
{
    public JwtException(string message) : base(message) { }
}

/// <summary>Claims extracted from a validated access token.</summary>
public sealed class JwtClaims
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string TenantCode { get; set; } = "";
    public string Role { get; set; } = "";
    public long LicenseExpiresAtEpoch { get; set; } = 0;   // 0 = no license claim
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Hand-rolled HS256 JWT signer/verifier (README phase-1 note: "a small
/// hand-rolled or library helper"). Kept dependency-free on purpose:
///   * tokens are verified here (session endpoint, tests) AND by the
///     JwtBearer filter (Program.cs wires the same HMAC key into it);
///   * claim set is flat and fully controlled by us.
/// </summary>
public sealed class JwtService
{
    private const string HeaderJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";

    // 48 random bytes → 64 base64url characters (no padding). The refresh token
    // therefore has 384 bits of entropy while staying a fixed 64-char string.
    private const int RefreshTokenBytes = 48;

    private readonly byte[] _secret;
    private readonly JwtConfig _config;

    public JwtService(string secret, JwtConfig config)
    {
        _secret = Encoding.UTF8.GetBytes(secret);
        _config = config;
    }

    // --- issuing ----------------------------------------------------------------

    public long AccessTtlSeconds() => _config.AccessTtlSeconds;
    public long RefreshTtlSeconds() => _config.RefreshTtlSeconds;

    public string IssueAccessToken(User user, Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = _config.Issuer,
            ["aud"] = _config.Audience,
            ["sub"] = user.Id.ToString(),
            ["user_id"] = user.Id.ToString(),
            ["tenant_id"] = tenant.Id.ToString(),
            ["tenant_code"] = tenant.Code,
            ["role"] = user.Role,
            ["license_expires_at"] = tenant.LicenseExpiresAt?.ToUnixTimeSeconds(),
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + _config.AccessTtlSeconds,
            ["jti"] = Guid.NewGuid().ToString(),
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        var signingInput = B64UrlEncode(Encoding.UTF8.GetBytes(HeaderJson)) + "." + B64UrlEncode(payload);
        return signingInput + "." + B64UrlEncode(HmacSha256(Encoding.UTF8.GetBytes(signingInput)));
    }

    // --- verification ------------------------------------------------------------

    /// <summary>Throws <c>JwtException</c> on any structural, signature, or lifetime failure.</summary>
    public JwtClaims VerifyAccessToken(string token)
    {
        if (token.IsEmpty())
            throw new JwtException("missing token");

        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new JwtException("malformed token");

        var signingInput = parts[0] + "." + parts[1];
        var expected = HmacSha256(Encoding.UTF8.GetBytes(signingInput));

        byte[] actual;
        try { actual = B64UrlDecode(parts[2]); }
        catch (FormatException) { throw new JwtException("malformed signature"); }

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new JwtException("invalid signature");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(B64UrlDecode(parts[1]));
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { throw new JwtException("payload not an object"); }

        if (root.ValueKind != JsonValueKind.Object)
            throw new JwtException("payload not an object");

        if (ReadString(root, "iss") != _config.Issuer)
            throw new JwtException("invalid issuer");
        if (ReadString(root, "aud") != _config.Audience)
            throw new JwtException("invalid audience");

        if (!root.TryGetProperty("exp", out var expElement) || expElement.ValueKind != JsonValueKind.Number)
            throw new JwtException("token expired or missing exp");
        var expEpoch = expElement.GetInt64();
        if (expEpoch <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new JwtException("token expired or missing exp");

        var claims = new JwtClaims();
        if (Guid.TryParse(ReadString(root, "user_id") ?? ReadString(root, "sub"), out var userId))
            claims.UserId = userId;
        if (Guid.TryParse(ReadString(root, "tenant_id"), out var tenantId))
            claims.TenantId = tenantId;
        claims.TenantCode = ReadString(root, "tenant_code") ?? "";
        claims.Role = ReadString(root, "role") ?? "";

        if (root.TryGetProperty("license_expires_at", out var licenseElement)
            && licenseElement.ValueKind == JsonValueKind.Number)
            claims.LicenseExpiresAtEpoch = licenseElement.GetInt64();

        if (root.TryGetProperty("iat", out var iatElement) && iatElement.ValueKind == JsonValueKind.Number)
            claims.IssuedAt = DateTimeOffset.FromUnixTimeSeconds(iatElement.GetInt64());

        if (claims.UserId == Guid.Empty || claims.TenantId == Guid.Empty)
            throw new JwtException("missing subject claims");

        claims.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expEpoch);
        return claims;
    }

    // --- refresh tokens -----------------------------------------------------------

    /// <summary>48 random bytes, base64url — the opaque 64-char refresh token handed to the client.</summary>
    public static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenBytes);
        return B64UrlEncode(bytes);
    }

    /// <summary>Lowercase SHA-256 hex of the raw refresh token — what master.refresh_tokens stores.</summary>
    public static string HashToken(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    // --- internals -----------------------------------------------------------------

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private byte[] HmacSha256(byte[] input)
    {
        using var mac = new HMACSHA256(_secret);
        return mac.ComputeHash(input);
    }

    private static string B64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] B64UrlDecode(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        b64 = (b64.Length % 4) switch
        {
            2 => b64 + "==",
            3 => b64 + "=",
            _ => b64,
        };
        return Convert.FromBase64String(b64);
    }
}

