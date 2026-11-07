using Chandlery.Data;
using Chandlery.Models;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

// ---- the sync status chip ----

public record SyncStatusView(bool Enabled, DateTimeOffset? LastSuccess, int Failures, string? Error, int Waiting, bool Offline, string Text, string Tone);

/// <summary>The words for the sync chip, kept apart so they can be checked without a running link.</summary>
public static class SyncStatusText
{
    public const int OfflineAfterFailures = 3;

    public static string Ago(DateTimeOffset now, DateTimeOffset then)
    {
        var span = now - then;
        if (span < TimeSpan.FromSeconds(45)) return "just now";
        if (span < TimeSpan.FromMinutes(90)) return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min ago";
        if (span < TimeSpan.FromHours(36)) return $"{(int)Math.Round(span.TotalHours)} h ago";
        return $"{(int)Math.Round(span.TotalDays)} d ago";
    }

    public static SyncStatusView Describe(DateTimeOffset now, DateTimeOffset? lastSuccess, int failures, string? error, int waiting)
    {
        var offline = failures >= OfflineAfterFailures;
        var changes = waiting == 1 ? "1 change waiting" : $"{waiting} changes waiting";
        if (lastSuccess is null && !offline)
            return new SyncStatusView(true, null, failures, error, waiting, false, waiting > 0 ? $"Not synced yet · {changes}" : "Not synced yet", "warning");
        if (offline)
        {
            var since = lastSuccess is null ? "never connected" : $"last synced {Ago(now, lastSuccess.Value)}";
            return new SyncStatusView(true, lastSuccess, failures, error, waiting, true, $"Offline · {since}" + (waiting > 0 ? $" · {changes}" : ""), "danger");
        }
        return new SyncStatusView(true, lastSuccess, failures, error, waiting, false,
            $"Synced {Ago(now, lastSuccess!.Value)}" + (waiting > 0 ? $" · {changes}" : ""), waiting > 0 ? "warning" : "success");
    }
}

/// <summary>Reads the state of the link to the office from the sync engine (nothing on an installation without sync).</summary>
public class SyncStatusService(IServiceProvider services)
{
    public async Task<SyncStatusView> GetAsync()
    {
        var control = services.GetService<INodeSyncControl>();
        var agent = services.GetService<NodeAgent>();
        if (control is null || agent is null) return new SyncStatusView(false, null, 0, null, 0, false, "", "neutral");
        var waiting = (await agent.UpBacklogAsync()).Messages;
        var status = control.Status;
        return SyncStatusText.Describe(DateTimeOffset.UtcNow, status.LastSuccessUtc, status.ConsecutiveFailures, status.LastError, waiting);
    }

    public void SyncNow() => services.GetService<INodeSyncControl>()?.SyncNow();
}

// ---- the vessel home ----

public record HomeRequisition(Guid Id, string Number, RequisitionStatus Status, DateTime Date, int Lines);
public record HomeDelivery(Guid OrderId, string PoNumber, string Supplier, string? Port, DeliveryStatus Delivery, decimal Total, string Currency);

public record VesselHomeData(
    int OpenRequisitions,
    List<HomeRequisition> Requisitions,
    int RfqsAwaitingQuotes,
    int QuotesToEvaluate,
    List<OrderRow> NeedsMyApproval,
    int AwaitingMaster,
    int AwaitingOffice,
    List<HomeDelivery> Deliveries);

public record PortCall(Guid? PortId, string Port, DateTime? Eta, List<HomeRequisition> Requisitions, List<HomeDelivery> Orders);

/// <summary>What the vessel's home page and port-call view show, worked out from the vessel's own data.</summary>
public class VesselHomeService(IDbContextFactory<ChandleryDbContext> factory)
{
    public async Task<VesselHomeData> GetAsync(Guid companyId, Guid? userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var requisitions = await db.Requisitions.AsNoTracking().Include(r => r.Lines).OrderByDescending(r => r.ReqnDate).ToListAsync();
        var open = requisitions.Where(r => r.Status is RequisitionStatus.Open or RequisitionStatus.RfqPosted).ToList();

        var rfqs = await db.Rfqs.AsNoTracking().Include(r => r.Lines).Where(r => r.OwnerStage == SyncStages.Vessel).ToListAsync();
        var orders = await db.PurchaseOrders.AsNoTracking().ToListAsync();
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);

        var mine = userId is { } uid
            ? (await db.PoApprovals.AsNoTracking().Where(a => a.ApproverUserId == uid && a.Decision == ApprovalDecision.Pending).Select(a => a.PurchaseOrderId).ToListAsync()).ToHashSet()
            : [];
        var needsMe = orders.Where(o => mine.Contains(o.Id) && o.Status == PoStatus.PendingApproval).OrderBy(o => o.CreatedDate).ToList();
        var pending = orders.Where(o => o.Status == PoStatus.PendingApproval).ToList();

