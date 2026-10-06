namespace Pharmco.Core.Security;

/// <summary>bcrypt hashing. Work factor 12 is baked into the contract.</summary>
public static class PasswordHasher
{
    public const int WorkFactor = 12;

    public static string Hash(string password)
        => BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(WorkFactor));

    public static bool Verify(string password, string hash)
        => BCrypt.Net.BCrypt.Verify(password, hash);
}