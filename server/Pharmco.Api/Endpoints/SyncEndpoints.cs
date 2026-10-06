using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmco.Api.Services;
using Pharmco.Core.Data;
using Pharmco.Core.Licensing;
using Pharmco.Core.Sales;
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
                // A rejected SQL statement aborts its PostgreSQL transaction.
                // Isolate each operation so one invalid queued item does not
                // poison the remainder of this batch.
                await conn.ExecuteAsync("SAVEPOINT sync_operation", transaction: tx);
                try
                {
                    var result = await ProcessOperationAsync(
                        conn, tx, op, tenant, claims, licenseService);
                    await conn.ExecuteAsync("RELEASE SAVEPOINT sync_operation", transaction: tx);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to process sync op {Uuid}", op.Uuid);
                    await conn.ExecuteAsync("ROLLBACK TO SAVEPOINT sync_operation", transaction: tx);
                    await conn.ExecuteAsync("RELEASE SAVEPOINT sync_operation", transaction: tx);
                    results.Add(new
                    {
                        uuid = op.Uuid,
                        status = "error",
                        error = "Operation could not be applied; verify its data before retrying."
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
        string? since,
        TenantRepository tenantRepo,
        NpgsqlConnectionFactory connectionFactory,
        JwtService jwt,
        LicenseService licenseService)
    {
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

        // Get current license (verified from the tenant's signed key)
        var license = GetCurrentLicense(tenant, licenseService);

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
        // Idempotency: a client op uuid is processed at most once.
        if (!Guid.TryParse(op.Uuid, out var opUuid))
            throw new InvalidOperationException("Invalid operation uuid");

        var existing = await conn.ExecuteScalarAsync<int>(
            $"SELECT 1 FROM {tenant.SchemaName}.sync_queue WHERE client_uuid = @Uuid",
            new { Uuid = opUuid }, tx);

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
            $"VALUES (@Entity, @Operation, CAST(@Payload AS jsonb), @Uuid, NOW())",
            new
            {
                Entity = op.Entity,
                Operation = op.Operation,
                Payload = op.Payload,
                Uuid = opUuid
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
        if (!Guid.TryParse(product.Id, out var productId))
            throw new InvalidOperationException("Invalid product id");

        // LWW: only update if server version is newer
        var existing = await conn.ExecuteScalarAsync<DateTimeOffset?>(
            $"SELECT updated_at FROM {tenant.SchemaName}.products WHERE id = @Id",
            new { Id = productId }, tx);

        if (existing.HasValue && existing.Value > product.UpdatedAt)
        {
            // Client has newer data — don't overwrite
            return;
        }

        if (op.Operation == "delete")
        {
            await conn.ExecuteAsync(
                $"UPDATE {tenant.SchemaName}.products SET deleted_at = NOW() WHERE id = @Id",
                new { Id = productId }, tx);
        }
        else
        {
            await conn.ExecuteAsync(
                $"INSERT INTO {tenant.SchemaName}.products " +
                $"(id, tenant_id, name, barcode, category, unit, buying_price, selling_price, " +
                $"stock_qty, reorder_level, is_active, updated_at) " +
                $"VALUES (@Id, @TenantId, @Name, @Barcode, @Category, @Unit, @BuyingPrice, " +
                $"@SellingPrice, @StockQty, @ReorderLevel, @IsActive, @UpdatedAt) " +
                $"ON CONFLICT (id) DO UPDATE SET " +
                $"name = excluded.name, barcode = excluded.barcode, " +
                $"category = excluded.category, unit = excluded.unit, " +
                $"buying_price = excluded.buying_price, selling_price = excluded.selling_price, " +
                $"stock_qty = excluded.stock_qty, reorder_level = excluded.reorder_level, " +
                $"is_active = excluded.is_active, updated_at = excluded.updated_at",
                new
                {
                    Id = productId,
                    TenantId = tenant.Id,
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

        var clientSaleUuid = Guid.TryParse(sale.ClientSaleUuid, out var parsedUuid) ? parsedUuid : (Guid?)null;
        var deviceId = Guid.NewGuid(); // server-side device identity for cloud-originated sync

        // Check idempotency by client_sale_uuid
        if (clientSaleUuid is not null)
        {
            var existing = await conn.ExecuteScalarAsync<int>(
                $"SELECT 1 FROM {tenant.SchemaName}.sales WHERE client_sale_uuid = @Uuid",
                new { Uuid = clientSaleUuid }, tx);

            if (existing == 1)
                return; // Already exists
        }

        // Insert sale
        await conn.ExecuteAsync(
            $"INSERT INTO {tenant.SchemaName}.sales " +
            $"(id, tenant_id, invoice_no, device_id, cashier_user_id, customer_phone, " +
            $"total, payment_mode, mpesa_ref, client_sale_uuid, status, created_at, updated_at) " +
            $"VALUES (@Id, @TenantId, @InvoiceNo, @DeviceId, @CashierUserId, @CustomerPhone, " +
            $"@Total, @PaymentMode, @MpesaRef, @ClientSaleUuid, 'completed', @CreatedAt, NOW())",
            new
            {
                Id = sale.Id,
                TenantId = tenant.Id,
                InvoiceNo = sale.InvoiceNo,
                DeviceId = deviceId,
                CashierUserId = claims.UserId,
                CustomerPhone = (object?)sale.CustomerPhone ?? DBNull.Value,
                Total = (double)sale.TotalCents / 100.0,
                PaymentMode = PaymentModeWire(sale.PaymentMode),
                MpesaRef = (object?)sale.MpesaRef ?? DBNull.Value,
                ClientSaleUuid = (object?)clientSaleUuid ?? DBNull.Value,
                CreatedAt = DateTimeOffset.UtcNow
            }, tx);

        // Insert sale items
        foreach (var line in sale.Lines)
        {
            await conn.ExecuteAsync(
                $"INSERT INTO {tenant.SchemaName}.sale_items " +
                $"(id, tenant_id, sale_id, product_id, qty, unit_price, subtotal) " +
                $"VALUES (@Id, @TenantId, @SaleId, @ProductId, @Qty, @UnitPrice, @Subtotal)",
                new
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
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
                $"(id, tenant_id, product_id, device_id, user_id, qty_change, reason, ref_id, created_at) " +
                $"VALUES (@Id, @TenantId, @ProductId, @DeviceId, @UserId, @QtyChange, 'sale', @RefId, NOW())",
                new
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    ProductId = line.ProductId,
                    DeviceId = deviceId,
                    UserId = claims.UserId,
                    QtyChange = -(double)line.Qty,
                    RefId = sale.Id
                }, tx);
        }
    }

    private static Task ProcessSaleItemOperationAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        SyncOperationDto op,
        Tenant tenant)
    {
        // Sale items are created as part of sale sync — ignore standalone operations
        _logger?.LogDebug("Ignoring standalone sale_item sync operation");
        return Task.CompletedTask;
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
            $"(id, tenant_id, product_id, device_id, user_id, qty_change, reason, ref_id, created_at) " +
            $"VALUES (@Id, @TenantId, @ProductId, @DeviceId, @UserId, @QtyChange, @Reason, @RefId, @CreatedAt)",
            new
            {
                Id = move.Id,
                TenantId = tenant.Id,
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

    /// <summary>
    /// Signature-verified license claims for the tenant, or null when the key is
    /// missing/invalid. Expiry is deliberately NOT enforced here — the desktop
    /// client owns the escalation timeline (banner → grace → read-only → lock).
    /// </summary>
    private static object? GetCurrentLicense(Tenant tenant, LicenseService licenseService)
    {
        if (string.IsNullOrEmpty(tenant.LicenseKey))
            return null;

        try
        {
            var claims = licenseService.ReadClaims(tenant.LicenseKey);
            return new
            {
                tenant_id = claims.TenantId,
                code = claims.Code,
                expires_at = claims.ExpiresAt,
                max_users = claims.MaxUsers,
                max_terminals = claims.MaxTerminals,
                features = claims.Features,
                signature = tenant.LicenseKey,
            };
        }
        catch (LicenseException)
        {
            return null;
        }
    }

    /// <summary>Maps the client payment-mode enum to the DB check-constraint values.</summary>
    private static string PaymentModeWire(PaymentMode mode) => mode switch
    {
        PaymentMode.MpesaOnline => "mpesa_online",
        PaymentMode.MpesaManual => "mpesa_manual",
        _ => "cash",
    };

    private static ILogger<ApiLog>? _logger;

    public static void SetLogger(ILogger<ApiLog> logger) => _logger = logger;

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
