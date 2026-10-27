using Chandlery.Models;

namespace Chandlery.Services;

public class NewRfq
{
    /// <summary>When set, the RFQ is created from this requisition (its vessel, port and dates are used).</summary>
    public int? RequisitionId { get; set; }
    public RfqType Type { get; set; } = RfqType.Stores;
    public int? VesselId { get; set; }
    public string VesselName { get; set; } = "";
    public int? DeliveryPortId { get; set; }
    public DateTime? VesselEta { get; set; }
    public DateTime? DateRequired { get; set; }
    public DateTime? DueDate { get; set; }
    public string BuyerName { get; set; } = "";
    public string BuyerDesignation { get; set; } = "";
    public string BuyerEmail { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string EquipmentDescription { get; set; } = "";
    public string EquipmentParticulars { get; set; } = "";
    public string EquipmentMaker { get; set; } = "";
    public string EquipmentSerialNo { get; set; } = "";
    public List<NewRfqLine> Lines { get; set; } = new();
    public List<int> InvitedSupplierIds { get; set; } = new();

    /// <summary>Lets the invited suppliers quote in another unit of measure (converted from the requested one).</summary>
    public bool AllowSupplierUomChange { get; set; }
}

/// <summary>An earlier document (RFQ or requisition) whose item lines can be copied into a new one.</summary>
public record LineCopySource(int Id, string Label, List<NewRfqLine> Lines);

/// <summary>A line being entered on a new RFQ or requisition (the line-entry component is shared by both).</summary>
public class NewRfqLine
{
    public int ItemId { get; set; }
    public int? RequisitionLineId { get; set; }

    /// <summary>Unit chosen for this line; blank means the item's own unit.</summary>
    public string Uom { get; set; } = "";

    /// <summary>Only used by the Spot PO form, which prices its lines up front.</summary>
    public decimal UnitPrice { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string Description { get; set; } = "";
    public string DrawingNo { get; set; } = "";

    /// <summary>Files picked for this line on the requisition form (saved with the requisition).</summary>
    public List<PendingAttachment> Files { get; set; } = new();
}

/// <summary>A purchase order raised directly with a supplier, without an RFQ.</summary>
public class NewSpotPo
{
    public int? VesselId { get; set; }
    public int? DepartmentId { get; set; }
    public int SupplierId { get; set; }
    public string SupplierRef { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public int? OrderPortId { get; set; }
    public string Remarks { get; set; } = "";

    /// <summary>A spot PO with no value, only so deliveries and invoices have something to attach to. Skips approval.</summary>
    public bool IsDummy { get; set; }
    public decimal DiscountPct { get; set; }
    public decimal AdditionalCharges { get; set; }
    public string AdditionalChargeDescription { get; set; } = "";
    public List<NewRfqLine> Lines { get; set; } = new();
}

public class QuoteInput
{
    public string SupplierRef { get; set; } = "";
    public int? DeliveryDays { get; set; }
    public DateTime? ValidityDate { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal DiscountPct { get; set; }
    public decimal AdditionalCharges { get; set; }
    public string AdditionalChargeDescription { get; set; } = "";
    public string Remarks { get; set; } = "";
    public List<QuoteLine> Lines { get; set; } = new();
}

public enum SupplierRfqTab
{
    Pending,
    Archived,
    Orders
}

public enum SupplierRfqStatus
{
    Pending,
    QuotationSubmitted,
    Closed,
    Ordered,
    NotSelected
}

public record SupplierRfqRow(Rfq Rfq, SupplierRfqStatus Status, int QuotedLines, List<string> PoNumbers, bool Archived)
{
    public string StatusLabel => Status switch
    {
        SupplierRfqStatus.Pending => "Pending quote",
        SupplierRfqStatus.QuotationSubmitted => "Quotation submitted",
        SupplierRfqStatus.Closed => "Closed",
        SupplierRfqStatus.Ordered => PoNumbers.Count > 0 ? string.Join(", ", PoNumbers) : "Ordered",
        SupplierRfqStatus.NotSelected => "Not selected",
        _ => ""
    };
}

public record SupplierRfqPage(List<SupplierRfqRow> Rows, int Total);
