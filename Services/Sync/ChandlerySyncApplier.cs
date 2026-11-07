using Chandlery.Data;
using Chandlery.Models;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services.Sync;

/// <summary>
/// Stores a document that arrived from the other side. At the hub it receives vessels' requisitions (stream Up); onboard it
/// receives the office's (stream Down). Applying is an upsert by document id, so a resent message does no harm. Anything that
/// cannot be stored (a missing vessel, item or user, a sender acting for another vessel) throws, which parks the message
/// where an operator can see it and replay it once the cause is fixed.
/// </summary>
public sealed class ChandlerySyncApplier(IDbContextFactory<ChandleryDbContext> factory, NodeIdentity node) : IDocumentApplier
{
    public async Task ApplyAsync(StreamKey stream, SyncDocument document, CancellationToken ct = default)
    {
        if (document.DocType != SyncDocTypes.Requisition)
            throw new NotSupportedException($"Document type '{document.DocType}' is not supported by this installation.");
        if (document.SchemaVersion > RequisitionDocument.SchemaVersion)
            throw new NotSupportedException($"Requisition schema {document.SchemaVersion} is newer than this installation understands ({RequisitionDocument.SchemaVersion}).");

        var incoming = RequisitionDocument.FromJson(document.Payload);
        var atHub = stream.Direction == Direction.Up;
        if (atHub == node.IsVessel)
            throw new InvalidOperationException(atHub ? "A vessel installation cannot receive vessel documents." : "The office cannot receive office documents.");

        await using var db = await factory.CreateDbContextAsync(ct);
        db.Node.SyncEnabled = node.SyncEnabled;
        db.ApplyingSync = true;   // keep the arriving version and origin; do not announce the change again

        var local = await db.Requisitions.IgnoreQueryFilters().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == incoming.Id, ct);
        var localVersion = local is null ? (int?)null : (int?)db.Entry(local).Property(ChandleryDbContext.SyncVersion).CurrentValue;
        if (!RequisitionOwnership.ShouldApply(atHub, local?.OwnerStage, localVersion, incoming.OwnerStage, incoming.SyncVersion))
            return;

        await EnsureMasterDataAsync(db, incoming, ct);

        if (atHub)
        {
            // A vessel may only submit for the vessel it runs.
            var vessel = await db.Vessels.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.Id == incoming.VesselId, ct)
                ?? throw new InvalidOperationException($"Vessel {incoming.VesselId} is not known at the office.");
            if (vessel.NodeId?.ToString("N") != stream.NodeId)
                throw new InvalidOperationException($"Node {stream.NodeId} may not send requisitions for vessel {vessel.Name}.");
        }

        var isNew = local is null;
        local ??= new Requisition { Id = incoming.Id };
        Map(incoming, local);
        if (isNew) db.Requisitions.Add(local);

        var entry = db.Entry(local);
        var version = incoming.SyncVersion;
        var now = DateTime.UtcNow;
        if (atHub)
        {
            // Accepted: the office takes over and tells the vessel so, as a new version only the office may build on.
            local.OwnerStage = SyncStages.Office;
            version = incoming.SyncVersion + 1;
            db.SyncDirties.Add(new SyncDirty { DocType = SyncDocTypes.Requisition, DocId = local.Id, At = now });
        }
        entry.Property(ChandleryDbContext.SyncVersion).CurrentValue = version;
        entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue = incoming.OriginNodeId;
        entry.Property(ChandleryDbContext.SyncCreatedAt).CurrentValue = incoming.SyncCreatedAt;
        entry.Property(ChandleryDbContext.SyncUpdatedAt).CurrentValue = atHub ? now : incoming.SyncUpdatedAt;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException(
                $"Requisition {incoming.ReqnNumber} could not be stored, probably because master data it refers to is missing here: {ex.GetBaseException().Message}", ex);
        }
    }

    /// <summary>
    /// Everything the requisition refers to must already be held here. Master data arrives on its own schedule, so a
    /// document can overtake it; refusing now parks the message until the missing record turns up and an operator replays it.
    /// </summary>
    private static async Task EnsureMasterDataAsync(ChandleryDbContext db, RequisitionDocument d, CancellationToken ct)
    {
        var missing = new List<string>();
        if (!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.Id == d.CompanyId, ct)) missing.Add($"company {d.CompanyId}");
        if (!await db.Vessels.IgnoreQueryFilters().AnyAsync(v => v.Id == d.VesselId, ct)) missing.Add($"vessel {d.VesselId}");
        if (d.DepartmentId is { } dept && !await db.Departments.IgnoreQueryFilters().AnyAsync(x => x.Id == dept, ct)) missing.Add($"department {dept}");
        if (d.DeliveryPortId is { } port && !await db.Ports.AnyAsync(p => p.Id == port, ct)) missing.Add($"port {port}");

        var itemIds = d.Lines.Select(l => l.ItemId).Distinct().ToList();
        var known = await db.Items.IgnoreQueryFilters().Where(i => itemIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
        missing.AddRange(itemIds.Except(known).Select(id => $"item {id}"));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Requisition {d.ReqnNumber} refers to master data that is missing here ({string.Join(", ", missing)}).");
    }

    private static void Map(RequisitionDocument d, Requisition r)
    {
        r.CompanyId = d.CompanyId;
        r.ReqnNumber = d.ReqnNumber;
        r.Type = d.Type;
        r.VesselId = d.VesselId;
        r.DepartmentId = d.DepartmentId;
        r.DeliveryPortId = d.DeliveryPortId;
        r.Urgency = d.Urgency;
        r.ReqnDate = d.ReqnDate;
        r.VesselEta = d.VesselEta;
        r.VesselEtd = d.VesselEtd;
        r.EstDeliveryDate = d.EstDeliveryDate;
        r.IsBreakdown = d.IsBreakdown;
        r.Remarks = d.Remarks;
        r.Status = d.Status;
        r.CreatedByUserId = d.CreatedByUserId;
        r.CreatedByName = d.CreatedByName;
        r.CreatedAt = d.CreatedAt;
        r.OwnerStage = d.OwnerStage;

        // Lines are matched by id so existing ones are updated in place; ones the sender no longer has are removed.
        var incomingIds = d.Lines.Select(l => l.Id).ToHashSet();
        r.Lines.RemoveAll(l => !incomingIds.Contains(l.Id));
        foreach (var line in d.Lines)
        {
            var target = r.Lines.FirstOrDefault(l => l.Id == line.Id);
            if (target is null)
            {
                target = new RequisitionLine { Id = line.Id };
                r.Lines.Add(target);
            }
            target.ItemId = line.ItemId;
            target.Quantity = line.Quantity;
            target.ApprovedQuantity = line.ApprovedQuantity;
            target.Uom = line.Uom;
            target.Description = line.Description;
            target.DrawingNo = line.DrawingNo;
        }
    }
}
