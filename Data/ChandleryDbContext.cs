using Chandlery.Services.Sync;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Chandlery.Data;

public class ChandleryDbContext(DbContextOptions<ChandleryDbContext> options, NodeIdentity? node = null) : DbContext(options)
{
    /// <summary>Which installation this context works for. Supplied by dependency injection; falls back to the application-wide identity.</summary>
    public NodeIdentity Node { get; } = node ?? NodeContext.Current;

    /// <summary>
    /// Set while sync is applying a document from the other side: the change keeps the version and origin it arrived with and
    /// is not announced again, so documents never bounce between hub and vessel.
    /// </summary>
    public bool ApplyingSync { get; set; }

    /// <summary>
    /// Set explicitly by the caller right after the context is created (via IDbContextFactory —
    /// contexts are short-lived, one per operation, per Blazor Server + EF Core guidance).
    /// Null bypasses the company filter entirely — used for Supplier-side cross-company queries,
    /// which apply their own explicit filtering instead.
    /// </summary>
    public Guid? CurrentCompanyId { get; set; }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<CompanySupplier> CompanySuppliers => Set<CompanySupplier>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Vessel> Vessels => Set<Vessel>();
    public DbSet<VesselGroup> VesselGroups => Set<VesselGroup>();
    public DbSet<VesselPolicy> VesselPolicies => Set<VesselPolicy>();
    public DbSet<VesselModuleSwitch> VesselModuleSwitches => Set<VesselModuleSwitch>();
    public DbSet<UserGroupScope> UserGroupScopes => Set<UserGroupScope>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Port> Ports => Set<Port>();
    public DbSet<CrewMember> CrewMembers => Set<CrewMember>();
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
    public DbSet<NodeInfo> NodeInfos => Set<NodeInfo>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<SyncDirty> SyncDirties => Set<SyncDirty>();

    // Change tracking for sync, kept as shadow properties so the entity classes stay clean.
    internal const string SyncCreatedAt = "SyncCreatedAt";
    internal const string SyncUpdatedAt = "SyncUpdatedAt";
    internal const string SyncVersion = "SyncVersion";
    internal const string OriginNodeId = "OriginNodeId";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>(b => b.Property(c => c.OfficeCode).HasMaxLength(10).HasDefaultValue("HO"));

        modelBuilder.Entity<SyncDirty>(b =>
        {
            b.Property(d => d.DocType).HasMaxLength(60);
            b.HasIndex(d => d.At);
        });

        modelBuilder.Entity<NodeInfo>(b =>
        {
            b.Property(n => n.Profile).HasMaxLength(20);
            b.Property(n => n.Name).HasMaxLength(120);
        });

        modelBuilder.Entity<NumberSequence>(b =>
        {
            b.HasKey(n => new { n.Scope, n.Type, n.Year, n.Lane });
            b.Property(n => n.Scope).HasMaxLength(20);
            b.Property(n => n.Type).HasMaxLength(10);
            b.Property(n => n.Lane).HasMaxLength(2);
        });

        modelBuilder.Entity<Vessel>(b =>
        {
            b.Property(v => v.ShortCode).HasMaxLength(10).HasDefaultValue("");
            b.HasIndex(v => new { v.CompanyId, v.ShortCode }).IsUnique().HasFilter("\"ShortCode\" <> ''");
        });

        modelBuilder.Entity<Supplier>();