        var ports = await db.Ports.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var deliveries = orders.Where(o => o.Status == PoStatus.Approved && o.DeliveryStatus != DeliveryStatus.Delivered)
            .OrderBy(o => o.CreatedDate).Select(o => ToDelivery(o, suppliers, ports, requisitions, rfqs)).ToList();

        return new VesselHomeData(
            open.Count,
            requisitions.Take(6).Select(ToRow).ToList(),
            rfqs.Count(r => r.Status == RfqStatus.Sent),
            rfqs.Count(r => r.Status == RfqStatus.QuotesReceived && r.Lines.Any(l => l.WinningSupplierId is null)),
            needsMe.Select(o => new OrderRow(o.Id, o.PoNumber, suppliers.GetValueOrDefault(o.SupplierId) ?? "", o.CreatedDate, o.Status, o.DeliveryStatus, o.Currency, o.Total, o.Lines.Count, o.HandoverRequested)).ToList(),
            pending.Count(o => !o.HandoverRequested),
            pending.Count(o => o.HandoverRequested),
            deliveries);
    }

    /// <summary>Ports the vessel is waiting on: requisitions still open and orders still to be delivered, grouped by delivery port and ordered by ETA.</summary>
    public async Task<List<PortCall>> PortCallsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var requisitions = await db.Requisitions.AsNoTracking().Include(r => r.Lines).ToListAsync();
        var rfqs = await db.Rfqs.AsNoTracking().ToListAsync();
        var orders = await db.PurchaseOrders.AsNoTracking().Where(o => o.Status == PoStatus.Approved && o.DeliveryStatus != DeliveryStatus.Delivered).ToListAsync();
        var suppliers = await db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);
        var ports = await db.Ports.AsNoTracking().ToDictionaryAsync(p => p.Id, p => $"{p.Name}, {p.Country}");

        // Guid.Empty stands for "no port chosen" (a dictionary cannot be keyed by null).
        var calls = new Dictionary<Guid, PortCall>();
        PortCall Call(Guid? portId) => calls.TryGetValue(portId ?? Guid.Empty, out var c) ? c
            : calls[portId ?? Guid.Empty] = new PortCall(portId, portId is { } id ? ports.GetValueOrDefault(id) ?? "Unknown port" : "No port chosen", null, [], []);

        foreach (var r in requisitions.Where(r => r.Status is RequisitionStatus.Open or RequisitionStatus.RfqPosted))
        {
            var call = Call(r.DeliveryPortId);
            call.Requisitions.Add(ToRow(r));
            if (r.VesselEta is { } eta && (call.Eta is null || eta < call.Eta)) calls[r.DeliveryPortId ?? Guid.Empty] = call with { Eta = eta };
        }
        foreach (var o in orders)
        {
            var portId = o.OrderPortId
                ?? (o.RequisitionId is { } rid ? requisitions.FirstOrDefault(r => r.Id == rid)?.DeliveryPortId : null)
                ?? (o.RfqId is { } fid ? rfqs.FirstOrDefault(r => r.Id == fid)?.DeliveryPortId : null);
            Call(portId).Orders.Add(new HomeDelivery(o.Id, o.PoNumber, suppliers.GetValueOrDefault(o.SupplierId) ?? "", ports.GetValueOrDefault(portId ?? Guid.Empty), o.DeliveryStatus, o.Total, o.Currency));
        }
        return calls.Values.OrderBy(c => c.Eta ?? DateTime.MaxValue).ThenBy(c => c.Port).ToList();
    }

    private static HomeRequisition ToRow(Requisition r) => new(r.Id, r.ReqnNumber, r.Status, r.ReqnDate, r.Lines.Count);

    private static HomeDelivery ToDelivery(PurchaseOrder o, Dictionary<Guid, string> suppliers, Dictionary<Guid, string> ports, List<Requisition> requisitions, List<Rfq> rfqs)
    {
        var portId = o.OrderPortId
            ?? (o.RequisitionId is { } rid ? requisitions.FirstOrDefault(r => r.Id == rid)?.DeliveryPortId : null)
            ?? (o.RfqId is { } fid ? rfqs.FirstOrDefault(r => r.Id == fid)?.DeliveryPortId : null);
        return new HomeDelivery(o.Id, o.PoNumber, suppliers.GetValueOrDefault(o.SupplierId) ?? "", portId is { } p ? ports.GetValueOrDefault(p) : null, o.DeliveryStatus, o.Total, o.Currency);
    }
}
