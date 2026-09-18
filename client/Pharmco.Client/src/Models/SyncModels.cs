using System.Text.Json.Serialization;

namespace Pharmco.Client.Models;

/// <summary>Sync operation queued locally for eventual upload.</summary>
public sealed record SyncOperation
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = Guid.NewGuid().ToString();

    [JsonPropertyName("entity")]
    public string Entity { get; init; } = "";

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";

    [JsonPropertyName("payload")]
    public string Payload { get; init; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SyncPushRequest
{
    [JsonPropertyName("ops")]
    public List<SyncOperation> Operations { get; init; } = new();
}

public sealed record SyncPushResult
{
    [JsonPropertyName("results")]
    public List<SyncPushOpResult> Results { get; init; } = new();
}

public sealed record SyncPushOpResult
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record SyncPullRequest
{
    [JsonPropertyName("since")]
    public DateTimeOffset Since { get; init; }
}

public sealed record SyncPullResponse
{
    [JsonPropertyName("products")]
    public List<ProductSyncDto> Products { get; init; } = new();

    [JsonPropertyName("users")]
    public List<UserSyncDto> Users { get; init; } = new();

    [JsonPropertyName("license")]
    public LicenseSyncDto? License { get; init; }

    [JsonPropertyName("server_time")]
    public DateTimeOffset ServerTime { get; init; }
}

public sealed record ProductSyncDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("barcode")]
    public string? Barcode { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "piece";

    [JsonPropertyName("buying_price")]
    public decimal BuyingPrice { get; init; }

    [JsonPropertyName("selling_price")]
    public decimal SellingPrice { get; init; }

    [JsonPropertyName("stock_qty")]
    public decimal StockQty { get; init; }

    [JsonPropertyName("reorder_level")]
    public decimal ReorderLevel { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record UserSyncDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("username")]
    public string Username { get; init; } = "";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record LicenseSyncDto
{
    [JsonPropertyName("tenant_id")]
    public string TenantId { get; init; } = "";

    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("max_users")]
    public int MaxUsers { get; init; }

    [JsonPropertyName("max_terminals")]
    public int MaxTerminals { get; init; }

    [JsonPropertyName("features")]
    public List<string> Features { get; init; } = new();

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";
}

public enum SyncStatus
{
    Synced,
    Pending,
    Offline,
    Error
}
