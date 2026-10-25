using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>A record's id and display name, so a row can link to it.</summary>
public record NameRef(int Id, string Name);

/// <summary>A row of the buyer's RFQ list: the RFQ (without its lines), how many quotes are in, and the invited suppliers.</summary>
public record RfqListRow(Rfq Rfq, int QuoteCount, List<NameRef> Suppliers)
{
    public string SupplierText => string.Join(", ", Suppliers.Select(s => s.Name));
}

/// <summary>A row of the buyer's purchase-order list.</summary>
public record PoListRow(PurchaseOrder Po, string SupplierName, string RfqNumber);
