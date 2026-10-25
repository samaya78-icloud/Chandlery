namespace Chandlery.Models;

/// <summary>Shared, platform-wide port list (not company-scoped), like Category and Supplier.</summary>
public class Port
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
}
