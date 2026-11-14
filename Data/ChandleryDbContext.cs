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
    /// Set by the one action that lets the office take over a document a vessel created (for example an RFQ it raised onboard).
    /// Without it, changing a document owned by the other side is refused.
    /// </summary>
    public bool AllowTakeOver { get; set; }

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
    public DbSet<PoDispatch> PoDispatches => Set<PoDispatch>();
    public DbSet<WorklistListItem> WorklistListItems => Set<WorklistListItem>();
    public DbSet<WorklistTask> WorklistTasks => Set<WorklistTask>();
    public DbSet<WorklistEntry> WorklistEntries => Set<WorklistEntry>();
    public DbSet<MachineryClass> MachineryClasses => Set<MachineryClass>();
    public DbSet<JobTemplate> JobTemplates => Set<JobTemplate>();
    public DbSet<MachineryItem> MachineryItems => Set<MachineryItem>();
    public DbSet<PlannedJob> PlannedJobs => Set<PlannedJob>();
    public DbSet<RunningHoursReading> RunningHoursReadings => Set<RunningHoursReading>();
    public DbSet<JobCompletion> JobCompletions => Set<JobCompletion>();
    public DbSet<JobVerification> JobVerifications => Set<JobVerification>();
    public DbSet<JobDeferralRequest> JobDeferralRequests => Set<JobDeferralRequest>();
    public DbSet<JobDeferralDecision> JobDeferralDecisions => Set<JobDeferralDecision>();
    public DbSet<PlanChangeRequest> PlanChangeRequests => Set<PlanChangeRequest>();
    public DbSet<PlanChangeDecision> PlanChangeDecisions => Set<PlanChangeDecision>();
    public DbSet<MachinerySpare> MachinerySpares => Set<MachinerySpare>();
    public DbSet<TankChecklistTemplate> TankChecklistTemplates => Set<TankChecklistTemplate>();
    public DbSet<TankCriterion> TankCriteria => Set<TankCriterion>();
    public DbSet<Tank> Tanks => Set<Tank>();
    public DbSet<TankInspection> TankInspections => Set<TankInspection>();
    public DbSet<TankInspectionResult> TankInspectionResults => Set<TankInspectionResult>();
    public DbSet<TankInspectionReview> TankInspectionReviews => Set<TankInspectionReview>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingAttendee> MeetingAttendees => Set<MeetingAttendee>();
    public DbSet<MeetingItem> MeetingItems => Set<MeetingItem>();
    public DbSet<MeetingNote> MeetingNotes => Set<MeetingNote>();
    public DbSet<MeetingComment> MeetingComments => Set<MeetingComment>();
    public DbSet<MeetingType> MeetingTypes => Set<MeetingType>();
    public DbSet<PermitType> PermitTypes => Set<PermitType>();
    public DbSet<PermitChecklistItem> PermitChecklistItems => Set<PermitChecklistItem>();
    public DbSet<PermitConflictPair> PermitConflictPairs => Set<PermitConflictPair>();
    public DbSet<PermitJobRule> PermitJobRules => Set<PermitJobRule>();
    public DbSet<Permit> Permits => Set<Permit>();
    public DbSet<PermitAnswer> PermitAnswers => Set<PermitAnswer>();
    public DbSet<PermitPerson> PermitPersons => Set<PermitPerson>();
    public DbSet<PermitGasReading> PermitGasReadings => Set<PermitGasReading>();
    public DbSet<PermitEntry> PermitEntries => Set<PermitEntry>();
    public DbSet<PermitEvent> PermitEvents => Set<PermitEvent>();
    public DbSet<PermitComment> PermitComments => Set<PermitComment>();
    public DbSet<Drill> Drills => Set<Drill>();
    public DbSet<DrillParticipant> DrillParticipants => Set<DrillParticipant>();
    public DbSet<DrillNote> DrillNotes => Set<DrillNote>();
    public DbSet<DrillComment> DrillComments => Set<DrillComment>();
    public DbSet<DrillType> DrillTypes => Set<DrillType>();
    public DbSet<DrillOverride> DrillOverrides => Set<DrillOverride>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentPerson> IncidentPersons => Set<IncidentPerson>();
    public DbSet<IncidentSupplement> IncidentSupplements => Set<IncidentSupplement>();
    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();
    public DbSet<IncidentInvestigation> IncidentInvestigations => Set<IncidentInvestigation>();
    public DbSet<IncidentExposure> IncidentExposures => Set<IncidentExposure>();
    public DbSet<IncidentCause> IncidentCauses => Set<IncidentCause>();
    public DbSet<IncidentSettings> IncidentSettingsRows => Set<IncidentSettings>();
    public DbSet<RestRuleSet> RestRuleSets => Set<RestRuleSet>();
    public DbSet<RestEntry> RestEntries => Set<RestEntry>();
    public DbSet<RestSignOff> RestSignOffs => Set<RestSignOff>();
    public DbSet<WorkArrangementSignOff> WorkArrangementSignOffs => Set<WorkArrangementSignOff>();
    public DbSet<RestBreachNote> RestBreachNotes => Set<RestBreachNote>();
    public DbSet<VesselDayStatus> VesselDayStatuses => Set<VesselDayStatus>();
    public DbSet<WorkArrangementTable> WorkArrangementTables => Set<WorkArrangementTable>();
    public DbSet<RaCategory> RaCategories => Set<RaCategory>();
    public DbSet<RaMatrixLabel> RaMatrixLabels => Set<RaMatrixLabel>();
    public DbSet<RaMatrixBand> RaMatrixBands => Set<RaMatrixBand>();
    public DbSet<RaTemplate> RaTemplates => Set<RaTemplate>();
    public DbSet<RaTemplateActivity> RaTemplateActivities => Set<RaTemplateActivity>();
    public DbSet<RaTemplateHazard> RaTemplateHazards => Set<RaTemplateHazard>();
    public DbSet<RiskAssessment> RiskAssessments => Set<RiskAssessment>();
    public DbSet<RaTeamMember> RaTeamMembers => Set<RaTeamMember>();
    public DbSet<RaActivity> RaActivities => Set<RaActivity>();
    public DbSet<RaHazard> RaHazards => Set<RaHazard>();
    public DbSet<RaSignOff> RaSignOffs => Set<RaSignOff>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectItem> ProjectItems => Set<ProjectItem>();
    public DbSet<ProjectStatusEvent> ProjectStatusEvents => Set<ProjectStatusEvent>();
    public DbSet<ProjectNote> ProjectNotes => Set<ProjectNote>();
    public DbSet<ProjectLink> ProjectLinks => Set<ProjectLink>();
    public DbSet<ProjectItemRequest> ProjectItemRequests => Set<ProjectItemRequest>();
    public DbSet<ProjectItemDecision> ProjectItemDecisions => Set<ProjectItemDecision>();
    public DbSet<ProjectBudgetLine> ProjectBudgetLines => Set<ProjectBudgetLine>();
    public DbSet<ProjectItemCost> ProjectItemCosts => Set<ProjectItemCost>();
    public DbSet<ProjectCostEntry> ProjectCostEntries => Set<ProjectCostEntry>();
    public DbSet<ProjectBudgetApproval> ProjectBudgetApprovals => Set<ProjectBudgetApproval>();
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
            b.Property(r => r.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
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
            b.Property(q => q.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
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
            b.Property(p => p.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
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

        modelBuilder.Entity<WorklistListItem>(b =>
        {
            b.Property(i => i.Name).HasMaxLength(120);
            b.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorklistListItem>().WithMany().HasForeignKey(i => i.ParentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(i => new { i.CompanyId, i.Kind });
            b.HasQueryFilter(i => CurrentCompanyId == null || i.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<WorklistTask>(b =>
        {
            b.Property(t => t.Code).HasMaxLength(40);
            b.Property(t => t.Title).HasMaxLength(200);
            b.Property(t => t.AssigneeName).HasMaxLength(120);
            b.Property(t => t.CreatedByName).HasMaxLength(120);
            b.Property(t => t.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.Ignore(t => t.AwaitingVerification);
            b.Ignore(t => t.IsClosed);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(t => t.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => new { t.CompanyId, t.Code }).IsUnique();
            b.HasIndex(t => t.VesselId);
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<WorklistEntry>(b =>
        {
            b.Property(e => e.AuthorName).HasMaxLength(120);
            b.Property(e => e.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorklistTask>().WithMany().HasForeignKey(e => e.TaskId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.TaskId, e.At });
            b.HasQueryFilter(e => CurrentCompanyId == null || e.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<MachineryClass>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(120);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<MachineryClass>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<JobTemplate>(b =>
        {
            b.Property(t => t.Code).HasMaxLength(40);
            b.Property(t => t.Title).HasMaxLength(200);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<MachineryClass>().WithMany().HasForeignKey(t => t.MachineryClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Department>().WithMany().HasForeignKey(t => t.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => new { t.CompanyId, t.Code }).IsUnique();
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<MachineryItem>(b =>
        {
            b.Property(m => m.Code).HasMaxLength(40);
            b.Property(m => m.Name).HasMaxLength(160);
            b.Property(m => m.Maker).HasMaxLength(120);
            b.Property(m => m.Model).HasMaxLength(120);
            b.Property(m => m.Serial).HasMaxLength(120);
            b.Property(m => m.CreatedByName).HasMaxLength(120);
            b.Property(m => m.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(m => m.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<MachineryItem>().WithMany().HasForeignKey(m => m.ParentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<MachineryClass>().WithMany().HasForeignKey(m => m.MachineryClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(m => new { m.VesselId, m.ParentId });
            b.HasQueryFilter(m => CurrentCompanyId == null || m.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<PlannedJob>(b =>
        {
            b.Property(j => j.Code).HasMaxLength(40);
            b.Property(j => j.Title).HasMaxLength(200);
            b.Property(j => j.DueBasis).HasMaxLength(10);
            b.Property(j => j.CreatedByName).HasMaxLength(120);
            b.Property(j => j.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.Property(j => j.BaselineDoneHours).HasPrecision(12, 1);
            b.Property(j => j.LastDoneHours).HasPrecision(12, 1);
            b.HasOne<Company>().WithMany().HasForeignKey(j => j.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(j => j.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<MachineryItem>().WithMany().HasForeignKey(j => j.MachineryItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JobTemplate>().WithMany().HasForeignKey(j => j.TemplateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Department>().WithMany().HasForeignKey(j => j.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(j => new { j.VesselId, j.NextDueDate });
            b.HasIndex(j => j.MachineryItemId);
            b.HasQueryFilter(j => CurrentCompanyId == null || j.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RunningHoursReading>(b =>
        {
            b.Property(r => r.Hours).HasPrecision(12, 1);
            b.Property(r => r.Source).HasMaxLength(10);
            b.Property(r => r.Note).HasMaxLength(400);
            b.Property(r => r.AuthorName).HasMaxLength(120);
            b.Property(r => r.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<MachineryItem>().WithMany().HasForeignKey(r => r.MachineryItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(r => new { r.MachineryItemId, r.ReadingDate });
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<JobCompletion>(b =>
        {
            b.Property(c => c.HoursAtDone).HasPrecision(12, 1);
            b.Property(c => c.PerformedBy).HasMaxLength(120);
            b.Property(c => c.AuthorName).HasMaxLength(120);
            b.Property(c => c.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(c => c.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PlannedJob>().WithMany().HasForeignKey(c => c.PlannedJobId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.PlannedJobId, c.DoneDate });
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<JobVerification>(b =>
        {
            b.Property(v => v.VerifiedBy).HasMaxLength(120);
            b.Property(v => v.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(v => v.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(v => v.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JobCompletion>().WithMany().HasForeignKey(v => v.CompletionId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(v => v.CompletionId);
            b.HasQueryFilter(v => CurrentCompanyId == null || v.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<JobDeferralRequest>(b =>
        {
            b.Property(r => r.Reason).HasMaxLength(1000);
            b.Property(r => r.RequestedByName).HasMaxLength(120);
            b.Property(r => r.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PlannedJob>().WithMany().HasForeignKey(r => r.PlannedJobId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(r => r.PlannedJobId);
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<JobDeferralDecision>(b =>
        {
            b.Property(d => d.Comment).HasMaxLength(1000);
            b.Property(d => d.DecidedByName).HasMaxLength(120);
            b.Property(d => d.DecidedByRole).HasMaxLength(10);
            b.Property(d => d.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(d => d.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JobDeferralRequest>().WithMany().HasForeignKey(d => d.RequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(d => d.RequestId);
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<PlanChangeRequest>(b =>
        {
            b.Property(r => r.Reason).HasMaxLength(1000);
            b.Property(r => r.RequestedByName).HasMaxLength(120);
            b.Property(r => r.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PlannedJob>().WithMany().HasForeignKey(r => r.PlannedJobId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<MachineryItem>().WithMany().HasForeignKey(r => r.MachineryItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<PlanChangeDecision>(b =>
        {
            b.Property(d => d.Comment).HasMaxLength(1000);
            b.Property(d => d.DecidedByName).HasMaxLength(120);
            b.Property(d => d.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(d => d.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PlanChangeRequest>().WithMany().HasForeignKey(d => d.RequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(d => d.RequestId);
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<MachinerySpare>(b =>
        {
            b.Property(m => m.Quantity).HasPrecision(12, 2);
            b.Property(m => m.Note).HasMaxLength(300);
            b.Property(m => m.CreatedByName).HasMaxLength(120);
            b.Property(m => m.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(m => m.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<MachineryItem>().WithMany().HasForeignKey(m => m.MachineryItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Item>().WithMany().HasForeignKey(m => m.ItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(m => new { m.MachineryItemId, m.ItemId }).IsUnique();
            b.HasQueryFilter(m => CurrentCompanyId == null || m.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<TankChecklistTemplate>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(160);
            b.Property(t => t.ShipType).HasMaxLength(80);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<TankCriterion>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(200);
            b.Property(c => c.ExceptionValues).HasMaxLength(100);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<TankChecklistTemplate>().WithMany().HasForeignKey(c => c.TemplateId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => c.TemplateId);
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Tank>(b =>
        {
            b.Property(t => t.Code).HasMaxLength(40);
            b.Property(t => t.Name).HasMaxLength(160);
            b.Property(t => t.SteelGradeDeck).HasMaxLength(60);
            b.Property(t => t.SteelGradeBottom).HasMaxLength(60);
            b.Property(t => t.SteelGradeSide).HasMaxLength(60);
            b.Property(t => t.SteelGradeBulkhead).HasMaxLength(60);
            b.Property(t => t.OriginalCoating).HasMaxLength(200);
            b.Property(t => t.CathodicProtection).HasMaxLength(200);
            b.Property(t => t.CoatedAreaM2).HasPrecision(12, 1);
            b.Property(t => t.ProtectedAreaM2).HasPrecision(12, 1);
            b.Property(t => t.CreatedByName).HasMaxLength(120);
            b.Property(t => t.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(t => t.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => new { t.VesselId, t.Code }).IsUnique();
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<TankInspection>(b =>
        {
            b.Property(i => i.Code).HasMaxLength(40);
            b.Property(i => i.TemplateName).HasMaxLength(160);
            b.Property(i => i.Place).HasMaxLength(120);
            b.Property(i => i.WorkDone).HasMaxLength(200);
            b.Property(i => i.InspectorName).HasMaxLength(120);
            b.Property(i => i.CreatedByName).HasMaxLength(120);
            b.Property(i => i.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(i => i.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Tank>().WithMany().HasForeignKey(i => i.TankId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(i => i.Results).WithOne().HasForeignKey(r => r.InspectionId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(i => new { i.CompanyId, i.Code }).IsUnique();
            b.HasIndex(i => new { i.TankId, i.InspectedOn });
            b.HasQueryFilter(i => CurrentCompanyId == null || i.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<TankInspectionResult>(b =>
        {
            b.Property(r => r.CriterionName).HasMaxLength(200);
            b.Property(r => r.ExceptionValues).HasMaxLength(100);
            b.Property(r => r.Value).HasMaxLength(20);
            b.Property(r => r.Comment).HasMaxLength(1000);
            b.HasIndex(r => r.InspectionId);
        });

        modelBuilder.Entity<TankInspectionReview>(b =>
        {
            b.Property(r => r.Comment).HasMaxLength(1000);
            b.Property(r => r.ReviewerName).HasMaxLength(120);
            b.Property(r => r.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TankInspection>().WithMany().HasForeignKey(r => r.InspectionId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(r => r.InspectionId);
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        // ---- safety meetings (spec 13, C3): the meeting is one document with its attendees, items and notes ----
        modelBuilder.Entity<MeetingType>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(120);
            b.Property(t => t.StandardAgenda).HasMaxLength(2000);
            b.Property(t => t.Reference).HasMaxLength(300);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CompanyId, t.Name }).IsUnique();
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<Meeting>(b =>
        {
            b.Property(m => m.Code).HasMaxLength(40);
            b.Property(m => m.TypeName).HasMaxLength(120);
            b.Property(m => m.Place).HasMaxLength(200);
            b.Property(m => m.ChairName).HasMaxLength(120);
            b.Property(m => m.SecretaryName).HasMaxLength(120);
            b.Property(m => m.Summary).HasMaxLength(1000);
            b.Property(m => m.SignedByName).HasMaxLength(120);
            b.Property(m => m.CreatedByName).HasMaxLength(120);
            b.Property(m => m.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.Ignore(m => m.IsSigned);
            b.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(m => m.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(m => m.Attendees).WithOne().HasForeignKey(a => a.MeetingId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(m => m.Items).WithOne().HasForeignKey(i => i.MeetingId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(m => m.Notes).WithOne().HasForeignKey(n => n.MeetingId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(m => new { m.VesselId, m.MeetingTypeId, m.HeldAt });
            b.HasIndex(m => new { m.CompanyId, m.Code });
            b.HasQueryFilter(m => CurrentCompanyId == null || m.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<MeetingAttendee>(b =>
        {
            b.Property(a => a.Name).HasMaxLength(120);
            b.Property(a => a.Rank).HasMaxLength(80);
            b.HasIndex(a => a.MeetingId);
        });
        modelBuilder.Entity<MeetingItem>(b =>
        {
            b.Property(i => i.Title).HasMaxLength(300);
            b.Property(i => i.Notes).HasMaxLength(4000);
            b.Property(i => i.RaisedBy).HasMaxLength(120);
            b.HasIndex(i => i.MeetingId);
        });
        modelBuilder.Entity<MeetingNote>(b =>
        {
            b.Property(n => n.Text).HasMaxLength(2000);
            b.Property(n => n.ByName).HasMaxLength(120);
            b.HasIndex(n => n.MeetingId);
        });
        modelBuilder.Entity<MeetingComment>(b =>
        {
            b.Property(c => c.Text).HasMaxLength(2000);
            b.Property(c => c.ByName).HasMaxLength(120);
            b.Property(c => c.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(c => c.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Meeting>().WithMany().HasForeignKey(c => c.MeetingId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.MeetingId, c.At });
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        // ---- permit to work (spec 14): a permit is one document with its answers, persons, readings, entries and events ----
        modelBuilder.Entity<PermitType>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(120);
            b.Property(t => t.Code).HasMaxLength(12);
            b.Property(t => t.Reference).HasMaxLength(300);
            b.Property(t => t.OxygenMin).HasPrecision(5, 2);
            b.Property(t => t.OxygenMax).HasPrecision(5, 2);
            b.Property(t => t.FlammableMaxPercentLel).HasPrecision(6, 2);
            b.Property(t => t.H2sMaxPpm).HasPrecision(8, 2);
            b.Property(t => t.CoMaxPpm).HasPrecision(8, 2);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CompanyId, t.Name }).IsUnique();
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<PermitChecklistItem>(b =>
        {
            b.Property(i => i.Section).HasMaxLength(120);
            b.Property(i => i.Text).HasMaxLength(500);
            b.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<PermitType>().WithMany().HasForeignKey(i => i.PermitTypeId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(i => new { i.PermitTypeId, i.Version, i.Position });
            b.HasQueryFilter(i => CurrentCompanyId == null || i.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<PermitConflictPair>(b =>
        {
            b.Property(p => p.Message).HasMaxLength(300);
            b.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<PermitType>().WithMany().HasForeignKey(p => p.PermitTypeAId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<PermitType>().WithMany().HasForeignKey(p => p.PermitTypeBId).OnDelete(DeleteBehavior.Restrict);
            b.HasQueryFilter(p => CurrentCompanyId == null || p.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<PermitJobRule>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<PermitType>().WithMany().HasForeignKey(r => r.PermitTypeId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(r => new { r.CompanyId, r.JobTemplateId }).IsUnique();
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<Permit>(b =>
        {
            b.Property(p => p.Code).HasMaxLength(40);
            b.Property(p => p.TypeName).HasMaxLength(120);
            b.Property(p => p.Terms).HasMaxLength(1000);
            b.Property(p => p.Location).HasMaxLength(200);
            b.Property(p => p.Description).HasMaxLength(2000);
            b.Property(p => p.Isolation).HasMaxLength(1000);
            b.Property(p => p.Conditions).HasMaxLength(2000);
            b.Property(p => p.RequesterName).HasMaxLength(120);
            b.Property(p => p.IssuerName).HasMaxLength(120);
            b.Property(p => p.AttendantName).HasMaxLength(120);
            b.Property(p => p.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(p => p.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(p => p.Answers).WithOne().HasForeignKey(a => a.PermitId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Persons).WithOne().HasForeignKey(a => a.PermitId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Readings).WithOne().HasForeignKey(a => a.PermitId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Entries).WithOne().HasForeignKey(a => a.PermitId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Events).WithOne().HasForeignKey(a => a.PermitId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(p => new { p.VesselId, p.ValidFrom });
            b.HasIndex(p => new { p.CompanyId, p.Code });
            b.HasQueryFilter(p => CurrentCompanyId == null || p.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<PermitAnswer>(b =>
        {
            b.Property(a => a.Section).HasMaxLength(120);
            b.Property(a => a.Text).HasMaxLength(500);
            b.Property(a => a.Note).HasMaxLength(1000);
            b.Property(a => a.ByName).HasMaxLength(120);
            b.HasIndex(a => a.PermitId);
        });
        modelBuilder.Entity<PermitPerson>(b =>
        {
            b.Property(a => a.Name).HasMaxLength(120);
            b.HasIndex(a => a.PermitId);
        });
        modelBuilder.Entity<PermitGasReading>(b =>
        {
            b.Property(a => a.Instrument).HasMaxLength(120);
            b.Property(a => a.Location).HasMaxLength(200);
            b.Property(a => a.Failures).HasMaxLength(1000);
            b.Property(a => a.ByName).HasMaxLength(120);
            b.Property(a => a.Oxygen).HasPrecision(5, 2);
            b.Property(a => a.FlammablePercentLel).HasPrecision(6, 2);
            b.Property(a => a.H2sPpm).HasPrecision(8, 2);
            b.Property(a => a.CoPpm).HasPrecision(8, 2);
            b.HasIndex(a => a.PermitId);
        });
        modelBuilder.Entity<PermitEntry>(b =>
        {
            b.Property(a => a.PersonName).HasMaxLength(120);
            b.Property(a => a.AttendantName).HasMaxLength(120);
            b.HasIndex(a => a.PermitId);
        });
        modelBuilder.Entity<PermitEvent>(b =>
        {
            b.Property(a => a.ByName).HasMaxLength(120);
            b.Property(a => a.Note).HasMaxLength(1000);
            b.HasIndex(a => new { a.PermitId, a.At });
        });
        modelBuilder.Entity<PermitComment>(b =>
        {
            b.Property(c => c.Text).HasMaxLength(2000);
            b.Property(c => c.ByName).HasMaxLength(120);
            b.Property(c => c.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(c => c.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Permit>().WithMany().HasForeignKey(c => c.PermitId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.PermitId, c.At });
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        // ---- drills (spec 13): a drill is one document with its participants and notes; the schedule is worked out from the drills held ----
        modelBuilder.Entity<DrillType>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(120);
            b.Property(t => t.Scenario).HasMaxLength(2000);
            b.Property(t => t.Checklist).HasMaxLength(2000);
            b.Property(t => t.Reference).HasMaxLength(300);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CompanyId, t.Name }).IsUnique();
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<DrillOverride>(b =>
        {
            b.Property(o => o.Reason).HasMaxLength(500);
            b.HasOne<Company>().WithMany().HasForeignKey(o => o.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(o => o.VesselId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<DrillType>().WithMany().HasForeignKey(o => o.DrillTypeId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(o => new { o.VesselId, o.DrillTypeId }).IsUnique();
            b.HasQueryFilter(o => CurrentCompanyId == null || o.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<Drill>(b =>
        {
            b.Property(d => d.Code).HasMaxLength(40);
            b.Property(d => d.TypeName).HasMaxLength(120);
            b.Property(d => d.Place).HasMaxLength(200);
            b.Property(d => d.Scenario).HasMaxLength(2000);
            b.Property(d => d.ConductedByName).HasMaxLength(120);
            b.Property(d => d.EquipmentTested).HasMaxLength(1000);
            b.Property(d => d.Findings).HasMaxLength(2000);
            b.Property(d => d.Deficiencies).HasMaxLength(2000);
            b.Property(d => d.Remarks).HasMaxLength(2000);
            b.Property(d => d.SignedByName).HasMaxLength(120);
            b.Property(d => d.CreatedByName).HasMaxLength(120);
            b.Property(d => d.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.Ignore(d => d.IsSigned);
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(d => d.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(d => d.Participants).WithOne().HasForeignKey(p => p.DrillId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(d => d.Notes).WithOne().HasForeignKey(n => n.DrillId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(d => new { d.VesselId, d.DrillTypeId, d.StartedAt });
            b.HasIndex(d => new { d.CompanyId, d.Code });
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<DrillParticipant>(b =>
        {
            b.Property(p => p.Name).HasMaxLength(120);
            b.Property(p => p.Rank).HasMaxLength(80);
            b.HasIndex(p => p.DrillId);
            b.HasIndex(p => p.CrewMemberId);
        });
        modelBuilder.Entity<DrillNote>(b =>
        {
            b.Property(n => n.Text).HasMaxLength(2000);
            b.Property(n => n.ByName).HasMaxLength(120);
            b.HasIndex(n => n.DrillId);
        });
        modelBuilder.Entity<DrillComment>(b =>
        {
            b.Property(c => c.Text).HasMaxLength(2000);
            b.Property(c => c.ByName).HasMaxLength(120);
            b.Property(c => c.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(c => c.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Drill>().WithMany().HasForeignKey(c => c.DrillId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.DrillId, c.At });
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });

        // ---- incidents (spec 12): the report is one document with its persons and supplements; what happens to it afterwards is appended ----
        modelBuilder.Entity<Incident>(b =>
        {
            b.Property(i => i.Code).HasMaxLength(40);
            b.Property(i => i.Place).HasMaxLength(200);
            b.Property(i => i.Weather).HasMaxLength(200);
            b.Property(i => i.Description).HasMaxLength(4000);
            b.Property(i => i.ImmediateAction).HasMaxLength(2000);
            b.Property(i => i.EquipmentName).HasMaxLength(200);
            b.Property(i => i.RiskAssessmentRef).HasMaxLength(120);
            b.Property(i => i.ReporterName).HasMaxLength(120);
            b.Property(i => i.Band).HasMaxLength(40);
            b.Property(i => i.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(i => i.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(i => i.Persons).WithOne().HasForeignKey(p => p.IncidentId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(i => i.Supplements).WithOne().HasForeignKey(s => s.IncidentId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(i => new { i.VesselId, i.OccurredAt });
            b.HasIndex(i => new { i.CompanyId, i.Code });
            b.HasQueryFilter(i => CurrentCompanyId == null || i.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<IncidentPerson>(b =>
        {
            b.Property(p => p.Name).HasMaxLength(120);
            b.Property(p => p.Rank).HasMaxLength(80);
            b.Property(p => p.Company).HasMaxLength(120);
            b.Property(p => p.InjuryType).HasMaxLength(120);
            b.Property(p => p.BodyPart).HasMaxLength(120);
            b.HasIndex(p => p.IncidentId);
        });
        modelBuilder.Entity<IncidentSupplement>(b =>
        {
            b.Property(s => s.Text).HasMaxLength(2000);
            b.Property(s => s.ByName).HasMaxLength(120);
            b.HasIndex(s => s.IncidentId);
        });
        modelBuilder.Entity<IncidentEvent>(b =>
        {
            b.Property(e => e.Text).HasMaxLength(2000);
            b.Property(e => e.ByName).HasMaxLength(120);
            b.Property(e => e.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(e => e.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Incident>().WithMany().HasForeignKey(e => e.IncidentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.IncidentId, e.At });
            b.HasQueryFilter(e => CurrentCompanyId == null || e.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<IncidentInvestigation>(b =>
        {
            b.Property(v => v.Facts).HasMaxLength(4000);
            b.Property(v => v.ImmediateCause).HasMaxLength(2000);
            b.Property(v => v.RootCauses).HasMaxLength(1000);
            b.Property(v => v.WhyChain).HasMaxLength(2000);
            b.Property(v => v.ContributingFactors).HasMaxLength(2000);
            b.Property(v => v.Lessons).HasMaxLength(2000);
            b.Property(v => v.InvestigatedBy).HasMaxLength(200);
            b.Property(v => v.ByName).HasMaxLength(120);
            b.Property(v => v.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(v => v.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(v => v.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Incident>().WithMany().HasForeignKey(v => v.IncidentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(v => new { v.IncidentId, v.Version }).IsUnique();
            b.HasQueryFilter(v => CurrentCompanyId == null || v.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<IncidentExposure>(b =>
        {
            b.Property(x => x.Hours).HasPrecision(14, 2);
            b.Property(x => x.Note).HasMaxLength(300);
            b.Property(x => x.ByName).HasMaxLength(120);
            b.Property(x => x.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(x => x.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.VesselId, x.Month, x.At });
            b.HasQueryFilter(x => CurrentCompanyId == null || x.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<IncidentCause>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(100);
            b.Property(c => c.Group).HasMaxLength(60);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<IncidentSettings>(b =>
        {
            b.Property(s => s.InvestigationDays).HasMaxLength(60);
            b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(s => s.CompanyId).IsUnique();
            b.HasQueryFilter(s => CurrentCompanyId == null || s.CompanyId == CurrentCompanyId);
        });

        // ---- rest hours (spec 11). Crew references are plain ids with the person's name kept beside them: a seafarer who signs off is removed from
        // the vessel's copy of the crew, but the records of their hours stay. ----
        modelBuilder.Entity<RestRuleSet>(b =>
        {
            b.Property(r => r.Name).HasMaxLength(160);
            b.Property(r => r.FlagState).HasMaxLength(80);
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(r => new { r.CompanyId, r.VesselId });
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RestEntry>(b =>
        {
            b.Property(e => e.CrewName).HasMaxLength(120);
            b.Property(e => e.Rank).HasMaxLength(60);
            b.Property(e => e.WorkSpans).HasMaxLength(400);
            b.Property(e => e.Reason).HasMaxLength(500);
            b.Property(e => e.EnteredByName).HasMaxLength(120);
            b.Property(e => e.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(e => e.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => new { e.CrewMemberId, e.Date, e.EnteredAt });
            b.HasIndex(e => new { e.VesselId, e.Date });
            b.HasQueryFilter(e => CurrentCompanyId == null || e.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RestSignOff>(b =>
        {
            b.Property(s => s.CrewName).HasMaxLength(120);
            b.Property(s => s.Fingerprint).HasMaxLength(64);
            b.Property(s => s.SignedByName).HasMaxLength(120);
            b.Property(s => s.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(s => s.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => new { s.CrewMemberId, s.FromDate });
            b.HasQueryFilter(s => CurrentCompanyId == null || s.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RestBreachNote>(b =>
        {
            b.Property(n => n.CrewName).HasMaxLength(120);
            b.Property(n => n.Rule).HasMaxLength(30);
            b.Property(n => n.Text).HasMaxLength(1000);
            b.Property(n => n.ByName).HasMaxLength(120);
            b.Property(n => n.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(n => n.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(n => n.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(n => new { n.CrewMemberId, n.Date });
            b.HasQueryFilter(n => CurrentCompanyId == null || n.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<VesselDayStatus>(b =>
        {
            b.Property(s => s.PortName).HasMaxLength(120);
            b.Property(s => s.SetByName).HasMaxLength(120);
            b.Property(s => s.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(s => s.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => new { s.VesselId, s.Date, s.At });
            b.HasQueryFilter(s => CurrentCompanyId == null || s.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<WorkArrangementTable>(b =>
        {
            b.Property(t => t.LawReference).HasMaxLength(300);
            b.Property(t => t.LimitsText).HasMaxLength(300);
            b.Property(t => t.OtherRequirements).HasMaxLength(1000);
            b.Property(t => t.CreatedByName).HasMaxLength(120);
            b.Property(t => t.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(t => t.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => new { t.VesselId, t.VersionNo }).IsUnique();
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<WorkArrangementSignOff>(b =>
        {
            b.Property(s => s.SignedByName).HasMaxLength(120);
            b.Property(s => s.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(s => s.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => new { s.VesselId, s.VersionNo });
            b.HasQueryFilter(s => CurrentCompanyId == null || s.CompanyId == CurrentCompanyId);
        });

        // ---- risk assessment: the library is the office's master data; an assessment is one document with its team, activities and hazards ----
        modelBuilder.Entity<RaCategory>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(100);
            b.Property(c => c.Code).HasMaxLength(10);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<RaMatrixLabel>(b =>
        {
            b.Property(l => l.Label).HasMaxLength(60);
            b.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(l => CurrentCompanyId == null || l.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<RaMatrixBand>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(40);
            b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => CurrentCompanyId == null || x.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<RaTemplate>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(200);
            b.Property(t => t.JobDescription).HasMaxLength(2000);
            b.Property(t => t.Contingency).HasMaxLength(2000);
            b.HasOne<Company>().WithMany().HasForeignKey(t => t.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(t => CurrentCompanyId == null || t.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<RaTemplateActivity>(b =>
        {
            b.Property(a => a.Title).HasMaxLength(300);
            b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<RaTemplate>().WithMany().HasForeignKey(a => a.TemplateId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => a.TemplateId);
            b.HasQueryFilter(a => CurrentCompanyId == null || a.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<RaTemplateHazard>(b =>
        {
            b.Property(h => h.Hazard).HasMaxLength(500);
            b.Property(h => h.Effect).HasMaxLength(1000);
            b.Property(h => h.ExistingControls).HasMaxLength(2000);
            b.Property(h => h.AdditionalControls).HasMaxLength(2000);
            b.Property(h => h.Contingency).HasMaxLength(2000);
            b.HasOne<Company>().WithMany().HasForeignKey(h => h.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<RaTemplate>().WithMany().HasForeignKey(h => h.TemplateId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(h => h.TemplateId);
            b.HasQueryFilter(h => CurrentCompanyId == null || h.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<RiskAssessment>(b =>
        {
            b.Property(a => a.Code).HasMaxLength(40);
            b.Property(a => a.CategoryName).HasMaxLength(100);
            b.Property(a => a.CategoryCode).HasMaxLength(10);
            b.Property(a => a.JobDescription).HasMaxLength(2000);
            b.Property(a => a.Contingency).HasMaxLength(2000);
            b.Property(a => a.HighestResidualBand).HasMaxLength(40);
            b.Property(a => a.CreatedByName).HasMaxLength(120);
            b.Property(a => a.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(a => a.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(a => a.Team).WithOne().HasForeignKey(t => t.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(a => a.Activities).WithOne().HasForeignKey(t => t.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(a => a.Hazards).WithOne().HasForeignKey(t => t.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(a => new { a.CompanyId, a.Code, a.Version });   // not unique: the office and a vessel may each start the next version while apart, and both are kept
            b.HasIndex(a => new { a.VesselId, a.CreatedAt });
            b.HasQueryFilter(a => CurrentCompanyId == null || a.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<RaTeamMember>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(120);
            b.Property(t => t.Rank).HasMaxLength(80);
            b.HasIndex(t => t.AssessmentId);
        });
        modelBuilder.Entity<RaActivity>(b =>
        {
            b.Property(t => t.Title).HasMaxLength(300);
            b.HasIndex(t => t.AssessmentId);
        });
        modelBuilder.Entity<RaHazard>(b =>
        {
            b.Property(h => h.Hazard).HasMaxLength(500);
            b.Property(h => h.Effect).HasMaxLength(1000);
            b.Property(h => h.ExistingControls).HasMaxLength(2000);
            b.Property(h => h.AdditionalControls).HasMaxLength(2000);
            b.Property(h => h.ResidualReason).HasMaxLength(500);
            b.Property(h => h.ResponsibleName).HasMaxLength(120);
            b.Property(h => h.Contingency).HasMaxLength(2000);
            b.Property(h => h.BandBefore).HasMaxLength(40);
            b.Property(h => h.BandAfter).HasMaxLength(40);
            b.HasIndex(h => h.AssessmentId);
        });
        modelBuilder.Entity<RaSignOff>(b =>
        {
            b.Property(s => s.Comment).HasMaxLength(1000);
            b.Property(s => s.ReviewerName).HasMaxLength(120);
            b.Property(s => s.ReviewerRank).HasMaxLength(80);
            b.Property(s => s.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(s => s.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RiskAssessment>().WithMany().HasForeignKey(s => s.AssessmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(s => s.AssessmentId);
            b.HasQueryFilter(s => CurrentCompanyId == null || s.CompanyId == CurrentCompanyId);
        });

        // ---- projects: the project, its items and the history around it sync; budget and money stay at the office ----
        modelBuilder.Entity<Project>(b =>
        {
            b.Property(p => p.Code).HasMaxLength(40);
            b.Property(p => p.Title).HasMaxLength(200);
            b.Property(p => p.Description).HasMaxLength(4000);
            b.Property(p => p.SuperintendentName).HasMaxLength(120);
            b.Property(p => p.Yard).HasMaxLength(200);
            b.Property(p => p.CreatedByName).HasMaxLength(120);
            b.Property(p => p.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(p => p.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(p => p.Items).WithOne().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(p => new { p.CompanyId, p.Code }).IsUnique();
            b.HasIndex(p => new { p.VesselId, p.CreatedAt });
            b.HasQueryFilter(p => CurrentCompanyId == null || p.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectItem>(b =>
        {
            b.Property(i => i.Group).HasMaxLength(100);
            b.Property(i => i.Description).HasMaxLength(1000);
            b.Property(i => i.SourceRef).HasMaxLength(60);
            b.HasIndex(i => i.ProjectId);
        });
        modelBuilder.Entity<ProjectStatusEvent>(b =>
        {
            b.Property(e => e.Note).HasMaxLength(2000);
            b.Property(e => e.ByName).HasMaxLength(120);
            b.Property(e => e.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(e => e.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => e.ProjectId);
            b.HasQueryFilter(e => CurrentCompanyId == null || e.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectNote>(b =>
        {
            b.Property(n => n.Text).HasMaxLength(4000);
            b.Property(n => n.AuthorName).HasMaxLength(120);
            b.Property(n => n.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(n => n.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(n => n.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(n => new { n.ProjectId, n.Date });
            b.HasQueryFilter(n => CurrentCompanyId == null || n.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectLink>(b =>
        {
            b.Property(l => l.ByName).HasMaxLength(120);
            b.Property(l => l.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(l => l.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(l => new { l.Kind, l.DocId });
            b.HasIndex(l => l.ProjectId);
            b.HasQueryFilter(l => CurrentCompanyId == null || l.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectItemRequest>(b =>
        {
            b.Property(r => r.Description).HasMaxLength(1000);
            b.Property(r => r.Reason).HasMaxLength(2000);
            b.Property(r => r.RequestedByName).HasMaxLength(120);
            b.Property(r => r.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Vessel);
            b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(r => r.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(r => r.ProjectId);
            b.HasQueryFilter(r => CurrentCompanyId == null || r.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectItemDecision>(b =>
        {
            b.Property(d => d.Note).HasMaxLength(2000);
            b.Property(d => d.DecidedByName).HasMaxLength(120);
            b.Property(d => d.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Vessel>().WithMany().HasForeignKey(d => d.VesselId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ProjectItemRequest>().WithMany().HasForeignKey(d => d.RequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(d => d.RequestId).IsUnique();
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectBudgetLine>(b =>
        {
            b.Property(l => l.Category).HasMaxLength(100);
            b.Property(l => l.PlannedUsd).HasPrecision(18, 2);
            b.Property(l => l.ContingencyPct).HasPrecision(5, 2);
            b.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(l => l.ProjectId);
            b.HasQueryFilter(l => CurrentCompanyId == null || l.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectItemCost>(b =>
        {
            b.Property(c => c.EstimatedUsd).HasPrecision(18, 2);
            b.Property(c => c.QuotedUsd).HasPrecision(18, 2);
            b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(c => c.ItemId).IsUnique();
            b.HasQueryFilter(c => CurrentCompanyId == null || c.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectCostEntry>(b =>
        {
            b.Property(e => e.Description).HasMaxLength(500);
            b.Property(e => e.Reference).HasMaxLength(100);
            b.Property(e => e.AmountUsd).HasPrecision(18, 2);
            b.Property(e => e.ByName).HasMaxLength(120);
            b.HasOne<Company>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(e => e.ProjectId);
            b.HasQueryFilter(e => CurrentCompanyId == null || e.CompanyId == CurrentCompanyId);
        });
        modelBuilder.Entity<ProjectBudgetApproval>(b =>
        {
            b.Property(a => a.TotalUsd).HasPrecision(18, 2);
            b.Property(a => a.ContingencyUsd).HasPrecision(18, 2);
            b.Property(a => a.ApprovedByName).HasMaxLength(120);
            b.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(a => a.ProjectId);
            b.HasQueryFilter(a => CurrentCompanyId == null || a.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<PoDispatch>(b =>
        {
            b.Property(d => d.AcknowledgedByName).HasMaxLength(120);
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(d => d.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(d => d.PurchaseOrderId).IsUnique();
            b.HasQueryFilter(d => CurrentCompanyId == null || d.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<DeliveryOrder>(b =>
        {
            b.HasOne<Company>().WithMany().HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(d => d.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Supplier>().WithMany().HasForeignKey(d => d.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(d => d.InvoiceId).OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(d => new { d.CompanyId, d.DoNumber }).IsUnique();
            b.Property(d => d.OwnerStage).HasMaxLength(10).HasDefaultValue(SyncStages.Office);
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
                // History is never rewritten: an entry cannot be changed or removed by anyone, and a task changes only in the columns that are
                // worked out from its entries.
                if (Aggregates.IsAppendOnly(entry.Metadata.ClrType) && entry.State is EntityState.Modified or EntityState.Deleted
                    && !(entry.State == EntityState.Modified && Aggregates.OnlyLocalChanged(entry)))
                    throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} records are history and cannot be changed or deleted.");

                // A vessel keeps requisitions, crew logins and its own bookkeeping; RFQs, orders, invoices and the like are the office's.
                var localOnlyOrder = entry.Entity is PurchaseOrder && Aggregates.OnlyLocalChanged(entry);   // e.g. the delivery status, worked out onboard
                if (Node.SyncEnabled && Node.IsVessel && MasterData.Find(entry.Metadata.ClrType) is null && !VesselRules.MayWrite(entry.Metadata.ClrType) && !localOnlyOrder)
                    throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} records are handled by the office; this is not available onboard.");

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

                // Purchase orders and deliveries have one owner at a time: the side that created them, until they are handed over.
                var mine = Node.IsVessel ? SyncStages.Vessel : SyncStages.Office;
                if (Node.SyncEnabled && entry.State == EntityState.Deleted && Aggregates.Find(entry.Metadata.ClrType) is { } removed)
                {
                    if (Aggregates.StageOf(entry.Entity) != mine)
                        throw new InvalidOperationException($"{removed.DocType} {DocLabel(entry.Entity)} belongs to the {(mine == SyncStages.Vessel ? "office" : "vessel")}; it cannot be removed from here.");
                    SyncDirties.Add(Aggregates.NoteFor(entry, removed, now));
                    continue;
                }

                var root = entry.Metadata.IsOwned() ? OwnerOf(entry) : entry.Entity is RequisitionLine ? RequisitionOf(entry) : entry.Entity is RfqLine ? RfqOf(entry) : entry.Entity is TankInspectionResult ? InspectionOf(entry) : entry.Entity is RaTeamMember or RaActivity or RaHazard ? AssessmentOf(entry) : entry.Entity is IncidentPerson or IncidentSupplement ? IncidentOf(entry) : entry.Entity is DrillParticipant or DrillNote ? DrillOf(entry) : entry.Entity is MeetingAttendee or MeetingItem or MeetingNote ? MeetingOf(entry) : entry.Entity is PermitAnswer or PermitPerson or PermitGasReading or PermitEntry or PermitEvent ? PermitOf(entry) : entry.Entity is ProjectItem ? ProjectOf(entry) : entry;
                if (root is null || root.State == EntityState.Deleted || root.Metadata.FindProperty(SyncUpdatedAt) is null) continue;

                if (Node.SyncEnabled && Aggregates.Find(root.Entity.GetType()) is { } owned)
                {
                    if (root.State == EntityState.Added) Aggregates.SetStage(root.Entity, mine);
                    else if (ReferenceEquals(entry.Entity, root.Entity) && Aggregates.OnlyLocalChanged(entry)) continue;
                    else
                    {
                        // The owner may change it (or hand it over); the other side may not, except through an explicit take-over.
                        var was = (root.Metadata.FindProperty("OwnerStage") is null ? null : root.Property("OwnerStage").OriginalValue as string) ?? Aggregates.StageOf(root.Entity);
                        var takenOver = AllowTakeOver && was != mine && Aggregates.StageOf(root.Entity) == mine;
                        if (was != mine && !takenOver)
                            throw new InvalidOperationException($"{owned.DocType} {DocLabel(root.Entity)} belongs to the {(mine == SyncStages.Vessel ? "office" : "vessel")}; it cannot be changed from here.");
                    }
                }
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
                else if (Node.SyncEnabled && Aggregates.Find(root.Entity.GetType()) is { } aggregate)
                    SyncDirties.Add(Aggregates.NoteFor(root, aggregate, now));
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

    private static string DocLabel(object root) => root switch
    {
        PurchaseOrder p => p.PoNumber,
        DeliveryOrder d => d.DoNumber,
        Rfq r => r.RfqNumber,
        Quote q => q.SupplierRef,
        WorklistTask t => t.Code,
        MachineryItem m => m.Name,
        PlannedJob j => j.Code,
        MachinerySpare sp => sp.Id.ToString(),
        Tank tk => tk.Name,
        TankInspection ti => ti.Code,
        RiskAssessment ra => ra.Code,
        Project pr => pr.Code,
        _ => "",
    };

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

    /// <summary>A team member, activity or hazard is its own table but part of the assessment: changing one moves the assessment's version.</summary>
    private EntityEntry? AssessmentOf(EntityEntry child)
    {
        var property = child.Property("AssessmentId");
        var assessmentId = (Guid)(child.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<RiskAssessment>().FirstOrDefault(e => e.Entity.Id == assessmentId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("A part of a risk assessment was changed without loading the assessment; load it with its team, activities and hazards.");
        return parent;
    }

    /// <summary>An answer, a person, a reading, an entry or an event of a permit is its own table but part of the permit: changing one moves the permit's version, so the change travels.</summary>
    private EntityEntry? PermitOf(EntityEntry child)
    {
        var property = child.Property("PermitId");
        var permitId = (Guid)(child.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<Permit>().FirstOrDefault(e => e.Entity.Id == permitId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("A part of a permit was changed without loading the permit; load it with its answers, persons, readings, entries and events.");
        return parent;
    }

    /// <summary>An attendee, an item or a note of a meeting is its own table but part of the meeting: changing one moves the meeting's version, so the change travels.</summary>
    private EntityEntry? MeetingOf(EntityEntry child)
    {
        var property = child.Property("MeetingId");
        var meetingId = (Guid)(child.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<Meeting>().FirstOrDefault(e => e.Entity.Id == meetingId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("A part of a meeting was changed without loading the meeting; load it with its attendees, items and notes.");
        return parent;
    }

    /// <summary>A participant or a note of a drill is its own table but part of the drill: changing one moves the drill's version, so the change travels.</summary>
    private EntityEntry? DrillOf(EntityEntry child)
    {
        var property = child.Property("DrillId");
        var drillId = (Guid)(child.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<Drill>().FirstOrDefault(e => e.Entity.Id == drillId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("A part of a drill was changed without loading the drill; load it with its participants and notes.");
        return parent;
    }

    /// <summary>A person or a supplement of an incident report is its own table but part of the report: changing one moves the report's version, so the change travels.</summary>
    private EntityEntry? IncidentOf(EntityEntry child)
    {
        var property = child.Property("IncidentId");
        var incidentId = (Guid)(child.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<Incident>().FirstOrDefault(e => e.Entity.Id == incidentId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("A part of an incident report was changed without loading the report; load it with its persons and supplements.");
        return parent;
    }

    /// <summary>A specification item is its own table but part of the project: changing one moves the project's version.</summary>
    private EntityEntry? ProjectOf(EntityEntry item)
    {
        var property = item.Property("ProjectId");
        var projectId = (Guid)(item.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<Project>().FirstOrDefault(e => e.Entity.Id == projectId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("A part of a project was changed without loading the project; load it with its specification items.");
        return parent;
    }

    /// <summary>An inspection result is its own table but part of the inspection: changing one moves the inspection's version.</summary>
    private EntityEntry? InspectionOf(EntityEntry result)
    {
        var property = result.Property("InspectionId");
        var inspectionId = (Guid)(result.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<TankInspection>().FirstOrDefault(e => e.Entity.Id == inspectionId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("An inspection result was changed without loading its inspection; load the inspection with its results.");
        return parent;
    }

    /// <summary>An RFQ line is its own table but part of the RFQ document, like a requisition line: changing one moves the RFQ's version.</summary>
    private EntityEntry? RfqOf(EntityEntry line)
    {
        var property = line.Property("RfqId");
        var rfqId = (Guid)(line.State == EntityState.Deleted ? property.OriginalValue : property.CurrentValue)!;
        var parent = ChangeTracker.Entries<Rfq>().FirstOrDefault(e => e.Entity.Id == rfqId);
        if (parent is null && Node.SyncEnabled)
            throw new InvalidOperationException("An RFQ line was changed without loading its RFQ; load the RFQ with its lines.");
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
