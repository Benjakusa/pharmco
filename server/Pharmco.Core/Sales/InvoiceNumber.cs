namespace Pharmco.Core.Sales;

/// <summary>
/// Invoice numbering per docs/data-model.md &amp; PROMPT-3:
///   PH-&lt;tenant_code&gt;-&lt;YYYYMMDD&gt;-&lt;NNNN&gt;
/// The day+seq pair is allocated atomically per tenant inside the sale
/// transaction (sale_sequences upsert, see SaleRepository), so sequential
/// numbers hold even under concurrent sales. Pure/static on purpose so the
/// format is unit-testable offline.
/// </summary>
public static class InvoiceNumber
{
    public const string Prefix = "PH";

    /// <summary>UTC day as YYYYMMDD (invariant culture).</summary>
    public static string Day(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>4-digit zero-padded sequence within the day.</summary>
    public static string Seq(long seq) => seq.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    public static string Format(string tenantCode, string day, long seq)
        => Prefix + "-" + tenantCode + "-" + day + "-" + Seq(seq);
}