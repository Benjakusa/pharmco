using Dapper;
using Npgsql;
using Pharmco.Core.Data;

namespace Pharmco.Core.Tenants;

/// <summary>Dapper access to the master schema (tenants, users).</summary>
public sealed class TenantRepository
{
    private const string TenantColumns =
        "id, code, name, owner_phone AS OwnerPhone, license_key AS LicenseKey, " +
        "license_expires_at AS LicenseExpiresAt, schema_name AS SchemaName, status, created_at AS CreatedAt";

    private readonly NpgsqlConnectionFactory _factory;

    public TenantRepository(NpgsqlConnectionFactory factory) => _factory = factory;

    public async Task<Tenant?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Tenant>(
            $"SELECT {TenantColumns} FROM master.tenants WHERE code = @code",
            new { code }, commandTimeout: 30);
    }

    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Tenant>(
            $"SELECT {TenantColumns} FROM master.tenants WHERE id = @id",
            new { id }, commandTimeout: 30);
    }

    /// <summary>Insert within the provisioning transaction. Caller owns conn/tx.</summary>
    public async Task<Guid> InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string code, string name, string ownerPhone, string schemaName)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            @"INSERT INTO master.tenants (id, code, name, owner_phone, schema_name, status)
              VALUES (@id, @code, @name, @ownerPhone, @schemaName, 'active')",
            new { id, code, name, ownerPhone, schemaName }, tx, commandTimeout: 30);
        return id;
    }

    public async Task InsertUserAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tenantId, string username, string passwordHash)
    {
        await conn.ExecuteAsync(
            @"INSERT INTO master.users (id, tenant_id, username, password_hash, role)
              VALUES (@id, @tenantId, @username, @passwordHash, 'admin')",
            new { id = Guid.NewGuid(), tenantId, username, passwordHash }, tx, commandTimeout: 30);
    }

    public async Task<int> CountUsersAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM master.users WHERE tenant_id = @tenantId",
            new { tenantId }, commandTimeout: 30);
    }

    public async Task SetLicenseAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid tenantId, string licenseKey, DateTimeOffset expiresAt)
    {
        await conn.ExecuteAsync(
            @"UPDATE master.tenants
              SET license_key = @licenseKey, license_expires_at = @expiresAt,
                  status = CASE WHEN status = 'provisioning' THEN 'active' ELSE status END
              WHERE id = @tenantId",
            new { tenantId, licenseKey, expiresAt }, tx, commandTimeout: 30);
    }

    public async Task SetStatusAsync(Guid tenantId, string status, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE master.tenants SET status = @status WHERE id = @tenantId",
            new { tenantId, status }, commandTimeout: 30);
    }

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<Tenant>(
            $"SELECT {TenantColumns} FROM master.tenants ORDER BY created_at", commandTimeout: 60);
        return rows.ToList();
    }
}