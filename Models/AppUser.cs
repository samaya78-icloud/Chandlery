namespace Chandlery.Models;

public enum UserRole
{
    CompanyAdmin,
    CompanyUser,
    SupplierAdmin,
    SupplierUser
}

public class AppUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>Set for Company* roles — the tenant this user belongs to.</summary>
    public Guid? CompanyId { get; set; }

    /// <summary>Set for Supplier* roles — links them to their (platform-shared) Supplier record.</summary>
    public Guid? SupplierId { get; set; }

    public bool IsCompanyRole => Role is UserRole.CompanyAdmin or UserRole.CompanyUser;
    public bool IsSupplierRole => Role is UserRole.SupplierAdmin or UserRole.SupplierUser;
}
