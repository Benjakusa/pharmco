using System.Security.Cryptography;
using System.Text;

namespace Pharmco.Core.Security;

/// <summary>
/// Cryptographically random passphrases for the one-time admin password.
/// Alphabet excludes ambiguous 0/O, 1/I/l.
/// </summary>
public static class CharacterSet
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string Generate(int length = 10)
    {
        if (length < 1) throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (var b in bytes)
            sb.Append(Alphabet[b % Alphabet.Length]);
        return sb.ToString();
    }
}