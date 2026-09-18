namespace Pharmco.Api.Services;

using Pharmco.Core.Auth;
using Pharmco.Core.Tenants;
using javax.crypto;
using java.security;
using java.nio.charset;

/// <summary>Tuning knobs — env-driven, defaults match the auth spec (15 min / 30 days).</summary>
public sealed class JwtConfig
{
    public string Issuer = "pharmco";
    public string Audience = "pharmco-client";
    public long AccessTtlSeconds = 15 * 60;              // access 15 min
    public long RefreshTtlSeconds = 30L * 24 * 60 * 60;  // refresh 30 days
}

public sealed class JwtException extends Exception
{
    public JwtException(string message) { super(message); }
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
    private const string B64_CHARS =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const string HEADER = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";

    private readonly byte[] _secret;
    private readonly JwtConfig _config;

    public JwtService(string secret, JwtConfig config)
    {
        _secret = ToUtf8(secret);
        _config = config;
    }

    // --- issuing ----------------------------------------------------------------

    public long AccessTtlSeconds() => _config.AccessTtlSeconds;
    public long RefreshTtlSeconds() => _config.RefreshTtlSeconds;

    public string IssueAccessToken(User user, Tenant tenant)
    {
        var now = DateTimeOffset.now().toEpochSecond();
        var payload = new StringBuilder();
        payload.Append("{\"iss\":\"").Append(_config.Issuer).Append("\"");
        payload.Append(",\"aud\":\"").Append(_config.Audience).Append("\"");
        payload.Append(",\"sub\":\"").Append(user.Id.ToString()).Append("\"");
        payload.Append(",\"user_id\":\"").Append(user.Id.ToString()).Append("\"");
        payload.Append(",\"tenant_id\":\"").Append(tenant.Id.ToString()).Append("\"");
        payload.Append(",\"tenant_code\":\"").Append(tenant.Code).Append("\"");
        payload.Append(",\"role\":\"").Append(user.Role).Append("\"");
        if (tenant.LicenseExpiresAt is not null)
            payload.Append(",\"license_expires_at\":").Append(tenant.LicenseExpiresAt.toEpochSecond());
        else
            payload.Append(",\"license_expires_at\":null");
        payload.Append(",\"iat\":").Append(now);
        payload.Append(",\"nbf\":").Append(now);
        payload.Append(",\"exp\":").Append(now + _config.AccessTtlSeconds);
        payload.Append(",\"jti\":\"").Append(Guid.NewGuid().ToString()).Append("\"}");

        var signingInput = B64UrlEncode(ToUtf8(HEADER)) + "." + B64UrlEncode(ToUtf8(payload.ToString()));
        return signingInput + "." + B64UrlEncode(HmacSha256(ToUtf8(signingInput)));
    }

    // --- verification ------------------------------------------------------------

    /// <summary>Throws <c>JwtException</c> on any structural, signature, or lifetime failure.</summary>
    public JwtClaims VerifyAccessToken(string token)
    {
        if (token is null || token.IsEmpty())
            throw new JwtException("missing token");
        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new JwtException("malformed token");

        var signingInput = parts[0] + "." + parts[1];
        if (!MessageDigest.isEqual(HmacSha256(ToUtf8(signingInput)), B64UrlDecode(parts[2])))
            throw new JwtException("invalid signature");

        var payloadJson = new string(B64UrlDecode(parts[1]), Charset.UTF_8);
        var claims = ScanFlatObject(payloadJson);

        var now = DateTimeOffset.now().toEpochSecond();
        var claimsOut = new JwtClaims();
        bool hasIssuer = false, hasAudience = false, hasExp = false, hasSub = false;
        long expEpoch = 0;
        foreach (var pair in claims)
        {
            switch (pair.Key)
            {
                case "iss" -> hasIssuer = pair.IsString && pair.Value.Equals(_config.Issuer);
                case "aud" -> hasAudience = pair.IsString && pair.Value.Equals(_config.Audience);
                case "sub" -> { hasSub = pair.IsString; claimsOut.UserId = Guid.Parse(pair.Value); }
                case "user_id" -> { if (pair.IsString) claimsOut.UserId = Guid.Parse(pair.Value); }
                case "tenant_id" -> { if (pair.IsString) claimsOut.TenantId = Guid.Parse(pair.Value); }
                case "tenant_code" -> { if (pair.IsString) claimsOut.TenantCode = pair.Value; }
                case "role" -> { if (pair.IsString) claimsOut.Role = pair.Value; }
                case "license_expires_at" -> { if (!pair.IsString) claimsOut.LicenseExpiresAtEpoch = long.Parse(pair.Value); }
                case "exp" -> { if (!pair.IsString) { expEpoch = long.Parse(pair.Value); hasExp = expEpoch > now; } }
                case "iat" -> { if (!pair.IsString) claimsOut.IssuedAt = DateTimeOffset.ofEpochSecond(long.Parse(pair.Value), ZoneOffset.UTC); }
                default -> { }
            }
        }
        if (!hasIssuer) throw new JwtException("invalid issuer");
        if (!hasAudience) throw new JwtException("invalid audience");
        if (!hasExp) throw new JwtException("token expired or missing exp");
        if (!hasSub || claimsOut.TenantId is null) throw new JwtException("missing subject claims");
        claimsOut.ExpiresAt = DateTimeOffset.ofEpochSecond(expEpoch, ZoneOffset.UTC);
        return claimsOut;
    }

