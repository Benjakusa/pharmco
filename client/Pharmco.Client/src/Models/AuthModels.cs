namespace Pharmco.Client.Models;

/// <summary>User block echoed by the auth endpoints.</summary>
public sealed class UserProfile
{
    public string Id = "";
    public string Role = "";
    public string TenantCode = "";
}

/// <summary>POST /api/auth/login and /api/auth/refresh response body.</summary>
public sealed class LoginResponse
{
    public string AccessToken = "";
    public string RefreshToken = "";
    public string TokenType = "";
    public int ExpiresIn = 0;
    public UserProfile User { get; set; } = new UserProfile();
}

/// <summary>
/// An active desktop session (online or offline). <c>Locked</c> is what the
/// auto-lock flips; the UI then requires the password (Unlock) before any
/// POS action is allowed again.
/// </summary>
public sealed class Session
{
    public string PharmacyCode = "";
    public UserProfile User { get; set; } = new UserProfile();
    public string AccessToken = "";
    public string RefreshToken = "";
    public bool Offline = false;
    public bool Locked = false;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastActivity { get; set; }
}

/// <summary>Row of the local SQLCipher credential cache (tokens DPAPI-encrypted).</summary>
public sealed class CachedCredential
{
    public string PharmacyCode = "";
    public string Username = "";
    public string PasswordHash = "";   // client-side bcrypt second hash (cost 12)
    public string Role = "";
    public string TenantCode = "";
    public string UserId = "";
    public string AccessToken = "";
    public string RefreshToken = "";
    public DateTimeOffset LastVerifiedAt { get; set; }
}

/// <summary>Machine-readable failure from the auth service / session manager.</summary>
public sealed class AuthError extends Exception
{
    public string Code = "";

    public AuthError(string code, string message)
    {
        super(message);
        Code = code;
    }
}