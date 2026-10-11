namespace Pharmco.Client.Services;

using System.IO;
using System.Text.Json;

/// <summary>
/// Minimal JSON value model for the desktop client (login/refresh/session
/// payloads are tiny and flat). Backed by System.Text.Json; kept as a small
/// hand-rolled DOM so call sites stay dependency-free and allocation-cheap.
/// </summary>
public abstract class JsonValue { }

public sealed class JsonEntry
{
    public string Key = "";
    public JsonValue Value { get; set; } = JsonNull.Instance;
}

public sealed class JsonObject : JsonValue
{
    private readonly Dictionary<string, JsonValue> _fields = new();

    public bool Has(string key) => _fields.ContainsKey(key);
    public JsonObject? GetObject(string key) => _fields.TryGetValue(key, out var v) ? v as JsonObject : null;
    public JsonArray? GetArray(string key) => _fields.TryGetValue(key, out var v) ? v as JsonArray : null;

    public string? GetString(string key)
        => _fields.TryGetValue(key, out var v) ? (v as JsonString)?.Value : null;

    public long GetLong(string key, long fallback)
        => _fields.TryGetValue(key, out var v) && v is JsonNumber n ? (long) Math.Round(n.Value) : fallback;

    public bool GetBool(string key, bool fallback)
        => _fields.TryGetValue(key, out var v) && v is JsonBool b ? b.Value : fallback;

    public void Put(string key, JsonValue value) => _fields[key] = value;

    public IReadOnlyList<JsonEntry> Entries()
    {
        var entries = new List<JsonEntry>(_fields.Count);
        foreach (var pair in _fields)
            entries.Add(new JsonEntry { Key = pair.Key, Value = pair.Value });
        return entries;
    }
}

public sealed class JsonArray : JsonValue
{
    private readonly List<JsonValue> _items = new();
    public void Add(JsonValue value) => _items.Add(value);
    public int Size() => _items.Count;
    public IReadOnlyList<JsonValue> Items() => _items;
}

public sealed class JsonString : JsonValue { public string Value { get; set; } = ""; }
public sealed class JsonNumber : JsonValue { public double Value { get; set; } }
public sealed class JsonBool : JsonValue { public bool Value { get; set; } }
public sealed class JsonNull : JsonValue { public static readonly JsonValue Instance = new JsonNull(); }

// --- static facade -------------------------------------------------------------

public static class Json
{
    /// <summary>Builds a JsonObject from alternating key/value arguments.</summary>
    public static JsonObject Obj(params object?[] entries)
    {
        var obj = new JsonObject();
        for (var i = 0; i + 1 < entries.Length; i += 2)
            obj.Put((string) entries[i]!, Value(entries[i + 1]));
        return obj;
    }

    public static JsonValue Value(object? value)
    {
        if (value is null) return JsonNull.Instance;
        if (value is JsonValue j) return j;
        if (value is bool b) return new JsonBool { Value = b };
        if (value is int i) return new JsonNumber { Value = i };
        if (value is long l) return new JsonNumber { Value = l };
        if (value is double d) return new JsonNumber { Value = d };
        if (value is float f) return new JsonNumber { Value = f };
        return new JsonString { Value = value.ToString() ?? "" };
    }

    public static JsonValue Parse(string text)
        => FromElement(JsonDocument.Parse(text).RootElement);

    public static string Stringify(JsonValue value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, value);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public sealed class ParseException : Exception
    {
        public ParseException(string message) : base(message) { }
    }

    // --- internals -----------------------------------------------------------------

    private static JsonValue FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                    obj.Put(property.Name, FromElement(property.Value));
                return obj;
            }
            case JsonValueKind.Array:
            {
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    array.Add(FromElement(item));
                return array;
            }
            case JsonValueKind.String:
                return new JsonString { Value = element.GetString() ?? "" };
            case JsonValueKind.Number:
                return new JsonNumber { Value = element.GetDouble() };
            case JsonValueKind.True:
            case JsonValueKind.False:
                return new JsonBool { Value = element.GetBoolean() };
            default:
                return JsonNull.Instance;
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonValue value)
    {
        switch (value)
        {
            case JsonObject o:
            {
                writer.WriteStartObject();
                foreach (var entry in o.Entries())
                {
                    writer.WritePropertyName(entry.Key);
                    Write(writer, entry.Value);
                }
                writer.WriteEndObject();
                break;
            }
            case JsonArray a:
            {
                writer.WriteStartArray();
                foreach (var item in a.Items())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            }
            case JsonString s:
                writer.WriteStringValue(s.Value);
                break;
            case JsonNumber n:
                writer.WriteNumberValue(n.Value);
                break;
            case JsonBool b:
                writer.WriteBooleanValue(b.Value);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
