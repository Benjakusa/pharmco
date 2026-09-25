namespace Pharmco.Api.Endpoints;

using System.Text.RegularExpressions;
using Pharmco.Api.Services;
using Pharmco.Core.Auth;

/// <summary>
/// Admin-only user management surface (equivalent of <c>[Authorize(Roles =
/// "admin")]</c> at the route level via Program.cs <c>.RequireAuthorization
/// (Authz.IsAdmin)</c>, re-verified handler-side with Authz.RequireAdmin):
///   GET    /api/users         → list this tenant's users
///   POST   /api/users         → create (role ∈ admin|pharmacist|cashier)
///   PATCH  /api/users/{id}    → change role / activate / deactivate
///   DELETE /api/users/{id}    → soft delete
/// All operations are tenant-scoped by the JWT tenant_id claim.
/// </summary>
public static class UserEndpoints
{
    private static readonly Regex UsernamePattern =
        new Regex(@"^[A-Za-z0-9._@-]{3,50}$", RegexOptions.Compiled);

    // --- DTOs --------------------------------------------------------------------

    public sealed class CreateUserRequest
    {
        public string Username = "";
        public string Password = "";
        public string Role = "";
    }

    public sealed class UpdateUserRequest
    {
        public string Role = "";      // empty = unchanged
        public bool? IsActive;        // null = unchanged
    }

    // --- GET /api/users -----------------------------------------------------------

    public static async Task<IResult> List(HttpContext http, AuthRepository auth, JwtService jwt)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null) return denied;
        var claims = Authz.Claims(http, jwt);
        if (claims is null) return Unauthorized();

        var users = await auth.ListUsersAsync(claims.TenantId);
        return Results.Json(
            new { users = users.Select(ToUserJson) },
            statusCode: StatusCodes.Status200OK);
    }

    // --- POST /api/users ------------------------------------------------------------

    public static async Task<IResult> Create(HttpContext http, CreateUserRequest body, AuthRepository auth, JwtService jwt)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null) return denied;
        var claims = Authz.Claims(http, jwt);
        if (claims is null) return Unauthorized();

        if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password) || string.IsNullOrWhiteSpace(body.Role))
            return Error("bad_request", "username, password and role are required", StatusCodes.Status400BadRequest);
        if (!UsernamePattern.IsMatch(body.Username))
            return Error("bad_request", "username must be 3-50 chars of A-Za-z0-9._@-", StatusCodes.Status400BadRequest);
        if (body.Password.Length < 8 || body.Password.Length > 72)
            return Error("bad_request", "password must be 8-72 characters", StatusCodes.Status400BadRequest);

        UserRole role;
        try { role = UserRoles.Parse(body.Role); }
        catch (ArgumentException) { return Error("bad_request", "role must be admin|pharmacist|cashier", StatusCodes.Status400BadRequest); }

        // Detect duplicate username within this tenant.
        var existing = await auth.FindUserAsync(claims.TenantId, body.Username);
        if (existing is not null)
            return Error("conflict", "a user with that username already exists in this pharmacy", StatusCodes.Status409Conflict);

        var id = await auth.InsertUserAsync(claims.TenantId, body.Username, PasswordService.Hash(body.Password), role);
        var user = await auth.FindUserByIdAsync(id, claims.TenantId);
        return Results.Json(new { user = ToUserJson(user) }, statusCode: StatusCodes.Status201Created);
    }

    // --- PATCH /api/users/{id} --------------------------------------------------------

    public static async Task<IResult> Patch(HttpContext http, Guid id, UpdateUserRequest body, AuthRepository auth, JwtService jwt)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null) return denied;
        var claims = Authz.Claims(http, jwt);
        if (claims is null) return Unauthorized();

        if (body is null || (string.IsNullOrWhiteSpace(body.Role) && body.IsActive is null))
            return Error("bad_request", "provide role and/or is_active", StatusCodes.Status400BadRequest);

        string? role = null;
        if (!string.IsNullOrWhiteSpace(body.Role))
        {
            try { role = UserRoles.Parse(body.Role).ToWireString(); }
            catch (ArgumentException) { return Error("bad_request", "role must be admin|pharmacist|cashier", StatusCodes.Status400BadRequest); }
        }

        // Safety: an admin must never de-role or deactivate their own account.
        if (claims.UserId.Equals(id) && (role is not null || body.IsActive == false))
            return Error("self_modification_forbidden", "you cannot change your own role or deactivate your own account", StatusCodes.Status400BadRequest);

        var updated = await auth.UpdateUserAsync(id, claims.TenantId, role, body.IsActive);
        if (!updated)
            return Error("not_found", "user not found in this tenant", StatusCodes.Status404NotFound);

        var user = await auth.FindUserByIdAsync(id, claims.TenantId);
        return Results.Json(new { user = ToUserJson(user) }, statusCode: StatusCodes.Status200OK);
    }

    // --- DELETE /api/users/{id} (soft delete) --------------------------------------

    public static async Task<IResult> Delete(HttpContext http, Guid id, AuthRepository auth, JwtService jwt)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null) return denied;
        var claims = Authz.Claims(http, jwt);
        if (claims is null) return Unauthorized();

        if (claims.UserId.Equals(id))
            return Error("self_modification_forbidden", "you cannot delete your own account", StatusCodes.Status400BadRequest);

        var deleted = await auth.SoftDeleteUserAsync(id, claims.TenantId);
        if (!deleted)
            return Error("not_found", "user not found in this tenant", StatusCodes.Status404NotFound);
        return Results.NoContent();
    }

    // --- helpers --------------------------------------------------------------------

    private static IResult Unauthorized()
        => Results.Json(new { error = new { code = "unauthorized", message = "invalid token" } },
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Error(string code, string message, int statusCode)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    private static object ToUserJson(User? user) => new
    {
        id = user?.Id.ToString() ?? "",
        username = user?.Username ?? "",
        role = user?.Role ?? "",
        is_active = user?.IsActive ?? false,
        last_login_at = user?.LastLoginAt,
        created_at = user?.CreatedAt,
    };
}