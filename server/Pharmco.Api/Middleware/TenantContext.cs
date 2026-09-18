using Npgsql;

namespace Pharmco.Api.Middleware;

/// <summary>
/// Request-scoped tenant state. The resolver middleware places one instance in
/// <c>HttpContext.Items</c>; endpoints resolve it via <see cref="TenantScope.Current"/>.
/// The <see cref="Connection"/> is an Npgsql pooled connection whose search_path
/// was SET to the tenant schema, so Dapper calls on it resolve to tenant tables
/// without any schema qualification.
/// </summary>
public sealed class TenantContext
{
    public required Guid TenantId { get; init; }
    public required string Code { get; init; }
    public required string SchemaName { get; init; }
    public required NpgsqlConnection Connection { get; init; }
    public DateTimeOffset? LicenseExpiresAt { get; init; }
}

public static class TenantScope
{
    public const string ContextKey = "pharmco.tenant";

    public static TenantContext? Current(HttpContext http)
        => http.Items.TryGetValue(ContextKey, out var value) ? value as TenantContext : null;
}