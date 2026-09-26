namespace Pharmco.Api.Endpoints;

using Pharmco.Api.Services;
using Pharmco.Core;
using Pharmco.Core.Auth;
using Pharmco.Core.Tenants;

/// <summary>
/// POST /api/auth/login · POST /api/auth/refresh · POST /api/auth/logout ·
/// GET /api/auth/session (client credential-cache validation).
///
/// Handler convention (successor minimal API): request bodies are typed
/// parameter <c>LoginRequest body</c>, path segments bind by name, and named
/// singletons (TenantRepository, AuthRepository, JwtService, FailureRateLimiter)
/// are resolved from the container registered in Program.cs.
///
/// Errors follow docs/api-contract.md: { "error": { "code", "message" } }.
/// </summary>
public static class AuthEndpoints
{
    // --- DTOs --------------------------------------------------------------------

    public sealed class LoginRequest
    {
        public string PharmacyCode { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public sealed class RefreshRequest
    {
        public string RefreshToken { get; set; } = "";
    }

    public sealed class LogoutRequest
    {
        public string? RefreshToken { get; set; }
    }

    // --- POST /api/auth/login ----------------------------------------------------

    public static async Task<IResult> Login(
        HttpContext http, LoginRequest body,
        TenantRepository tenants, AuthRepository auth, JwtService jwt, FailureRateLimiter limiter)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.PharmacyCode) || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
            return Error("bad_request", "pharmacy_code, username and password are required", StatusCodes.Status400BadRequest);

        var ip = ClientIp(http);
        var userAgent = UserAgent(http);
        var key = limiter.KeyFor(body.PharmacyCode, body.Username);

        // 5 failed attempts per username per 15 min → 429.
        if (limiter.IsBlocked(key))
        {
            await auth.LogLoginAsync(body.Username, body.PharmacyCode, "rate_limited", null, ip, userAgent, "{\"reason\":\"rate_limited\"}");
            return Error("rate_limited", "too many failed login attempts — try again later", StatusCodes.Status429TooManyRequests);
        }

        var tenant = await tenants.GetByCodeAsync(body.PharmacyCode);
        if (tenant is null || !string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            limiter.RecordFailure(key);
            await auth.LogLoginAsync(body.Username, body.PharmacyCode, "failure", tenant?.Id, ip, userAgent, "{\"reason\":\"unknown_tenant\"}");
            return Error("invalid_credentials", "invalid pharmacy code or credentials", StatusCodes.Status401Unauthorized);
        }

        var user = await auth.FindUserAsync(tenant.Id, body.Username);
        var passwordOk = user is not null && PasswordService.Verify(body.Password, user.PasswordHash);
        if (!passwordOk)
        {
            limiter.RecordFailure(key);
            await auth.LogLoginAsync(body.Username, tenant.Code, "failure", tenant.Id, ip, userAgent, "{\"reason\":\"bad_password\"}");
            return Error("invalid_credentials", "invalid pharmacy code or credentials", StatusCodes.Status401Unauthorized);
        }

        if (!user.IsActive || user.DeletedAt is not null)
        {
            limiter.RecordFailure(key);
            await auth.LogLoginAsync(user.Username, tenant.Code, "failure", tenant.Id, ip, userAgent, "{\"reason\":\"account_disabled\"}");
            return Error("account_disabled", "this account has been deactivated", StatusCodes.Status403Forbidden);
        }

        // Success: clean bucket, first refresh family, audit.
        limiter.Reset(key);
        var now = DateTimeOffset.UtcNow;
        var accessToken = jwt.IssueAccessToken(user, tenant);
        var refreshRaw = JwtService.GenerateRefreshToken();
        await auth.InsertRefreshTokenAsync(
            user.Id, Guid.NewGuid(), JwtService.HashToken(refreshRaw),
            now.AddSeconds(jwt.RefreshTtlSeconds()),
            ip, userAgent);
        await auth.TouchLastLoginAsync(user.Id, now);
        await auth.LogLoginAsync(user.Username, tenant.Code, "success", tenant.Id, ip, userAgent, null);

        return Results.Json(new
        {
            access_token = accessToken,
            refresh_token = refreshRaw,
            token_type = "Bearer",
            expires_in = jwt.AccessTtlSeconds(),
            user = new { id = user.Id.ToString(), role = user.Role, tenant_code = tenant.Code },
        }, statusCode: StatusCodes.Status200OK);
    }

    // --- POST /api/auth/refresh (rotation + reuse detection) ----------------------

