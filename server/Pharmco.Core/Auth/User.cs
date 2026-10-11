namespace Pharmco.Core.Auth;

/// <summary>Mutable DTO for Dapper mapping of master.users rows.</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "cashier";   // 'admin' | 'pharmacist' | 'cashier'
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public UserRole RoleValue() => UserRoles.Parse(Role);
}