        modelBuilder.Entity<AppUser>(b =>
        {
            b.HasIndex(u => u.Email).IsUnique();
            b.Property(u => u.LoginName).HasMaxLength(30);
            b.HasIndex(u => new { u.CompanyId, u.LoginName }).IsUnique().HasFilter("\"LoginName\" IS NOT NULL");
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
            b.HasOne<VesselGroup>().WithMany().HasForeignKey(v => v.GroupId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(v => CurrentCompanyId == null || v.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<VesselGroup>(b =>
        {
            b.Property(g => g.Name).HasMaxLength(80);
            b.HasOne<Company>().WithMany().HasForeignKey(g => g.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<VesselGroup>().WithMany().HasForeignKey(g => g.ParentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(g => new { g.CompanyId, g.ParentId });
            b.HasQueryFilter(g => CurrentCompanyId == null || g.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<VesselPolicy>(b =>
        {
            b.Property(p => p.MasterApprovalLimitUsd).HasColumnType("numeric(18,2)");
            b.Property(p => p.MonthlyBudgetUsd).HasColumnType("numeric(18,2)");
            b.Property(p => p.Note).HasMaxLength(300);
            b.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<VesselGroup>().WithMany().HasForeignKey(p => p.GroupId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Vessel>().WithMany().HasForeignKey(p => p.VesselId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Department>().WithMany().HasForeignKey(p => p.DepartmentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(p => new { p.CompanyId, p.GroupId });
            b.HasIndex(p => new { p.CompanyId, p.VesselId });
            b.HasQueryFilter(p => CurrentCompanyId == null || p.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<UserGroupScope>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<VesselGroup>().WithMany().HasForeignKey(s => s.GroupId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(s => new { s.UserId, s.GroupId }).IsUnique();
            b.HasQueryFilter(s => CurrentCompanyId == null || s.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<VesselModuleSwitch>(b =>
        {
            b.Property(m => m.Module).HasMaxLength(40);
            b.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<VesselGroup>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Vessel>().WithMany().HasForeignKey(m => m.VesselId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(m => new { m.CompanyId, m.GroupId, m.VesselId, m.Module }).IsUnique();
            b.HasQueryFilter(m => CurrentCompanyId == null || m.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<CrewMember>(b =>
        {
            b.Property(c => c.CrewCode).HasMaxLength(30);
            b.Property(c => c.FullName).HasMaxLength(120);
            b.Property(c => c.Rank).HasMaxLength(60);
            b.Property(c => c.Nationality).HasMaxLength(60);
            b.Property(c => c.IdType).HasMaxLength(40);
            b.Property(c => c.IdLast4).HasMaxLength(8);
            b.Property(c => c.Phone).HasMaxLength(40);
            b.Property(c => c.Email).HasMaxLength(200);
            b.Property(c => c.CredentialsChangedBy).HasMaxLength(120);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(c => c.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.CompanyId, c.CrewCode }).IsUnique();
            b.HasIndex(c => c.VesselId);
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
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
            b.Property(r => r.InvitedSupplierIds).HasColumnType("uuid[]");
            b.HasMany(r => r.Lines).WithOne().HasForeignKey("RfqId").OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RfqLine>(b =>
        {
            b.Property<Guid>("RfqId");
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
            b.Property<Guid>("RequisitionId");
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

        // Every root record (single GUID key) gets created/updated time, a version counter and the node that created it.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (entityType.IsOwned()) continue;
            var key = entityType.FindPrimaryKey();
            if (key is null) continue;
            var singleGuidId = key.Properties.Count == 1 && key.Properties[0].Name == "Id" && key.Properties[0].ClrType == typeof(Guid);
            if (!singleGuidId && MasterData.Find(entityType.ClrType) is null) continue;   // master records keyed by code or by two ids sync too
            var entity = modelBuilder.Entity(entityType.ClrType);
            entity.Property<DateTime>(SyncCreatedAt).HasDefaultValueSql("now()");
            entity.Property<DateTime>(SyncUpdatedAt).HasDefaultValueSql("now()");
            entity.Property<int>(SyncVersion).HasDefaultValue(1);
            entity.Property<Guid?>(OriginNodeId);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Stamps created/updated time, version and origin node on the records being saved. A change to a child line counts as a change
    /// to the document that owns it, so the document's version moves whenever anything inside it does.
    /// </summary>
    private void StampChanges()
    {
        if (ApplyingSync) return;
        ChangeTracker.DetectChanges();
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var now = DateTime.UtcNow;
            var entries = ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
            var stamped = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var entry in entries)
            {
                // Master data belongs to the office: a vessel installation holds a copy and may not change it.
                if (Node.SyncEnabled && MasterData.Find(entry.Metadata.ClrType) is { } master)
                {
                    // The one thing a vessel may change on a crew record is its login (a crew admin resets a password).
                    var credentialsOnly = entry.Entity is CrewMember && entry.State == EntityState.Modified && OnlyCredentialsChanged(entry);
                    if (Node.IsVessel && !credentialsOnly)
                        throw new InvalidOperationException($"{master.DocType} is master data kept by the office; a vessel receives it through sync and cannot change it.");
                    if (entry.State == EntityState.Deleted)
                    {
                        SyncDirties.Add(MasterData.NoteFor(entry, master, now));
                        continue;
                    }
                }

                var root = entry.Metadata.IsOwned() ? OwnerOf(entry) : entry.Entity is RequisitionLine ? RequisitionOf(entry) : entry;
                if (root is null || root.State == EntityState.Deleted || root.Metadata.FindProperty(SyncUpdatedAt) is null) continue;
                if (!stamped.Add(root.Entity)) continue;

                if (root.State == EntityState.Added)
                {
                    root.Property(SyncCreatedAt).CurrentValue = now;
                    root.Property(SyncUpdatedAt).CurrentValue = now;
                    root.Property(SyncVersion).CurrentValue = 1;
                    root.Property(OriginNodeId).CurrentValue = Node.Id;
                }
                else
                {
                    root.Property(SyncUpdatedAt).CurrentValue = now;
                    root.Property(SyncVersion).CurrentValue = (int)(root.Property(SyncVersion).CurrentValue ?? 0) + 1;
                }

                // A synced document changed: leave a note, in this same transaction, for the sync publisher.
                if (Node.SyncEnabled && root.Entity is Requisition requisition)
                    SyncDirties.Add(new SyncDirty { DocType = SyncDocTypes.Requisition, DocId = requisition.Id, At = now });
                else if (Node.SyncEnabled && MasterData.Find(root.Entity.GetType()) is { } changed)
                {
                    var note = MasterData.NoteFor(root, changed, now);
                    // Onboard, a crew change is a login change, and it travels up to the office as such.
                    if (Node.IsVessel) { note.DocType = SyncDocTypes.CrewCredential; note.Snapshot = null; }
                    SyncDirties.Add(note);
                }
            }
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private static bool OnlyCredentialsChanged(EntityEntry crew)
        => crew.Properties.Where(p => p.IsModified).All(p => CrewMember.CredentialColumns.Contains(p.Metadata.Name)
            || p.Metadata.Name is SyncUpdatedAt or SyncVersion);

    /// <summary>
    /// A requisition line is its own table but part of the requisition document: changing one changes the requisition's version.
    /// The requisition must be tracked alongside its lines (load it with them), otherwise its version could not move and sync
    /// would silently drop the change, so that case is refused.
    /// </summary>
    private EntityEntry? RequisitionOf(EntityEntry line)
    {
        var property = line.Property("RequisitionId");
        var requisitionId = (Guid)(line.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<Requisition>().FirstOrDefault(e => e.Entity.Id == requisitionId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("A requisition line was changed without loading its requisition; load the requisition with its lines.");
        return parent;
    }

    /// <summary>The tracked document that owns a child line, found through the line's owner key.</summary>
    private EntityEntry? OwnerOf(EntityEntry owned)
    {
        var ownership = owned.Metadata.FindOwnership();
        if (ownership is null) return null;
        var principalType = ownership.PrincipalEntityType;
        var keyProperties = principalType.FindPrimaryKey()!.Properties;
        var values = ownership.Properties.Select(p => owned.Property(p.Name).CurrentValue).ToArray();
        foreach (var candidate in ChangeTracker.Entries())
        {
            if (candidate.Metadata != principalType) continue;
            var match = true;
            for (var i = 0; i < keyProperties.Count; i++)
                if (!Equals(candidate.Property(keyProperties[i].Name).CurrentValue, values[i])) { match = false; break; }
            if (match) return candidate;
        }
        return null;
    }
}
