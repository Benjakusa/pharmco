namespace Pharmco.Core.Auth;

using BCrypt.Net;

/// <summary>
/// bcrypt (cost 12) wrapper used server-side for master.users.password_hash and
/// client-side for the offline credential cache's second hash.
/// </summary>
public sealed class PasswordService
{
    public const int Cost = 12;

    public static string Hash(string raw)
    {
        if (raw.IsEmpty())
            throw new ArgumentException("password must not be empty", nameof(raw));
        return BCrypt.HashPassword(raw, BCrypt.GenerateSalt(Cost));
    }

    public static bool Verify(string raw, string hash)
    {
        if (raw.IsEmpty() || hash.IsEmpty())
            return false;
        return BCrypt.Verify(raw, hash);
    }
}