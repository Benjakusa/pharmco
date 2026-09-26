using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmco.Core.Data;
using Pharmco.Core.Licensing;
using Pharmco.Core.Security;
using Pharmco.Core.Tenants;

namespace Pharmco.Core.Provisioning;

public sealed class ProvisioningException : Exception
{
    public ProvisioningException(string message) : base(message) { }
}

public sealed record ProvisionRequest(
    string Code,
    string Name,
    string OwnerPhone,
    string? AdminUsername = null,
    int LicenseDays = 365);

public sealed record ProvisionResult(
    Guid TenantId,
    string Code,
    string SchemaName,
    string AdminUsername,
    string TempPassword,
    string LicenseKey,
    DateTimeOffset LicenseExpiresAt);

/// <summary>
/// Tenant lifecycle: provision, renew, deprovision. All provisioning actions
/// are logged to master.audit_logs (append-only), as required.
/// </summary>
public sealed class TenantProvisioner
{
    private const string TenantTemplateResource = "Pharmco.Core.Db.002_tenant_template.sql";

    // Tenant migrations applied at provision time, in order. 004 hardens
    // products, 005 widens payment_mode/status + adds sale_sequences, 007 adds
    // sync_queue/client_meta — the sync engine and POS sale path need all three.
    private static readonly string[] TenantMigrations =
    {
        TenantTemplateResource,
        "Pharmco.Core.Db.004_tenant_products.sql",
        "Pharmco.Core.Db.005_tenant_sales.sql",
        "Pharmco.Core.Db.007_sync_queue.sql",
    };

    private readonly NpgsqlConnectionFactory _factory;
    private readonly TenantRepository _repository;
    private readonly LicenseService _license;

    public TenantProvisioner(NpgsqlConnectionFactory factory, TenantRepository repository, LicenseService license)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _license = license ?? throw new ArgumentNullException(nameof(license));
    }

    /// <summary>
    /// Creates <c>master.tenants</c> row, the per-tenant schema, the admin user
    /// (bcrypt cost 12, random 10-char temp password), and the RSA-signed
    /// license — all inside a single transaction.
    /// </summary>
    public async Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ProvisioningException("--name is required");
        if (string.IsNullOrWhiteSpace(request.OwnerPhone)) throw new ProvisioningException("--owner-phone is required");

        var schemaName = TenantNaming.SchemaNameFromCode(request.Code); // throws on bad code
        var existing = await _repository.GetByCodeAsync(request.Code, ct);
        if (existing is not null)
            throw new ProvisioningException($"tenant code exists: {request.Code}");

        var tempPassword = CharacterSet.Generate(10);
        var passwordHash = PasswordHasher.Hash(tempPassword);
        var licenseExpiresAt = DateTimeOffset.UtcNow.AddDays(request.LicenseDays);
        var adminUsername = request.AdminUsername ?? $"admin@{request.Code.ToLowerInvariant()}";

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var tenantId = await _repository.InsertAsync(conn, tx, request.Code, request.Name, request.OwnerPhone, schemaName);
        await ApplyTenantSchemaAsync(conn, tx, schemaName, ct);
        await _repository.InsertUserAsync(conn, tx, tenantId, adminUsername, passwordHash);

        var licenseKey = _license.Sign(new LicenseClaims
        {
            TenantId = tenantId.ToString(),
            Code = request.Code,
            ExpiresAt = licenseExpiresAt,
        });
        await _repository.SetLicenseAsync(conn, tx, tenantId, licenseKey, licenseExpiresAt);
        await LogMasterAuditAsync(conn, tx, tenantId, "provision-tenant",
            new { code = request.Code, schema = schemaName, admin_username = adminUsername, license_days = request.LicenseDays });

        await tx.CommitAsync(ct);

        return new ProvisionResult(tenantId, request.Code, schemaName, adminUsername, tempPassword, licenseKey, licenseExpiresAt);
    }

    /// <summary>Re-signs and extends the license by <paramref name="days"/> (default 365).</summary>
    public async Task<DateTimeOffset> RenewLicenseAsync(string code, int days = 365, CancellationToken ct = default)
    {
        var tenant = await _repository.GetByCodeAsync(code, ct)
            ?? throw new ProvisioningException($"tenant not found: {code}");
        if (tenant.Status == "inactive")
            throw new ProvisioningException($"tenant {code} is inactive and cannot be renewed");

        var newExpiry = RenewalPolicy.NextExpiry(tenant.LicenseExpiresAt, days, DateTimeOffset.UtcNow);
        var licenseKey = _license.Sign(new LicenseClaims
        {
            TenantId = tenant.Id.ToString(),
            Code = code,
            ExpiresAt = newExpiry,
        });

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await _repository.SetLicenseAsync(conn, tx, tenant.Id, licenseKey, newExpiry);
        await LogMasterAuditAsync(conn, tx, tenant.Id, "renew-license", new { code, days, new_expiry = newExpiry });
        await tx.CommitAsync(ct);

        return newExpiry;
    }
    /// <summary>
    /// Deprovisions a tenant: drops the per-tenant schema and marks the master
    /// row inactive. Requires <paramref name="confirm"/> to equal the code.
    /// </summary>
    public async Task DeprovisionAsync(string code, string confirm, CancellationToken ct = default)
    {
        if (!string.Equals(confirm, code, StringComparison.Ordinal))
            throw new ProvisioningException("deprovision requires --confirm with the exact tenant code");

        var tenant = await _repository.GetByCodeAsync(code, ct)
            ?? throw new ProvisioningException($"tenant not found: {code}");

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "UPDATE master.tenants SET status = 'inactive' WHERE id = @id",
            new { id = tenant.Id }, tx, commandTimeout: 30);
        // schema_name is CHECK-constrained to ^tenant_[a-z0-9_]+$ — safe to inline
        await conn.ExecuteAsync($"DROP SCHEMA IF EXISTS {tenant.SchemaName} CASCADE", tx, commandTimeout: 120);
        await LogMasterAuditAsync(conn, tx, tenant.Id, "deprovision-tenant", new { code });

        await tx.CommitAsync(ct);
    }

    public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default)
        => _repository.ListAsync(ct);

    public Task<Tenant?> FindAsync(string code, CancellationToken ct = default)
        => _repository.GetByCodeAsync(code, ct);

    private static async Task ApplyTenantSchemaAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string schemaName, CancellationToken ct)
    {
        foreach (var resource in TenantMigrations)
        {
            var template = await EmbeddedSql.LoadAsync(resource, ct);
            var sql = StripTransactionStatements(template)
                .Replace("{tenant_schema}", schemaName, StringComparison.Ordinal);
            await conn.ExecuteAsync(sql, transaction: tx, commandTimeout: 120);
        }
    }

    // Migration files are also runnable standalone, so they wrap themselves in
    // BEGIN/COMMIT. Applied inside the provisioning transaction those statements
    // would commit the outer transaction early — drop the standalone wrappers.
    private static string StripTransactionStatements(string sql)
        => string.Join('\n', sql.Split('\n').Where(line =>
        {
            var trimmed = line.Trim();
            return !trimmed.Equals("BEGIN;", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Equals("COMMIT;", StringComparison.OrdinalIgnoreCase);
        }));

    private static async Task LogMasterAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenantId, string action, object detail)
    {
        await conn.ExecuteAsync(
            "INSERT INTO master.audit_logs (tenant_id, action, detail) VALUES (@tenantId, @action, @detail::jsonb)",
            new { tenantId, action, detail = JsonSerializer.Serialize(detail) }, tx, commandTimeout: 30);
    }
}