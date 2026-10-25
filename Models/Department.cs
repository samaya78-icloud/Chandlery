namespace Chandlery.Models;

/// <summary>A department within a company (Deck, Engine, ...). Company-scoped.</summary>
public class Department
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
}
