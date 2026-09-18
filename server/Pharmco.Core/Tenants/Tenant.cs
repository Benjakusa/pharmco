namespace Pharmco.Core.Tenants;

/// <summary>Mutable DTO for Dapper mapping of master.tenants rows.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerPhone { get; set; } = "";
    public string? LicenseKey { get; set; }
    public DateTimeOffset? LicenseExpiresAt { get; set; }
    public string SchemaName { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}