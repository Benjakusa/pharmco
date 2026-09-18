namespace Pharmco.Core.Auth;

/// <summary>
/// Facts a desktop client can present about a cached login — enough for the
/// offline-gate decision without pulling in SQLCipher/DPAPI concerns.
/// </summary>
public sealed class CachedCredentialFacts
{
    public string Username = "";
    public string PasswordHash = "";        // client-side bcrypt second hash (cost 12)
    public DateTimeOffset LastVerifiedAt { get; set; }   // last successful ONLINE verification
}

/// <summary>
/// Pure offline-login policy (shared by the WPF SessionManager and the unit
/// tests). Mirrors docs/architecture.md: cached credentials grant offline
/// login only when they were verified online within the staleness window.
/// </summary>
public sealed class OfflineGate
{
    /// <summary>Offline grace is 7 days (architecture §2 "Offline auth").</summary>
    public const long MaxStalenessDays = 7;

    public sealed class Evaluation
    {
        public bool Allowed;
        public bool CacheMissing;
        public bool Stale;             // cache exists but last_verified_at is too old
        public bool PasswordMatched;
        public string Reason = "";     // machine-readable: none | no_cached_credentials | cache_stale | invalid_password
    }

    public static Evaluation Evaluate(CachedCredentialFacts? cached, string password, DateTimeOffset now)
    {
        if (cached is null)
            return new Evaluation { Allowed = false, CacheMissing = true, PasswordMatched = false, Reason = "no_cached_credentials" };

        var stale = (now - cached.LastVerifiedAt).TotalSeconds > MaxStalenessDays * 86_400;
        if (stale)
            return new Evaluation { Allowed = false, CacheMissing = false, Stale = true, PasswordMatched = false, Reason = "cache_stale" };

        var matched = PasswordService.Verify(password, cached.PasswordHash);
        return new Evaluation { Allowed = matched, CacheMissing = false, Stale = false, PasswordMatched = matched, Reason = matched ? "none" : "invalid_password" };
    }
}