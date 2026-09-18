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
        if (raw is null || raw.isEmpty())
            throw new ArgumentException("password must not be empty", "raw");
        return BCrypt.Hashpw(raw, BCrypt.Gensalt(Cost));
    }

    public static bool Verify(string raw, string hash)
    {
        if (raw is null || hash is null || hash.isEmpty())
            return false;
        return BCrypt.Verifypw(raw, hash);
    }
}