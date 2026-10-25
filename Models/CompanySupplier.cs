namespace Chandlery.Models;

/// <summary>A company's private address-book link to a (platform-shared) Supplier — not a global directory browse.</summary>
public class CompanySupplier
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int SupplierId { get; set; }
    public DateTime AddedAt { get; set; }
}
