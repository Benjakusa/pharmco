namespace Pharmco.Core.Data;

/// <summary>Reads the SQL migrations embedded in the Core assembly.</summary>
internal static class EmbeddedSql
{
    public static async Task<string> LoadAsync(string resourceName, CancellationToken ct = default)
    {
        var assembly = typeof(EmbeddedSql).Assembly;
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"missing embedded resource: {resourceName}");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}