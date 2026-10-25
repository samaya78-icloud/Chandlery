using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>Goods received against one approved PO. Lines are matched to the PO's lines by item.</summary>
public class NewDelivery
{
    public DateTime? ReceivedDate { get; set; } = DateTime.UtcNow.Date;
    public string Place { get; set; } = "";
    public string Remarks { get; set; } = "";
    public List<DeliveryOrderLine> Lines { get; set; } = new();
}

/// <summary>One PO line with how much has been received so far.</summary>
public record DeliveryProgress(int ItemId, decimal Ordered, decimal Delivered)
{
    public decimal Remaining => Math.Max(0m, Ordered - Delivered);
}

public class DeliveryService(IDbContextFactory<ChandleryDbContext> factory)
{
    public async Task<List<DeliveryOrder>> GetForPoAsync(int companyId, int poId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.DeliveryOrders
            .Where(d => d.CompanyId == companyId && d.PurchaseOrderId == poId)
            .OrderBy(d => d.ReceivedDate).ThenBy(d => d.Id)
            .ToListAsync();
    }

    /// <summary>The delivery orders for a PO a supplier may see: only when the PO is approved and theirs.</summary>
    public async Task<List<DeliveryOrder>> GetForSupplierAsync(int supplierId, int poId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var visible = await db.PurchaseOrders.IgnoreQueryFilters()
            .AnyAsync(p => p.Id == poId && p.SupplierId == supplierId && p.Status == PoStatus.Approved);
        if (!visible) return new List<DeliveryOrder>();
        return await db.DeliveryOrders.IgnoreQueryFilters()
            .Where(d => d.PurchaseOrderId == poId && d.SupplierId == supplierId)
            .OrderBy(d => d.ReceivedDate).ThenBy(d => d.Id)
            .ToListAsync();
    }

    /// <summary>Deliveries for several of a supplier's POs at once (callers pass POs the supplier may already see).</summary>
    public async Task<Dictionary<int, List<DeliveryOrder>>> GetForSupplierPosAsync(int supplierId, IEnumerable<int> poIds)
    {
        var ids = poIds.Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.DeliveryOrders.IgnoreQueryFilters()
            .Where(d => d.SupplierId == supplierId && ids.Contains(d.PurchaseOrderId))
            .OrderBy(d => d.ReceivedDate).ThenBy(d => d.Id)
            .ToListAsync();
        return rows.GroupBy(d => d.PurchaseOrderId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public static List<DeliveryProgress> Progress(PurchaseOrder po, IEnumerable<DeliveryOrder> deliveries)
    {
        var received = deliveries.SelectMany(d => d.Lines)
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        return po.Lines.Select(l => new DeliveryProgress(l.ItemId, l.Quantity, received.GetValueOrDefault(l.ItemId))).ToList();
    }

    public static DeliveryStatus StatusFor(IReadOnlyCollection<DeliveryProgress> progress)
    {
        if (progress.Count == 0 || progress.All(p => p.Delivered <= 0)) return DeliveryStatus.Pending;
        return progress.All(p => p.Delivered >= p.Ordered) ? DeliveryStatus.Delivered : DeliveryStatus.Partial;
    }

    public async Task<DeliveryOrder> RecordAsync(int companyId, int poId, NewDelivery request, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId)
            ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Status != PoStatus.Approved)
            throw new InvalidOperationException("Deliveries can only be recorded against an approved purchase order.");
        if (!request.ReceivedDate.HasValue)
            throw new InvalidOperationException("Enter the date the goods were received.");

        var lines = request.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("Enter a received quantity on at least one line.");
        if (lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1))
            throw new InvalidOperationException("An item can only appear once on a delivery.");

        var existing = await db.DeliveryOrders.Where(d => d.CompanyId == companyId && d.PurchaseOrderId == poId).ToListAsync();
        var before = Progress(po, existing).ToDictionary(p => p.ItemId);
        var names = await db.Items.Where(i => lines.Select(l => l.ItemId).Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name);
        foreach (var line in lines)
        {
            if (!before.TryGetValue(line.ItemId, out var progress))
                throw new InvalidOperationException("One of the lines is not on this purchase order.");
            if (line.Quantity > progress.Remaining)
                throw new InvalidOperationException(
                    $"{names.GetValueOrDefault(line.ItemId) ?? "An item"}: only {Money.Qty(progress.Remaining)} left to receive, you entered {Money.Qty(line.Quantity)}.");
        }

        // Based on the highest id (not a count) so a deleted delivery never makes the next number collide.
        var nextSeq = 1001 + (await db.DeliveryOrders.IgnoreQueryFilters().MaxAsync(d => (int?)d.Id) ?? 0);
        var delivery = new DeliveryOrder
        {
            CompanyId = companyId,
            DoNumber = $"DO-{nextSeq}",
            PurchaseOrderId = poId,
            SupplierId = po.SupplierId,
            ReceivedDate = DateTime.SpecifyKind(request.ReceivedDate.Value.Date, DateTimeKind.Utc),
            Place = request.Place.Trim(),
            Remarks = request.Remarks.Trim(),
            RecordedByUserId = actor.UserId,
            RecordedByName = actor.Name,
            CreatedAt = DateTime.UtcNow,
            Lines = lines.Select(l => new DeliveryOrderLine { ItemId = l.ItemId, Quantity = l.Quantity }).ToList()
        };
        db.DeliveryOrders.Add(delivery);
        existing.Add(delivery);

        var oldStatus = po.DeliveryStatus;
        po.DeliveryStatus = StatusFor(Progress(po, existing));
        await db.SaveChangesAsync(); // gives the delivery its id

        AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, poId, actor,
            $"RECORDED {delivery.DoNumber}: {delivery.Lines.Count} line(s)" +
            (delivery.Place == "" ? "" : $" at {delivery.Place}") +
            (oldStatus == po.DeliveryStatus ? "" : $"; delivery now {po.DeliveryStatus}"));
        await db.SaveChangesAsync();
        return delivery;
    }

    public async Task DeleteAsync(int companyId, int deliveryId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var delivery = await db.DeliveryOrders.FirstOrDefaultAsync(d => d.Id == deliveryId && d.CompanyId == companyId)
            ?? throw new InvalidOperationException("Delivery order not found.");
        if (delivery.InvoiceId.HasValue)
            throw new InvalidOperationException($"{delivery.DoNumber} is covered by an invoice, so it is locked.");

        var po = await db.PurchaseOrders.FirstAsync(p => p.Id == delivery.PurchaseOrderId && p.CompanyId == companyId);
        db.DeliveryOrders.Remove(delivery);
        await db.SaveChangesAsync();

        var remaining = await db.DeliveryOrders.Where(d => d.CompanyId == companyId && d.PurchaseOrderId == po.Id).ToListAsync();
        var oldStatus = po.DeliveryStatus;
        po.DeliveryStatus = StatusFor(Progress(po, remaining));

        AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
            $"DELETED {delivery.DoNumber}" + (oldStatus == po.DeliveryStatus ? "" : $"; delivery now {po.DeliveryStatus}"));
        await db.SaveChangesAsync();
    }
}
