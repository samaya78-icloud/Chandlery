namespace Chandlery.Models;

public enum RfqStatus
{
    Draft,
    Sent,
    QuotesReceived,
    Awarded
}

public enum RfqType
{
    Stores,
    Spares
}

public class Rfq
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string RfqNumber { get; set; } = "";
    public RfqType Type { get; set; } = RfqType.Stores;
    public string Vessel { get; set; } = "";
    public int? VesselId { get; set; }
    public int? DeliveryPortId { get; set; }

    /// <summary>Set when the RFQ was created from a requisition.</summary>
    public int? RequisitionId { get; set; }
    public DateTime? VesselEta { get; set; }
    public DateTime? DateRequired { get; set; }
    public DateTime? DueDate { get; set; }

    public string BuyerName { get; set; } = "";
    public string BuyerDesignation { get; set; } = "";
    public string BuyerEmail { get; set; } = "";
    public string Remarks { get; set; } = "";

    // Spares RFQs only: the equipment the spares are for.
    public string EquipmentDescription { get; set; } = "";
    public string EquipmentParticulars { get; set; } = "";
    public string EquipmentMaker { get; set; } = "";
    public string EquipmentSerialNo { get; set; } = "";

    public DateTime CreatedDate { get; set; }
    public RfqStatus Status { get; set; } = RfqStatus.Draft;
    public List<RfqLine> Lines { get; set; } = new();
    public List<int> InvitedSupplierIds { get; set; } = new();

    /// <summary>The buyer's note on the draft evaluation (why these suppliers), kept until purchase orders are raised.</summary>
    public string EvaluationNote { get; set; } = "";

    /// <summary>When the winner selections (or the note) were last changed, and by whom. Null until something is selected.</summary>
    public DateTime? EvaluationUpdatedAt { get; set; }
    public string EvaluationUpdatedByName { get; set; } = "";

    /// <summary>Quotes are closed once the due date (a whole day, UTC) has passed.</summary>
    public bool IsPastDue => DueDate.HasValue && DateTime.UtcNow.Date > DueDate.Value.Date;
}

public class RfqLine
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public decimal Quantity { get; set; }
    public string Uom { get; set; } = "";
    public string? Description { get; set; }
    public string? DrawingNo { get; set; }
    public int? RequisitionLineId { get; set; }
    public int? WinningSupplierId { get; set; }
}

/// <summary>A supplier's own archive flag for an RFQ (works even if they never quoted).</summary>
public class RfqArchive
{
    public int Id { get; set; }
    public int RfqId { get; set; }
    public int SupplierId { get; set; }
    public DateTime ArchivedAt { get; set; }
}
