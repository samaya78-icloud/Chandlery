using Chandlery.Data;
using Chandlery.Models;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services.Sync;

/// <summary>
/// Turns the notes left by saves (<see cref="SyncDirty"/>) into sync messages. A document changed several times since the last
/// run is sent once, in its latest state. A note is removed only after its message is queued, so a crash in between sends the
/// document again (harmless: documents are applied by version) and never loses it.
/// </summary>
public sealed class SyncPublisher(IDbContextFactory<ChandleryDbContext> factory, NodeIdentity node, HybridClock clock,
    IServiceProvider services, ILogger<SyncPublisher> log) : BackgroundService
{
    public async Task<int> PublishPendingAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Node.SyncEnabled = node.SyncEnabled;
        db.ApplyingSync = true;

        var notes = await db.SyncDirties.OrderBy(d => d.At).Take(500).ToListAsync(ct);
        if (notes.Count == 0) return 0;

        var sent = 0;
        foreach (var docId in notes.Where(n => n.DocType == SyncDocTypes.Requisition).Select(n => n.DocId).Distinct())
        {
            var requisition = await db.Requisitions.IgnoreQueryFilters().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == docId, ct);
            if (requisition is null) continue;
            if (await PublishRequisitionAsync(db, requisition)) sent++;
        }

        db.SyncDirties.RemoveRange(notes);
        await db.SaveChangesAsync(ct);
        return sent;
    }

    private async Task<bool> PublishRequisitionAsync(ChandleryDbContext db, Requisition requisition)
    {
        var entry = db.Entry(requisition);
        var document = RequisitionDocument.From(requisition,
            (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue,
            (DateTime)entry.Property(ChandleryDbContext.SyncCreatedAt).CurrentValue!,
            (DateTime)entry.Property(ChandleryDbContext.SyncUpdatedAt).CurrentValue!,
            (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue!);
        var message = document.ToSyncDocument(clock.Now());
        var lane = requisition.Urgency == Urgency.Urgent ? Lane.Urgent : Lane.Transaction;

        if (node.IsVessel)
        {
            // Onboard, only what the vessel still owns goes up; the office's own changes arrive here, they are not sent back.
            if (requisition.OwnerStage != SyncStages.Vessel) return false;
            await services.GetRequiredService<INodePublisher>().PublishAsync(message, lane);
            return true;
        }

        // Ashore, only what the office owns goes down, and only to the node that runs the vessel.
        if (requisition.OwnerStage != SyncStages.Office) return false;
        var vesselNode = await db.Vessels.IgnoreQueryFilters().Where(v => v.Id == requisition.VesselId).Select(v => v.NodeId).FirstOrDefaultAsync();
        if (vesselNode is null) return false;
        await services.GetRequiredService<HubEndpoint>().PublishToNodeAsync(vesselNode.Value.ToString("N"), message, lane);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Publishing changes to sync failed; will retry.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { }
        }
    }
}
