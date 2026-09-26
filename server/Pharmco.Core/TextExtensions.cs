namespace Pharmco.Core;

/// <summary>
/// Tiny string helpers shared by the server and the desktop client. Kept in the
/// root <c>Pharmco.Core</c> namespace so every <c>Pharmco.Core.*</c> type sees
/// them without an extra using.
/// </summary>
public static class TextExtensions
{
    /// <summary>True when the value is null or zero-length.</summary>
    public static bool IsEmpty(this string? value) => string.IsNullOrEmpty(value);
}
