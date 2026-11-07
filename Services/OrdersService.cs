using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record OrderRow(Guid Id, string PoNumber, string SupplierName, DateTime CreatedDate, PoStatus Status, DeliveryStatus Delivery, string Currency, decimal Total, int Lines, bool Handover = false);

public record OrderLineView(Guid ItemId, string Code, string Name, string Uom, decimal Ordered, decimal Received, decimal UnitPrice, decimal DiscountPct)
{
    public decimal Remaining => Math.Max(0m, Ordered - Received);
    public decimal Net => Money.LineNet(Ordered, UnitPrice, DiscountPct);
}

public record OrderView(PurchaseOrder Order, string SupplierName, string SupplierContact, string? RequisitionNumber, Guid? RequisitionId,
    List<OrderLineView> Lines, List<DeliveryOrder> Deliveries, string? PortName, bool CanDecide = false, DispatchView? Dispatch = null);

/// <summary>What a vessel sees of its purchase orders and where each one stands.</summary>
public class OrdersService(IDbContextFactory<ChandleryDbContext> factory)
{
    public async Task<List<OrderRow>> ListAsync(Guid companyId, string? search = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var orders = await db.PurchaseOrders.AsNoTracking().OrderByDescending(p => p.CreatedDate).ToListAsync();
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var rows = orders.Select(p => new OrderRow(p.Id, p.PoNumber, suppliers.GetValueOrDefault(p.SupplierId) ?? "", p.CreatedDate, p.Status, p.DeliveryStatus, p.Currency, p.Total, p.Lines.Count, p.HandoverRequested));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            rows = rows.Where(r => r.PoNumber.Contains(s, StringComparison.OrdinalIgnoreCase) || r.SupplierName.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        return rows.ToList();
    }

    public async Task<OrderView?> GetAsync(Guid companyId, Guid id, Guid? userId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var po = await db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (po is null) return null;

        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == po.SupplierId);
        var deliveries = await db.DeliveryOrders.AsNoTracking().Where(d => d.PurchaseOrderId == id).OrderBy(d => d.ReceivedDate).ThenBy(d => d.CreatedAt).ToListAsync();
        var items = await db.Items.AsNoTracking().Where(i => po.Lines.Select(l => l.ItemId).Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var received = deliveries.SelectMany(d => d.Lines).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var lines = po.Lines.Select(l => new OrderLineView(l.ItemId, items.GetValueOrDefault(l.ItemId)?.Code ?? "", items.GetValueOrDefault(l.ItemId)?.Name ?? "(item)",
            l.UomOr(items.GetValueOrDefault(l.ItemId)?.UnitOfMeasure), l.Quantity, received.GetValueOrDefault(l.ItemId), l.UnitPrice, l.DiscountPct)).ToList();

        string? number = null;
        if (po.RequisitionId is { } reqId) number = await db.Requisitions.AsNoTracking().Where(r => r.Id == reqId).Select(r => r.ReqnNumber).FirstOrDefaultAsync();
        var port = po.OrderPortId is { } portId ? await db.Ports.AsNoTracking().Where(p => p.Id == portId).Select(p => p.Name).FirstOrDefaultAsync() : null;
        // Is it this person's turn to approve (the Master, for an order within his authority)?
        var canDecide = userId is { } uid && po.Status == PoStatus.PendingApproval
            && await db.PoApprovals.AnyAsync(a => a.PurchaseOrderId == id && a.ApproverUserId == uid && a.Decision == ApprovalDecision.Pending);
        return new OrderView(po, supplier?.Name ?? "", supplier?.ContactEmail ?? "", number, number is null ? null : po.RequisitionId, lines, deliveries, port, canDecide, await DispatchService.ViewAsync(db, id));
    }

    /// <summary>The orders raised from one requisition, for showing where it stands.</summary>
    public async Task<List<OrderRow>> ForRequisitionAsync(Guid companyId, Guid requisitionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var orders = await db.PurchaseOrders.AsNoTracking().Where(p => p.RequisitionId == requisitionId).OrderBy(p => p.CreatedDate).ToListAsync();
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        return orders.Select(p => new OrderRow(p.Id, p.PoNumber, suppliers.GetValueOrDefault(p.SupplierId) ?? "", p.CreatedDate, p.Status, p.DeliveryStatus, p.Currency, p.Total, p.Lines.Count, p.HandoverRequested)).ToList();
    }
}
