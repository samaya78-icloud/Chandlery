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
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>Null means the rule applies to every department without its own rules.</summary>
    public Guid? DepartmentId { get; set; }

    /// <summary>Highest PO total this tier covers; null means no upper limit.</summary>
    public decimal? UpToAmount { get; set; }

    /// <summary>1 to 3; level N+1 opens only after level N is approved.</summary>
    public int Level { get; set; } = 1;
    public Guid ApproverUserId { get; set; }

    /// <summary>
    /// Set only for a rule about one project budget category (for example "Owner's costs"): it is then an extra sign-off on projects with that category in their budget, and is
    /// never used for purchase orders or invoices.
    /// </summary>
    public string? ProjectCategory { get; set; }
}

/// <summary>One approver's pending/decided slot on a purchase order.</summary>
public class PoApproval
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public int Level { get; set; }
    public Guid ApproverUserId { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string Comment { get; set; } = "";
}
