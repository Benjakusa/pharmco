using System.Text.Json;
using Dapper;
using Pharmco.Api.Services;
using Pharmco.Core.Data;
using Pharmco.Core.Tenants;

namespace Pharmco.Api.Endpoints;

/// <summary>
/// License management endpoints.
///   POST /api/license/renew-request — submit M-Pesa payment for renewal
/// </summary>
public static class LicenseEndpoints
{
    public static async Task<IResult> RenewRequest(
        HttpContext http,
        LicenseRenewRequest body,
        TenantRepository tenantRepo,
        NpgsqlConnectionFactory connectionFactory,
        JwtService jwt)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null)
            return denied;

        var claims = Authz.Claims(http, jwt);
        if (claims is null)
            return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized);

        if (body?.MpesaRef is null || string.IsNullOrWhiteSpace(body.MpesaRef))
            return Error("bad_request", "mpesa_ref is required", StatusCodes.Status400BadRequest);

        await using var conn = await connectionFactory.OpenAsync();

        // Find tenant
        var tenant = await tenantRepo.GetByIdAsync(claims.TenantId);
        if (tenant is null)
            return Error("not_found", "tenant not found", StatusCodes.Status404NotFound);

        // Log the renewal request
        await conn.ExecuteAsync(
            "INSERT INTO master.audit_logs (tenant_id, action, detail, created_at) " +
            "VALUES (@TenantId, 'license_renewal_request', @Detail, NOW())",
            new
            {
                TenantId = tenant.Id,
                Detail = JsonSerializer.Serialize(new
                {
                    mpesa_ref = body.MpesaRef,
                    requested_at = DateTimeOffset.UtcNow,
                    requested_by = claims.UserId.ToString()
                })
            }, commandTimeout: 30);

        _logger?.LogInformation("License renewal requested for tenant {TenantId}, M-Pesa ref {MpesaRef}",
            tenant.Id, body.MpesaRef);

        return Results.Json(new
        {
            message = "Renewal request submitted. The Pharmco team will process your payment and update your license within 60 seconds.",
            submitted_at = DateTimeOffset.UtcNow,
            mpesa_ref = body.MpesaRef
        }, statusCode: StatusCodes.Status202Accepted);
    }

    private static ILogger<ApiLog>? _logger;
    public static void SetLogger(ILogger<ApiLog> logger) => _logger = logger;

    private static IResult Error(string code, string message, int statusCode)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    public sealed class LicenseRenewRequest
    {
        public string? MpesaRef { get; init; }
    }
}
