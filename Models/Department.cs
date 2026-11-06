namespace Chandlery.Models;

/// <summary>A department within a company (Deck, Engine, ...). Company-scoped.</summary>
public class Department
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Says a department may use a category. Linking a category includes everything beneath it. A department can only
/// pick items from its linked categories on requisitions and purchase orders, and a department with no links picks nothing.
/// </summary>
public class DepartmentCategory
{
    public Guid CompanyId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid CategoryId { get; set; }
}
