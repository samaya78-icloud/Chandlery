namespace Chandlery.Models;

public class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? RegistrationNumber { get; set; }
    public string? Address { get; set; }
    public string ContactEmail { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>What the company calls approval steps 1 to 3 (for example "Superintendent review"). Blank uses the default wording.</summary>
    public string ApprovalLevel1Name { get; set; } = "";
    public string ApprovalLevel2Name { get; set; } = "";
    public string ApprovalLevel3Name { get; set; } = "";
}
