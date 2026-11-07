using Chandlery.Data;
using Chandlery.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Chandlery.Services.Sync;

/// <summary>
/// Stores a document that arrived from the other side. At the hub it receives vessels' requisitions (stream Up); onboard it
/// receives the office's (stream Down). Applying is an upsert by document id, so a resent message does no harm. Anything that
/// cannot be stored (a missing vessel, item or user, a sender acting for another vessel) throws, which parks the message
/// where an operator can see it and replay it once the cause is fixed.
/// </summary>
public sealed class ChandlerySyncApplier(IDbContextFactory<ChandleryDbContext> factory, NodeIdentity node) : IDocumentApplier
{
    // Parents already confirmed present here, so a long run of items does not look up its category every time.
    private readonly ConcurrentDictionary<string, bool> _present = new();

    public async Task ApplyAsync(StreamKey stream, SyncDocument document, CancellationToken ct = default)
    {
        if (document.DocType == SyncDocTypes.CrewCredential)
        {
            await ApplyCrewCredentialAsync(stream, document, ct);
            return;
        }
        if (MasterData.Find(document.DocType) is { } master)
        {
            await ApplyMasterAsync(stream, document, master, ct);
            return;
        }
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

        // Not an error yet: master data travels on its own schedule and may simply be on its way. The engine retries, and parks
        // the message for an operator only if it never turns up.
        if (missing.Count > 0)
            throw new DeferredException(
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

    /// <summary>
    /// Master data from the office, stored on a vessel. A record whose parent (its category, its group...) has not arrived is held
    /// back until it does; a removal that other records still depend on waits for those removals. Nothing here is announced again.
    /// </summary>
    private async Task ApplyMasterAsync(StreamKey stream, SyncDocument document, MasterType master, CancellationToken ct)
    {
        if (stream.Direction != Direction.Down || !node.IsVessel)
            throw new InvalidOperationException("Master data is sent by the office to vessels only.");
        if (document.SchemaVersion > MasterData.SchemaVersion)
            throw new NotSupportedException($"{master.DocType} schema {document.SchemaVersion} is newer than this installation understands ({MasterData.SchemaVersion}).");

        var payload = JsonNode.Parse(document.Payload)?.AsObject() ?? throw new JsonException($"Empty {master.DocType} document.");

        await using var db = await factory.CreateDbContextAsync(ct);
        db.Node.SyncEnabled = node.SyncEnabled;
        db.ApplyingSync = true;

        var type = db.Model.FindEntityType(master.Clr)!;
        var key = type.FindPrimaryKey()!.Properties
            .Select(p => MasterData.TryRead(p, payload, out var v) && v is not null ? v : throw new InvalidOperationException($"{master.DocType} document has no {p.Name}."))
            .ToArray();

        if (!document.Deleted) await RequireParentsAsync(db, type, master, payload, ct);
        var existing = await db.FindAsync(master.Clr, key, ct);

        if (document.Deleted)
        {
            _present.Clear();
            if (existing is null) return;
            if (existing is CrewMember leaving) await CrewAccounts.DisableAsync(db, leaving.UserId);
            db.Remove(existing);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                throw new DeferredException($"{master.DocType} cannot be removed yet, other records here still use it: {ex.GetBaseException().Message}");
            }
            return;
        }

        // A crew record carries the login too; the vessel keeps a login change of its own that is newer than the one arriving.
        var keepOwnLogin = existing is CrewMember held
            && MasterData.TryRead(type.FindProperty(nameof(CrewMember.CredentialsChangedAt))!, payload, out var incomingAt)
            && incomingAt is DateTime at && at <= held.CredentialsChangedAt;

        EntityEntry entry = existing is null ? db.Add(Activator.CreateInstance(master.Clr)!) : db.Entry(existing);
        foreach (var property in type.GetProperties())
        {
            if (keepOwnLogin && CrewMember.CredentialColumns.Contains(property.Name)) continue;
            if (MasterData.TryRead(property, payload, out var value))
                entry.Property(property.Name).CurrentValue = value;
        }
        if (entry.Entity is CrewMember arrived) await CrewAccounts.ApplyAsync(db, arrived);
        entry.Property(ChandleryDbContext.SyncVersion).CurrentValue = (int)document.Version;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException($"{master.DocType} could not be stored: {ex.GetBaseException().Message}", ex);
        }
    }

    private async Task RequireParentsAsync(ChandleryDbContext db, Microsoft.EntityFrameworkCore.Metadata.IEntityType type, MasterType master, JsonObject payload, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var fk in type.GetForeignKeys())
        {
            var values = new object?[fk.Properties.Count];
            var complete = true;
            for (var i = 0; i < values.Length; i++)
            {
                if (!MasterData.TryRead(fk.Properties[i], payload, out values[i]) || values[i] is null) { complete = false; break; }
            }
            if (!complete) continue;   // an optional reference left empty

            var principal = fk.PrincipalEntityType.ClrType;
            var name = $"{principal.Name} {string.Join("/", values)}";
            if (_present.ContainsKey(name)) continue;
            var found = await db.FindAsync(principal, values, ct);
            if (found is null) { missing.Add(name); continue; }
            db.Entry(found).State = EntityState.Detached;
            _present[name] = true;
        }

        if (missing.Count > 0)
            throw new DeferredException($"{master.DocType} refers to {string.Join(", ", missing)}, which has not arrived yet.");
    }

    /// <summary>
    /// A login change made onboard, arriving at the office. It must come from the vessel the person serves on, and it counts only
    /// if it is newer than the login the office holds.
    /// </summary>
    private async Task ApplyCrewCredentialAsync(StreamKey stream, SyncDocument document, CancellationToken ct)
    {
        if (stream.Direction != Direction.Up || node.IsVessel)
            throw new InvalidOperationException("Login changes are sent by vessels to the office.");
        if (document.SchemaVersion > CrewCredentialDocument.SchemaVersion)
            throw new NotSupportedException($"Crew credential schema {document.SchemaVersion} is newer than this installation understands.");

        var incoming = CrewCredentialDocument.FromJson(document.Payload);
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Node.SyncEnabled = node.SyncEnabled;
        db.ApplyingSync = true;

        var crew = await db.CrewMembers.FirstOrDefaultAsync(c => c.Id == incoming.CrewId, ct)
            ?? throw new DeferredException($"Crew member {incoming.CrewId} has not been created here yet.");
        var vessel = crew.VesselId is { } vesselId ? await db.Vessels.FirstOrDefaultAsync(v => v.Id == vesselId, ct) : null;
        if (vessel?.NodeId?.ToString("N") != stream.NodeId)
            throw new InvalidOperationException($"Node {stream.NodeId} may not change the login of {crew.CrewCode}, who does not serve on its vessel.");

        if (incoming.ChangedAt <= crew.CredentialsChangedAt) return;   // the office already holds a newer change
        crew.PasswordHash = incoming.PasswordHash;
        crew.MustChangePassword = incoming.MustChangePassword;
        crew.LoginEnabled = incoming.LoginEnabled;
        crew.CredentialsChangedAt = DateTime.SpecifyKind(incoming.ChangedAt, DateTimeKind.Utc);
        crew.CredentialsChangedBy = incoming.ChangedBy;
        await db.SaveChangesAsync(ct);
    }
}
