using Pharmco.Core.Data;
using Pharmco.Core.Licensing;
using Pharmco.Core.Tenants;

namespace Pharmco.Api.Middleware;

public sealed record AccessDecision(
    bool IsAllowed,
    int? RejectStatus,
    string? RejectReason,
    TenantContext? Tenant)
{
    public bool IsRejected => RejectStatus is not null;

    public static AccessDecision Allow(TenantContext tenant) => new(true, null, null, tenant);
    public static AccessDecision AllowAnonymous() => new(true, null, null, null);
    public static AccessDecision Reject(int status, string reason) => new(false, status, reason, null);
}

/// <summary>
/// Extracts <c>tenant_id</c> from the JWT, looks the tenant up in master,
/// enforces status + license grace, and opens a request-scoped connection with
/// <c>SET search_path TO tenant_&lt;code&gt;, master</c>.
///
///   • 401 when the caller is authenticated but the tenant_id claim is missing
///   • 403 when tenant.status != 'active' or the license is >30 days expired
///   • anonymous requests (e.g. /health) pass through untouched
/// </summary>
public sealed class TenantResolverMiddleware
{
    public const int LicenseGraceDays = 30;

    private readonly RequestDelegate _next;

    public TenantResolverMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, TenantRepository repository, NpgsqlConnectionFactory factory)
    {
        var decision = await ResolveAsync(context, repository, factory);

        if (decision.IsRejected)
        {
            context.Response.StatusCode = decision.RejectStatus!.Value;
            await context.Response.WriteAsJsonAsync(new { error = decision.RejectReason });
            return;
        }

        if (decision.Tenant is not null)
        {
            context.Items[TenantScope.ContextKey] = decision.Tenant;
            try
            {
                await _next(context);
            }
            finally
            {
                await decision.Tenant.Connection.DisposeAsync();
            }
        }
        else
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Decision logic factored out so it can be tested without an HTTP host.
    /// </summary>
    public static async Task<AccessDecision> ResolveAsync(
        HttpContext context,
        TenantRepository repository,
        NpgsqlConnectionFactory factory,
        CancellationToken ct = default)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return AccessDecision.AllowAnonymous();

        var tenantIdText = context.User.FindFirst(LicenseService.ClaimTenantId)?.Value;
        if (!Guid.TryParse(tenantIdText, out var tenantId))
            return AccessDecision.Reject(StatusCodes.Status401Unauthorized,
                $"missing '{LicenseService.ClaimTenantId}' claim");

        var tenant = await repository.GetByIdAsync(tenantId, ct);
        if (tenant is null || !string.Equals(tenant.Status, "active", StringComparison.Ordinal))
            return AccessDecision.Reject(StatusCodes.Status403Forbidden, "tenant is not active");

        if (tenant.LicenseExpiresAt is { } expiry
            && DateTimeOffset.UtcNow > expiry.AddDays(LicenseGraceDays))
            return AccessDecision.Reject(StatusCodes.Status403Forbidden, "license expired beyond grace period");

        var connection = await factory.OpenAsync(ct);
        try
        {
            await NpgsqlConnectionFactory.SetSearchPathAsync(connection, TenantNaming.SearchPathFor(tenant.SchemaName), ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return AccessDecision.Allow(new TenantContext
        {
            TenantId = tenant.Id,
            Code = tenant.Code,
            SchemaName = tenant.SchemaName,
            Connection = connection,
            LicenseExpiresAt = tenant.LicenseExpiresAt,
        });
    }
}