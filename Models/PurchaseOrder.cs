namespace Chandlery.Models;

public enum PoStatus
{
    PendingApproval,
    Approved,
    Rejected
}

public enum DeliveryStatus
{
    Pending,
    Partial,
    Delivered
}

public class PurchaseOrder
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string PoNumber { get; set; } = "";

    /// <summary>Null for a Spot PO, which is raised without an RFQ.</summary>
    public Guid? RfqId { get; set; }
    public Guid SupplierId { get; set; }
    public DateTime CreatedDate { get; set; }

    public bool IsSpot { get; set; }

    /// <summary>A "record-only" spot PO (the field keeps its old name): no value, used only so deliveries and invoices have something to attach to.</summary>
    public bool IsDummy { get; set; }
    public string SupplierRef { get; set; } = "";
    public Guid? VesselId { get; set; }
    public Guid? OrderPortId { get; set; }
    public string Remarks { get; set; } = "";

    /// <summary>Kept up to date whenever a delivery order is recorded or deleted, so it can be filtered.</summary>
    /// <summary>Which side may change this order: the side that created it, until it is handed over. See <see cref="SyncStages"/>.</summary>
    public string OwnerStage { get; set; } = SyncStages.Office;

    public DeliveryStatus DeliveryStatus { get; set; } = DeliveryStatus.Pending;

    /// <summary>Suppliers only see approved POs.</summary>
    public PoStatus Status { get; set; } = PoStatus.Approved;
    public Guid? RequisitionId { get; set; }
    public Guid? DepartmentId { get; set; }

    public string Currency { get; set; } = "USD";

    /// <summary>Units of Currency per 1 USD when the order was raised (null = USD or no rate was available). Divide the total by this for the USD equivalent.</summary>
    public decimal? UsdRate { get; set; }
    public decimal DiscountPct { get; set; }

    /// <summary>Copied from the quote onto the first PO raised for that supplier and RFQ only, so it is charged once.</summary>
    public decimal AdditionalCharges { get; set; }
    public string AdditionalChargeDescription { get; set; } = "";

    public List<PoLine> Lines { get; set; } = new();

    public decimal Subtotal => Lines.Sum(l => Money.LineNet(l.Quantity, l.UnitPrice, l.DiscountPct));
    public decimal Total => Money.GrandTotal(Subtotal, DiscountPct, AdditionalCharges);
}

public class PoLine
{
    public Guid ItemId { get; set; }

    /// <summary>The unit the quantity and price are in. Blank on older orders, which use the item's unit.</summary>
    public string Uom { get; set; } = "";

    /// <summary>The unit to show: this line's own, or the item's when the order predates units on lines.</summary>
    public string UomOr(string? itemUom) => string.IsNullOrWhiteSpace(Uom) ? itemUom ?? "" : Uom;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
}
