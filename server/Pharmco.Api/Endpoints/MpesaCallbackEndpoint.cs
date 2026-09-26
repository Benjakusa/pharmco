namespace Pharmco.Api.Endpoints;

using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmco.Api.Services.Daraja;
using Pharmco.Core.Sales;
using Microsoft.Extensions.Logging;

/// <summary>
/// Safaricom M-Pesa STK Push callback handler.
/// NO auth required — Safaricom calls this endpoint.
///   • Extracts ShortCode from callback body
///   • Looks up tenant by matching decrypted shortcode → sets tenant context
///   • Matches sale by AccountReference (invoice_no)
///   • If ResultCode == 0 → mark sale paid, store MpesaReceiptNumber
///   • Else → mark sale failed, log reason
///   • Always return { ResultCode: 0, ResultDesc: "Accepted" } to Safaricom
///   • Idempotent: same CheckoutRequestID processed once only
/// </summary>
public static class MpesaCallbackEndpoint
{
    public static async Task<IResult> HandleCallback(
        HttpContext http,
        DarajaService daraja,
        ILogger<ApiLog> logger)
    {
        // Read raw body for audit logging
        http.Request.EnableBuffering();
        using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        http.Request.Body.Position = 0;

        // Always return success to Safaricom to avoid retries
        var acceptResponse = Results.Json(
            new { ResultCode = 0, ResultDesc = "Accepted" },
            statusCode: StatusCodes.Status200OK);

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            logger.LogWarning("Empty callback body received");
            return acceptResponse;
        }

        // Parse callback JSON
        StkPushCallback? callback;
        try
        {
            callback = JsonSerializer.Deserialize<StkPushCallback>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse callback JSON");
            return acceptResponse;
        }

        if (callback is null)
            return acceptResponse;

        // Extract ShortCode from callback metadata
        var shortcode = ExtractShortCode(callback);
        if (string.IsNullOrWhiteSpace(shortcode))
        {
            logger.LogWarning("No ShortCode in callback: {Body}", Truncate(rawBody, 200));
            return acceptResponse;
        }

        logger.LogInformation("Callback received for shortcode {Shortcode}, checkout {CheckoutId}",
            shortcode, callback.CheckoutRequestID);

        // Look up tenant by matching shortcode (exact match on stored shortcode)
        var tenantId = await FindTenantByShortCodeAsync(daraja, shortcode, logger);
        if (tenantId is null)
        {
            logger.LogWarning("Unknown shortcode {Shortcode} — rejected", shortcode);
            // Don't return error to Safaricom — they'll keep retrying
            return acceptResponse;
        }

        // Idempotency check: has this CheckoutRequestID been processed?
        await using var conn = await daraja.GetOpenConnectionAsync();
        var processed = await conn.ExecuteScalarAsync<int?>(
            "SELECT 1 FROM master.daraja_callback_log WHERE checkout_request_id = @CheckoutRequestId",
            new { CheckoutRequestId = callback.CheckoutRequestID }, commandTimeout: 30);

        if (processed == 1)
        {
            logger.LogInformation("Duplicate callback for CheckoutRequestID {CheckoutId} — ignored",
                callback.CheckoutRequestID);
            return acceptResponse;
        }

        // Log the callback for audit trail
        await LogCallbackAsync(conn, callback, rawBody, tenantId, logger);

        // Extract invoice_no (AccountReference) to find the sale
        var invoiceNo = ExtractAccountReference(callback);
        if (string.IsNullOrWhiteSpace(invoiceNo))
        {
            logger.LogWarning("No AccountReference in callback for checkout {CheckoutId}",
                callback.CheckoutRequestID);
            return acceptResponse;
        }

        // Find sale in tenant's schema and update status
        var resultCode = int.TryParse(callback.ResultCode, out var code) ? code : -1;
        var receiptNo = ExtractMpesaReceiptNumber(callback);

        try
        {
            if (resultCode == 0)
            {
                await MarkSalePaidAsync(conn, tenantId.Value, invoiceNo, receiptNo, logger);
                logger.LogInformation("Sale {InvoiceNo} marked paid — receipt {ReceiptNo}",
                    invoiceNo, receiptNo);
            }
            else
            {
                var reason = callback.ResultDesc ?? $"ResultCode={resultCode}";
                await MarkSaleFailedAsync(conn, tenantId.Value, invoiceNo, resultCode, reason, logger);
                logger.LogWarning("Sale {InvoiceNo} marked failed — {Reason}", invoiceNo, reason);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update sale {InvoiceNo} for callback", invoiceNo);
            // Still return success to Safaricom
        }

        logger.LogInformation("Callback processed: tenant={TenantId}, checkout={CheckoutId}, " +
            "invoice={InvoiceNo}, result={ResultCode}",
            tenantId, callback.CheckoutRequestID, invoiceNo, resultCode);

        return acceptResponse;
    }

    // ------------------------------------------------------------------
    // Extractors
    // ------------------------------------------------------------------

