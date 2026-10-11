namespace Pharmco.Client.Services;

using System.IO;
using Microsoft.Data.Sqlite;
using Pharmco.Client.Models;

/// <summary>
/// Local credential cache — an SQLCipher-encrypted SQLite file holding ONLY
/// the offline-login material:
///   * username + client-side bcrypt second hash (cost 12) — offline login
///   * role, tenant_code, user_id — offline session identity
///   * access_token / refresh_token — DPAPI-encrypted blobs (SecretsBox)
///   * last_verified_at (epoch seconds) — the 7-day online-freshness gate
///
/// SQLCipher delivery: on Windows the app ships the SQLCipher build of the
/// SQLite native library and opens with PRAGMA key = "x'<256-bit hex>'"
/// (raw-key form, AES-256). The key itself never touches disk in clear — it is
/// DPAPI-wrapped by SecretsBox. The bare driver without cipher support is
/// DEV-only: set PHARMCO_PLAINTEXT_SQLITE=1 to disable the PRAGMA (never ship
/// or run pilots with that).
/// </summary>
public sealed class CredentialCache : IDisposable
{
    private const string Ddl =
        "CREATE TABLE IF NOT EXISTS credential_cache (" +
        "  id                INTEGER PRIMARY KEY AUTOINCREMENT," +
        "  pharmacy_code     TEXT    NOT NULL," +
        "  username          TEXT    NOT NULL," +
        "  password_hash     TEXT    NOT NULL," +   // client-side bcrypt (cost 12)
        "  role              TEXT    NOT NULL," +
        "  tenant_code       TEXT    NOT NULL," +
        "  user_id           TEXT    NOT NULL," +
        "  access_token      BLOB    NOT NULL," +   // DPAPI-wrapped
        "  refresh_token     BLOB," +               // DPAPI-wrapped
        "  last_verified_at  INTEGER NOT NULL," +   // epoch seconds (UTC)
        "  created_at        INTEGER NOT NULL," +
        "  updated_at        INTEGER NOT NULL," +
        "  UNIQUE (pharmacy_code, username)" +
        ")";

    private readonly string _connectionString;
    private readonly string _sqlCipherKeyHex;    // "" => plaintext dev mode

