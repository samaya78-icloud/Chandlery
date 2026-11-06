namespace Chandlery.Models;

/// <summary>One line of an entity's audit history, e.g. "CHANGED Remarks from [a] to [b]".</summary>
public class AuditEntry
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>"Requisition", "Rfq" or "PurchaseOrder".</summary>
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTime At { get; set; }
    public string Text { get; set; } = "";
}
