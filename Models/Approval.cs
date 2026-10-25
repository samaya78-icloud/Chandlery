namespace Chandlery.Models;

public enum ApprovalDecision
{
    Pending,
    Approved,
    Rejected
}

/// <summary>
/// One row of the approval matrix: this user can approve at this level for POs up to this amount.
/// Rows with the same department and the same UpToAmount form one tier; any approver at a level
/// satisfies that level.
/// </summary>
public class ApprovalRule
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>Null means the rule applies to every department without its own rules.</summary>
    public int? DepartmentId { get; set; }

    /// <summary>Highest PO total this tier covers; null means no upper limit.</summary>
    public decimal? UpToAmount { get; set; }

    /// <summary>1 to 3; level N+1 opens only after level N is approved.</summary>
    public int Level { get; set; } = 1;
    public int ApproverUserId { get; set; }
}

/// <summary>One approver's pending/decided slot on a purchase order.</summary>
public class PoApproval
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int PurchaseOrderId { get; set; }
    public int Level { get; set; }
    public int ApproverUserId { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public int? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string Comment { get; set; } = "";
}
