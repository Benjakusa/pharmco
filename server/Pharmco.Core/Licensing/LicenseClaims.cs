using System.Text.Json.Serialization;

namespace Pharmco.Core.Licensing;

/// <summary>License claim payload. Field names match the JSON on the wire.</summary>
public sealed class LicenseClaims
{
    [JsonPropertyName("tenant_id")]
    public string TenantId { get; set; } = "";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("max_users")]
    public int MaxUsers { get; set; } = 10;

    [JsonPropertyName("max_terminals")]
    public int MaxTerminals { get; set; } = 5;

    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = new() { "pos", "inventory", "mpesa" };
}