namespace Pharmco.Api.Services;

using Dapper;
using Npgsql;
using Pharmco.Api.Services.Daraja;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that runs every 15 minutes to verify pending M-Pesa manual sales.
/// For each 'pending_verification' sale, calls Daraja Transaction Status API.
/// If verified → update status to 'completed'.
/// If not found after 24h → flag as 'unverified' for admin review.
/// </summary>
public sealed class PendingVerificationJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PendingVerificationJob> _logger;
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(24);

    public PendingVerificationJob(IServiceProvider serviceProvider, ILogger<PendingVerificationJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PendingVerificationJob starting, polling every {Interval}", PollingInterval);

        // Initial delay to let the app fully start
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingVerificationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PendingVerificationJob");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingVerificationsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var daraja = scope.ServiceProvider.GetRequiredService<DarajaService>();

        await using var conn = await daraja.GetOpenConnectionAsync(ct);

        // Get all active tenants
        var activeTenants = await conn.QueryAsync<TenantPending>(
            @"SELECT t.id AS TenantId, t.schema_name AS SchemaName
              FROM master.tenants t
              JOIN information_schema.schemata s ON s.schema_name = t.schema_name
              WHERE t.status = 'active'",
            commandTimeout: 30);

        var activeTenantsList = activeTenants.ToList();
        if (activeTenantsList.Count == 0) return;

        // Build a single query to find which tenants actually have pending sales
        var sqlParts = new List<string>();
        foreach (var t in activeTenantsList)
        {
            sqlParts.Add($"SELECT '{t.TenantId}'::uuid AS TenantId, '{t.SchemaName}' AS SchemaName " +
                         $"WHERE EXISTS (SELECT 1 FROM \"{t.SchemaName}\".sales WHERE status = 'pending_verification')");
        }
        
        var tenantsWithPending = await conn.QueryAsync<TenantPending>(
            string.Join(" UNION ALL ", sqlParts), commandTimeout: 30);

        foreach (var tenant in tenantsWithPending)
        {
            try
            {
                await ProcessTenantPendingSalesAsync(conn, daraja, tenant, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing pending sales for tenant {TenantId}",
                    tenant.TenantId);
            }
        }
    }

    private async Task ProcessTenantPendingSalesAsync(
        NpgsqlConnection conn,
        DarajaService daraja,
        TenantPending tenant,
        CancellationToken ct)
    {
        var salesTable = $"\"{tenant.SchemaName}\".sales";

        // Get pending_verification sales that are older than 1 minute
        // (give the customer time to complete the STK Push if they just made the sale)
        var pendingSales = await conn.QueryAsync<PendingSale>(
            $"SELECT id, invoice_no, mpesa_ref, client_sale_uuid, customer_phone, created_at, total " +
            $"FROM {salesTable} " +
            $"WHERE status = 'pending_verification' " +
            $"AND created_at < NOW() - INTERVAL '1 minute' " +
            $"ORDER BY created_at ASC",
            commandTimeout: 30);

        foreach (var sale in pendingSales)
        {
            try
            {
                await ProcessPendingSaleAsync(conn, daraja, tenant, sale, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing pending sale {InvoiceNo} for tenant {TenantId}",
                    sale.InvoiceNo, tenant.TenantId);
            }
        }
    }

    private async Task ProcessPendingSaleAsync(
        NpgsqlConnection conn,
        DarajaService daraja,
        TenantPending tenant,
        PendingSale sale,
        CancellationToken ct)
    {
        // Check if sale is stale (>24h old)
        var age = DateTimeOffset.UtcNow - sale.CreatedAt;
        if (age > StaleThreshold)
        {
            // Flag as unverified for admin review
            var salesTable = $"\"{tenant.SchemaName}\".sales";
            await conn.ExecuteAsync(
                $"UPDATE {salesTable} SET status = 'unverified' WHERE id = @SaleId",
                new { SaleId = sale.Id }, commandTimeout: 30);

            _logger.LogWarning("Sale {InvoiceNo} flagged as unverified (age: {Age})",
                sale.InvoiceNo, age);
            return;
        }

        // Get Daraja credentials for this tenant
        var credentials = await daraja.GetCredentialsAsync(tenant.TenantId, ct);
        if (credentials is null)
        {
            _logger.LogWarning("No Daraja config for tenant {TenantId} — cannot verify sale {InvoiceNo}",
                tenant.TenantId, sale.InvoiceNo);
            return;
        }

        // Query Transaction Status API to verify using ClientSaleUuid (which contains CheckoutRequestID)
        if (!string.IsNullOrWhiteSpace(sale.ClientSaleUuid))
        {
            var statusResult = await daraja.GetTransactionStatusAsync(
                tenant.TenantId,
                sale.ClientSaleUuid,  // Using ClientSaleUuid as checkout request ID
                ct);

            if (statusResult is not null && statusResult.ResponseCode == "0")
            {
                // Verified! Mark as completed
                var salesTable = $"\"{tenant.SchemaName}\".sales";
                await conn.ExecuteAsync(
                    $"UPDATE {salesTable} SET status = 'completed', updated_at = NOW() " +
                    $"WHERE id = @SaleId",
                    new { SaleId = sale.Id }, commandTimeout: 30);

                _logger.LogInformation("Sale {InvoiceNo} verified via Daraja — receipt {ReceiptNo}",
                    sale.InvoiceNo, statusResult.MpesaReceiptNumber);
            }
            else
            {
                _logger.LogDebug("Sale {InvoiceNo} not yet verified (status: {Status})",
                    sale.InvoiceNo, statusResult?.ResponseCode);
            }
        }
        else
        {
            _logger.LogWarning("Sale {InvoiceNo} has no ClientSaleUuid (CheckoutRequestID) — cannot verify", sale.InvoiceNo);
        }
    }

    // ------------------------------------------------------------------
    // Inner types
    // ------------------------------------------------------------------

    private sealed class TenantPending
    {
        public Guid TenantId { get; init; }
        public string SchemaName { get; init; } = "";
    }

    private sealed class PendingSale
    {
        public Guid Id { get; init; }
        public string InvoiceNo { get; init; } = "";
        public string? MpesaRef { get; init; }
        public string? ClientSaleUuid { get; init; }
        public string? CustomerPhone { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public decimal Total { get; init; }
    }
}
