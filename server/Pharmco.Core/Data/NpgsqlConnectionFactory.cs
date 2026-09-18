using Npgsql;

namespace Pharmco.Core.Data;

/// <summary>
/// Opens Npgsql connections from the master connection string
/// (<c>ConnectionStrings__Master</c>). The middleware additionally calls
/// <see cref="SetSearchPathAsync"/> so unqualified queries resolve inside the
/// caller's tenant schema (defense-in-depth: tenant_id columns still guard).
/// </summary>
public sealed class NpgsqlConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("ConnectionStrings__Master is required", nameof(connectionString));
        _connectionString = connectionString;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>Opens a connection and sets <c>SET search_path</c> immediately.</summary>
    public async Task<NpgsqlConnection> OpenWithSearchPathAsync(string searchPath, CancellationToken ct = default)
    {
        var connection = await OpenAsync(ct);
        try
        {
            await SetSearchPathAsync(connection, searchPath, ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        return connection;
    }

    /// <summary>
    /// Sets the session search_path (e.g. <c>tenant_test_001, master</c>).
    /// The middleware does this explicitly on the request-scoped connection so a
    /// pooled connection can never silently carry another tenant's path.
    /// </summary>
    public static async Task SetSearchPathAsync(NpgsqlConnection connection, string searchPath, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand($"SET search_path TO {searchPath}", connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}