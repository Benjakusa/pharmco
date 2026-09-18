namespace Pharmco.Core.Auth;

using Dapper;
using Npgsql;
using Pharmco.Core.Data;

/// <summary>
/// Dapper access to the auth surface of the master schema: user lookup/CRUD,
/// rotating refresh tokens (SHA-256 hashed), and login-attempt auditing.
/// Every query is tenant-scoped by an explicit <c>tenant_id</c> predicate —
/// never trust request context to have set the search_path.
/// </summary>
public sealed class AuthRepository
{
    private const string UserColumns =
        "id, tenant_id AS TenantId, username, password_hash AS PasswordHash, role, " +
        "is_active AS IsActive, last_login AS LastLoginAt, deleted_at AS DeletedAt, created_at AS CreatedAt";

    private const string RefreshColumns =
        "rt.id, rt.user_id AS UserId, u.tenant_id AS TenantId, rt.family_id AS FamilyId, " +
        "rt.token_hash AS TokenHash, rt.expires_at AS ExpiresAt, rt.revoked_at AS RevokedAt, " +
        "rt.replaced_by AS ReplacedBy, rt.created_at AS CreatedAt";

    private const string RefreshFrom =
        "FROM master.refresh_tokens rt JOIN master.users u ON u.id = rt.user_id";

    private readonly NpgsqlConnectionFactory _factory;

    public AuthRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    // --- users -----------------------------------------------------------------

    public async Task<User?> FindUserAsync(Guid tenantId, string username, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<User>(
            $"SELECT {UserColumns} FROM master.users " +
            "WHERE tenant_id = @tenantId AND lower(username) = lower(@username) AND deleted_at IS NULL",
            new { tenantId, username }, commandTimeout: 30);
    }

    public async Task<User?> FindUserByIdAsync(Guid id, Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<User>(
            $"SELECT {UserColumns} FROM master.users WHERE id = @id AND tenant_id = @tenantId AND deleted_at IS NULL",
            new { id, tenantId }, commandTimeout: 30);
    }

    public async Task<IReadOnlyList<User>> ListUsersAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<User>(
            $"SELECT {UserColumns} FROM master.users WHERE tenant_id = @tenantId AND deleted_at IS NULL ORDER BY username",
            new { tenantId }, commandTimeout: 30);
        return rows.ToList();
    }

    public async Task<Guid> InsertUserAsync(Guid tenantId, string username, string passwordHash, UserRole role, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO master.users (id, tenant_id, username, password_hash, role) " +
            "VALUES (@id, @tenantId, @username, @passwordHash, @role)",
            new { id, tenantId, username, passwordHash, role = role.ToString() }, commandTimeout: 30);
        return id;
    }

    /// <summary>Role/active toggle. Returns false when the row is not in the tenant.</summary>
    public async Task<bool> UpdateUserAsync(Guid id, Guid tenantId, string? role = null, bool? isActive = null, CancellationToken ct = default)
    {
        var sets = new List<string>();
        var args = new DynamicParameters();
        args.Add("id", id);
        args.Add("tenantId", tenantId);
        if (role is not null)
        {
            sets.Add("role = @role");
            args.Add("role", UserRoles.Parse(role).ToWireString());
        }
        if (isActive is not null)
        {
            sets.Add("is_active = @isActive");
            args.Add("isActive", isActive.Value);
        }
        if (sets.Count == 0)
            return false;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            $"UPDATE master.users SET {string.Join(", ", sets)} WHERE id = @id AND tenant_id = @tenantId AND deleted_at IS NULL",
            args, commandTimeout: 30);
        return rows > 0;
    }

    /// <summary>Soft delete: is_active=false + deleted_at=now(). Returns false when absent.</summary>
    public async Task<bool> SoftDeleteUserAsync(Guid id, Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var affected = await conn.ExecuteAsync(
            "UPDATE master.users SET is_active = false, deleted_at = now() " +
            "WHERE id = @id AND tenant_id = @tenantId AND deleted_at IS NULL",
            new { id, tenantId }, commandTimeout: 30);
        return affected > 0;
    }

    public async Task TouchLastLoginAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE master.users SET last_login = @at WHERE id = @id",
            new { id, at }, commandTimeout: 30);
    }

    // --- refresh tokens (rotating family + reuse detection) ----------------------

    public async Task<Guid> InsertRefreshTokenAsync(
        Guid userId, Guid familyId, string tokenHash, DateTimeOffset expiresAt,
        string? ip, string? userAgent, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            @"INSERT INTO master.refresh_tokens (id, user_id, family_id, token_hash, expires_at, ip, user_agent)
              VALUES (@id, @userId, @familyId, @tokenHash, @expiresAt, NULLIF(@ip, '')::inet, @userAgent)",
            new { id, userId, familyId, tokenHash, expiresAt, ip = ip ?? "", userAgent }, commandTimeout: 30);
        return id;
    }

    public async Task<RefreshTokenRow?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RefreshTokenRow>(
            $"SELECT {RefreshColumns} {RefreshFrom} WHERE rt.token_hash = @tokenHash",
            new { tokenHash }, commandTimeout: 30);
    }

    public async Task RevokeTokenAsync(Guid id, Guid? replacedBy = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE master.refresh_tokens SET revoked_at = now(), replaced_by = @replacedBy WHERE id = @id",
            new { id, replacedBy }, commandTimeout: 30);
    }

    /// <summary>Revoke the whole rotation family (triggered by token reuse / logout).</summary>
    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE master.refresh_tokens SET revoked_at = now() " +
            "WHERE family_id = @familyId AND revoked_at IS NULL",
            new { familyId }, commandTimeout: 30);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE master.refresh_tokens SET revoked_at = now() " +
            "WHERE user_id = @userId AND revoked_at IS NULL",
            new { userId }, commandTimeout: 30);
    }

    // --- login audit -------------------------------------------------------------

    /// <summary>
    /// Insert-only audit row (master.audit_logs). outcome ∈ success|failure|rate_limited.
    /// <c>detail</c> is an optional JSON fragment carried in the detail jsonb column.
    /// </summary>
    public async Task LogLoginAsync(
        string username, string tenantCode, string outcome, Guid? tenantId,
        string? ip, string? userAgent, string? detail = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            @"INSERT INTO master.audit_logs
                  (tenant_id, action, detail, username, tenant_code, ip, user_agent, outcome, created_at)
              VALUES (@tenantId, 'login', COALESCE(@detail::jsonb, '{}'), @username, @tenantCode,
                      NULLIF(@ip, '')::inet, @userAgent, @outcome, now())",
            new { tenantId, username, tenantCode, outcome, ip = ip ?? "", userAgent, detail }, commandTimeout: 30);
    }
}