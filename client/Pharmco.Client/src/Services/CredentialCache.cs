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
/// SQLite native library and opens with <c>PRAGMA key = "x'&lt;256-bit hex&gt;'"</c>
/// (raw-key form, AES-256). The key itself never touches disk in clear — it is
/// DPAPI-wrapped by SecretsBox. The plain SQLite provider without cipher support
/// is DEV-only: set PHARMCO_PLAINTEXT_SQLITE=1 to disable the PRAGMA (never ship
/// or run pilots with that).
/// </summary>
public sealed class CredentialCache
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
        "  access_token      BLOB    NOT NULL," +   // SecretsBox-wrapped
        "  refresh_token     BLOB," +               // SecretsBox-wrapped
        "  last_verified_at  INTEGER NOT NULL," +   // epoch seconds (UTC)
        "  created_at        INTEGER NOT NULL," +
        "  updated_at        INTEGER NOT NULL," +
        "  UNIQUE (pharmacy_code, username)" +
        ")";

    private readonly string _dbPath;
    private readonly string _sqlCipherKeyHex;    // "" => plaintext dev mode

    public CredentialCache(string dbPath, string sqlCipherKeyHex)
    {
        _dbPath = dbPath;
        _sqlCipherKeyHex = sqlCipherKeyHex ?? "";
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
            "WHERE pharmacy_code = @pharmacy_code AND lower(username) = lower(@username)";
        cmd.Parameters.AddWithValue("@pharmacy_code", pharmacyCode);
        cmd.Parameters.AddWithValue("@username", username);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRow(reader) : null;
    }

    public void Save(CachedCredential c)
    {
        // Idempotent INSERT (ON CONFLICT DO UPDATE) — the UNIQUE (pharmacy_code, username)
        // row and the absolute clock ("last write wins") keep re-login deterministic.
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO credential_cache (access_token, refresh_token, pharmacy_code, username, " +
            "password_hash, role, tenant_code, user_id, last_verified_at, created_at, updated_at) " +
            "VALUES (@access_token, @refresh_token, @pharmacy_code, @username, " +
            "@password_hash, @role, @tenant_code, @user_id, @last_verified_at, @created_at, @updated_at) " +
            "ON CONFLICT (pharmacy_code, username) DO UPDATE SET " +
            "access_token = excluded.access_token, refresh_token = excluded.refresh_token, " +
            "password_hash = excluded.password_hash, role = excluded.role, " +
            "tenant_code = excluded.tenant_code, user_id = excluded.user_id, " +
            "last_verified_at = excluded.last_verified_at, updated_at = excluded.updated_at";
        BindToken(cmd, "@access_token", c.AccessToken);
        BindToken(cmd, "@refresh_token", c.RefreshToken);
        cmd.Parameters.AddWithValue("@pharmacy_code", c.PharmacyCode);
        cmd.Parameters.AddWithValue("@username", c.Username);
        cmd.Parameters.AddWithValue("@password_hash", c.PasswordHash);
        cmd.Parameters.AddWithValue("@role", c.Role);
        cmd.Parameters.AddWithValue("@tenant_code", c.TenantCode);
        cmd.Parameters.AddWithValue("@user_id", c.UserId);
        cmd.Parameters.AddWithValue("@last_verified_at", c.LastVerifiedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void UpdateVerifiedAt(string pharmacyCode, string username, DateTimeOffset at)
    {
        RunUpdate(
            "UPDATE credential_cache SET last_verified_at = @last_verified_at, updated_at = @now " +
            "WHERE pharmacy_code = @pharmacy_code AND lower(username) = lower(@username)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@last_verified_at", at.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@pharmacy_code", pharmacyCode);
                cmd.Parameters.AddWithValue("@username", username);
            });
    }

    public void UpdateTokens(string pharmacyCode, string username, string accessToken, string refreshToken)
    {
        RunUpdate(
            "UPDATE credential_cache SET access_token = @access_token, refresh_token = @refresh_token, " +
            "updated_at = @now WHERE pharmacy_code = @pharmacy_code AND lower(username) = lower(@username)",
            cmd =>
            {
                BindToken(cmd, "@access_token", accessToken);
                BindToken(cmd, "@refresh_token", refreshToken);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@pharmacy_code", pharmacyCode);
                cmd.Parameters.AddWithValue("@username", username);
            });
    }

    public void Delete(string pharmacyCode, string username)
    {
        RunUpdate(
            "DELETE FROM credential_cache WHERE pharmacy_code = @pharmacy_code AND lower(username) = lower(@username)",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@pharmacy_code", pharmacyCode);
                cmd.Parameters.AddWithValue("@username", username);
            });
    }

    /// <summary>Logout clears the WHOLE cache (spec: "Logout clears cache").</summary>
    public void Clear()
    {
        RunUpdate("DELETE FROM credential_cache", _ => { /* no params */ });
    }

    // --- internals -----------------------------------------------------------------

    private void RunUpdate(string sql, Action<SqliteCommand> bind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        try
        {
            if (!string.IsNullOrEmpty(_sqlCipherKeyHex))
                Execute(conn, "PRAGMA key = \"x'" + _sqlCipherKeyHex + "'\"");
            Execute(conn, Ddl);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Wraps a token for storage; empty ⇒ NULL (the column is nullable).</summary>
    private static void BindToken(SqliteCommand cmd, string name, string token)
    {
        if (string.IsNullOrEmpty(token))
            cmd.Parameters.AddWithValue(name, DBNull.Value);
        else
            cmd.Parameters.AddWithValue(name, SecretsBox.Protect(token));
    }

    private static CachedCredential MapRow(SqliteDataReader reader)
    {
        var access = reader.IsDBNull(6) ? null : reader.GetFieldValue<byte[]>(6);
        var refresh = reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7);
        return new CachedCredential
        {
            PharmacyCode = reader.GetString(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2),
            Role = reader.GetString(3),
            TenantCode = reader.GetString(4),
            UserId = reader.GetString(5),
            AccessToken = access is null ? "" : SecretsBox.UnprotectString(access),
            RefreshToken = refresh is null ? "" : SecretsBox.UnprotectString(refresh),
            LastVerifiedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)),
        };
    }
}

