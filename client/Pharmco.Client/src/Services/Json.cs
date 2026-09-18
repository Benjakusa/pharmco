namespace Pharmco.Client.Services;

using java.util;

/// <summary>
/// Minimal JSON reader/writer for the desktop client (login/refresh/session
/// payloads are tiny and flat). No third-party JSON required.
/// </summary>
public abstract sealed class JsonValue { }

public sealed class JsonEntry
{
    public string Key = "";
    public JsonValue Value { get; set; }
}

public sealed class JsonObject extends JsonValue
{
    private final LinkedHashMap<string, JsonValue> _fields = new();

    public bool Has(string key) => _fields.ContainsKey(key);
    public JsonObject GetObject(string key) => _fields.Get(key) as? JsonObject;
    public JsonArray GetArray(string key) => _fields.Get(key) as? JsonArray;
    public string? GetString(string key)
    {
        var v = _fields.Get(key);
        return v is JsonString s ? s.Value : null;
    }
    public long GetLong(string key, long fallback)
    {
        var v = _fields.Get(key);
        return v is JsonNumber n ? (long) Math.round(n.Value) : fallback;
    }
    public bool GetBool(string key, bool fallback)
    {
        var v = _fields.Get(key);
        return v is JsonBool b ? b.Value : fallback;
    }
    public void Put(string key, JsonValue value) => _fields.Put(key, value);

    public IReadOnlyList<JsonEntry> Entries()
    {
        var out = new ArrayList<JsonEntry>();
        foreach (var entry in _fields)
            out.Add(new JsonEntry { Key = entry.Key, Value = entry.Value });
        return out;
    }
}

public sealed class JsonArray extends JsonValue
{
    private final ArrayList<JsonValue> _items = new();
    public void Add(JsonValue value) => _items.Add(value);
    public int Size() => _items.Count;
    public IReadOnlyList<JsonValue> Items() => _items;
}

public sealed class JsonString extends JsonValue { public string Value { get; set; } }
public sealed class JsonNumber extends JsonValue { public double Value { get; set; } }
public sealed class JsonBool extends JsonValue { public bool Value { get; set; } }
public sealed class JsonNull extends JsonValue { public static readonly JsonValue Instance = new JsonNull(); }

// --- static facade -------------------------------------------------------------

public static class Json
{
    public static JsonObject Obj(object... entries)
    {
        var object = new JsonObject();
        for (var i = 0; i + 1 < entries.Length; i += 2)
            object.Put((string) entries[i], Value(entries[i + 1]));
        return object;
    }

    public static JsonValue Value(object value)
    {
        if (value is null) return JsonNull.Instance;
        if (value is JsonValue j) return j;
        if (value is bool b) return new JsonBool { Value = b };
        if (value is long || value is int || value is double || value is float)
            return new JsonNumber { Value = ((Number) value).doubleValue() };
        return new JsonString { Value = value.ToString() };
    }

    public static JsonValue Parse(string text)
    {
        var p = new Parser(text);
        var value = p.ParseValue();
        p.SkipWs();
        if (!p.AtEnd()) throw new ParseException("trailing characters", p.Pos);
        return value;
    }

    public static string Stringify(JsonValue value) => new Writer().Write(value);

    public sealed class ParseException extends Exception
    {
        public ParseException(string message, int pos) { super(message + " @ " + pos); }
    }
}

// --- parser ---------------------------------------------------------------------

private sealed class Parser
{
    private readonly string _text;
    public int Pos { get; set; } = 0;

    public Parser(string text) => _text = text;

    public void SkipWs()
    {
        while (!AtEnd())
        {
            var c = _text.charAt(Pos);
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Pos++;
            else break;
        }
    }

    public bool AtEnd() => Pos >= _text.Length;

    private void Expect(char c)
    {
        if (AtEnd() || _text.charAt(Pos) != c)
            throw new Json.ParseException("expected '" + c + "'", Pos);
        Pos++;
    }

    public JsonValue ParseValue()
    {
        SkipWs();
        if (AtEnd()) throw new Json.ParseException("unexpected end of input", Pos);
        return switch (_text.charAt(Pos))
        {
            case '{' -> ParseObject();
            case '[' -> ParseArray();
            case '"' -> new JsonString { Value = ParseString() };
            case 't' -> { ParseLiteral("true"); new JsonBool { Value = true }; }
            case 'f' -> { ParseLiteral("false"); new JsonBool { Value = false }; }
            case 'n' -> { ParseLiteral("null"); JsonNull.Instance; }
            default -> ParseNumber();
        };
    }

