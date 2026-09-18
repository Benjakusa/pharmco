namespace Pharmco.Core.Auth;

/// <summary>
/// Fixed role set for the MVP (matches the CHECK constraint on
/// master.users.role). The role also rides in every JWT access token.
/// </summary>
public enum UserRole
{
    admin,
    pharmacist,
    cashier,
}

/// <summary>Case-insensitive parse + wire/DB helpers for <see cref="UserRole"/>.</summary>
public static class UserRoles
{
    /// <summary>Parse the DB/JWT value ('admin' | 'pharmacist' | 'cashier').</summary>
    public static UserRole Parse(string value)
    {
        if (Enum.TryParse<UserRole>(value, ignoreCase: true, out var role)
            && Enum.IsDefined(typeof(UserRole), role))
            return role;
        throw new ArgumentException(
            $"unknown role '{value}' (expected admin|pharmacist|cashier)", nameof(value));
    }

    /// <summary>Wire/DB form — lowercase enum member name.</summary>
    public static string ToWireString(this UserRole role) => role.ToString().ToLowerInvariant();
}