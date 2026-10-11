namespace Pharmco.Client.Services;

using System.Threading;
using Pharmco.Client.Models;
using Pharmco.Core.Auth;

/// <summary>
/// Desktop session orchestrator.
///   * LoginOnlineAsync  → API login, seed the SQLCipher cache, start session
///   * LoginOffline      → OfflineGate policy: cached bcrypt hash + ≤7-day
///                         last_verified_at, or reject with a machine-readable
///                         reason (spec: stale cache → require online login)
///   * OnLaunchAsync     → online-first: validate cached access token (or
///                         rotate via refresh token), else the UI falls through
///                         to the login screen where offline login may succeed
///   * Auto-lock         → IdlePolicy: after AutoLockMinutes idle the session
///                         is Locked and a password is required to Unlock
///   * LogoutAsync       → server revoke (best-effort) + cache cleared
/// </summary>
public sealed class SessionManager
{
    public sealed class Options
    {
        public int AutoLockMinutes = IdlePolicy.DefaultAutoLockMinutes; // 5, configurable
        public long IdleCheckIntervalSeconds = 30;
    }

    public sealed class LoginOutcome
    {
        public bool Allowed = false;
        public string Reason = "no_cached_credentials"; // none | invalid_password | cache_stale | server_rejected
        public Session? Session;
    }

    public sealed class LaunchOutcome
    {
        public bool HasSession = false;
        public Session? Session;
    }

    private readonly AuthService _auth;
    private readonly CredentialCache _cache;
    private readonly Options _options;
    private Session? _session;
    private Timer? _idleTimer;
    private Action<Session>? _onLocked;

    public SessionManager(AuthService auth, CredentialCache cache, Options options)
    {
        _auth = auth;
        _cache = cache;
        _options = options ?? new Options();
    }

    public Session? CurrentSession() => _session;

    public bool IsLocked() => _session is not null && _session.Locked;

    /// <summary>UI hook: invoked when auto-lock flips a session to Locked.</summary>
    public void SetOnLocked(Action<Session> onLocked) => _onLocked = onLocked;

    // --- login ------------------------------------------------------------------

    public async Task<LoginOutcome> LoginOnlineAsync(string pharmacyCode, string username, string password)
    {
        LoginResponse resp;
        try { resp = await _auth.LoginAsync(pharmacyCode, username, password); }
        catch (AuthError) { return new LoginOutcome { Allowed = false, Reason = "server_rejected" }; }

        var now = DateTimeOffset.UtcNow;
        _cache.Save(new CachedCredential
        {
            PharmacyCode = pharmacyCode,
            Username = username.ToLower(),
            PasswordHash = PasswordService.Hash(password),     // client-side second hash
            Role = resp.User.Role,
            TenantCode = resp.User.TenantCode,
            UserId = resp.User.Id,
            AccessToken = resp.AccessToken,
            RefreshToken = resp.RefreshToken,
            LastVerifiedAt = now,
        });

        var session = NewSession(pharmacyCode, resp, offline: false);
        StartSession(session);
        return new LoginOutcome { Allowed = true, Reason = "none", Session = session };
    }

    // Synchronous on purpose: everything happens against the local cache.
    public LoginOutcome LoginOffline(string pharmacyCode, string username, string password)
    {
        var cached = _cache.Find(pharmacyCode, username);
        var facts = cached is null ? null : new CachedCredentialFacts
        {
            Username = cached.Username,
            PasswordHash = cached.PasswordHash,
            LastVerifiedAt = cached.LastVerifiedAt,
        };
        var verdict = OfflineGate.Evaluate(facts, password, DateTimeOffset.UtcNow);
        if (!verdict.Allowed)
            return new LoginOutcome { Allowed = false, Reason = verdict.Reason };

        var session = SessionFromCache(pharmacyCode, cached!, offline: true);
        StartSession(session);
        return new LoginOutcome { Allowed = true, Reason = "none", Session = session };
    }

    // --- launch -----------------------------------------------------------------

