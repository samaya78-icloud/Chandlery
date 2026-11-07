namespace Chandlery.Models;

/// <summary>
/// What the office and the supplier know about getting an order to the supplier: when it was made available to them through the
/// system, and when the supplier acknowledged it. Owned by the office and sent down to the vessel the order is for, so the vessel
/// can show "sent" and "acknowledged" without ever being able to change them. (That the vessel emailed the order is recorded on
/// the order itself, which the vessel owns.)
/// </summary>
public class PoDispatch
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PurchaseOrderId { get; set; }

    /// <summary>When the approved order was made available to the supplier in their portal (or made available again).</summary>
    public DateTime? ViaSystemAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }
    public string AcknowledgedByName { get; set; } = "";
}
