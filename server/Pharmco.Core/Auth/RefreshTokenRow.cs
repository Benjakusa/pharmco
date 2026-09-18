namespace Pharmco.Core.Auth;

/// <summary>
/// Mutable DTO for Dapper mapping of master.refresh_tokens rows (JOINed to
/// master.users so TenantId is available for scoped lookups after refresh).
/// </summary>
public sealed class RefreshTokenRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}