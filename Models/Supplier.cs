namespace Chandlery.Models;

public class Supplier
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ContactEmail { get; set; } = "";

    /// <summary>Addresses (semicolon-separated) the supplier wants RFQ notices sent to. Stored only; nothing sends email yet.</summary>
    public string NotificationEmails { get; set; } = "";
}
