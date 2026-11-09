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

        var aggregateNotes = notes.Where(n => Aggregates.Find(n.DocType) is not null).ToList();
        if (aggregateNotes.Count > 0) sent += await PublishAggregatesAsync(db, aggregateNotes, ct);

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

    // ---- purchase orders and deliveries ----

    private async Task<int> PublishAggregatesAsync(ChandleryDbContext db, List<SyncDirty> notes, CancellationToken ct)
    {
        var sent = 0;
        foreach (var group in notes.GroupBy(n => (n.DocType, n.DocId)).OrderBy(g => g.Min(n => n.At)))
        {
            var aggregate = Aggregates.Find(group.Key.DocType)!;
            var latest = group.OrderBy(n => n.At).Last();

            if (latest.Deleted)
            {
                var snapshot = JsonNode.Parse(latest.Snapshot!)!.AsObject();
                var stage = snapshot[nameof(PurchaseOrder.OwnerStage)]?.GetValue<string>() ?? SyncStages.Office;
                var tombstone = Aggregates.Document(aggregate, group.Key.DocId, latest.Version, snapshot, stage, clock.Now(), null, deleted: true);
                sent += await SendAggregateAsync(db, aggregate, snapshot, stage, tombstone, withSupplier: false, Lane.Transaction, ct);
                continue;
            }

            var root = await Aggregates.LoadAsync(db, aggregate, group.Key.DocId, ct);
            if (root is null) continue;   // removed since; its own note carries the removal
            var entry = db.Entry(root);
            var payload = Aggregates.ToJson(db, root);
            var document = Aggregates.Document(aggregate, group.Key.DocId, (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue!, payload,
                Aggregates.StageOf(root), clock.Now(), (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue);
            sent += await SendAggregateAsync(db, aggregate, payload, document.OwnerStage, document, withSupplier: aggregate.Clr == typeof(PurchaseOrder), LaneFor(root), ct);
        }
        return sent;
    }

    /// <summary>
    /// Onboard, only what the vessel owns goes up. Ashore, only what the office owns goes down, to the node that runs the vessel the
    /// order is for. An order is sent with its supplier, so the vessel can always show who it is from.
    /// </summary>
    private async Task<int> SendAggregateAsync(ChandleryDbContext db, AggregateType aggregate, JsonObject payload, string stage, SyncDocument document, bool withSupplier, Lane lane, CancellationToken ct)
    {
        if (node.IsVessel)
        {
            if (stage != SyncStages.Vessel) return 0;
            await services.GetRequiredService<INodePublisher>().PublishAsync(document, lane);
            return 1;
        }

        if (stage != SyncStages.Office) return 0;
        var vesselId = await VesselOfAsync(db, aggregate, payload, ct);
        if (vesselId is null) return 0;
        var vesselNode = await db.Vessels.Where(v => v.Id == vesselId).Select(v => v.NodeId).FirstOrDefaultAsync(ct);
        if (vesselNode is null) return 0;

        var hub = services.GetRequiredService<HubEndpoint>();
        var nodeId = vesselNode.Value.ToString("N");
        if (withSupplier && MasterData.ReadGuid(payload, nameof(PurchaseOrder.SupplierId)) is { } supplierId)
            await PushSupplierAsync(db, hub, nodeId, supplierId, ct);
        await hub.PublishToNodeAsync(nodeId, document, lane);
        return 1;
    }

    /// <summary>
    /// The lane a document travels on. An emergency purchase tells the office at once; an urgent task, and the entry that makes it urgent
    /// (its first entry, or a later change of priority), reach the other side ahead of routine documents.
    /// </summary>
    internal static Lane LaneFor(object root) => root switch
    {
        PurchaseOrder { IsEmergency: true } => Lane.Urgent,
        WorklistTask { Priority: WorklistPriority.Urgent } => Lane.Urgent,
        JobDeferralRequest { IsCritical: true } => Lane.Urgent,
        JobDeferralDecision { IsCritical: true } => Lane.Urgent,
        TankInspection { HasBadRating: true, SubmissionCount: > 0 } => Lane.Urgent,                  // a Bad rating reaches the office at once
        WorklistEntry { Type: WorklistEntryType.Created or WorklistEntryType.Priority } entry when SaysUrgent(entry.Payload) => Lane.Urgent,
        _ => Lane.Transaction,
    };

    private static bool SaysUrgent(string payload)
    {
        try { return JsonNode.Parse(payload)?["priority"]?.GetValue<string>() == nameof(WorklistPriority.Urgent); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The vessel a document belongs to. A quote belongs to the vessel only when the RFQ it answers was raised onboard: quotes on
    /// the office's own RFQs are never sent to a vessel.
    /// </summary>
    internal static async Task<Guid?> VesselOfAsync(ChandleryDbContext db, AggregateType aggregate, JsonObject payload, CancellationToken ct)
    {
        if (aggregate.Clr == typeof(PurchaseOrder)) return MasterData.ReadGuid(payload, nameof(PurchaseOrder.VesselId));
        if (aggregate.Clr == typeof(Rfq)) return MasterData.ReadGuid(payload, nameof(Rfq.VesselId));
        if (aggregate.Clr == typeof(WorklistTask)) return MasterData.ReadGuid(payload, nameof(WorklistTask.VesselId));
        if (aggregate.Clr == typeof(MachineryItem) || aggregate.Clr == typeof(PlannedJob) || aggregate.Clr == typeof(RunningHoursReading) || aggregate.Clr == typeof(JobCompletion)
            || aggregate.Clr == typeof(JobDeferralRequest) || aggregate.Clr == typeof(JobDeferralDecision) || aggregate.Clr == typeof(PlanChangeRequest) || aggregate.Clr == typeof(PlanChangeDecision) || aggregate.Clr == typeof(MachinerySpare)
            || aggregate.Clr == typeof(Tank) || aggregate.Clr == typeof(TankInspection) || aggregate.Clr == typeof(TankInspectionReview))
            return MasterData.ReadGuid(payload, nameof(PlannedJob.VesselId));
        if (aggregate.Clr == typeof(RestEntry) || aggregate.Clr == typeof(RestSignOff) || aggregate.Clr == typeof(RestBreachNote) || aggregate.Clr == typeof(VesselDayStatus) || aggregate.Clr == typeof(WorkArrangementTable) || aggregate.Clr == typeof(WorkArrangementSignOff))
            return MasterData.ReadGuid(payload, nameof(RestEntry.VesselId));
        if (aggregate.Clr == typeof(JobVerification))
        {
            var completionId = MasterData.ReadGuid(payload, nameof(JobVerification.CompletionId));
            return await db.JobCompletions.Where(c => c.Id == completionId).Select(c => (Guid?)c.VesselId).FirstOrDefaultAsync(ct);
        }
        if (aggregate.Clr == typeof(WorklistEntry))
        {
            var taskId = MasterData.ReadGuid(payload, nameof(WorklistEntry.TaskId));
            return await db.WorklistTasks.Where(t => t.Id == taskId).Select(t => (Guid?)t.VesselId).FirstOrDefaultAsync(ct);
        }
        if (aggregate.Clr == typeof(PoDispatch))
        {
            var dispatchOrderId = MasterData.ReadGuid(payload, nameof(PoDispatch.PurchaseOrderId));
            return await db.PurchaseOrders.Where(p => p.Id == dispatchOrderId).Select(p => p.VesselId).FirstOrDefaultAsync(ct);
        }
        if (aggregate.Clr == typeof(DeliveryOrder))
        {
            var orderId = MasterData.ReadGuid(payload, nameof(DeliveryOrder.PurchaseOrderId));
            return await db.PurchaseOrders.Where(p => p.Id == orderId).Select(p => p.VesselId).FirstOrDefaultAsync(ct);
        }
        var rfqId = MasterData.ReadGuid(payload, nameof(Quote.RfqId));
        var rfq = await db.Rfqs.Where(r => r.Id == rfqId).Select(r => new { r.VesselId, r.OwnerStage }).FirstOrDefaultAsync(ct);
        return rfq is { OwnerStage: SyncStages.Vessel } ? rfq.VesselId : null;
    }

    private async Task PushSupplierAsync(ChandleryDbContext db, HubEndpoint hub, string nodeId, Guid supplierId, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null) return;
        var entry = db.Entry(supplier);
        var master = MasterData.Find(typeof(Supplier))!;
        var document = MasterData.Document(master, supplier.Id, (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue!, MasterData.ToJson(entry), clock.Now(),
            (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue);
        await hub.PublishToNodeAsync(nodeId, document, Lane.Master);
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

            // A supplier newly linked to the company: its vessels need the supplier itself, which did not change.
            if (master.Clr == typeof(CompanySupplier) && MasterData.ReadGuid(payload, nameof(CompanySupplier.SupplierId)) is { } linked)
                foreach (var scope in scopes.Where(s => MasterData.Includes(s, master.DocType, payload)).Select(s => s.NodeId).Distinct())
                    await PushSupplierAsync(db, hub, scope, linked, ct);

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

        // Documents the office already holds for this vessel, so a vessel started (or rebuilt) from nothing has its orders too.
        foreach (var scope in scopes)
        {
            foreach (var requisition in await db.Requisitions.IgnoreQueryFilters().Include(r => r.Lines)
                         .Where(r => r.VesselId == scope.VesselId && r.OwnerStage == SyncStages.Office).ToListAsync(ct))
                if (await PublishRequisitionAsync(db, requisition)) queued++;
            db.ChangeTracker.Clear();

            var orders = await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.VesselId == scope.VesselId && p.OwnerStage == SyncStages.Office).ToListAsync(ct);
            foreach (var order in orders)
            {
                var po = Aggregates.Find(typeof(PurchaseOrder))!;
                var entry = db.Entry(order);
                var payload = Aggregates.ToJson(db, order);
                await PushSupplierAsync(db, hub, nodeId, order.SupplierId, ct);
                await hub.PublishToNodeAsync(nodeId, Aggregates.Document(po, order.Id, (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue!, payload,
                    SyncStages.Office, clock.Now(), (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue), Lane.Transaction);
                queued++;
            }

            var orderIds = orders.Select(o => o.Id).ToList();
            foreach (var delivery in await db.DeliveryOrders.IgnoreQueryFilters().Where(d => orderIds.Contains(d.PurchaseOrderId) && d.OwnerStage == SyncStages.Office).ToListAsync(ct))
            {
                var dto = Aggregates.Find(typeof(DeliveryOrder))!;
                var entry = db.Entry(delivery);
                await hub.PublishToNodeAsync(nodeId, Aggregates.Document(dto, delivery.Id, (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue!,
                    Aggregates.ToJson(db, delivery), SyncStages.Office, clock.Now(), (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue), Lane.Transaction);
                queued++;
            }
            db.ChangeTracker.Clear();
        }

        // The tasks and the maintenance plan the office already holds for this vessel (what the vessel needs to start from nothing).
        foreach (var scope in scopes)
        {
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.WorklistTasks.IgnoreQueryFilters().Where(t => t.VesselId == scope.VesselId && t.OwnerStage == SyncStages.Office), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.WorklistEntries.IgnoreQueryFilters().Where(e => e.OwnerStage == SyncStages.Office
                && db.WorklistTasks.IgnoreQueryFilters().Any(t => t.Id == e.TaskId && t.VesselId == scope.VesselId)), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.MachineryItems.IgnoreQueryFilters().Where(m => m.VesselId == scope.VesselId && m.OwnerStage == SyncStages.Office)
                .OrderBy(m => m.CreatedAt), ct, parentsFirst: true);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.PlannedJobs.IgnoreQueryFilters().Where(j => j.VesselId == scope.VesselId && j.OwnerStage == SyncStages.Office), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.Tanks.IgnoreQueryFilters().Where(t => t.VesselId == scope.VesselId && t.OwnerStage == SyncStages.Office), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.MachinerySpares.IgnoreQueryFilters().Where(m => m.VesselId == scope.VesselId && m.OwnerStage == SyncStages.Office), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.RunningHoursReadings.IgnoreQueryFilters().Where(r => r.VesselId == scope.VesselId && r.OwnerStage == SyncStages.Office), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.JobCompletions.IgnoreQueryFilters().Where(c => c.VesselId == scope.VesselId && c.OwnerStage == SyncStages.Office), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.JobVerifications.IgnoreQueryFilters().Where(v => v.VesselId == scope.VesselId && v.OwnerStage == SyncStages.Office), ct);
            queued += await BaselineOwnedAsync(db, hub, nodeId, db.WorkArrangementTables.IgnoreQueryFilters().Where(t => t.VesselId == scope.VesselId && t.OwnerStage == SyncStages.Office), ct);
        }
        log.LogInformation("Queued {Count} documents for node {Node}.", queued, nodeId);
        return queued;
    }

    /// <summary>Sends every record of a query, in batches so a plan of thousands of jobs does not sit in memory at once.</summary>
    private async Task<int> BaselineOwnedAsync<T>(ChandleryDbContext db, HubEndpoint hub, string nodeId, IQueryable<T> query, CancellationToken ct, bool parentsFirst = false) where T : class
    {
        var aggregate = Aggregates.Find(typeof(T))!;
        var queued = 0;
        const int Batch = 500;
        var ordered = query is IOrderedQueryable<T> ? query : query.OrderBy(e => EF.Property<Guid>(e, "Id"));
        var records = await ordered.AsNoTracking().ToListAsync(ct);
        if (parentsFirst) records = SortParentsFirst(records);
        for (var from = 0; from < records.Count; from += Batch)
        {
            foreach (var record in records.Skip(from).Take(Batch))
            {
                var entry = db.Attach(record);
                var payload = Aggregates.ToJson(db, record);
                await hub.PublishToNodeAsync(nodeId, Aggregates.Document(aggregate, (Guid)entry.Property("Id").CurrentValue!, (int)entry.Property(ChandleryDbContext.SyncVersion).CurrentValue!, payload,
                    Aggregates.StageOf(record), clock.Now(), (Guid?)entry.Property(ChandleryDbContext.OriginNodeId).CurrentValue), LaneFor(record));
                queued++;
            }
            db.ChangeTracker.Clear();
        }
        return queued;
    }

    /// <summary>Machinery items refer to their parent; a vessel must receive the parent first.</summary>
    private static List<T> SortParentsFirst<T>(List<T> items) where T : class
    {
        if (items is not List<MachineryItem> machinery) return items;
        var byId = machinery.ToDictionary(m => m.Id);
        var sorted = new List<MachineryItem>();
        var placed = new HashSet<Guid>();
        void Place(MachineryItem m)
        {
            if (!placed.Add(m.Id)) return;
            if (m.ParentId is { } parent && byId.TryGetValue(parent, out var p)) Place(p);
            sorted.Add(m);
        }
        foreach (var m in machinery) Place(m);
        return (List<T>)(object)sorted;
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
