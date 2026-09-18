namespace Pharmco.Client.Services;

using java.sql;
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
/// JDBC SQLite driver and opens with <c>PRAGMA key = "x'&lt;256-bit hex&gt;'"</c>
/// (raw-key form, AES-256). The key itself never touches disk in clear — it is
/// DPAPI-wrapped by SecretsBox. The bare JDBC driver without cipher support is
/// DEV-only: set PHARMCO_PLAINTEXT_SQLITE=1 to disable the PRAGMA (never ship
/// or run pilots with that).
/// </summary>
public sealed class CredentialCache
{
    private static const string Ddl =
        "CREATE TABLE IF NOT EXISTS credential_cache (" +
        "  id                INTEGER PRIMARY KEY AUTOINCREMENT," +
        "  pharmacy_code     TEXT    NOT NULL," +
        "  username          TEXT    NOT NULL," +
        "  password_hash     TEXT    NOT NULL," +       // client-side bcrypt (cost 12)
        "  role              TEXT    NOT NULL," +
        "  tenant_code       TEXT    NOT NULL," +
        "  user_id           TEXT    NOT NULL," +
        "  access_token      BLOB    NOT NULL," +       // DPAPI-wrapped
        "  refresh_token     BLOB," +                   // DPAPI-wrapped
        "  last_verified_at  INTEGER NOT NULL," +       // epoch seconds (UTC)
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
        var keyFile = new java.io.File(keyPath);
        string keyHex;
        if (keyFile.exists())
            keyHex = SecretsBox.UnprotectString(java.nio.file.Files.readAllBytes(keyFile.toPath()));
        else
        {
            keyHex = SecretsBox.RandomHex(32);
            keyFile.getParentFile().mkdirs();
            java.nio.file.Files.write(keyFile.toPath(), SecretsBox.Protect(keyHex));
        }

        var plaintext = java.lang.System.getenv("PHARMCO_PLAINTEXT_SQLITE") == "1";
        return new CredentialCache(
            ConfigDir.DataPath("credentials.db"),
            plaintext ? "" : keyHex);
    }

    // --- queries ------------------------------------------------------------------

    public CachedCredential? Find(string pharmacyCode, string username)
    {
        var conn = Open();
        try
        {
            try (var st = conn.prepareStatement(
                "SELECT pharmacy_code, username, password_hash, role, tenant_code, user_id, " +
                "access_token, refresh_token, last_verified_at FROM credential_cache " +
                "WHERE pharmacy_code = ? AND lower(username) = lower(?)"))
            {
                st.setString(1, pharmacyCode);
                st.setString(2, username);
                try (var rs = st.executeQuery())
                {
                    if (!rs.next())
                        return null;
                    return MapRow(rs);
                }
            }
        }
        finally { conn.Close(); }
    }

    public void Save(CachedCredential c)
    {
        var now = DateTimeOffset.now().toEpochSecond();
        RunUpdate(
            "INSERT INTO credential_cache " +
            "(pharmacy_code, username, password_hash, role, tenant_code, user_id, " +
            " access_token, refresh_token, last_verified_at, created_at, updated_at) " +
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) " +
            "ON CONFLICT(pharmacy_code, username) DO UPDATE SET " +
            " password_hash = excluded.password_hash, role = excluded.role, " +
            " tenant_code = excluded.tenant_code, user_id = excluded.user_id, " +
            " access_token = excluded.access_token, refresh_token = excluded.refresh_token, " +
            " last_verified_at = excluded.last_verified_at, updated_at = excluded.updated_at",
            st =>
            {
                st.setString(1, c.PharmacyCode);
                st.setString(2, c.Username.ToLower());
                st.setString(3, c.PasswordHash);
                st.setString(4, c.Role);
                st.setString(5, c.TenantCode);
                st.setString(6, c.UserId);
                st.setBytes(7, SecretsBox.Protect(c.AccessToken));
                if (c.RefreshToken.IsEmpty()) st.setNull(8, Types.BLOB);
                else st.setBytes(8, SecretsBox.Protect(c.RefreshToken));
                st.setLong(9, c.LastVerifiedAt.toEpochSecond());
                st.setLong(10, now);
                st.setLong(11, now);
            });
    }

    public void UpdateVerifiedAt(string pharmacyCode, string username, DateTimeOffset at)
    {
        RunUpdate(
            "UPDATE credential_cache SET last_verified_at = ?, updated_at = ? " +
            "WHERE pharmacy_code = ? AND lower(username) = lower(?)",
            st =>
            {
                st.setLong(1, at.toEpochSecond());
                st.setLong(2, DateTimeOffset.now().toEpochSecond());
                st.setString(3, pharmacyCode);
                st.setString(4, username);
            });
    }

    public void UpdateTokens(string pharmacyCode, string username, string accessToken, string refreshToken)
    {
        RunUpdate(
            "UPDATE credential_cache SET access_token = ?, refresh_token = ?, updated_at = ? " +
            "WHERE pharmacy_code = ? AND lower(username) = lower(?)",
            st =>
            {
                st.setBytes(1, SecretsBox.Protect(accessToken));
                if (refreshToken.IsEmpty()) st.setNull(2, Types.BLOB);
                else st.setBytes(2, SecretsBox.Protect(refreshToken));
                st.setLong(3, DateTimeOffset.now().toEpochSecond());
                st.setString(4, pharmacyCode);
                st.setString(5, username);
            });
    }

    public void Delete(string pharmacyCode, string username)
    {
        RunUpdate(
            "DELETE FROM credential_cache WHERE pharmacy_code = ? AND lower(username) = lower(?)",
            st =>
            {
                st.setString(1, pharmacyCode);
                st.setString(2, username);
            });
    }

    /// <summary>Logout clears the WHOLE cache (spec: "Logout clears cache").</summary>
    public void Clear()
    {
        RunUpdate("DELETE FROM credential_cache", st => { /* no params */ });
    }

    // --- internals -----------------------------------------------------------------

    private interface Binder { void Bind(java.sql.PreparedStatement st); }

    private void RunUpdate(string sql, Binder binder)
    {
        var conn = Open();
        try
        {
            try (var st = conn.prepareStatement(sql))
            {
                binder.Bind(st);
                st.executeUpdate();
            }
            conn.Commit();
        }
        finally { conn.Close(); }
    }

    private Connection Open()
    {
        var conn = DriverManager.getConnection("jdbc:sqlite:" + _dbPath);
        try
        {
            if (!_sqlCipherKeyHex.IsEmpty())
            {
                try (var st = conn.createStatement())
                    st.execute("PRAGMA key = \"x'" + _sqlCipherKeyHex + "'\"");
            }
            try (var st = conn.createStatement())
                st.execute(Ddl);
            return conn;
        }
        catch (Throwable t)
        {
            conn.Close();
            throw t;
        }
    }

    private CachedCredential MapRow(java.sql.ResultSet rs) throws java.sql.SQLException
    {
        var access = rs.getBytes("access_token");
        var refresh = rs.getBytes("refresh_token");
        return new CachedCredential
        {
            PharmacyCode = rs.getString("pharmacy_code"),
            Username = rs.getString("username"),
            PasswordHash = rs.getString("password_hash"),
            Role = rs.getString("role"),
            TenantCode = rs.getString("tenant_code"),
            UserId = rs.getString("user_id"),
            AccessToken = access is null ? "" : SecretsBox.UnprotectString(access),
            RefreshToken = refresh is null ? "" : SecretsBox.UnprotectString(refresh),
            LastVerifiedAt = DateTimeOffset.ofEpochSecond(rs.getLong("last_verified_at"), ZoneOffset.UTC),
        };
    }
}