using System.Text.Json;
using Dapper;
using Npgsql;
using Pharmco.Core.Auth;
using Pharmco.Core.Tenants;
using static System.Math;

namespace Pharmco.Core.Sales;

/// <summary>
/// In-progress POS sale. All money is in INTEGER cents; Npgsql stores the
/// multi-row snapshot as JSON (<code>sale_items_bytes</code>). The caller
/// (SaleRepository) runs this inside a SERIALIZABLE transaction so concurrent
/// sales of the same product cannot over-sell.
/// </summary>
public sealed record InFlightSale
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    public Guid CashierUserId { get; init; }
    /// <summary>Client-assigned invoice number (PH-&lt;tenant&gt;-&lt;day&gt;-&lt;seq&gt;).</summary>
    public string InvoiceNo { get; init; } = "";
    public string? CustomerPhone { get; init; }
    public PaymentMode PaymentMode { get; init; } = PaymentMode.Cash;
    public string? MpesaRef { get; init; }
    public string? ClientSaleUuid { get; init; }
    public long TotalCents { get; init; }
    public List<InFlightLine> Lines { get; init; } = new();
}

public sealed record InFlightLine
{
    public Guid ProductId { get; init; }
    public int Qty { get; init; } = 1;
    public long UnitPriceCents { get; init; }   // Npgsql col is sale_items.unit_price integer cents

    public long SubtotalCents => Qty * UnitPriceCents;
}

/// <summary>cash | mpesa_online | mpesa_manual</summary>
public enum PaymentMode
{
    Cash,
    MpesaOnline,
    MpesaManual
}

public static class DbModel
{
    /// <summary>In-flight sale JSON serialised for the Npgsql bytea param.</summary>
    public static byte[] LinesJson(IEnumerable<InFlightLine> lines)
        => JsonSerializer.SerializeToUtf8Bytes(lines);
}
