using System.Text.Json.Nodes;
using Chandlery.Data;
using Chandlery.Models;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Chandlery.Services.Sync;

/// <summary>
/// Turns the notes left by saves (<see cref="SyncDirty"/>) into sync messages. A document changed several times since the last
/// run is sent once, in its latest state. A note is removed only after its message is queued, so a crash in between sends the
/// document again (harmless: documents are applied by version) and never loses it.
/// Ashore this carries two kinds of document: requisitions the office now owns (to the vessel they are for) and master data (to
/// each vessel that is entitled to it). Onboard it carries requisitions the vessel still owns.
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
        // Master data first: documents that refer to it should not have to wait for it.
        var masterNotes = notes.Where(n => MasterData.Find(n.DocType) is not null).ToList();
        if (masterNotes.Count > 0 && !node.IsVessel) sent += await PublishMasterAsync(db, masterNotes, ct);

        if (node.IsVessel)
        {
            foreach (var crewId in notes.Where(n => n.DocType == SyncDocTypes.CrewCredential).Select(n => n.DocId).Distinct())
            {
                var crew = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewId, ct);
                if (crew is null) continue;
                var message = CrewCredentialDocument.From(crew).ToSyncDocument(clock.Now(), node.SyncNodeId ?? "");
                await services.GetRequiredService<INodePublisher>().PublishAsync(message, Lane.Transaction);
                sent++;
            }
        }

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

    // ---- master data ----

    private async Task<int> PublishMasterAsync(ChandleryDbContext db, List<SyncDirty> notes, CancellationToken ct)
    {
        var hub = services.GetRequiredService<HubEndpoint>();
        var scopes = await MasterData.LoadScopesAsync(db, ct);
        if (scopes.Count == 0) return 0;   // no vessel has an installation yet; a baseline sends everything when one is registered

        var sent = 0;
        foreach (var group in notes.GroupBy(n => (n.DocType, n.DocId)).OrderBy(g => g.Min(n => n.At)))
        {
            var master = MasterData.Find(group.Key.DocType)!;
            var latest = group.OrderBy(n => n.At).Last();
            var before = group.Where(n => n.Snapshot is not null).Select(n => JsonNode.Parse(n.Snapshot!)!.AsObject()).ToList();

            if (latest.Deleted)
            {
                var snapshot = before.Last();
                var tombstone = MasterData.Document(master, group.Key.DocId, latest.Version, snapshot, clock.Now(), null, deleted: true);
                sent += await SendAsync(hub, scopes.Where(s => MasterData.Includes(s, master.DocType, snapshot)), tombstone);
                continue;
            }

            var record = await FindAsync(db, master, group.Key.DocId, before, ct);
            if (record is null) continue;   // removed since; its own note carries the removal
            var entry = db.Entry(record);
            var payload = MasterData.ToJson(entry);
            var version = (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue!;
            var origin = (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue;
            var document = MasterData.Document(master, group.Key.DocId, version, payload, clock.Now(), origin);

            sent += await SendAsync(hub, scopes.Where(s => MasterData.Includes(s, master.DocType, payload)), document);

            // A vessel that held the record before the change but not after it must drop it.
            var dropped = scopes.Where(s => !MasterData.Includes(s, master.DocType, payload) && before.Any(b => MasterData.Includes(s, master.DocType, b)));
            sent += await SendAsync(hub, dropped, document with { Deleted = true });

            if (master.Clr == typeof(Vessel)) sent += await RescopeAsync(db, hub, scopes, group.Key.DocId, before, payload, ct);
        }
        return sent;
    }

    private static async Task<object?> FindAsync(ChandleryDbContext db, MasterType master, Guid docId, List<JsonObject> snapshots, CancellationToken ct)
    {
        var type = db.Model.FindEntityType(master.Clr)!;
        if (MasterData.HasSingleGuidKey(type)) return await db.FindAsync(master.Clr, [docId], ct);

        // Records keyed by several columns: the note kept the key.
        var snapshot = snapshots.FirstOrDefault();
        if (snapshot is null) return null;
        var key = type.FindPrimaryKey()!.Properties.Select(p => MasterData.TryRead(p, snapshot, out var v) ? v : null).ToArray();
        return key.Any(k => k is null) ? null : await db.FindAsync(master.Clr, key, ct);
    }

    /// <summary>Queue a document for every node among the scopes (a node once, even when it runs several vessels).</summary>
    private async Task<int> SendAsync(HubEndpoint hub, IEnumerable<MasterData.VesselScope> scopes, SyncDocument document)
    {
        var sent = 0;
        foreach (var nodeId in scopes.Select(s => s.NodeId).Distinct())
        {
            await hub.PublishToNodeAsync(nodeId, document, Lane.Master);
            sent++;
        }
        return sent;
    }

    /// <summary>
    /// A vessel moved to another group: it now needs the groups, policies and switches of its new branch and must forget those of
    /// the old one. These rows themselves did not change, so their versions are moved on here, otherwise a vessel that once
    /// forgot a row would ignore it coming back.
    /// </summary>
    private async Task<int> RescopeAsync(ChandleryDbContext db, HubEndpoint hub, List<MasterData.VesselScope> scopes, Guid vesselId,
        List<JsonObject> before, JsonObject after, CancellationToken ct)
    {
        var oldGroup = before.Count > 0 ? MasterData.ReadGuid(before[0], "GroupId") : null;
        var newGroup = MasterData.ReadGuid(after, "GroupId");
        if (oldGroup == newGroup) return 0;
        var scope = scopes.FirstOrDefault(s => s.VesselId == vesselId);
        if (scope is null) return 0;

        var parents = await db.VesselGroups.Select(g => new { g.Id, g.ParentId }).ToDictionaryAsync(g => g.Id, g => g.ParentId, ct);
        var oldChain = MasterData.ChainOf(parents, oldGroup);
        var gained = scope.Groups.Except(oldChain).ToHashSet();
        var lost = oldChain.Except(scope.Groups).ToHashSet();

        var sent = 0;
        foreach (var master in new[] { MasterData.Find(nameof(VesselGroup))!, MasterData.Find(nameof(VesselPolicy))!, MasterData.Find(nameof(VesselModuleSwitch))! })
        {
            foreach (var record in await MasterData.LoadAllAsync(db, master, ct))
            {
                var entry = db.Entry(record);
                var payload = MasterData.ToJson(entry);
                var groupColumn = master.Clr == typeof(VesselGroup) ? "Id" : "GroupId";
                if (MasterData.ReadGuid(payload, groupColumn) is not { } group) continue;
                var drop = lost.Contains(group);
                if (!drop && !gained.Contains(group)) continue;

                var version = (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue! + 1;
                entry.Property(ChandleryDbContext.SyncVersion).CurrentValue = version;
                payload[ChandleryDbContext.SyncVersion] = version.ToString();
                var docId = MasterData.DocIdOf(entry.Metadata, p => entry.Property(p.Name).CurrentValue);
                var document = MasterData.Document(master, docId, version, payload, clock.Now(), (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue, deleted: drop);
                await hub.PublishToNodeAsync(scope.NodeId, document, Lane.Master);
                sent++;
            }
        }
        return sent;
    }

    /// <summary>
    /// Queue everything a vessel is entitled to, parents before children: how a new vessel starts from an empty database, and how
    /// an operator refills one that lost its data. Records the vessel already holds at that version are skipped by sync itself.
    /// </summary>
    public async Task<int> BaselineAsync(string nodeId, CancellationToken ct = default)
    {
        var hub = services.GetRequiredService<HubEndpoint>();
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Node.SyncEnabled = node.SyncEnabled;
        db.ApplyingSync = true;

        var scopes = (await MasterData.LoadScopesAsync(db, ct)).Where(s => s.NodeId == nodeId).ToList();
        if (scopes.Count == 0) return 0;

        var queued = 0;
        foreach (var master in MasterData.Types)
        {
            var type = db.Model.FindEntityType(master.Clr)!;
            var records = new List<JsonObject>();
            foreach (var record in await MasterData.LoadAllAsync(db, master, ct))
            {
                var payload = MasterData.ToJson(db.Entry(record));
                if (scopes.Any(s => MasterData.Includes(s, master.DocType, payload))) records.Add(payload);
            }

            foreach (var payload in MasterData.ParentsFirst(type, records))
            {
                var docId = MasterData.DocIdOf(type, p => MasterData.TryRead(p, payload, out var v) ? v : null);
                _ = MasterData.TryRead(type.FindProperty(ChandleryDbContext.SyncVersion)!, payload, out var version);
                _ = MasterData.TryRead(type.FindProperty(ChandleryDbContext.OriginNodeId)!, payload, out var origin);
                var document = MasterData.Document(master, docId, Convert.ToInt64(version), payload, clock.Now(), origin as Guid?);
                await hub.PublishToNodeAsync(nodeId, document, Lane.Master);
                queued++;
            }
            db.ChangeTracker.Clear();
        }
        log.LogInformation("Queued {Count} master data documents for node {Node}.", queued, nodeId);
        return queued;
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

/// <summary>Lets the fleet console refill a vessel with its master data.</summary>
public sealed class ChandleryBaselineProvider(SyncPublisher publisher) : IBaselineProvider
{
    public Task<int> BaselineAsync(string nodeId, HubEndpoint hub, CancellationToken ct = default) => publisher.BaselineAsync(nodeId, ct);
}
