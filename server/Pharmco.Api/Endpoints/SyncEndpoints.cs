using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmco.Api.Middleware;
using Pharmco.Api.Models;
using Pharmco.Api.Services;
using Pharmco.Core.Auth;
using Pharmco.Core.Licensing;
using Pharmco.Core.Tenants;

namespace Pharmco.Api.Endpoints;

/// <summary>
/// Sync endpoints for offline-first POS clients.
///   POST /api/sync/push — push queued operations from client
///   GET  /api/sync/pull?since=ISO8601 — pull updates since timestamp
/// Requires JWT authentication and valid tenant context.
/// </summary>
public static class SyncEndpoints
{
    // ------------------------------------------------------------------
    // POST /api/sync/push
    // ------------------------------------------------------------------

    public static async Task<IResult> PushSync(
        HttpContext http,
        SyncPushRequestDto body,
        TenantRepository tenantRepo,
        NpgsqlConnectionFactory connectionFactory,
        JwtService jwt,
        LicenseService licenseService)
    {
        // Require auth
        if (var denied = Authz.RequireAdmin(http); denied is not null)
            return denied;

        var claims = Authz.Claims(http, jwt);
        if (claims is null)
            return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized);

        // Get tenant and verify license
        var tenant = await tenantRepo.GetByIdAsync(claims.TenantId);
        if (tenant is null)
            return Error("not_found", "tenant not found", StatusCodes.Status404NotFound);

        // Check license expiry
        if (tenant.LicenseExpiresAt is { } expiry
            && DateTimeOffset.UtcNow > expiry.AddDays(30))
        {
            return Error("license_expired",
                "License expired more than 30 days ago — read-only mode",
                StatusCodes.Status403Forbidden);
        }

        if (body?.Operations is null || body.Operations.Count == 0)
            return Results.Json(new { results = Array.Empty<object>() }, statusCode: StatusCodes.Status200OK);

        // Process each operation idempotently
        var results = new List<object>();

        await using var conn = await connectionFactory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            foreach (var op in body.Operations)
            {
                try
                {
                    var result = await ProcessOperationAsync(
                        conn, tx, op, tenant, claims, licenseService);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to process sync op {Uuid}", op.Uuid);
                    results.Add(new
                    {
                        uuid = op.Uuid,
                        status = "error",
                        error = ex.Message
                    });
                }
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        return Results.Json(new { results }, statusCode: StatusCodes.Status200OK);
    }

    // ------------------------------------------------------------------
    // GET /api/sync/pull
    // ------------------------------------------------------------------

    public static async Task<IResult> PullSync(
        HttpContext http,
        [FromQuery] string? since,
        TenantRepository tenantRepo,
        NpgsqlConnectionFactory connectionFactory,
        JwtService jwt,
        LicenseService licenseService)
    {
        if (var denied = Authz.RequireAdmin(http); denied is not null)
            return denied;

        var claims = Authz.Claims(http, jwt);
        if (claims is null)
            return Error("unauthorized", "invalid token", StatusCodes.Status401Unauthorized);

        var tenant = await tenantRepo.GetByIdAsync(claims.TenantId);
        if (tenant is null)
            return Error("not_found", "tenant not found", StatusCodes.Status404NotFound);

        // Check license expiry
        if (tenant.LicenseExpiresAt is { } expiry
            && DateTimeOffset.UtcNow > expiry.AddDays(30))
        {
            return Error("license_expired",
                "License expired more than 30 days ago",
                StatusCodes.Status403Forbidden);
        }

        var sinceTime = since != null
            ? DateTimeOffset.Parse(since)
            : DateTimeOffset.MinValue;

        await using var conn = await connectionFactory.OpenAsync();

        // Pull products changed since timestamp
        var products = await PullProductsAsync(conn, tenant.SchemaName, sinceTime);

        // Pull users (excluding password hashes)
        var users = await PullUsersAsync(conn, tenant.Id, sinceTime);

        // Get current license
        var license = await GetCurrentLicenseAsync(tenant, licenseService);

        return Results.Json(new
        {
            products = products,
            users = users,
            license = license,
            server_time = DateTimeOffset.UtcNow
        }, statusCode: StatusCodes.Status200OK);
    }

