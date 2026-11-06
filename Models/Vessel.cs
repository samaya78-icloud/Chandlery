namespace Chandlery.Models;

/// <summary>A vessel belonging to a company. Company-scoped.</summary>
public class Vessel
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string? ImoNumber { get; set; }

    /// <summary>Short code that starts every document reference for this vessel (MVSF-REQ-2026-H0007). Unique within the company.</summary>
    public string ShortCode { get; set; } = "";

    /// <summary>The node of the vessel group tree this vessel belongs to (every vessel has one; the tree's level 1 is mandatory).</summary>
    public Guid? GroupId { get; set; }
}
