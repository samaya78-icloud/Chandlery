namespace Chandlery.Models;

/// <summary>A department within a company (Deck, Engine, ...). Company-scoped.</summary>
public class Department
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Says a department may use a category. Linking a category includes everything beneath it. A department can only
/// pick items from its linked categories on requisitions and purchase orders, and a department with no links picks nothing.
/// </summary>
public class DepartmentCategory
{
    public int CompanyId { get; set; }
    public int DepartmentId { get; set; }
    public int CategoryId { get; set; }
}
