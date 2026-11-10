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

    /// <summary>Sign-in name for people without an email (crew onboard sign in with their crew code). Null for everyone else.</summary>
    public string? LoginName { get; set; }

    /// <summary>A disabled account cannot sign in. Crew accounts are disabled when the person signs off the vessel.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The next sign-in must be followed by choosing a new password (a temporary one was handed over).</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>May start a review of, sign or return a risk assessment ashore. A company admin always may.</summary>
    public bool IsRiskReviewer { get; set; }

    /// <summary>May plan and run projects ashore (create them, set budgets, record approval and progress). A company admin always may.</summary>
    public bool IsProjectManager { get; set; }

    public bool IsCompanyRole => Role is UserRole.CompanyAdmin or UserRole.CompanyUser;
    public bool IsSupplierRole => Role is UserRole.SupplierAdmin or UserRole.SupplierUser;
}
