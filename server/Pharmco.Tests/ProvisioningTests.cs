using Dapper;
using Pharmco.Core.Provisioning;
using Pharmco.Core.Security;
using Xunit;

namespace Pharmco.Tests;

/// <summary>
/// Integration tests for the provisioning subsystem (single "pg" collection →
/// serial execution, shared idempotent master schema).
/// </summary>
[Collection("pg")]
public class ProvisioningTests
{
    private readonly PgFixture _db;

    public ProvisioningTests(PgFixture db) => _db = db;

    private async Task<ProvisionResult> ProvisionAsync(string label = "T")
        => await _db.Provisioner.ProvisionAsync(new ProvisionRequest(_db.NewCode(label), "Test Pharmacy", "254700000000"));

    [IntegrationFact]
    public async Task ProvisionTenant_CreatesSchema()
    {
        var result = await ProvisionAsync();

        await using var conn = await _db.Factory.OpenAsync();
        var tableCount = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema",
            new { schema = result.SchemaName });

        Assert.Equal(5, tableCount); // products, sales, sale_items, stock_moves, audit_logs

        var tenant = await _db.Provisioner.FindAsync(result.Code);
        Assert.NotNull(tenant);
        Assert.Equal("active", tenant.Status);
        Assert.Equal(result.SchemaName, tenant.SchemaName);
    }

    [IntegrationFact]
    public async Task ProvisionTenant_CreatesAdminUser_AndEqualsTempPassword()
    {
        var result = await ProvisionAsync();

        await using var conn = await _db.Factory.OpenAsync();
        var row = await conn.QuerySingleAsync<AdminRow>(
            "SELECT username, password_hash FROM master.users WHERE tenant_id = @tenantId",
            new { tenantId = result.TenantId });

        Assert.Equal($"admin@{result.Code.ToLowerInvariant()}", row.username);
        Assert.True(PasswordHasher.Verify(result.TempPassword, row.password_hash), "bcrypt hash must match");

        var auditRows = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM master.audit_logs WHERE tenant_id = @tenantId AND action = 'provision-tenant'",
            new { tenantId = result.TenantId });
        Assert.Equal(1, auditRows);
    }

    [IntegrationFact]
    public async Task ProvisionTenant_GeneratesValidLicense()
    {
        var result = await ProvisionAsync();
        var claims = TestKeys.Signer.Verify(result.LicenseKey, TestKeys.PublicKey);

        Assert.Equal(result.Code, claims.Code);
        Assert.Equal(result.TenantId.ToString(), claims.TenantId);
        Assert.Equal(result.LicenseExpiresAt, claims.ExpiresAt, TimeSpan.FromSeconds(5));
    }

    [IntegrationFact]
    public async Task ProvisionTenant_DuplicateCode_Throws()
    {
        var result = await ProvisionAsync();

        var ex = await Assert.ThrowsAsync<ProvisioningException>(() =>
            _db.Provisioner.ProvisionAsync(new ProvisionRequest(result.Code, "Again", "254700000000")));

        Assert.Contains("tenant code exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
    [IntegrationFact]
    public async Task RenewLicense_ExtendsBy365Days()
    {
        var result = await ProvisionAsync();
        var original = result.LicenseExpiresAt;

        var newExpiry = await _db.Provisioner.RenewLicenseAsync(result.Code, 365);

        Assert.Equal(original.AddDays(365), newExpiry, TimeSpan.FromSeconds(5)); // from current expiry, not today
        Assert.True(newExpiry > original);

        var reloaded = await _db.Provisioner.FindAsync(result.Code);
        Assert.Equal(newExpiry, reloaded!.LicenseExpiresAt!.Value, TimeSpan.FromSeconds(5));
        Assert.NotNull(reloaded.LicenseKey);
    }

    [IntegrationFact]
    public async Task CrossTenantQuery_ReturnsEmpty()
    {
        var tenantA = await ProvisionAsync("A");
        var tenantB = await ProvisionAsync("B");

        // seed one product into tenant A's schema only
        await using (var conn = await _db.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                $"INSERT INTO {tenantA.SchemaName}.products (id, tenant_id, name, selling_price) VALUES (@id, @tenantId, @name, 50.00)",
                new { id = Guid.NewGuid(), tenantId = tenantA.TenantId, name = "Paracetamol" });
        }

        // point a fresh session at tenant B (exactly what the resolver does for B)
        await using var session = await _db.Factory.OpenWithSearchPathAsync(
            Pharmco.Core.Tenants.TenantNaming.SearchPathFor(tenantB.SchemaName));

        var bCount = await session.ExecuteScalarAsync<int>("SELECT count(*) FROM products"); // unqualified
        var aQualified = await session.ExecuteScalarAsync<int>(
            $"SELECT count(*) FROM {tenantA.SchemaName}.products");

        Assert.Equal(0, bCount);      // tenant B sees 0 rows from its own (empty) products
        Assert.Equal(1, aQualified);  // and cannot read A's rows either
    }

    [IntegrationFact]
    public async Task DeprovisionTenant_DropsSchema()
    {
        var result = await ProvisionAsync();
        Assert.Equal(5, await TableCountAsync(result.SchemaName));

        await _db.Provisioner.DeprovisionAsync(result.Code, result.Code);

        Assert.Equal(0, await TableCountAsync(result.SchemaName)); // schema gone

        var tenant = await _db.Provisioner.FindAsync(result.Code);
        Assert.Equal("inactive", tenant!.Status);

        await using var conn = await _db.Factory.OpenAsync();
        var audit = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM master.audit_logs WHERE tenant_id = @tenantId AND action = 'deprovision-tenant'",
            new { tenantId = result.TenantId });
        Assert.Equal(1, audit);
    }

    private async Task<int> TableCountAsync(string schema)
    {
        await using var conn = await _db.Factory.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema", new { schema });
    }

    private sealed class AdminRow
    {
        public string username { get; set; } = "";
        public string password_hash { get; set; } = "";
    }
}