    // ------------------------------------------------------------------
    // Private Helpers
    // ------------------------------------------------------------------

    private static async Task<object> ProcessOperationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        SyncOperationDto op,
        Tenant tenant,
        JwtClaims claims,
        LicenseService licenseService)
    {
        // Check for duplicate by client_uuid
        var existing = await conn.ExecuteScalarAsync<int>(
            $"SELECT 1 FROM {tenant.SchemaName}.sync_queue WHERE client_uuid = @Uuid",
            new { Uuid = op.Uuid }, tx);

        if (existing == 1)
        {
            // Already processed — idempotent success
            return new { uuid = op.Uuid, status = "ok" };
        }

        // Process based on entity type
        switch (op.Entity)
        {
            case "product":
                await ProcessProductOperationAsync(conn, tx, op, tenant);
                break;
            case "sale":
                await ProcessSaleOperationAsync(conn, tx, op, tenant, claims);
                break;
            case "sale_item":
                await ProcessSaleItemOperationAsync(conn, tx, op, tenant);
                break;
            case "stock_move":
                await ProcessStockMoveOperationAsync(conn, tx, op, tenant);
                break;
            default:
                throw new InvalidOperationException($"Unknown entity: {op.Entity}");
        }

        // Mark as synced in sync_queue
        await conn.ExecuteAsync(
            $"INSERT INTO {tenant.SchemaName}.sync_queue " +
            $"(entity, operation, payload_json, client_uuid, synced_at) " +
            $"VALUES (@Entity, @Operation, @Payload, @Uuid, NOW())",
            new
            {
                Entity = op.Entity,
                Operation = op.Operation,
                Payload = op.Payload,
                Uuid = op.Uuid
            }, tx);

        return new { uuid = op.Uuid, status = "ok" };
    }

    private static async Task ProcessProductOperationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        SyncOperationDto op,
        Tenant tenant)
    {
        var product = JsonSerializer.Deserialize<ProductSyncDto>(op.Payload);
        if (product == null) throw new InvalidOperationException("Invalid product payload");

        // LWW: only update if server version is newer
        var existing = await conn.ExecuteScalarAsync<DateTimeOffset?>(
            $"SELECT updated_at FROM {tenant.SchemaName}.products WHERE id = @Id",
            new { Id = product.Id }, tx);

        if (existing.HasValue && existing.Value > product.UpdatedAt)
        {
            // Client has newer data — don't overwrite
            return;
        }

        if (op.Operation == "delete")
        {
            await conn.ExecuteAsync(
                $"UPDATE {tenant.SchemaName}.products SET deleted_at = NOW() WHERE id = @Id",
                new { Id = product.Id }, tx);
        }
        else
        {
            await conn.ExecuteAsync(
                $"INSERT INTO {tenant.SchemaName}.products " +
                $"(id, name, barcode, category, unit, buying_price, selling_price, " +
                $"stock_qty, reorder_level, is_active, updated_at) " +
                $"VALUES (@Id, @Name, @Barcode, @Category, @Unit, @BuyingPrice, " +
                $"@SellingPrice, @StockQty, @ReorderLevel, @IsActive, @UpdatedAt) " +
                $"ON CONFLICT (id) DO UPDATE SET " +
                $"name = excluded.name, barcode = excluded.barcode, " +
                $"category = excluded.category, unit = excluded.unit, " +
                $"buying_price = excluded.buying_price, selling_price = excluded.selling_price, " +
                $"stock_qty = excluded.stock_qty, reorder_level = excluded.reorder_level, " +
                $"is_active = excluded.is_active, updated_at = excluded.updated_at",
                new
                {
                    Id = product.Id,
                    Name = product.Name,
                    Barcode = (object?)product.Barcode ?? DBNull.Value,
                    Category = (object?)product.Category ?? DBNull.Value,
                    Unit = product.Unit,
                    BuyingPrice = (double)product.BuyingPrice,
                    SellingPrice = (double)product.SellingPrice,
                    StockQty = (double)product.StockQty,
                    ReorderLevel = (double)product.ReorderLevel,
                    IsActive = product.IsActive,
                    UpdatedAt = product.UpdatedAt
                }, tx);
        }
    }

    private static async Task ProcessSaleOperationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        SyncOperationDto op,
        Tenant tenant,
        JwtClaims claims)
    {
        // Sales are append-only — never update or delete
        if (op.Operation != "create")
            throw new InvalidOperationException("Sales can only be created, not updated or deleted");

        var sale = JsonSerializer.Deserialize<InFlightSale>(op.Payload);
        if (sale == null) throw new InvalidOperationException("Invalid sale payload");

        // Check idempotency by client_sale_uuid
        if (!string.IsNullOrEmpty(sale.ClientSaleUuid))
        {
            var existing = await conn.ExecuteScalarAsync<int>(
                $"SELECT 1 FROM {tenant.SchemaName}.sales WHERE client_sale_uuid = @Uuid",
                new { Uuid = sale.ClientSaleUuid }, tx);

            if (existing == 1)
                return; // Already exists
        }

        // Insert sale
        await conn.ExecuteAsync(
            $"INSERT INTO {tenant.SchemaName}.sales " +
            $"(id, invoice_no, device_id, cashier_user_id, customer_phone, " +
            $"total, payment_mode, mpesa_ref, client_sale_uuid, status, created_at, updated_at) " +
            $"VALUES (@Id, @InvoiceNo, @DeviceId, @CashierUserId, @CustomerPhone, " +
            $"@Total, @PaymentMode, @MpesaRef, @ClientSaleUuid, 'completed', @CreatedAt, NOW())",
            new
            {
                Id = sale.Id,
                InvoiceNo = sale.InvoiceNo,
                DeviceId = Guid.NewGuid(), // Server-side device ID
                CashierUserId = sale.CashierUserId,
                CustomerPhone = (object?)sale.CustomerPhone ?? DBNull.Value,
                Total = (double)sale.TotalCents / 100.0,
                PaymentMode = sale.PaymentMode.ToString().ToLower(),
                MpesaRef = (object?)sale.MpesaRef ?? DBNull.Value,
                ClientSaleUuid = (object?)sale.ClientSaleUuid ?? DBNull.Value,
                CreatedAt = DateTimeOffset.UtcNow
            }, tx);

        // Insert sale items
        foreach (var line in sale.Lines)
        {
            await conn.ExecuteAsync(
                $"INSERT INTO {tenant.SchemaName}.sale_items " +
                $"(id, sale_id, product_id, qty, unit_price, subtotal) " +
                $"VALUES (@Id, @SaleId, @ProductId, @Qty, @UnitPrice, @Subtotal)",
                new
                {
                    Id = Guid.NewGuid(),
                    SaleId = sale.Id,
                    ProductId = line.ProductId,
                    Qty = (double)line.Qty,
                    UnitPrice = (double)line.UnitPriceCents / 100.0,
                    Subtotal = (double)line.SubtotalCents / 100.0
                }, tx);
        }

        // Create stock moves
        foreach (var line in sale.Lines)
        {
            await conn.ExecuteAsync(
                $"INSERT INTO {tenant.SchemaName}.stock_moves " +
                $"(id, product_id, device_id, user_id, qty_change, reason, ref_id, created_at) " +
                $"VALUES (@Id, @ProductId, @DeviceId, @UserId, @QtyChange, 'sale', @RefId, NOW())",
                new
                {
                    Id = Guid.NewGuid(),
                    ProductId = line.ProductId,
                    DeviceId = Guid.NewGuid(),
                    UserId = claims.UserId,
                    QtyChange = -(double)line.Qty,
                    RefId = sale.Id
                }, tx);
        }
    }

    private static async Task ProcessSaleItemOperationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        SyncOperationDto op,
        Tenant tenant)
    {
        // Sale items are created as part of sale sync — ignore standalone operations
        _logger?.LogDebug("Ignoring standalone sale_item sync operation");
    }

    private static async Task ProcessStockMoveOperationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        SyncOperationDto op,
        Tenant tenant)
    {
        var move = JsonSerializer.Deserialize<StockMoveDto>(op.Payload);
        if (move == null) throw new InvalidOperationException("Invalid stock move payload");

        await conn.ExecuteAsync(
            $"INSERT INTO {tenant.SchemaName}.stock_moves " +
            $"(id, product_id, device_id, user_id, qty_change, reason, ref_id, created_at) " +
            $"VALUES (@Id, @ProductId, @DeviceId, @UserId, @QtyChange, @Reason, @RefId, @CreatedAt)",
            new
            {
                Id = move.Id,
                ProductId = move.ProductId,
                DeviceId = move.DeviceId,
                UserId = move.UserId,
                QtyChange = move.QtyChange,
                Reason = move.Reason,
                RefId = (object?)move.RefId ?? DBNull.Value,
                CreatedAt = move.CreatedAt
            }, tx);
    }

    private static async Task<List<ProductSyncDto>> PullProductsAsync(
        NpgsqlConnection conn,
        string schemaName,
        DateTimeOffset since)
    {
        var products = await conn.QueryAsync<ProductRow>(
            $"SELECT id, name, barcode, category, unit, buying_price, selling_price, " +
            $"stock_qty, reorder_level, is_active, updated_at " +
            $"FROM {schemaName}.products " +
            $"WHERE deleted_at IS NULL AND updated_at > @Since " +
            $"ORDER BY updated_at",
            new { Since = since }, commandTimeout: 30);

        return products.Select(p => new ProductSyncDto
        {
            Id = p.Id,
            Name = p.Name,
            Barcode = p.Barcode,
            Category = p.Category,
            Unit = p.Unit,
            BuyingPrice = (decimal)p.BuyingPrice,
            SellingPrice = (decimal)p.SellingPrice,
            StockQty = (decimal)p.StockQty,
            ReorderLevel = (decimal)p.ReorderLevel,
            IsActive = p.IsActive,
            UpdatedAt = p.UpdatedAt
        }).ToList();
    }

    private static async Task<List<UserSyncDto>> PullUsersAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        DateTimeOffset since)
    {
        var users = await conn.QueryAsync<UserRow>(
            $"SELECT id, username, role, is_active, updated_at " +
            $"FROM master.users WHERE tenant_id = @TenantId " +
            $"AND updated_at > @Since " +
            $"ORDER BY updated_at",
            new { TenantId = tenantId, Since = since }, commandTimeout: 30);

        return users.Select(u => new UserSyncDto
        {
            Id = u.Id.ToString(),
            Username = u.Username,
            Role = u.Role,
            IsActive = u.IsActive,
            UpdatedAt = u.UpdatedAt
        }).ToList();
    }

    private static async Task<object?> GetCurrentLicenseAsync(
        Tenant tenant,
        LicenseService licenseService)
    {
        // Get license claims from tenant
        if (string.IsNullOrEmpty(tenant.LicenseKey))
            return null;

        try
        {
            var rsa = licenseService.GetType().GetField("_key",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            // This is a simplified approach — in production, expose a method
            return new
            {
                tenant_id = tenant.Id.ToString(),
                code = tenant.Code,
                expires_at = tenant.LicenseExpiresAt,
                max_users = 10,
                max_terminals = 5,
                features = new[] { "pos", "inventory", "mpesa" }
            };
        }
        catch
        {
            return null;
        }
    }

    private static ILogger<SyncEndpoints>? _logger;

    public static void SetLogger(ILogger<SyncEndpoints> logger) => _logger = logger;

    private static IResult Error(string code, string message, int statusCode)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    // ------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------

    private sealed class ProductRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Barcode { get; init; }
        public string? Category { get; init; }
        public string Unit { get; init; } = "";
        public double BuyingPrice { get; init; }
        public double SellingPrice { get; init; }
        public double StockQty { get; init; }
        public double ReorderLevel { get; init; }
        public bool IsActive { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed class UserRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = "";
        public string Role { get; init; } = "";
        public bool IsActive { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed class StockMoveDto
    {
        public Guid Id { get; init; }
        public Guid ProductId { get; init; }
        public Guid DeviceId { get; init; }
        public Guid UserId { get; init; }
        public double QtyChange { get; init; }
        public string Reason { get; init; } = "";
        public Guid? RefId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}