    private JsonObject ParseObject()
    {
        Expect('{');
        var object = new JsonObject();
        SkipWs();
        if (!AtEnd() && _text.charAt(Pos) == '}') { Pos++; return object; }
        while (true)
        {
            SkipWs();
            var key = ParseString();
            SkipWs();
            Expect(':');
            var value = ParseValue();
            object.Put(key, value);
            SkipWs();
            if (AtEnd()) throw new Json.ParseException("unterminated object", Pos);
            var c = _text.charAt(Pos);
            if (c == ',') { Pos++; continue; }
            if (c == '}') { Pos++; return object; }
            throw new Json.ParseException("expected ',' or '}'", Pos);
        }
    }

    private JsonArray ParseArray()
    {
        Expect('[');
        var array = new JsonArray();
        SkipWs();
        if (!AtEnd() && _text.charAt(Pos) == ']') { Pos++; return array; }
        while (true)
        {
            array.Add(ParseValue());
            SkipWs();
            if (AtEnd()) throw new Json.ParseException("unterminated array", Pos);
            var c = _text.charAt(Pos);
            if (c == ',') { Pos++; continue; }
            if (c == ']') { Pos++; return array; }
            throw new Json.ParseException("expected ',' or ']'", Pos);
        }
    }

    private string ParseString()
    {
        Expect('"');
        var sb = new StringBuilder();
        while (!AtEnd())
        {
            var c = _text.charAt(Pos++);
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (AtEnd()) throw new Json.ParseException("dangling escape", Pos);
            var e = _text.charAt(Pos++);
            switch (e)
            {
                case '"' -> sb.Append('"');
                case '\\' -> sb.Append('\\');
                case '/' -> sb.Append('/');
                case 'b' -> sb.Append('\b');
                case 'f' -> sb.Append('\f');
                case 'n' -> sb.Append('\n');
                case 'r' -> sb.Append('\r');
                case 't' -> sb.Append('\t');
                case 'u' ->
                {
                    if (Pos + 4 > _text.Length) throw new Json.ParseException("bad \\u escape", Pos);
                    var hex = _text.Substring(Pos, 4);
                    Pos += 4;
                    sb.Append((char) Integer.parseInt(hex, 16));
                }
                default -> throw new Json.ParseException("unknown escape '\\" + e + "'", Pos);
            }
        }
        throw new Json.ParseException("unterminated string", Pos);
    }

    private JsonNumber ParseNumber()
    {
        var start = Pos;
        while (!AtEnd())
        {
            var c = _text.charAt(Pos);
            if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') Pos++;
            else break;
        }
        var raw = _text.Substring(start, Pos - start);
        if (raw.IsEmpty() || raw.Equals("-") || raw.Equals("."))
            throw new Json.ParseException("invalid number", start);
        return new JsonNumber { Value = Double.Parse(raw) };
    }

    private void ParseLiteral(string literal)
    {
        if (Pos + literal.Length > _text.Length || !_text.Substring(Pos, literal.Length).Equals(literal))
            throw new Json.ParseException("invalid literal", Pos);
        Pos += literal.Length;
    }
}

// --- writer ----------------------------------------------------------------------

private sealed class Writer
{
    public string Write(JsonValue value)
    {
        if (value is JsonObject o)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var entry in o.Entries())
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, entry.Key);
                sb.Append(':').Append(Write(entry.Value));
            }
            return sb.Append('}').ToString();
        }
        if (value is JsonArray a)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var item in a.Items())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Write(item));
            }
            return sb.Append(']').ToString();
        }
        if (value is JsonString s)
        {
            var sb = new StringBuilder();
            WriteString(sb, s.Value);
            return sb.ToString();
        }
        if (value is JsonNumber n) return n.Value.ToString();
        if (value is JsonBool b) return b.Value ? "true" : "false";
        return "null";
    }

    private void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s.ToCharArray())
        {
            switch (c)
            {
                case '"' -> sb.Append("\\\"");
                case '\\' -> sb.Append("\\\\");
                case '\n' -> sb.Append("\\n");
                case '\r' -> sb.Append("\\r");
                case '\t' -> sb.Append("\\t");
                case '\b' -> sb.Append("\\b");
                case '\f' -> sb.Append("\\f");
                default -> sb.Append(c);
            }
        }
        sb.Append('"');
    }
}