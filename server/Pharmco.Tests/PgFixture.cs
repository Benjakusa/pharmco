using Pharmco.Core.Data;
using Pharmco.Core.Provisioning;
using Pharmco.Core.Tenants;
using Xunit;

namespace Pharmco.Tests;

/// <summary>
/// One shared fixture per "pg" collection. Applies the idempotent master
/// schema once per collection. Skipped entirely when PHARMCO_TEST_CONNECTION
/// is absent (see IntegrationFactAttribute).
/// </summary>
public sealed class PgFixture : IAsyncLifetime
{
    private readonly string? _connectionString;

    public PgFixture()
        => _connectionString = Environment.GetEnvironmentVariable("PHARMCO_TEST_CONNECTION");

    public async Task InitializeAsync()
    {
        if (_connectionString is null) return; // integration tests self-skip via [IntegrationFact]
        await using var conn = await Factory.OpenAsync();
        await SchemaInstaller.ApplyMasterAsync(conn);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public NpgsqlConnectionFactory Factory => new(Require());

    public TenantRepository Repository => new(Factory);

    public TenantProvisioner Provisioner => new(Factory, Repository, TestKeys.Signer);

    /// <summary>Unique, uppercase, ≤ 20 chars: e.g. T142315123 + label prefix.</summary>
    public string NewCode(string label)
        => $"{label}{DateTime.UtcNow:HHmmssfff}";

    private string Require()
        => _connectionString ?? throw new InvalidOperationException("PHARMCO_TEST_CONNECTION is not set");
}