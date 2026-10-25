using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record PoDocLine(int Number, string Code, string Name, string Uom, decimal Quantity, decimal UnitPrice, decimal DiscountPct, decimal Amount);

public record PoApproverStamp(int Level, string LevelName, string Name, DateTime? At);

/// <summary>Everything the formal purchase order document shows, gathered in one place.</summary>
public record PoDocument(
    Company Company,
    Supplier Supplier,
    PurchaseOrder Po,
    string? RfqNumber,
    string? RequisitionNumber,
    string Vessel,
    string? Imo,
    string? Port,
    string? Department,
    List<PoDocLine> Lines,
    List<PoApproverStamp> Approvers);

public class PoDocumentService(IDbContextFactory<ChandleryDbContext> factory)
{
    public async Task<PoDocument?> GetForCompanyAsync(int companyId, int poId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId);
        return po is null ? null : await BuildAsync(db, po);
    }

    /// <summary>A supplier may open the document only for its own POs, and only once they are approved.</summary>
    public async Task<PoDocument?> GetForSupplierAsync(int supplierId, int poId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var po = await db.PurchaseOrders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == poId && p.SupplierId == supplierId && p.Status == PoStatus.Approved);
        return po is null ? null : await BuildAsync(db, po);
    }

    private static async Task<PoDocument> BuildAsync(ChandleryDbContext db, PurchaseOrder po)
    {
        var company = await db.Companies.FirstAsync(c => c.Id == po.CompanyId);
        var supplier = await db.Suppliers.FirstAsync(s => s.Id == po.SupplierId);

        var rfq = po.RfqId.HasValue
            ? await db.Rfqs.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == po.RfqId.Value)
            : null;
        var requisitionId = po.RequisitionId ?? rfq?.RequisitionId;
        var requisitionNumber = requisitionId.HasValue
            ? await db.Requisitions.IgnoreQueryFilters().Where(r => r.Id == requisitionId.Value).Select(r => r.ReqnNumber).FirstOrDefaultAsync()
            : null;

        Vessel? vessel = null;
        var vesselId = po.VesselId ?? rfq?.VesselId;
        if (vesselId.HasValue)
            vessel = await db.Vessels.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.Id == vesselId.Value);

        var portId = po.OrderPortId ?? rfq?.DeliveryPortId;
        var port = portId.HasValue ? await db.Ports.FirstOrDefaultAsync(p => p.Id == portId.Value) : null;

        var department = po.DepartmentId.HasValue
            ? await db.Departments.IgnoreQueryFilters().Where(d => d.Id == po.DepartmentId.Value).Select(d => d.Name).FirstOrDefaultAsync()
            : null;

        var itemIds = po.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.IgnoreQueryFilters().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var lines = po.Lines.Select((l, index) =>
        {
            var item = items.GetValueOrDefault(l.ItemId);
            return new PoDocLine(index + 1, item?.Code ?? "", item?.Name ?? "", item?.UnitOfMeasure ?? "",
                l.Quantity, l.UnitPrice, l.DiscountPct, Money.LineNet(l.Quantity, l.UnitPrice, l.DiscountPct));
        }).ToList();

        var approvals = await db.PoApprovals.IgnoreQueryFilters()
            .Where(a => a.PurchaseOrderId == po.Id && a.Decision == ApprovalDecision.Approved).ToListAsync();
        var userIds = approvals.Select(a => a.DecidedByUserId ?? a.ApproverUserId).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var levelNames = await ApprovalLevelNames.LoadAsync(db, po.CompanyId);
        var stamps = approvals.OrderBy(a => a.Level)
            .Select(a => new PoApproverStamp(a.Level, levelNames.Name(a.Level), names.GetValueOrDefault(a.DecidedByUserId ?? a.ApproverUserId) ?? "", a.DecidedAt))
            .ToList();

        return new PoDocument(company, supplier, po, rfq?.RfqNumber, requisitionNumber,
            vessel?.Name ?? rfq?.Vessel ?? "", vessel?.ImoNumber,
            port is null ? null : $"{port.Name}, {port.Country}", department, lines, stamps);
    }
}
