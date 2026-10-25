namespace Chandlery.Models;

/// <summary>A vessel belonging to a company. Company-scoped.</summary>
public class Vessel
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string? ImoNumber { get; set; }
}