    public static async Task<IResult> Refresh(
        HttpContext http, RefreshRequest body,
        AuthRepository auth, TenantRepository tenants, JwtService jwt)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.RefreshToken))
            return Error("bad_request", "refresh_token is required", StatusCodes.Status400BadRequest);

        var ip = ClientIp(http);
        var userAgent = UserAgent(http);
        var row = await auth.FindRefreshTokenByHashAsync(JwtService.HashToken(body.RefreshToken));

        if (row is null)
            return Error("invalid_grant", "refresh token not recognised", StatusCodes.Status401Unauthorized);

        if (row.RevokedAt is not null)
        {
            // A rotated token was replayed → revoke the entire family.
            await auth.RevokeFamilyAsync(row.FamilyId);
            return Error("invalid_grant", "refresh token reuse detected", StatusCodes.Status401Unauthorized);
        }

        var now = DateTimeOffset.UtcNow;
        if (row.ExpiresAt <= now)
        {
            await auth.RevokeTokenAsync(row.Id);
            return Error("invalid_grant", "refresh token expired", StatusCodes.Status401Unauthorized);
        }

        var user = await auth.FindUserByIdAsync(row.UserId, row.TenantId);
        if (user is null)
        {
            await auth.RevokeFamilyAsync(row.FamilyId);
            return Error("invalid_grant", "user no longer exists", StatusCodes.Status401Unauthorized);
        }
        if (!user.IsActive || user.DeletedAt is not null)
        {
            await auth.RevokeFamilyAsync(row.FamilyId);
            return Error("account_disabled", "this account has been deactivated", StatusCodes.Status403Forbidden);
        }

        var tenant = await tenants.GetByIdAsync(row.TenantId);
        if (tenant is null || !string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            await auth.RevokeFamilyAsync(row.FamilyId);
            return Error("tenant_unavailable", "this pharmacy is not active", StatusCodes.Status403Forbidden);
        }

        // Rotate: revoke the presented row, mint a new one in the same family.
        var newRaw = JwtService.GenerateRefreshToken();
        var newId = await auth.InsertRefreshTokenAsync(row.UserId, row.FamilyId, JwtService.HashToken(newRaw),
            now.AddSeconds(jwt.RefreshTtlSeconds()),
            ip, userAgent);
        await auth.RevokeTokenAsync(row.Id, newId);

        var accessToken = jwt.IssueAccessToken(user, tenant);
        await auth.LogLoginAsync(user.Username, tenant.Code, "success", tenant.Id, ip, userAgent, "{\"event\":\"refresh\"}");

        return Results.Json(new
        {
            access_token = accessToken,
            refresh_token = newRaw,
            token_type = "Bearer",
            expires_in = jwt.AccessTtlSeconds(),
            user = new { id = user.Id.ToString(), role = user.Role, tenant_code = tenant.Code },
        }, statusCode: StatusCodes.Status200OK);
    }

    // --- POST /api/auth/logout -----------------------------------------------------

    public static async Task<IResult> Logout(HttpContext http, LogoutRequest? body, AuthRepository auth, JwtService jwt)
    {
        var token = Authz.Bearer(http);
        if (token.IsEmpty())
            return Error("unauthorized", "missing bearer token", StatusCodes.Status401Unauthorized);

        JwtClaims claims;
        try { claims = jwt.VerifyAccessToken(token); }
        catch (JwtException) { return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized); }

        // Revoke the presented refresh token (precise) or every live session.
        if (body is not null && !string.IsNullOrWhiteSpace(body.RefreshToken))
        {
            var row = await auth.FindRefreshTokenByHashAsync(JwtService.HashToken(body.RefreshToken));
            if (row is not null && row.UserId.Equals(claims.UserId))
                await auth.RevokeTokenAsync(row.Id);
        }
        else
        {
            await auth.RevokeAllForUserAsync(claims.UserId);
        }
        await auth.LogLoginAsync("", claims.TenantCode, "success", claims.TenantId,
            ClientIp(http), UserAgent(http), "{\"event\":\"logout\"}");
        return Results.NoContent();
    }

    // --- GET /api/auth/session — validates a bearer token (desktop cache check) ---

    public static async Task<IResult> Session(HttpContext http, JwtService jwt)
    {
        var token = Authz.Bearer(http);
        if (token.IsEmpty())
            return Error("unauthorized", "missing bearer token", StatusCodes.Status401Unauthorized);

        JwtClaims claims;
        try { claims = jwt.VerifyAccessToken(token); }
        catch (JwtException) { return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized); }

        return Results.Json(new
        {
            user = new { id = claims.UserId.ToString(), role = claims.Role, tenant_code = claims.TenantCode },
            license_expires_at = claims.LicenseExpiresAtEpoch == 0 ? (long?) null : claims.LicenseExpiresAtEpoch,
            token_expires_at = claims.ExpiresAt.ToUnixTimeSeconds(),
        }, statusCode: StatusCodes.Status200OK);
    }

    // --- helpers ------------------------------------------------------------------

    private static IResult Error(string code, string message, int statusCode)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    private static string ClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["x-forwarded-for"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (!first.IsEmpty())
                return first;
        }
        return http.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    private static string UserAgent(HttpContext http)
        => http.Request.Headers["user-agent"].ToString();

    private static string Bearer(HttpContext http)
    {
        var header = http.Request.Headers["authorization"].ToString();
        if (header.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
            return header.Substring(7).Trim();
        return "";
    }
}