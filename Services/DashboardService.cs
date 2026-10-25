using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record SpendRow(int SupplierId, string Supplier, decimal Total);
public record ActivityRow(DateTime At, string UserName, string Text, DocKind Kind, int Id, string Number);

public record CompanyDashboard(
    int OpenRequisitions,
    int RfqsAwaitingQuotes,
    int RfqsWithQuotes,
    int PendingMyApproval,
    int AwaitingDelivery,
    string SpendCurrency,
    decimal Spend30Days,
    List<PendingApprovalRow> MyApprovals,
    List<RfqBrief> QuotesToCompare,
    List<ReqnBrief> RequisitionsWithoutRfq,
    List<SpendRow> TopSuppliers,
    List<ActivityRow> Activity);

public record SupplierDashboard(
    int RfqsToQuote,
    int QuotesSubmitted,
    int ApprovedPos,
    string ValueCurrency,
    decimal ApprovedValue,
    List<RfqBrief> OpenRfqs,
    List<PoBrief> RecentPos);

/// <summary>Numbers and work queues for the home page.</summary>
public class DashboardService(IDbContextFactory<ChandleryDbContext> factory, ApprovalService approvals, HubService hub)
{
    public async Task<CompanyDashboard> GetCompanyAsync(int companyId, int userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var openReqns = await db.Requisitions.CountAsync(r => r.CompanyId == companyId && r.Status == RequisitionStatus.Open);

        var openRfqs = await db.Rfqs.Where(r => r.CompanyId == companyId && r.Status != RfqStatus.Awarded)
            .Select(r => new { Rfq = r, Quotes = db.Quotes.Count(q => q.RfqId == r.Id) }).ToListAsync();
        var awaiting = openRfqs.Count(x => x.Quotes == 0);
        var withQuotes = openRfqs.Where(x => x.Quotes > 0).Select(x => x.Rfq).OrderByDescending(r => r.CreatedDate).Take(5).ToList();
        var withQuotesCount = openRfqs.Count(x => x.Quotes > 0);

        var pending = await approvals.GetPendingForUserAsync(companyId, userId);

        var awaitingDelivery = await db.PurchaseOrders.CountAsync(p => p.CompanyId == companyId && p.Status == PoStatus.Approved
            && p.DeliveryStatus != DeliveryStatus.Delivered);

        // Spend: approved POs of the last 30 days, in the currency used most (amounts in different currencies are not added).
        var since = DateTime.UtcNow.AddDays(-30);
        var recentPos = await db.PurchaseOrders.Where(p => p.CompanyId == companyId && p.Status == PoStatus.Approved && p.CreatedDate >= since).ToListAsync();
        var currency = recentPos.GroupBy(p => p.Currency).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "USD";
        var inCurrency = recentPos.Where(p => p.Currency == currency).ToList();
        var supplierNames = await db.Suppliers.Where(s => inCurrency.Select(p => p.SupplierId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var top = inCurrency.GroupBy(p => p.SupplierId)
            .Select(g => new SpendRow(g.Key, supplierNames.GetValueOrDefault(g.Key) ?? "", g.Sum(p => p.Total)))
            .OrderByDescending(s => s.Total).Take(5).ToList();

        var noRfq = await db.Requisitions.Where(r => r.CompanyId == companyId && r.Status == RequisitionStatus.Open
                && !db.Rfqs.Any(x => x.RequisitionId == r.Id))
            .OrderByDescending(r => r.ReqnDate).Take(5).ToListAsync();
        var vessels = await db.Vessels.Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        var depts = await db.Departments.Where(d => d.CompanyId == companyId).ToDictionaryAsync(d => d.Id, d => d.Name);
        var reqnBriefs = new List<ReqnBrief>();
        foreach (var r in noRfq)
        {
            var lines = await db.Requisitions.Where(x => x.Id == r.Id).Select(x => x.Lines.Count).FirstAsync();
            reqnBriefs.Add(new ReqnBrief(r.Id, r.ReqnNumber, r.VesselId, vessels.GetValueOrDefault(r.VesselId) ?? "", r.DepartmentId,
                r.DepartmentId.HasValue ? depts.GetValueOrDefault(r.DepartmentId.Value) ?? "" : "", r.ReqnDate, r.Status, lines));
        }

        var activity = await GetActivityAsync(db, companyId);

        return new CompanyDashboard(
            openReqns, awaiting, withQuotesCount, pending.Count, awaitingDelivery, currency, inCurrency.Sum(p => p.Total),
            pending.Take(5).ToList(),
            withQuotes.Select(r => new RfqBrief(r.Id, r.RfqNumber, r.Vessel, r.VesselId, r.CreatedDate, r.DueDate, r.Status, r.InvitedSupplierIds.Count)).ToList(),
            reqnBriefs, top, activity);
    }

    private static async Task<List<ActivityRow>> GetActivityAsync(ChandleryDbContext db, int companyId)
    {
        var entries = await db.AuditEntries.Where(a => a.CompanyId == companyId)
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(40).ToListAsync();

        var reqIds = entries.Where(e => e.EntityType == AuditEntityTypes.Requisition).Select(e => e.EntityId).Distinct().ToList();
        var rfqIds = entries.Where(e => e.EntityType == AuditEntityTypes.Rfq).Select(e => e.EntityId).Distinct().ToList();
        var poIds = entries.Where(e => e.EntityType == AuditEntityTypes.PurchaseOrder).Select(e => e.EntityId).Distinct().ToList();
        var reqNumbers = await db.Requisitions.Where(r => reqIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.ReqnNumber);
        var rfqNumbers = await db.Rfqs.Where(r => rfqIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.RfqNumber);
        var poNumbers = await db.PurchaseOrders.Where(p => poIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PoNumber);

        var rows = new List<ActivityRow>();
        foreach (var e in entries)
        {
            var (kind, number) = e.EntityType switch
            {
                AuditEntityTypes.Requisition => (DocKind.Requisition, reqNumbers.GetValueOrDefault(e.EntityId)),
                AuditEntityTypes.Rfq => (DocKind.Rfq, rfqNumbers.GetValueOrDefault(e.EntityId)),
                AuditEntityTypes.PurchaseOrder => (DocKind.PurchaseOrder, poNumbers.GetValueOrDefault(e.EntityId)),
                _ => (DocKind.Requisition, null)
            };
            if (number is null) continue;
            rows.Add(new ActivityRow(e.At, e.UserName, e.Text, kind, e.EntityId, number));
            if (rows.Count == 10) break;
        }
        return rows;
    }

    public async Task<SupplierDashboard> GetSupplierAsync(int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var today = DateTime.UtcNow.Date;
        var invited = await db.Rfqs.IgnoreQueryFilters()
            .Where(r => r.InvitedSupplierIds.Contains(supplierId))
            .OrderByDescending(r => r.CreatedDate).ToListAsync();
        var quotedRfqIds = await db.Quotes.IgnoreQueryFilters().Where(q => q.SupplierId == supplierId).Select(q => q.RfqId).ToListAsync();
        var toQuote = invited.Where(r => !quotedRfqIds.Contains(r.Id) && (r.DueDate == null || r.DueDate.Value.Date >= today) && r.Status != RfqStatus.Awarded).ToList();

        var pos = await db.PurchaseOrders.IgnoreQueryFilters()
            .Where(p => p.SupplierId == supplierId && p.Status == PoStatus.Approved)
            .OrderByDescending(p => p.CreatedDate).ToListAsync();
        var currency = pos.GroupBy(p => p.Currency).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "USD";

        return new SupplierDashboard(
            toQuote.Count, quotedRfqIds.Distinct().Count(), pos.Count, currency, pos.Where(p => p.Currency == currency).Sum(p => p.Total),
            toQuote.Take(5).Select(r => new RfqBrief(r.Id, r.RfqNumber, r.Vessel, r.VesselId, r.CreatedDate, r.DueDate, r.Status, r.InvitedSupplierIds.Count)).ToList(),
            pos.Take(5).Select(p => new PoBrief(p.Id, p.PoNumber, p.SupplierId, "", p.CreatedDate, p.Currency, p.Total, p.Status, p.DeliveryStatus, p.IsSpot)).ToList());
    }
}