    public CredentialCache(string dbPath, string sqlCipherKeyHex)
    {
        _sqlCipherKeyHex = sqlCipherKeyHex ?? "";
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>Default location: %APPDATA%/Pharmco/credentials.db (+ DPAPI-wrapped .key).</summary>
    public static CredentialCache OpenDefault()
    {
        var keyPath = ConfigDir.DataPath("credentials.db.key");
        string keyHex;
        if (File.Exists(keyPath))
            keyHex = SecretsBox.UnprotectString(File.ReadAllBytes(keyPath));
        else
        {
            keyHex = SecretsBox.RandomHex(32);
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllBytes(keyPath, SecretsBox.Protect(keyHex));
        }

        var plaintext = Environment.GetEnvironmentVariable("PHARMCO_PLAINTEXT_SQLITE") == "1";
        return new CredentialCache(
            ConfigDir.DataPath("credentials.db"),
            plaintext ? "" : keyHex);
    }

    // --- queries ------------------------------------------------------------------

    public CachedCredential? Find(string pharmacyCode, string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT pharmacy_code, username, password_hash, role, tenant_code, user_id, " +
            "access_token, refresh_token, last_verified_at FROM credential_cache " +
            "WHERE pharmacy_code = @pharmacyCode AND lower(username) = lower(@username)";
        cmd.Parameters.AddWithValue("@pharmacyCode", pharmacyCode);
        cmd.Parameters.AddWithValue("@username", username);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRow(reader) : null;
    }

    public void Save(CachedCredential credential)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var stmt = conn.CreateCommand())
        {
            stmt.Transaction = tx;
            stmt.CommandText =
                "INSERT INTO credential_cache (pharmacy_code, username, password_hash, role, " +
                "  tenant_code, user_id, access_token, refresh_token, last_verified_at, created_at, updated_at) " +
                "VALUES (@pharmacyCode, @username, @passwordHash, @role, @tenantCode, @userId, " +
                "  @accessToken, @refreshToken, @lastVerifiedAt, @now, @now) " +
                "ON CONFLICT(pharmacy_code, username) DO UPDATE SET " +
                "  password_hash = excluded.password_hash, role = excluded.role, " +
                "  tenant_code = excluded.tenant_code, user_id = excluded.user_id, " +
                "  access_token = excluded.access_token, refresh_token = excluded.refresh_token, " +
                "  last_verified_at = excluded.last_verified_at, updated_at = excluded.updated_at";
            stmt.Parameters.AddWithValue("@pharmacyCode", credential.PharmacyCode);
            stmt.Parameters.AddWithValue("@username", credential.Username);
            stmt.Parameters.AddWithValue("@passwordHash", credential.PasswordHash);
            stmt.Parameters.AddWithValue("@role", credential.Role);
            stmt.Parameters.AddWithValue("@tenantCode", credential.TenantCode);
            stmt.Parameters.AddWithValue("@userId", credential.UserId);
            stmt.Parameters.AddWithValue("@accessToken", SecretsBox.Protect(credential.AccessToken));
            stmt.Parameters.Add("@refreshToken", SqliteType.Blob).Value =
                string.IsNullOrEmpty(credential.RefreshToken)
                    ? DBNull.Value
                    : SecretsBox.Protect(credential.RefreshToken);
            stmt.Parameters.AddWithValue("@lastVerifiedAt", credential.LastVerifiedAt.ToUnixTimeSeconds());
            stmt.Parameters.AddWithValue("@now", now);
            stmt.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpdateVerifiedAt(string pharmacyCode, string username, DateTimeOffset at)
    {
        RunUpdate(
            "UPDATE credential_cache SET last_verified_at = @at, updated_at = @now " +
            "WHERE pharmacy_code = @pharmacyCode AND lower(username) = lower(@username)",
            stmt =>
            {
                stmt.Parameters.AddWithValue("@at", at.ToUnixTimeSeconds());
                stmt.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                stmt.Parameters.AddWithValue("@pharmacyCode", pharmacyCode);
                stmt.Parameters.AddWithValue("@username", username);
            });
    }

    public void UpdateTokens(string pharmacyCode, string username, string accessToken, string refreshToken)
    {
        RunUpdate(
            "UPDATE credential_cache SET access_token = @accessToken, refresh_token = @refreshToken, updated_at = @now " +
            "WHERE pharmacy_code = @pharmacyCode AND lower(username) = lower(@username)",
            stmt =>
            {
                stmt.Parameters.AddWithValue("@accessToken", SecretsBox.Protect(accessToken));
                stmt.Parameters.Add("@refreshToken", SqliteType.Blob).Value =
                    string.IsNullOrEmpty(refreshToken)
                        ? DBNull.Value
                        : SecretsBox.Protect(refreshToken);
                stmt.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                stmt.Parameters.AddWithValue("@pharmacyCode", pharmacyCode);
                stmt.Parameters.AddWithValue("@username", username);
            });
    }

    public void Delete(string pharmacyCode, string username)
    {
        RunUpdate(
            "DELETE FROM credential_cache WHERE pharmacy_code = @pharmacyCode AND lower(username) = lower(@username)",
            stmt =>
            {
                stmt.Parameters.AddWithValue("@pharmacyCode", pharmacyCode);
                stmt.Parameters.AddWithValue("@username", username);
            });
    }

    /// <summary>Logout clears the WHOLE cache (spec: "Logout clears cache").</summary>
    public void Clear()
    {
        RunUpdate("DELETE FROM credential_cache", _ => { /* no params */ });
    }

    public void Dispose() { /* connections are per-call */ }

    // --- internals -----------------------------------------------------------------

    private void RunUpdate(string sql, Action<SqliteCommand> binder)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var stmt = conn.CreateCommand();
        stmt.Transaction = tx;
        stmt.CommandText = sql;
        binder(stmt);
        stmt.ExecuteNonQuery();
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        try
        {
            if (!string.IsNullOrEmpty(_sqlCipherKeyHex))
            {
                using var key = conn.CreateCommand();
                key.CommandText = "PRAGMA key = \"x'" + _sqlCipherKeyHex + "'\"";
                key.ExecuteNonQuery();
            }
            using var ddl = conn.CreateCommand();
            ddl.CommandText = Ddl;
            ddl.ExecuteNonQuery();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static CachedCredential MapRow(SqliteDataReader reader)
    {
        var access = (byte[]?) reader["access_token"];
        var refresh = (byte[]?) reader["refresh_token"];
        return new CachedCredential
        {
            PharmacyCode = (string) reader["pharmacy_code"],
            Username = (string) reader["username"],
            PasswordHash = (string) reader["password_hash"],
            Role = (string) reader["role"],
            TenantCode = (string) reader["tenant_code"],
            UserId = (string) reader["user_id"],
            AccessToken = access is null || access.Length == 0 ? "" : SecretsBox.UnprotectString(access),
            RefreshToken = refresh is null || refresh.Length == 0 ? "" : SecretsBox.UnprotectString(refresh),
            LastVerifiedAt = DateTimeOffset.FromUnixTimeSeconds((long) reader["last_verified_at"]),
        };
    }
}
