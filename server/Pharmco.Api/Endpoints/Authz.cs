namespace Pharmco.Api.Endpoints;

using Pharmco.Api.Services;

/// <summary>
/// Admin-role checks + token extraction helpers for the /api/users surface.
///
/// Registration-level equivalent of <c>[Authorize(Roles = "admin")]</c>: routes
/// are declared with <c>.RequireAuthorization(Authz.IsAdmin)</c> in Program.cs,
/// and every handler ALSO calls <c>Authz.RequireAdmin(http)</c> before touching
/// data — the second check is what the integration tests actually exercise.
/// </summary>
public static class Authz
{
    public const string RoleAdmin = "admin";

    /// <summary>Predicate form for .RequireAuthorization (framework gate).</summary>
    public static bool IsAdmin(HttpContext http)
        => http.User is not null && http.User.IsInRole(RoleAdmin);

    /// <summary>
    /// Handler-level guard. Returns a 403 <c>IResult</c> when the caller lacks
    /// the admin role, or null (allowed) otherwise. Callers: <c>if (var denied = Authz.RequireAdmin(http); denied is not null) return denied;</c>
    /// </summary>
    public static IResult? RequireAdmin(HttpContext http)
    {
        if (IsAdmin(http))
            return null;
        return Results.Json(
            new { error = new { code = "forbidden", message = "admin role required" } },
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Bearer token from the Authorization header, or "".</summary>
    public static string Bearer(HttpContext http)
    {
        var header = http.Request.Headers["authorization"] ?? "";
        if (header.ToLower().StartsWith("bearer "))
            return header.Substring(7).Trim();
        return "";
    }

    /// <summary>Verified JWT claims for the current request, or null when missing/bad.</summary>
    public static JwtClaims? Claims(HttpContext http, JwtService jwt)
    {
        var token = Bearer(http);
        if (string.IsNullOrEmpty(token))
            return null;
        try { return jwt.VerifyAccessToken(token); }
        catch (JwtException) { return null; }
    }
}