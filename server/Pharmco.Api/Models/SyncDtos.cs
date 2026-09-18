using System.Text.Json.Serialization;

namespace Pharmco.Api.Endpoints;

public sealed record SyncOperationDto
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = "";

    [JsonPropertyName("entity")]
    public string Entity { get; init; } = "";

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";

    [JsonPropertyName("payload")]
    public string Payload { get; init; } = "";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; init; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record SyncPushRequestDto
{
    [JsonPropertyName("ops")]
    public List<SyncOperationDto> Operations { get; init; } = new();
}

public sealed record SyncPushOpResultDto
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; init; }
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
