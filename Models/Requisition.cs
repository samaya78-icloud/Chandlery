namespace Chandlery.Models;

public enum RequisitionStatus
{
    Open,
    RfqPosted,
    Ordered,
    Cancelled
}

public enum Urgency
{
    Normal,
    Urgent
}

/// <summary>A vessel's request for items, entered once. RFQs to many suppliers are created from it.</summary>
public class Requisition
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string ReqnNumber { get; set; } = "";
    public RfqType Type { get; set; } = RfqType.Stores;
    public int VesselId { get; set; }
    public int? DepartmentId { get; set; }
    public int? DeliveryPortId { get; set; }
    public Urgency Urgency { get; set; } = Urgency.Normal;
    public DateTime ReqnDate { get; set; }
    public DateTime? VesselEta { get; set; }
    public DateTime? VesselEtd { get; set; }
    public DateTime? EstDeliveryDate { get; set; }
    public bool IsBreakdown { get; set; }
    public string Remarks { get; set; } = "";
    public RequisitionStatus Status { get; set; } = RequisitionStatus.Open;
    public int? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public List<RequisitionLine> Lines { get; set; } = new();
}

public class RequisitionLine
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>What the approver allows; starts equal to <see cref="Quantity"/>.</summary>
    public decimal ApprovedQuantity { get; set; }
    public string Uom { get; set; } = "";
    public string? Description { get; set; }
    public string? DrawingNo { get; set; }
}
