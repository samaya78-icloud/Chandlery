namespace Chandlery.Models;

public class Item
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    public string UnitOfMeasure { get; set; } = "";
}
