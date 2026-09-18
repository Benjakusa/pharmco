using System.Text;
using System.Text.RegularExpressions;

namespace Pharmco.Core.Tenants;

/// <summary>Tenant-code → schema-name mapping and validation (shared by CLI + API).</summary>
public static partial class TenantNaming
{
    public const string MasterSchema = "master";

    // Mirrors the master.tenants code check: ^[A-Z0-9][A-Z0-9._-]{0,19}$
    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,19}$")]
    private static partial Regex TenantCodePattern();

    // Mirrors the master.tenants schema_name check: ^tenant_[a-z0-9_]+$
    [GeneratedRegex("^tenant_[a-z0-9_]+$")]
    private static partial Regex SchemaNamePattern();

    /// <summary>
    /// <c>PHARMCO-001</c> → <c>tenant_pharmco_001</c>; lowercases and folds
    /// non-alphanumerics to underscores so the result is always a valid identifier.
    /// </summary>
    public static string SchemaNameFromCode(string code)
    {
        if (!TenantCodePattern().IsMatch(code))
            throw new ArgumentException($"invalid tenant code format: '{code}'", nameof(code));

        var sb = new StringBuilder("tenant_");
        foreach (var ch in code.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');

        var schema = sb.ToString();
        if (!IsValidSchemaName(schema))
            throw new ArgumentException($"code '{code}' maps to invalid schema '{schema}'", nameof(code));
        return schema;
    }

    public static bool IsValidSchemaName(string schema)
        => !string.IsNullOrWhiteSpace(schema) && schema.Length <= 80 && SchemaNamePattern().IsMatch(schema);

    /// <summary>Defense-in-depth: master last so unqualified tenant tables win.</summary>
    public static string SearchPathFor(string schemaName)
    {
        if (!IsValidSchemaName(schemaName))
            throw new ArgumentException($"refusing to build search_path for unsafe schema '{schemaName}'", nameof(schemaName));
        return $"{schemaName}, {MasterSchema}";
    }
}