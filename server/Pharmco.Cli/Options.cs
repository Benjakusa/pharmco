namespace Pharmco.Cli;

/// <summary>Minimal "--key value" / "--flag" parser (no external dependency).</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException($"unexpected argument '{arg}' (expected --key value)");
            var key = arg[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[key] = "";
                continue;
            }
            options._values[key] = args[++i];
        }
        return options;
    }

    public string Required(string key)
        => _values.GetValueOrDefault(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"--{key} is required");

    public string? Optional(string key)
        => _values.GetValueOrDefault(key) is { Length: > 0 } value ? value : null;

    public int? OptionalInt(string key)
        => Optional(key) is { } text && int.TryParse(text, out var parsed) ? parsed : null;
}