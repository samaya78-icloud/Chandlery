namespace Chandlery.Models;

public class Item
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid CategoryId { get; set; }
    public string UnitOfMeasure { get; set; } = "";

    /// <summary>A disabled item stays on old documents but cannot be added to new ones.</summary>
    public bool IsActive { get; set; } = true;
}