    /// <summary>Online-first launch: validate cached access token (or refresh), else fall to login.</summary>
    public async Task<LaunchOutcome> OnLaunchAsync(string? pharmacyCode, string? lastUsername)
    {
        if (string.IsNullOrEmpty(pharmacyCode) || string.IsNullOrEmpty(lastUsername))
            return new LaunchOutcome { HasSession = false };

        var cached = _cache.Find(pharmacyCode, lastUsername);
        if (cached is null)
            return new LaunchOutcome { HasSession = false };

        // 1. Cached access token still valid online?
        try
        {
            if (!string.IsNullOrEmpty(cached.AccessToken)
                && await _auth.ValidateOnlineAsync(cached.AccessToken))
            {
                _cache.UpdateVerifiedAt(pharmacyCode, lastUsername, DateTimeOffset.UtcNow);
                var session = SessionFromCache(pharmacyCode, cached, offline: false);
                StartSession(session);
                return new LaunchOutcome { HasSession = true, Session = session };
            }

            // 2. Rotate via refresh token.
            if (!string.IsNullOrEmpty(cached.RefreshToken))
            {
                var resp = await _auth.RefreshAsync(cached.RefreshToken);
                _cache.UpdateTokens(pharmacyCode, lastUsername, resp.AccessToken, resp.RefreshToken);
                _cache.UpdateVerifiedAt(pharmacyCode, lastUsername, DateTimeOffset.UtcNow);
                var session = NewSession(pharmacyCode, resp, offline: false);
                StartSession(session);
                return new LaunchOutcome { HasSession = true, Session = session };
            }
        }
        catch (AuthError) { /* fall through to login screen */ }

        return new LaunchOutcome { HasSession = false };
    }

    // --- activity / lock --------------------------------------------------------

    public void NotifyActivity()
    {
        if (_session is not null && !_session.Locked)
            _session.LastActivity = DateTimeOffset.UtcNow;
    }

    public void Lock()
    {
        if (_session is null || _session.Locked) return;
        _session.Locked = true;
        _onLocked?.Invoke(_session);
    }

    /// <summary>Unlock online (preferred) or against a fresh cache.</summary>
    public async Task<bool> UnlockAsync(string pharmacyCode, string username, string password)
    {
        var ok = false;
        try { await _auth.LoginAsync(pharmacyCode, username, password); ok = true; }
        catch (AuthError)
        {
            var cached = _cache.Find(pharmacyCode, username);
            if (cached is not null)
            {
                var facts = new CachedCredentialFacts
                {
                    Username = cached.Username,
                    PasswordHash = cached.PasswordHash,
                    LastVerifiedAt = cached.LastVerifiedAt,
                };
                ok = OfflineGate.Evaluate(facts, password, DateTimeOffset.UtcNow).Allowed;
            }
        }
        if (ok && _session is not null)
            _session.Locked = false;
        NotifyActivity();
        return ok;
    }

    // --- logout -----------------------------------------------------------------

    public async Task LogoutAsync()
    {
        var session = _session;
        StopIdleTimer();
        _session = null;
        if (session is not null)
        {
            try { await _auth.LogoutAsync(session.AccessToken, session.RefreshToken); }
            catch (AuthError) { /* best-effort — cache is cleared regardless */ }
        }
        _cache.Clear();                    // spec: "Logout clears cache"
    }

    // --- internals ---------------------------------------------------------------

    private void StartSession(Session session)
    {
        StopIdleTimer();
        _session = session;
        if (_options.AutoLockMinutes > 0)
        {
            var interval = TimeSpan.FromSeconds(_options.IdleCheckIntervalSeconds);
            _idleTimer = new Timer(_ => CheckIdle(), null, interval, interval);
        }
    }

    private void CheckIdle()
    {
        if (_session is null || _session.Locked) return;
        if (IdlePolicy.ShouldLock(_session.LastActivity, DateTimeOffset.UtcNow, _options.AutoLockMinutes))
            Lock();
    }

    private void StopIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private static Session NewSession(string pharmacyCode, LoginResponse resp, bool offline)
    {
        var now = DateTimeOffset.UtcNow;
        return new Session
        {
            PharmacyCode = pharmacyCode,
            User = new UserProfile { Id = resp.User.Id, Role = resp.User.Role, TenantCode = resp.User.TenantCode },
            AccessToken = resp.AccessToken,
            RefreshToken = resp.RefreshToken,
            Offline = offline,
            Locked = false,
            StartedAt = now,
            LastActivity = now,
        };
    }

    private static Session SessionFromCache(string pharmacyCode, CachedCredential cached, bool offline)
    {
        var now = DateTimeOffset.UtcNow;
        return new Session
        {
            PharmacyCode = pharmacyCode,
            User = new UserProfile { Id = cached.UserId, Role = cached.Role, TenantCode = cached.TenantCode },
            AccessToken = cached.AccessToken,
            RefreshToken = cached.RefreshToken,
            Offline = offline,
            Locked = false,
            StartedAt = now,
            LastActivity = now,
        };
    }
}
