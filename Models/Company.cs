namespace Chandlery.Models;

public class Company
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? RegistrationNumber { get; set; }
    public string? Address { get; set; }
    public string ContactEmail { get; set; } = "";

    /// <summary>Stands in for the vessel code on documents that have no vessel (HO-INV-2026-H0003).</summary>
    public string OfficeCode { get; set; } = "HO";
    public DateTime CreatedAt { get; set; }

    /// <summary>What the company calls approval steps 1 to 3 (for example "Superintendent review"). Blank uses the default wording.</summary>
    public string ApprovalLevel1Name { get; set; } = "";
    public string ApprovalLevel2Name { get; set; } = "";
    public string ApprovalLevel3Name { get; set; } = "";

    /// <summary>Words the office reviewer is shown highlighted in tank inspection comments, separated by commas. Blank uses the standard list. Set by the office; vessels receive it with the rest of the company record and cannot change it.</summary>
    public string TankReviewKeywords { get; set; } = "";

    /// <summary>When on, whoever wrote a risk assessment cannot be the one to sign it, if another reviewer is available on that side.</summary>
    public bool RiskSignOffByAnotherPerson { get; set; }

    /// <summary>When on, putting off a critical maintenance job needs a signed risk assessment of the vessel to be named.</summary>
    public bool RiskRequiredForCriticalDeferral { get; set; }
}
