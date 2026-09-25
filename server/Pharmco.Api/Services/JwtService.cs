namespace Pharmco.Api.Services;

using System.Security.Cryptography;
using System.Text;
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
/// Hand-rolled HS256 JWT signer/verifier. Kept dependency-free on purpose:
///   * tokens are verified here (session endpoint, tests) AND by the
///     JwtBearer filter (Program.cs wires the same HMAC key into it);
///   * claim set is flat and fully controlled by us.
/// </summary>
public sealed class JwtService
{
    private const string B64_CHARS =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const string HEADER = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";

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
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new StringBuilder();
        payload.Append("{\"iss\":\"").Append(_config.Issuer).Append("\"");
        payload.Append(",\"aud\":\"").Append(_config.Audience).Append("\"");
        payload.Append(",\"sub\":\"").Append(user.Id).Append("\"");
        payload.Append(",\"user_id\":\"").Append(user.Id).Append("\"");
        payload.Append(",\"tenant_id\":\"").Append(tenant.Id).Append("\"");
        payload.Append(",\"tenant_code\":\"").Append(tenant.Code).Append("\"");
        payload.Append(",\"role\":\"").Append(user.Role).Append("\"");
        if (tenant.LicenseExpiresAt is not null)
            payload.Append(",\"license_expires_at\":").Append(tenant.LicenseExpiresAt.Value.ToUnixTimeSeconds());
        else
            payload.Append(",\"license_expires_at\":null");
        payload.Append(",\"iat\":").Append(now);
        payload.Append(",\"nbf\":").Append(now);
        payload.Append(",\"exp\":").Append(now + _config.AccessTtlSeconds);
        payload.Append(",\"jti\":\"").Append(Guid.NewGuid()).Append("\"}");

