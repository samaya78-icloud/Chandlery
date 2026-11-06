namespace Chandlery.Models;

/// <summary>A company's private address-book link to a (platform-shared) Supplier — not a global directory browse.</summary>
public class CompanySupplier
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SupplierId { get; set; }
    public DateTime AddedAt { get; set; }
}
