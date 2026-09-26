using Dapper;
using Npgsql;

namespace Pharmco.Core.Data;

/// <summary>
/// Applies the idempotent master schema (db/master/001_master.sql). Used by the
/// CLI bootstrap, the API on startup, and the integration-test fixture.
/// </summary>
public static class SchemaInstaller
{
    public const string MasterSchemaResource = "Pharmco.Core.Db.001_master.sql";

    // Master migrations, applied in order. 003 adds refresh_tokens / audit
    // columns / users.deleted_at, 006 adds the Daraja callback log tables — the
    // API cannot serve auth or M-Pesa without them.
    private static readonly string[] MasterMigrations =
    {
        MasterSchemaResource,
        "Pharmco.Core.Db.003_refresh_tokens.sql",
        "Pharmco.Core.Db.006_daraja.sql",
    };

    public static async Task ApplyMasterAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        foreach (var resource in MasterMigrations)
        {
            var sql = await EmbeddedSql.LoadAsync(resource, ct);
            // No parameters → Npgsql uses the simple query protocol, which supports
            // the multi-statement BEGIN/COMMIT batch in the migration files.
            await connection.ExecuteAsync(sql, commandTimeout: 120);
        }
    }
}