        var signingInput = B64UrlEncode(Encoding.UTF8.GetBytes(HEADER)) + "." +
                           B64UrlEncode(Encoding.UTF8.GetBytes(payload.ToString()));
        return signingInput + "." + B64UrlEncode(HmacSha256(Encoding.UTF8.GetBytes(signingInput)));
    }

    // --- verification ------------------------------------------------------------

    /// <summary>Throws <c>JwtException</c> on any structural, signature, or lifetime failure.</summary>
    public JwtClaims VerifyAccessToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new JwtException("missing token");
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new JwtException("malformed token");

        var signingInput = parts[0] + "." + parts[1];
        var expectedSig = HmacSha256(Encoding.UTF8.GetBytes(signingInput));
        var actualSig = B64UrlDecode(parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
            throw new JwtException("invalid signature");

        var payloadJson = Encoding.UTF8.GetString(B64UrlDecode(parts[1]));
        var claims = ScanFlatObject(payloadJson);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claimsOut = new JwtClaims();
        bool hasIssuer = false, hasAudience = false, hasExp = false, hasSub = false;
        long expEpoch = 0;

        foreach (var pair in claims)
        {
            switch (pair.Key)
            {
                case "iss":
                    hasIssuer = pair.IsString && pair.Value.Equals(_config.Issuer, StringComparison.Ordinal);
                    break;
                case "aud":
                    hasAudience = pair.IsString && pair.Value.Equals(_config.Audience, StringComparison.Ordinal);
                    break;
                case "sub":
                    hasSub = pair.IsString;
                    if (pair.IsString) claimsOut.UserId = Guid.Parse(pair.Value);
                    break;
                case "user_id":
                    if (pair.IsString) claimsOut.UserId = Guid.Parse(pair.Value);
                    break;
                case "tenant_id":
                    if (pair.IsString) claimsOut.TenantId = Guid.Parse(pair.Value);
                    break;
                case "tenant_code":
                    if (pair.IsString) claimsOut.TenantCode = pair.Value;
                    break;
                case "role":
                    if (pair.IsString) claimsOut.Role = pair.Value;
                    break;
                case "license_expires_at":
                    if (!pair.IsString && long.TryParse(pair.Value, out var licEpoch))
                        claimsOut.LicenseExpiresAtEpoch = licEpoch;
                    break;
                case "exp":
                    if (!pair.IsString && long.TryParse(pair.Value, out expEpoch))
                        hasExp = expEpoch > now;
                    break;
                case "iat":
                    if (!pair.IsString && long.TryParse(pair.Value, out var iatEpoch))
                        claimsOut.IssuedAt = DateTimeOffset.FromUnixTimeSeconds(iatEpoch);
                    break;
            }
        }

        if (!hasIssuer) throw new JwtException("invalid issuer");
        if (!hasAudience) throw new JwtException("invalid audience");
        if (!hasExp) throw new JwtException("token expired or missing exp");
        if (!hasSub || claimsOut.TenantId == Guid.Empty) throw new JwtException("missing subject claims");
        claimsOut.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expEpoch);
        return claimsOut;
    }

    // --- refresh tokens -----------------------------------------------------------

    /// <summary>64 random bytes, base64url — the opaque refresh token handed to the client.</summary>
    public static string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        RandomNumberGenerator.Fill(bytes);
        return B64UrlEncode(bytes);
    }

    /// <summary>SHA-256 hex of the raw refresh token — what master.refresh_tokens stores.</summary>
    public static string HashToken(string raw)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(digest.Length * 2);
        foreach (var b in digest)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // --- internals -----------------------------------------------------------------

    private sealed class KeyValue
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsString { get; set; }
    }

    /// <summary>
    /// Minimal flat-object scanner for OUR claim payload (string / number / null
    /// values only). Throws JwtException on anything that isn't a flat object.
    /// </summary>
    private static List<KeyValue> ScanFlatObject(string text)
    {
        var result = new List<KeyValue>();
        var i = SkipWs(text, 0);
        if (i >= text.Length || text[i] != '{')
            throw new JwtException("payload not an object");
        i = SkipWs(text, i + 1);
        while (i < text.Length)
        {
            if (text[i] == '}') break;
            if (text[i] == ',') { i = SkipWs(text, i + 1); continue; }
            if (text[i] != '"') throw new JwtException("bad claim key");
            var keyEnd = text.IndexOf('"', i + 1);
            if (keyEnd < 0) throw new JwtException("unterminated claim key");
            var key = text.Substring(i + 1, keyEnd - i - 1);

            i = SkipWs(text, keyEnd + 1);
            if (i >= text.Length || text[i] != ':') throw new JwtException("missing colon");
            i = SkipWs(text, i + 1);

            var pair = new KeyValue { Key = key };
            if (i < text.Length && text[i] == '"')
            {
                var valEnd = text.IndexOf('"', i + 1);
                if (valEnd < 0) throw new JwtException("unterminated claim value");
                pair.Value = text.Substring(i + 1, valEnd - i - 1);
                pair.IsString = true;
                i = valEnd + 1;
            }
            else
            {
                var j = i;
                while (j < text.Length && text[j] != ',' && text[j] != '}') j++;
                pair.Value = text.Substring(i, j - i).Trim();
                i = j;
            }
            result.Add(pair);
        }
        return result;
    }

    private static int SkipWs(string text, int from)
    {
        var i = from;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r'))
            i++;
        return i;
    }

    private byte[] HmacSha256(byte[] input)
    {
        using var mac = new HMACSHA256(_secret);
        return mac.ComputeHash(input);
    }

    private static string B64UrlEncode(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 3)
        {
            var n = (data[i] & 0xFF) << 16;
            int count = 1;
            if (i + 1 < data.Length) { n |= (data[i + 1] & 0xFF) << 8; count = 2; }
            if (i + 2 < data.Length) { n |= data[i + 2] & 0xFF; count = 3; }
            sb.Append(B64_CHARS[(n >> 18) & 0x3F]);
            sb.Append(B64_CHARS[(n >> 12) & 0x3F]);
            if (count >= 2) sb.Append(B64_CHARS[(n >> 6) & 0x3F]);
            if (count == 3) sb.Append(B64_CHARS[n & 0x3F]);
        }
        return sb.ToString();
    }

    private static byte[] B64UrlDecode(string input)
    {
        var clean = input.Replace('-', '+').Replace('_', '/');
        var pad = (4 - clean.Length % 4) % 4;
        clean += new string('=', pad);
        return Convert.FromBase64String(clean);
    }
}