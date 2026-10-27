using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Data;

public class ChandleryDbContext(DbContextOptions<ChandleryDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Set explicitly by the caller right after the context is created (via IDbContextFactory —
    /// contexts are short-lived, one per operation, per Blazor Server + EF Core guidance).
    /// Null bypasses the company filter entirely — used for Supplier-side cross-company queries,
    /// which apply their own explicit filtering instead.
    /// </summary>
    public int? CurrentCompanyId { get; set; }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<CompanySupplier> CompanySuppliers => Set<CompanySupplier>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Vessel> Vessels => Set<Vessel>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Port> Ports => Set<Port>();
    public DbSet<RfqArchive> RfqArchives => Set<RfqArchive>();
    public DbSet<Rfq> Rfqs => Set<Rfq>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<Requisition> Requisitions => Set<Requisition>();
    public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
    public DbSet<PoApproval> PoApprovals => Set<PoApproval>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<DeliveryOrder> DeliveryOrders => Set<DeliveryOrder>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceApproval> InvoiceApprovals => Set<InvoiceApproval>();
    public DbSet<InvoiceDocument> InvoiceDocuments => Set<InvoiceDocument>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Uom> Uoms => Set<Uom>();
    public DbSet<UomConversion> UomConversions => Set<UomConversion>();
    public DbSet<SupplierBankAccount> SupplierBankAccounts => Set<SupplierBankAccount>();
    public DbSet<TaxCode> TaxCodes => Set<TaxCode>();
    public DbSet<DepartmentCategory> DepartmentCategories => Set<DepartmentCategory>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<CompanyCurrency> CompanyCurrencies => Set<CompanyCurrency>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>();

        modelBuilder.Entity<Supplier>();

        modelBuilder.Entity<AppUser>(b =>
        {
            b.HasIndex(u => u.Email).IsUnique();
            b.HasOne<Company>().WithMany().HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(u => u.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CompanySupplier>(b =>
        {
            b.HasIndex(cs => new { cs.CompanyId, cs.SupplierId }).IsUnique();
            b.HasOne<Company>().WithMany().HasForeignKey(cs => cs.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Supplier>().WithMany().HasForeignKey(cs => cs.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(cs => CurrentCompanyId == null || cs.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Category>().WithMany().HasForeignKey(c => c.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
            b.Property(c => c.IsActive).HasDefaultValue(true);
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Port>(b =>
        {
            b.HasIndex(p => new { p.Name, p.Country }).IsUnique();
        });

        modelBuilder.Entity<Vessel>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(v => v.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(v => new { v.CompanyId, v.Name }).IsUnique();
            b.HasQueryFilter(v => CurrentCompanyId == null || v.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Department>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(d => new { d.CompanyId, d.Name }).IsUnique();
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Item>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Category>().WithMany().HasForeignKey(i => i.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.Property(i => i.IsActive).HasDefaultValue(true);
            b.HasQueryFilter(i => CurrentCompanyId == null || i.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Rfq>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Port>().WithMany().HasForeignKey(r => r.DeliveryPortId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Requisition>().WithMany().HasForeignKey(r => r.RequisitionId).OnDelete(DeleteBehavior.Restrict);
            b.Property(r => r.InvitedSupplierIds).HasColumnType("integer[]");
            b.HasMany(r => r.Lines).WithOne().HasForeignKey("RfqId").OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RfqLine>(b =>
        {
            b.Property<int>("RfqId");
            b.Property(l => l.Quantity).HasPrecision(18, 3);
        });

        modelBuilder.Entity<RfqArchive>(b =>
        {
            b.HasIndex(a => new { a.RfqId, a.SupplierId }).IsUnique();
            b.HasOne<Rfq>().WithMany().HasForeignKey(a => a.RfqId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Supplier>().WithMany().HasForeignKey(a => a.SupplierId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Quote>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(q => q.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(q => q.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.Property(q => q.DiscountPct).HasPrecision(5, 2);
            b.Property(q => q.AdditionalCharges).HasPrecision(18, 2);
            b.OwnsMany(q => q.Lines, lb =>
            {
                lb.WithOwner().HasForeignKey("QuoteId");
                lb.Property(l => l.UnitPrice).HasPrecision(18, 2);
                lb.Property(l => l.DiscountPct).HasPrecision(5, 2);
            });
            b.HasQueryFilter(q => CurrentCompanyId == null || q.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<PurchaseOrder>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(p => p.Total);
            b.Ignore(p => p.Subtotal);
            b.Property(p => p.DiscountPct).HasPrecision(5, 2);
            b.Property(p => p.AdditionalCharges).HasPrecision(18, 2);
            b.Property(p => p.UsdRate).HasPrecision(18, 8);
            b.OwnsMany(p => p.Lines, lb =>
            {
                lb.WithOwner().HasForeignKey("PurchaseOrderId");
                lb.Property(l => l.Quantity).HasPrecision(18, 3);
                lb.Property(l => l.UnitPrice).HasPrecision(18, 2);
                lb.Property(l => l.DiscountPct).HasPrecision(5, 2);
            });
            b.HasQueryFilter(p => CurrentCompanyId == null || p.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Requisition>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Department>().WithMany().HasForeignKey(r => r.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Port>().WithMany().HasForeignKey(r => r.DeliveryPortId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(r => r.Lines).WithOne().HasForeignKey("RequisitionId").OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(r => new { r.CompanyId, r.ReqnNumber }).IsUnique();
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RequisitionLine>(b =>
        {
            b.Property<int>("RequisitionId");
            b.Property(l => l.Quantity).HasPrecision(18, 3);
            b.Property(l => l.ApprovedQuantity).HasPrecision(18, 3);
        });

        modelBuilder.Entity<ApprovalRule>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Department>().WithMany().HasForeignKey(r => r.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AppUser>().WithMany().HasForeignKey(r => r.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
            b.Property(r => r.UpToAmount).HasPrecision(18, 2);
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<PoApproval>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(a => a.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AppUser>().WithMany().HasForeignKey(a => a.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.PurchaseOrderId, a.Level });
            b.HasQueryFilter(a => CurrentCompanyId == null || a.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<DeliveryOrder>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(d => d.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(d => d.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(d => d.InvoiceId).OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(d => new { d.CompanyId, d.DoNumber }).IsUnique();
            b.OwnsMany(d => d.Lines, lb =>
            {
                lb.WithOwner().HasForeignKey("DeliveryOrderId");
                lb.Property(l => l.Quantity).HasPrecision(18, 3);
            });
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<DepartmentCategory>(b =>
        {
            b.HasKey(x => new { x.DepartmentId, x.CategoryId });
            b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => CurrentCompanyId == null || x.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Currency>(b =>
        {
            b.HasKey(c => c.Code);
            b.Property(c => c.Code).HasMaxLength(3);
            b.Property(c => c.Name).HasMaxLength(80);
        });

        modelBuilder.Entity<CompanyCurrency>(b =>
        {
            b.HasKey(c => new { c.CompanyId, c.CurrencyCode });
            b.Property(c => c.CurrencyCode).HasMaxLength(3);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<ExchangeRate>(b =>
        {
            b.Property(r => r.CurrencyCode).HasMaxLength(3);
            b.Property(r => r.UnitsPerUsd).HasPrecision(18, 8);
            b.Property(r => r.Source).HasMaxLength(40);
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            // One shared market rate per currency and day, and one company rate per company, currency and day.
            b.HasIndex(r => new { r.CurrencyCode, r.RateDate }).IsUnique().HasFilter("\"CompanyId\" IS NULL");
            b.HasIndex(r => new { r.CompanyId, r.CurrencyCode, r.RateDate }).IsUnique().HasFilter("\"CompanyId\" IS NOT NULL");
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<SupplierBankAccount>(b =>
        {
            b.HasOne<Supplier>().WithMany().HasForeignKey(a => a.SupplierId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaxCode>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CompanyId, t.Code }).IsUnique();
            b.Property(t => t.RatePct).HasPrecision(5, 2);
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(i => i.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(i => i.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaxCode>().WithMany().HasForeignKey(i => i.TaxCodeId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SupplierBankAccount>().WithMany().HasForeignKey(i => i.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Department>().WithMany().HasForeignKey(i => i.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Vessel>().WithMany().HasForeignKey(i => i.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(i => new { i.CompanyId, i.InvoiceNumber }).IsUnique();
            b.Ignore(i => i.TotalExclTax);
            b.Ignore(i => i.TaxAmount);
            b.Ignore(i => i.TotalInclTax);
            b.Property(i => i.InvoiceAmount).HasPrecision(18, 2);
            b.Property(i => i.AdditionalItemsCost).HasPrecision(18, 2);
            b.Property(i => i.AdditionalDeliveryCost).HasPrecision(18, 2);
            b.Property(i => i.DiscountOrCredit).HasPrecision(18, 2);
            b.Property(i => i.TaxRatePct).HasPrecision(5, 2);
            b.Property(i => i.UsdRate).HasPrecision(18, 8);
            b.OwnsMany(i => i.Lines, lb =>
            {
                lb.WithOwner().HasForeignKey("InvoiceId");
                lb.Property(l => l.Quantity).HasPrecision(18, 3);
                lb.Property(l => l.UnitPrice).HasPrecision(18, 2);
                lb.Property(l => l.DiscountPct).HasPrecision(5, 2);
            });
            b.HasQueryFilter(i => CurrentCompanyId == null || i.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<InvoiceApproval>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(a => a.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AppUser>().WithMany().HasForeignKey(a => a.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.InvoiceId, a.Level });
            b.HasQueryFilter(a => CurrentCompanyId == null || a.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<InvoiceDocument>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(d => d.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Uom>(b =>
        {
            b.Property(u => u.Code).HasMaxLength(12);
            b.Property(u => u.Name).HasMaxLength(60);
            b.HasOne<Company>().WithMany().HasForeignKey(u => u.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(u => new { u.CompanyId, u.Code }).IsUnique();
            b.HasQueryFilter(u => CurrentCompanyId == null || u.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<UomConversion>(b =>
        {
            b.Property(c => c.Factor).HasPrecision(18, 8);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Uom>().WithMany().HasForeignKey(c => c.FromUomId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Uom>().WithMany().HasForeignKey(c => c.ToUomId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Item>().WithMany().HasForeignKey(c => c.ItemId).OnDelete(DeleteBehavior.Cascade);
            // Null item ids never collide in a unique index, so the general and the per-item conversions each get their own.
            b.HasIndex(c => new { c.CompanyId, c.FromUomId, c.ToUomId }).IsUnique().HasFilter("\"ItemId\" IS NULL");
            b.HasIndex(c => new { c.CompanyId, c.FromUomId, c.ToUomId, c.ItemId }).IsUnique().HasFilter("\"ItemId\" IS NOT NULL");
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Attachment>(b =>
        {
            b.Property(a => a.FileName).HasMaxLength(260);
            b.Property(a => a.ContentType).HasMaxLength(80);
            b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Requisition>().WithMany().HasForeignKey(a => a.RequisitionId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(a => new { a.RequisitionId, a.RequisitionLineId });
            b.HasQueryFilter(a => CurrentCompanyId == null || a.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(a => new { a.CompanyId, a.EntityType, a.EntityId });
            b.HasQueryFilter(a => CurrentCompanyId == null || a.CompanyId == CurrentCompanyId);
        });
    }
}
