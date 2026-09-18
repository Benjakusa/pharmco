using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Http;
using Pharmco.Api.Middleware;
using Pharmco.Core.Data;
using Pharmco.Core.Provisioning;
using Pharmco.Core.Tenants;
using Xunit;

namespace Pharmco.Tests;

/// <summary>
/// TenantResolverMiddleware behavior. HTTP-level outcomes (401/403/200) are
/// asserted through a real <see cref="DefaultHttpContext"/> + middleware invoke;
/// the DB-bearing cases run in the "pg" collection.
/// </summary>
[Collection("pg")]
public class MiddlewareTests
{
    private readonly PgFixture _db;

    public MiddlewareTests(PgFixture db) => _db = db;

    [Fact]
    public async Task TenantMiddleware_RejectsMissingTenantClaim()
    {
        // Authenticated, but the JWT carries no tenant_id claim → 401.
        // Uses throwaway factory/repo — the decision happens before any DB I/O.
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("code", "TEST-001") }, "Bearer")),
        };
        var dummyFactory = new NpgsqlConnectionFactory("Host=localhost;Database=does-not-matter");
        var dummyRepo = new TenantRepository(dummyFactory);

        var decision = await TenantResolverMiddleware.ResolveAsync(context, dummyRepo, dummyFactory);

        Assert.True(decision.IsRejected);
        Assert.Equal(StatusCodes.Status401Unauthorized, decision.RejectStatus);
    }

    [IntegrationFact]
    public async Task TenantMiddleware_SetsSearchPath()
    {
        var result = await _db.Provisioner.ProvisionAsync(
            new ProvisionRequest(_db.NewCode("M"), "Test Pharmacy", "254700000000"));

        // seed one product inside the tenant schema
        await using (var seed = await _db.Factory.OpenAsync())
        {
            await seed.ExecuteAsync(
                $"INSERT INTO {result.SchemaName}.products (id, tenant_id, name, selling_price) VALUES (@id, @tenantId, @name, 50.00)",
                new { id = Guid.NewGuid(), tenantId = result.TenantId, name = "Amoxicillin" });
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("tenant_id", result.TenantId.ToString()) }, "Bearer")),
        };

        int seenProducts = -1;
        var middleware = new TenantResolverMiddleware(nextContext =>
        {
            var scope = TenantScope.Current(nextContext);
            Assert.NotNull(scope);
            // UNQUALIFIED query on the request-scoped connection must land in
            // the tenant schema (search_path was SET by the middleware).
            seenProducts = scope!.Connection.ExecuteScalar<int>("SELECT count(*) FROM products");
            nextContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, _db.Repository, _db.Factory);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, seenProducts);
    }

    [IntegrationFact]
    public async Task TenantMiddleware_RejectsExpiredLicense()
    {
        var result = await _db.Provisioner.ProvisionAsync(
            new ProvisionRequest(_db.NewCode("E"), "Test Pharmacy", "254700000000"));

        // 45 days past expiry — beyond the 30-day grace → 403
        await using (var conn = await _db.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE master.tenants SET license_expires_at = now() - interval '45 days' WHERE id = @id",
                new { id = result.TenantId });
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("tenant_id", result.TenantId.ToString()) }, "Bearer")),
        };

        var middleware = new TenantResolverMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, _db.Repository, _db.Factory);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [IntegrationFact]
    public async Task TenantMiddleware_RejectsInactiveTenant()
    {
        var result = await _db.Provisioner.ProvisionAsync(
            new ProvisionRequest(_db.NewCode("S"), "Test Pharmacy", "254700000000"));

        await using (var conn = await _db.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE master.tenants SET status = 'suspended' WHERE id = @id",
                new { id = result.TenantId });
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("tenant_id", result.TenantId.ToString()) }, "Bearer")),
        };

        var middleware = new TenantResolverMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, _db.Repository, _db.Factory);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public void TenantNaming_MapsCodeToSchema()
    {
        Assert.Equal("tenant_pharmco_001", TenantNaming.SchemaNameFromCode("PHARMCO-001"));
        Assert.Equal("tenant_test_2", TenantNaming.SchemaNameFromCode("TEST.2"));
        Assert.Equal("tenant_a_b", TenantNaming.SchemaNameFromCode("A_B"));
    }
}