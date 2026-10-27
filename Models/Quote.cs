namespace Chandlery.Models;

public class Quote
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int RfqId { get; set; }
    public int SupplierId { get; set; }
    public DateTime SubmittedDate { get; set; }

    public string SupplierRef { get; set; } = "";
    public int? DeliveryDays { get; set; }
    public DateTime? ValidityDate { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal DiscountPct { get; set; }
    public decimal AdditionalCharges { get; set; }
    public string AdditionalChargeDescription { get; set; } = "";
    public string Remarks { get; set; } = "";

    public List<QuoteLine> Lines { get; set; } = new();

    public decimal Subtotal(IEnumerable<RfqLine> rfqLines) =>
        rfqLines.Sum(l =>
        {
            var ql = Lines.FirstOrDefault(x => x.RfqLineId == l.Id);
            return ql is null ? 0m : Money.LineNet(ql.QuotedQuantity(l.Quantity), ql.UnitPrice, ql.DiscountPct);
        });

    public decimal GrandTotal(IEnumerable<RfqLine> rfqLines) =>
        Money.GrandTotal(Subtotal(rfqLines), DiscountPct, AdditionalCharges);
}

public class QuoteLine
{
    public int RfqLineId { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    /// <summary>The unit the price is per, when the supplier quoted in a different unit than the RFQ line (null: the RFQ line's unit).</summary>
    public string? Uom { get; set; }

    /// <summary>Quoted units in one RFQ unit (1 Drum = 205 Litre gives 205), saved when the quote is submitted so later changes to the conversion table do not alter it.</summary>
    public decimal ConversionFactor { get; set; } = 1m;

    /// <summary>The quantity in the quoted unit.</summary>
    public decimal QuotedQuantity(decimal rfqQuantity) => rfqQuantity * ConversionFactor;

    /// <summary>What the supplier is actually offering, if different from what was asked for.</summary>
    public string OfferText { get; set; } = "";
}
