namespace Pharmco.Api.Endpoints;

using Dapper;
using Pharmco.Api.Services;
using Pharmco.Api.Services.Daraja;
using Pharmco.Core.Security;
using Pharmco.Core.Sales;
using Microsoft.Extensions.Logging;

/// <summary>
/// Admin-only Daraja configuration endpoints (admin role required).
///   POST   /api/daraja/config    — save encrypted credentials
///   POST   /api/daraja/test      — send KSh 1 test STK Push
///   GET    /api/daraja/status    — get config status (masked)
/// </summary>
public static class DarajaConfigEndpoints
{
    // ------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------

    public sealed class DarajaConfigRequestDto
    {
        public string ConsumerKey { get; set; } = "";
        public string ConsumerSecret { get; set; } = "";
        public string Passkey { get; set; } = "";
        public string Shortcode { get; set; } = "";
        public string ShortcodeType { get; set; } = "paybill";
    }

    public sealed class DarajaTestRequestDto
    {
        public string PhoneNumber { get; set; } = "";
    }

    // ------------------------------------------------------------------
    // POST /api/daraja/config
    // ------------------------------------------------------------------

    public static async Task<IResult> SaveConfig(
        HttpContext http,
        DarajaConfigRequestDto body,
        DarajaService daraja,
        IConfiguration configuration,
        ILogger<ApiLog> logger)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null)
            return denied;

        if (body is null || string.IsNullOrWhiteSpace(body.ConsumerKey)
            || string.IsNullOrWhiteSpace(body.ConsumerSecret)
            || string.IsNullOrWhiteSpace(body.Passkey)
            || string.IsNullOrWhiteSpace(body.Shortcode))
        {
            return Error("bad_request",
                "consumer_key, consumer_secret, passkey and shortcode are required",
                StatusCodes.Status400BadRequest);
        }

        var shortcodeType = (body.ShortcodeType ?? "").ToLowerInvariant();
        if (shortcodeType != "paybill" && shortcodeType != "till")
        {
            return Error("bad_request", "shortcode_type must be 'paybill' or 'till'",
                StatusCodes.Status400BadRequest);
        }

        // Validate shortcode format: numeric, 6-10 digits
        if (!System.Text.RegularExpressions.Regex.IsMatch(body.Shortcode, @"^\d{6,10}$"))
        {
            return Error("bad_request", "shortcode must be 6-10 digits",
                StatusCodes.Status400BadRequest);
        }

        var claims = Authz.Claims(http, http.RequestServices.GetRequiredService<JwtService>());
        if (claims is null)
            return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized);

        var encryptionKey = configuration["Daraja:EncryptionKey"]
            ?? throw new InvalidOperationException("Daraja__EncryptionKey environment variable is not set");

        var encryption = new DarajaEncryption(encryptionKey);

        // Encrypt each field with AES-256-GCM (per-field random IV)
        var consumerKeyEnc = encryption.Encrypt(body.ConsumerKey);
        var consumerSecretEnc = encryption.Encrypt(body.ConsumerSecret);
        var passkeyEnc = encryption.Encrypt(body.Passkey);

        await using var conn = await daraja.GetOpenConnectionAsync();

        // Upsert into master.daraja_config for current tenant
        var existing = await conn.QuerySingleOrDefaultAsync<DarajaConfigRow>(
            "SELECT * FROM master.daraja_config WHERE tenant_id = @TenantId",
            new { TenantId = claims.TenantId }, commandTimeout: 30);

        if (existing is null)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO master.daraja_config
                      (tenant_id, consumer_key_enc, consumer_secret_enc, passkey_enc, shortcode, shortcode_type)
                  VALUES (@TenantId, @ConsumerKeyEnc, @ConsumerSecretEnc, @PasskeyEnc, @Shortcode, @ShortcodeType)",
                new
                {
                    TenantId = claims.TenantId,
                    ConsumerKeyEnc = consumerKeyEnc,
                    ConsumerSecretEnc = consumerSecretEnc,
                    PasskeyEnc = passkeyEnc,
                    Shortcode = body.Shortcode,
                    ShortcodeType = shortcodeType,
                }, commandTimeout: 30);
        }
        else
        {
            await conn.ExecuteAsync(
                @"UPDATE master.daraja_config
                  SET consumer_key_enc = @ConsumerKeyEnc,
                      consumer_secret_enc = @ConsumerSecretEnc,
                      passkey_enc = @PasskeyEnc,
                      shortcode = @Shortcode,
                      shortcode_type = @ShortcodeType,
                      verified_at = NULL
                  WHERE tenant_id = @TenantId",
                new
                {
                    TenantId = claims.TenantId,
                    ConsumerKeyEnc = consumerKeyEnc,
                    ConsumerSecretEnc = consumerSecretEnc,
                    PasskeyEnc = passkeyEnc,
                    Shortcode = body.Shortcode,
                    ShortcodeType = shortcodeType,
                }, commandTimeout: 30);
        }

        logger.LogInformation("Daraja config saved for tenant {TenantId}, shortcode {Shortcode}",
            claims.TenantId, body.Shortcode);

        return Results.Json(new { message = "Daraja config saved" }, statusCode: StatusCodes.Status200OK);
    }

    // ------------------------------------------------------------------
    // POST /api/daraja/test
    // ------------------------------------------------------------------

    public static async Task<IResult> TestConfig(
        HttpContext http,
        DarajaTestRequestDto body,
        DarajaService daraja,
        ILogger<ApiLog> logger)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null)
            return denied;

        if (body is null || string.IsNullOrWhiteSpace(body.PhoneNumber))
        {
            return Error("bad_request", "phone_number is required",
                StatusCodes.Status400BadRequest);
        }

        // Validate phone: 10-15 digits, optionally starting with +
        if (!System.Text.RegularExpressions.Regex.IsMatch(body.PhoneNumber, @"^\+?\d{10,15}$"))
        {
            return Error("bad_request", "phone_number must be 10-15 digits (optionally with + prefix)",
                StatusCodes.Status400BadRequest);
        }

        var claims = Authz.Claims(http, http.RequestServices.GetRequiredService<JwtService>());
        if (claims is null)
            return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized);

        // Check config exists
        var credentials = await daraja.GetCredentialsAsync(claims.TenantId);
        if (credentials is null)
        {
            return Error("not_configured", "Daraja config not found. Save config first.",
                StatusCodes.Status404NotFound);
        }

        // Send KSh 1 STK Push
        var result = await daraja.PushStkAsync(
            claims.TenantId,
            body.PhoneNumber,
            1,  // KSh 1 test
            "TEST-001",
            "Daraja Configuration Test",
            http.RequestAborted);

        if (!result.Success)
        {
            logger.LogWarning("Daraja test STK Push failed for tenant {TenantId}: {Error}",
                claims.TenantId, result.Error);
            return Error("stk_push_failed",
                $"STK Push failed: {result.Error}",
                StatusCodes.Status422UnprocessableEntity);
        }

        // Update verified_at on success
        await using var conn = await daraja.GetOpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE master.daraja_config SET verified_at = NOW() WHERE tenant_id = @TenantId",
            new { TenantId = claims.TenantId }, commandTimeout: 30);

        logger.LogInformation("Daraja test STK Push successful for tenant {TenantId}, checkout {CheckoutId}",
            claims.TenantId, result.CheckoutRequestId);

        return Results.Json(new
        {
            message = "Test STK Push sent successfully. Check your phone.",
            checkout_request_id = result.CheckoutRequestId,
        }, statusCode: StatusCodes.Status200OK);
    }

    // ------------------------------------------------------------------
    // GET /api/daraja/status
    // ------------------------------------------------------------------

    public static async Task<IResult> GetStatus(
        HttpContext http,
        DarajaService daraja,
        ILogger<ApiLog> logger)
    {
        var denied = Authz.RequireAdmin(http);
        if (denied is not null)
            return denied;

        var claims = Authz.Claims(http, http.RequestServices.GetRequiredService<JwtService>());
        if (claims is null)
            return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized);

        await using var conn = await daraja.GetOpenConnectionAsync();
        var row = await conn.QuerySingleOrDefaultAsync<DarajaConfigRow>(
            "SELECT * FROM master.daraja_config WHERE tenant_id = @TenantId",
            new { TenantId = claims.TenantId }, commandTimeout: 30);

        if (row is null)
        {
            return Results.Json(new DarajaStatusResponse
            {
                Configured = false,
                VerifiedAt = null,
                ShortcodeMasked = "",
                ShortcodeType = "",
            }, statusCode: StatusCodes.Status200OK);
        }

        return Results.Json(new DarajaStatusResponse
        {
            Configured = true,
            VerifiedAt = row.VerifiedAt,
            ShortcodeMasked = DarajaEncryption.MaskShortcode(row.Shortcode),
            ShortcodeType = row.ShortcodeType,
        }, statusCode: StatusCodes.Status200OK);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static IResult Error(string code, string message, int statusCode)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    private sealed class DarajaConfigRow
    {
        public Guid TenantId { get; init; }
        public byte[] ConsumerKeyEnc { get; init; } = Array.Empty<byte>();
        public byte[] ConsumerSecretEnc { get; init; } = Array.Empty<byte>();
        public byte[] PasskeyEnc { get; init; } = Array.Empty<byte>();
        public string Shortcode { get; init; } = "";
        public string ShortcodeType { get; init; } = "paybill";
        public DateTimeOffset? VerifiedAt { get; init; }
    }
}
