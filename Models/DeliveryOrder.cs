namespace Chandlery.Models;

/// <summary>Goods received against a purchase order. Lines are matched to the PO's lines by ItemId.</summary>
public class DeliveryOrder
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string DoNumber { get; set; } = "";
    public int PurchaseOrderId { get; set; }
    public int SupplierId { get; set; }
    public DateTime ReceivedDate { get; set; }
    public string Place { get; set; } = "";
    public string Remarks { get; set; } = "";
    public int? RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>Set once an invoice covers this delivery; the delivery is then locked.</summary>
    public int? InvoiceId { get; set; }

    public List<DeliveryOrderLine> Lines { get; set; } = new();
}

public class DeliveryOrderLine
{
    public int ItemId { get; set; }
    public decimal Quantity { get; set; }
}
