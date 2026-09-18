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

    public static async Task ApplyMasterAsync(NpgsqlConnection connection, CancellationToken ct = default)
    {
        var sql = await EmbeddedSql.LoadAsync(MasterSchemaResource, ct);
        // No parameters → Npgsql uses the simple query protocol, which supports
        // the multi-statement BEGIN/COMMIT batch in the migration file.
        await connection.ExecuteAsync(sql, commandTimeout: 120);
    }
}