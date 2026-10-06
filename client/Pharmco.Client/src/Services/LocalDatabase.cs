using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pharmco.Client.Models;
using Pharmco.Core.Sales;

namespace Pharmco.Client.Services;

/// <summary>
/// Local SQLite database with SQLCipher encryption support.
/// Mirrors tenant schema for offline operation.
/// Uses Microsoft.Data.Sqlite with PRAGMA key for encryption.
/// </summary>
public sealed class LocalDatabase : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public LocalDatabase(string dbPath, string? encryptionKey = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _connectionString = builder.ToString();

        Initialize(encryptionKey);
    }

    private void Initialize(string? encryptionKey)
    {
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();

        // Apply SQLCipher encryption if key provided
        if (!string.IsNullOrEmpty(encryptionKey))
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"PRAGMA key = '{encryptionKey}'";
            cmd.ExecuteNonQuery();
        }

        CreateSchema();
    }

    private void CreateSchema()
    {
        // Products table (mirror of server)
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS products (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                barcode TEXT,
                category TEXT,
                unit TEXT NOT NULL DEFAULT 'piece',
                buying_price REAL NOT NULL DEFAULT 0,
                selling_price REAL NOT NULL,
                stock_qty REAL NOT NULL DEFAULT 0,
                reorder_level REAL NOT NULL DEFAULT 0,
                is_active INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL,
                deleted_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_products_barcode ON products(barcode);
            CREATE INDEX IF NOT EXISTS idx_products_updated ON products(updated_at);
        ";
        cmd.ExecuteNonQuery();

        // Sales table
        using var cmd2 = _connection.CreateCommand();
        cmd2.CommandText = @"
            CREATE TABLE IF NOT EXISTS sales (
                id TEXT PRIMARY KEY,
                invoice_no TEXT NOT NULL UNIQUE,
                cashier_user_id TEXT NOT NULL,
                customer_phone TEXT,
                total_cents INTEGER NOT NULL,
                payment_mode TEXT NOT NULL DEFAULT 'cash',
                mpesa_ref TEXT,
                client_sale_uuid TEXT,
                status TEXT NOT NULL DEFAULT 'pending',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                synced INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_sales_invoice ON sales(invoice_no);
            CREATE INDEX IF NOT EXISTS idx_sales_status ON sales(status);
            CREATE INDEX IF NOT EXISTS idx_sales_sync ON sales(synced);
        ";
        cmd2.ExecuteNonQuery();

        // Sale items table
        using var cmd3 = _connection.CreateCommand();
        cmd3.CommandText = @"
            CREATE TABLE IF NOT EXISTS sale_items (
                id TEXT PRIMARY KEY,
                sale_id TEXT NOT NULL REFERENCES sales(id),
                product_id TEXT NOT NULL,
                qty REAL NOT NULL,
                unit_price REAL NOT NULL,
                subtotal REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sale_items_sale ON sale_items(sale_id);
        ";
        cmd3.ExecuteNonQuery();

        // Stock moves table
        using var cmd4 = _connection.CreateCommand();
        cmd4.CommandText = @"
            CREATE TABLE IF NOT EXISTS stock_moves (
                id TEXT PRIMARY KEY,
                product_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                qty_change REAL NOT NULL,
                reason TEXT NOT NULL,
                ref_id TEXT,
                created_at TEXT NOT NULL,
                synced INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_stock_moves_product ON stock_moves(product_id);
            CREATE INDEX IF NOT EXISTS idx_stock_moves_sync ON stock_moves(synced);
        ";
        cmd4.ExecuteNonQuery();

        // Sync queue table
        using var cmd5 = _connection.CreateCommand();
        cmd5.CommandText = @"
            CREATE TABLE IF NOT EXISTS sync_queue (
                id TEXT PRIMARY KEY DEFAULT (lower(hex(randomblob(16)))),
                entity TEXT NOT NULL,
                operation TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                client_uuid TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                retry_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                synced_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_sync_queue_pending ON sync_queue(created_at)
                WHERE synced_at IS NULL;
        ";
        cmd5.ExecuteNonQuery();

        // Client meta table
        using var cmd6 = _connection.CreateCommand();
        cmd6.CommandText = @"
            CREATE TABLE IF NOT EXISTS client_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL DEFAULT '{}',
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ";
        cmd6.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // Sync Queue Operations
    // ------------------------------------------------------------------

    public async Task<string> EnqueueSyncOperationAsync(
        string entity,
        string operation,
        object payload,
        CancellationToken ct = default)
    {
        var uuid = Guid.NewGuid();
        var payloadJson = JsonSerializer.Serialize(payload);

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sync_queue (id, entity, operation, payload_json, client_uuid, created_at)
            VALUES (@id, @entity, @operation, @payload, @client_uuid, @created_at)
        ";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@entity", entity);
        cmd.Parameters.AddWithValue("@operation", operation);
        cmd.Parameters.AddWithValue("@payload", payloadJson);
        cmd.Parameters.AddWithValue("@client_uuid", uuid.ToString());
        cmd.Parameters.AddWithValue("@created_at", DateTimeOffset.UtcNow.ToString("o"));

        await cmd.ExecuteNonQueryAsync(ct);
        return uuid.ToString();
    }

    public async Task<List<SyncOperation>> GetPendingOperationsAsync(
        int maxCount = 500,
        CancellationToken ct = default)
    {
        var operations = new List<SyncOperation>();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT id, entity, operation, payload_json, client_uuid, created_at
            FROM sync_queue
            WHERE synced_at IS NULL
            ORDER BY created_at ASC
            LIMIT @maxCount
        ";
        cmd.Parameters.AddWithValue("@maxCount", maxCount);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            operations.Add(new SyncOperation
            {
                Uuid = reader.GetString(4), // client_uuid
                Entity = reader.GetString(1),
                Operation = reader.GetString(2),
                Payload = reader.GetString(3),
                DeviceId = GetDeviceId(),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(5))
            });
        }

        return operations;
    }

    public async Task MarkOperationSyncedAsync(string clientUuid, CancellationToken ct = default)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            UPDATE sync_queue SET synced_at = @synced_at
            WHERE client_uuid = @client_uuid
        ";
        cmd.Parameters.AddWithValue("@synced_at", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@client_uuid", clientUuid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkOperationFailedAsync(
        string clientUuid,
        string error,
        CancellationToken ct = default)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            UPDATE sync_queue
            SET retry_count = retry_count + 1, last_error = @error
            WHERE client_uuid = @client_uuid
        ";
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@client_uuid", clientUuid);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------
    // Product Operations (local)
    // ------------------------------------------------------------------

    public async Task UpsertProductAsync(ProductSyncDto product, CancellationToken ct = default)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO products (id, name, barcode, category, unit, buying_price,
                selling_price, stock_qty, reorder_level, is_active, updated_at)
            VALUES (@id, @name, @barcode, @category, @unit, @buying_price,
                @selling_price, @stock_qty, @reorder_level, @is_active, @updated_at)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                barcode = excluded.barcode,
                category = excluded.category,
                unit = excluded.unit,
                buying_price = excluded.buying_price,
                selling_price = excluded.selling_price,
                stock_qty = excluded.stock_qty,
                reorder_level = excluded.reorder_level,
                is_active = excluded.is_active,
                updated_at = excluded.updated_at
            WHERE excluded.updated_at > products.updated_at
        ";
        cmd.Parameters.AddWithValue("@id", product.Id);
        cmd.Parameters.AddWithValue("@name", product.Name);
        cmd.Parameters.AddWithValue("@barcode", (object?)product.Barcode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@category", (object?)product.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@unit", product.Unit);
        cmd.Parameters.AddWithValue("@buying_price", (double)product.BuyingPrice);
        cmd.Parameters.AddWithValue("@selling_price", (double)product.SellingPrice);
        cmd.Parameters.AddWithValue("@stock_qty", (double)product.StockQty);
        cmd.Parameters.AddWithValue("@reorder_level", (double)product.ReorderLevel);
        cmd.Parameters.AddWithValue("@is_active", product.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@updated_at", product.UpdatedAt.ToString("o"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ProductSyncDto>> GetProductsAsync(CancellationToken ct = default)
    {
        var products = new List<ProductSyncDto>();

        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, barcode, category, unit, buying_price, selling_price,
                   stock_qty, reorder_level, is_active, updated_at
            FROM products WHERE deleted_at IS NULL
            ORDER BY name
        ";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            products.Add(new ProductSyncDto
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Barcode = reader.IsDBNull(2) ? null : reader.GetString(2),
                Category = reader.IsDBNull(3) ? null : reader.GetString(3),
                Unit = reader.GetString(4),
                BuyingPrice = (decimal)reader.GetDouble(5),
                SellingPrice = (decimal)reader.GetDouble(6),
                StockQty = (decimal)reader.GetDouble(7),
                ReorderLevel = (decimal)reader.GetDouble(8),
                IsActive = reader.GetInt32(9) == 1,
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
            });
        }

        return products;
    }

    // ------------------------------------------------------------------
    // Sale Operations (local)
    // ------------------------------------------------------------------

    public async Task InsertSaleAsync(InFlightSale sale, CancellationToken ct = default)
    {
        await using var tx = _connection!.BeginTransaction();
        try
        {
            // Insert sale
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO sales (id, invoice_no, cashier_user_id, customer_phone,
                    total_cents, payment_mode, mpesa_ref, client_sale_uuid, status,
                    created_at, updated_at, synced)
                VALUES (@id, @invoice_no, @cashier_user_id, @customer_phone,
                    @total_cents, @payment_mode, @mpesa_ref, @client_sale_uuid,
                    'pending', @created_at, @updated_at, 0)
            ";
            cmd.Parameters.AddWithValue("@id", sale.Id.ToString());
            cmd.Parameters.AddWithValue("@invoice_no", sale.InvoiceNo);
            cmd.Parameters.AddWithValue("@cashier_user_id", sale.CashierUserId.ToString());
            cmd.Parameters.AddWithValue("@customer_phone", (object?)sale.CustomerPhone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@total_cents", (long)sale.TotalCents);
            cmd.Parameters.AddWithValue("@payment_mode", sale.PaymentMode.ToString().ToLower());
            cmd.Parameters.AddWithValue("@mpesa_ref", (object?)sale.MpesaRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@client_sale_uuid", (object?)sale.ClientSaleUuid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", DateTimeOffset.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);

            // Insert sale items
            foreach (var line in sale.Lines)
            {
                await using var cmd2 = _connection.CreateCommand();
                cmd2.Transaction = tx;
                cmd2.CommandText = @"
                    INSERT INTO sale_items (id, sale_id, product_id, qty, unit_price, subtotal)
                    VALUES (@id, @sale_id, @product_id, @qty, @unit_price, @subtotal)
                ";
                cmd2.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd2.Parameters.AddWithValue("@sale_id", sale.Id.ToString());
                cmd2.Parameters.AddWithValue("@product_id", line.ProductId.ToString());
                cmd2.Parameters.AddWithValue("@qty", (double)line.Qty);
                cmd2.Parameters.AddWithValue("@unit_price", (double)line.UnitPriceCents);
                cmd2.Parameters.AddWithValue("@subtotal", (double)line.SubtotalCents);
                await cmd2.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Meta Operations
    // ------------------------------------------------------------------

    public async Task<string?> GetMetaAsync(string key, CancellationToken ct = default)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT value FROM client_meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken ct = default)
    {
        await using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO client_meta (key, value, updated_at)
            VALUES (@key, @value, @updated_at)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value,
                updated_at = excluded.updated_at
        ";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string GetDeviceId()
    {
        // Generate or retrieve a stable device ID
        var deviceId = Environment.MachineName + "-" + Environment.UserName.GetHashCode();
        return deviceId;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    public SqliteConnection GetConnection()
    {
        return _connection! ?? throw new InvalidOperationException("Database not initialized");
    }
}