    // --- refresh tokens -----------------------------------------------------------

    /// <summary>64 random bytes, base64url — the opaque refresh token handed to the client.</summary>
    public static string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        SecureRandom.getInstanceStrong().nextBytes(bytes);
        return B64UrlEncode(bytes);
    }

    /// <summary>SHA-256 hex of the raw refresh token — what master.refresh_tokens stores.</summary>
    public static string HashToken(string raw)
    {
        var digest = MessageDigest.getInstance("SHA-256").digest(ToUtf8(raw));
        var sb = new StringBuilder();
        foreach (var b in digest)
            sb.Append(java.lang.Integer.toHexString(b & 0xFF, 2));
        return sb.ToString();
    }

    // --- internals -----------------------------------------------------------------

    private sealed class KeyValue
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public bool IsString { get; set; }
    }

    /// <summary>
    /// Minimal flat-object scanner for OUR claim payload (string / number / null
    /// values only). Throws JwtException on anything that isn't a flat object.
    /// </summary>
    private static java.util.List<KeyValue> ScanFlatObject(string text)
    {
        var out = new java.util.ArrayList<KeyValue>();
        var i = SkipWs(text, 0);
        if (i >= text.Length || text.charAt(i) != '{')
            throw new JwtException("payload not an object");
        i = SkipWs(text, i + 1);
        while (i < text.Length)
        {
            if (text.charAt(i) == '}') break;
            if (text.charAt(i) == ',') { i = SkipWs(text, i + 1); continue; }
            if (text.charAt(i) != '"') throw new JwtException("bad claim key");
            var keyEnd = text.IndexOf("\"", i + 1);
            if (keyEnd < 0) throw new JwtException("unterminated claim key");
            var key = text.Substring(i + 1, keyEnd - i - 1);

            i = SkipWs(text, keyEnd + 1);
            if (i >= text.Length || text.charAt(i) != ':') throw new JwtException("missing colon");
            i = SkipWs(text, i + 1);

            var pair = new KeyValue { Key = key };
            if (i < text.Length && text.charAt(i) == '"')
            {
                var valEnd = text.IndexOf("\"", i + 1);
                if (valEnd < 0) throw new JwtException("unterminated claim value");
                pair.Value = text.Substring(i + 1, valEnd - i - 1);
                pair.IsString = true;
                i = valEnd + 1;
            }
            else
            {
                var j = i;
                while (j < text.Length && text.charAt(j) != ',' && text.charAt(j) != '}') j++;
                pair.Value = text.Substring(i, j - i).Trim();
                i = j;
            }
            out.Add(pair);
        }
        return out;
    }

    private static int SkipWs(string text, int from)
    {
        var i = from;
        while (i < text.Length && (text.charAt(i) == ' ' || text.charAt(i) == '\t' || text.charAt(i) == '\n' || text.charAt(i) == '\r'))
            i++;
        return i;
    }

    private static byte[] HmacSha256(byte[] input)
    {
        var mac = Mac.getInstance("HmacSHA256");
        mac.init(_secret);
        return mac.doFinal(input);
    }

    private static string B64UrlEncode(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 3)
        {
            var n = (int) (data[i] & 0xFF) << 16;
            int count = 1;
            if (i + 1 < data.Length) { n |= (data[i + 1] & 0xFF) << 8; count = 2; }
            if (i + 2 < data.Length) { n |= data[i + 2] & 0xFF; count = 3; }
            sb.Append(B64_CHARS.charAt((n >> 18) & 0x3F));
            sb.Append(B64_CHARS.charAt((n >> 12) & 0x3F));
            if (count >= 2) sb.Append(B64_CHARS.charAt((n >> 6) & 0x3F));
            if (count == 3) sb.Append(B64_CHARS.charAt(n & 0x3F));
        }
        return sb.ToString();
    }

    private static byte[] B64UrlDecode(string input)
    {
        var clean = input.Replace('-', '+').Replace('_', '/');
        var pad = (4 - clean.Length % 4) % 4;
        for (var i = 0; i < pad; i++) clean += "=";
        return java.util.Base64.getDecoder().decode(clean);
    }

    private static byte[] ToUtf8(string s) => s.getBytes(Charset.UTF_8);
}