    private static string ExtractShortCode(StkPushCallback callback)
    {
        if (callback.CallbackMetadata?.Item?.Values is null)
            return "";

        // Safaricom sends ShortCode in the callback metadata values
        return callback.CallbackMetadata.Item.Values
            .FirstOrDefault(v => v.Key.Equals("ShortCode", StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private static string ExtractAccountReference(StkPushCallback callback)
    {
        if (callback.CallbackMetadata?.Item?.Values is null)
            return "";

        return callback.CallbackMetadata.Item.Values
            .FirstOrDefault(v => v.Key.Equals("AccountReference", StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private static string ExtractMpesaReceiptNumber(StkPushCallback callback)
    {
        if (callback.CallbackMetadata?.Item?.Values is null)
            return "";

        return callback.CallbackMetadata.Item.Values
            .FirstOrDefault(v => v.Key.Equals("MpesaReceiptNumber", StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    // ------------------------------------------------------------------
    // Tenant resolution
    // ------------------------------------------------------------------

    private static async Task<Guid?> FindTenantByShortCodeAsync(
        DarajaService daraja,
        string shortcode,
        ILogger logger)
    {
        await using var conn = await daraja.GetOpenConnectionAsync();

        // Query master.daraja_config for matching shortcode
        // Shortcode is stored in plaintext (only secrets are encrypted)
        var row = await conn.QuerySingleOrDefaultAsync<DarajaConfigRow>(
            "SELECT tenant_id, shortcode FROM master.daraja_config WHERE shortcode = @Shortcode",
            new { Shortcode = shortcode }, commandTimeout: 30);

        if (row is null)
            return null;

        logger.LogInformation("Shortcode {Shortcode} → tenant {TenantId}", shortcode, row.TenantId);
        return row.TenantId;
    }

    // ------------------------------------------------------------------
    // Sale updates
    // ------------------------------------------------------------------

    private static async Task MarkSalePaidAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string invoiceNo,
        string receiptNo,
        ILogger logger)
    {
        // Get tenant schema name
        var tenant = await conn.QuerySingleOrDefaultAsync<TenantSchema>(
            "SELECT schema_name FROM master.tenants WHERE id = @TenantId",
            new { TenantId = tenantId }, commandTimeout: 30);

        if (tenant is null)
        {
            logger.LogError("Tenant {TenantId} not found for invoice {InvoiceNo}", tenantId, invoiceNo);
            return;
        }

        var salesTable = $"{tenant.SchemaName}.sales";

        await conn.ExecuteAsync(
            $"UPDATE {salesTable} SET status = 'completed', " +
            $"mpesa_ref = @ReceiptNo, updated_at = NOW() " +
            $"WHERE invoice_no = @InvoiceNo AND tenant_id = @TenantId",
            new { ReceiptNo = receiptNo, InvoiceNo = invoiceNo, TenantId = tenantId },
            commandTimeout: 30);
    }

    private static async Task MarkSaleFailedAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string invoiceNo,
        int resultCode,
        string reason,
        ILogger logger)
    {
        var tenant = await conn.QuerySingleOrDefaultAsync<TenantSchema>(
            "SELECT schema_name FROM master.tenants WHERE id = @TenantId",
            new { TenantId = tenantId }, commandTimeout: 30);

        if (tenant is null)
        {
            logger.LogError("Tenant {TenantId} not found for invoice {InvoiceNo}", tenantId, invoiceNo);
            return;
        }

        var salesTable = $"{tenant.SchemaName}.sales";

        await conn.ExecuteAsync(
            $"UPDATE {salesTable} SET status = 'failed', updated_at = NOW() " +
            $"WHERE invoice_no = @InvoiceNo AND tenant_id = @TenantId",
            new { InvoiceNo = invoiceNo, TenantId = tenantId },
            commandTimeout: 30);
    }

    // ------------------------------------------------------------------
    // Audit logging
    // ------------------------------------------------------------------

    private static async Task LogCallbackAsync(
        NpgsqlConnection conn,
        StkPushCallback callback,
        string rawBody,
        Guid? tenantId,
        ILogger logger)
    {
        try
        {
            await conn.ExecuteAsync(
                @"INSERT INTO master.daraja_callback_log
                      (checkout_request_id, merchant_request_id, result_code, result_desc,
                       raw_payload, tenant_id, processed_at)
                  VALUES (@CheckoutRequestId, @MerchantRequestId, @ResultCode, @ResultDesc,
                          @RawPayload::jsonb, @TenantId, NOW())",
                new
                {
                    CheckoutRequestId = callback.CheckoutRequestID ?? "",
                    MerchantRequestId = callback.MerchantRequestID ?? "",
                    ResultCode = int.TryParse(callback.ResultCode, out var c) ? c : -1,
                    ResultDesc = callback.ResultDesc ?? "",
                    RawPayload = rawBody,
                    TenantId = tenantId,
                }, commandTimeout: 30);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to log callback to daraja_callback_log");
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string Truncate(string s, int maxLength)
        => s.Length <= maxLength ? s : s[..maxLength] + "...[truncated]";

    private sealed class DarajaConfigRow
    {
        public Guid TenantId { get; init; }
        public string Shortcode { get; init; } = "";
    }

    private sealed class TenantSchema
    {
        public string SchemaName { get; init; } = "";
    }
}
