namespace Pharmco.Core.Tenants;

using Npgsql;

/// <summary>
/// Request-scoped tenant context attached by <c>TenantResolverMiddleware</c>.
/// <c>Connection</c> is a fresh Npgsql connection whose search_path already
/// resolves unqualified table names to this tenant's schema (then master), so
/// repositories run against the tenant data without embedding schema names.
///
/// <c>Current(http)</c> is how endpoint handlers reach the tenant scope; the
/// middleware actually stores/removes it, and the connection is disposed by the
/// middleware's finally block after the handler completes.
/// </summary>
public sealed class TenantScope
{
    public const string RequestKey = "pharmco.tenant_scope";

    public Guid TenantId { get; set; }
    public string Code = "";
    public string SchemaName = "";
    public DateTimeOffset? LicenseExpiresAt { get; set; }
    public NpgsqlConnection Connection { get; set; }

    /// <summary>
    /// Reads the tenant scope previously attached by TenantResolverMiddleware.
    /// Returns null on public routes (login/refresh) where no bearer token was
    /// presented — handlers must treat null as "not tenant-resolvable".
    /// </summary>
    public static TenantScope? Current(HttpContext request)
        => request.Items.TryGetValue(RequestKey, out var value) ? value as TenantScope : null;
}