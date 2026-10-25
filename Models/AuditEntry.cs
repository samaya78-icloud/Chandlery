namespace Chandlery.Models;

/// <summary>One line of an entity's audit history, e.g. "CHANGED Remarks from [a] to [b]".</summary>
public class AuditEntry
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>"Requisition", "Rfq" or "PurchaseOrder".</summary>
    public string EntityType { get; set; } = "";
    public int EntityId { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTime At { get; set; }
    public string Text { get; set; } = "";
}
