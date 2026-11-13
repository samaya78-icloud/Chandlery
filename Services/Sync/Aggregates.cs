using System.Reflection;
using System.Text.Json.Nodes;
using Chandlery.Data;
using Chandlery.Models;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Chandlery.Services.Sync;

/// <summary>A document with children (a purchase order and its lines) that travels as one unit and has one owner at a time.</summary>
public sealed record AggregateType(string DocType, Type Clr);

/// <summary>
/// Documents that carry children and an owner: the generic counterpart of <see cref="MasterData"/> for purchase orders and
/// deliveries. A document is its root's stored properties plus every child collection (read from the model, so adding a column
/// needs no sync change). The owner (<c>OwnerStage</c>) is the side that created it; only the owner may change it. Values that
/// each side works out for itself are left out of the document ("local only").
/// </summary>
public static class Aggregates
{
    public const int SchemaVersion = 1;

    public static readonly IReadOnlyList<AggregateType> Types =
    [
        new(nameof(PurchaseOrder), typeof(PurchaseOrder)),
        new(nameof(DeliveryOrder), typeof(DeliveryOrder)),
        new(nameof(Rfq), typeof(Rfq)),
        new(nameof(Quote), typeof(Quote)),
        new(nameof(PoDispatch), typeof(PoDispatch)),
        new(nameof(WorklistTask), typeof(WorklistTask)),
        new(nameof(WorklistEntry), typeof(WorklistEntry)),
        new(nameof(MachineryItem), typeof(MachineryItem)),
        new(nameof(PlannedJob), typeof(PlannedJob)),
        new(nameof(RunningHoursReading), typeof(RunningHoursReading)),
        new(nameof(JobCompletion), typeof(JobCompletion)),
        new(nameof(JobVerification), typeof(JobVerification)),
        new(nameof(JobDeferralRequest), typeof(JobDeferralRequest)),
        new(nameof(JobDeferralDecision), typeof(JobDeferralDecision)),
        new(nameof(PlanChangeRequest), typeof(PlanChangeRequest)),
        new(nameof(PlanChangeDecision), typeof(PlanChangeDecision)),
        new(nameof(MachinerySpare), typeof(MachinerySpare)),
        new(nameof(Tank), typeof(Tank)),
        new(nameof(TankInspection), typeof(TankInspection)),
        new(nameof(TankInspectionReview), typeof(TankInspectionReview)),
        new(nameof(RestEntry), typeof(RestEntry)),
        new(nameof(RestSignOff), typeof(RestSignOff)),
        new(nameof(RestBreachNote), typeof(RestBreachNote)),
        new(nameof(VesselDayStatus), typeof(VesselDayStatus)),
        new(nameof(WorkArrangementTable), typeof(WorkArrangementTable)),
        new(nameof(WorkArrangementSignOff), typeof(WorkArrangementSignOff)),
        new(nameof(RiskAssessment), typeof(RiskAssessment)),
        new(nameof(RaSignOff), typeof(RaSignOff)),
        new(nameof(Incident), typeof(Incident)),
        new(nameof(IncidentEvent), typeof(IncidentEvent)),
        new(nameof(IncidentInvestigation), typeof(IncidentInvestigation)),
        new(nameof(IncidentExposure), typeof(IncidentExposure)),
        new(nameof(Project), typeof(Project)),
        new(nameof(ProjectStatusEvent), typeof(ProjectStatusEvent)),
        new(nameof(ProjectNote), typeof(ProjectNote)),
        new(nameof(ProjectLink), typeof(ProjectLink)),
        new(nameof(ProjectItemRequest), typeof(ProjectItemRequest)),
        new(nameof(ProjectItemDecision), typeof(ProjectItemDecision)),
        new(nameof(ChatMessage), typeof(ChatMessage)),
    ];

    /// <summary>Documents that are history: never changed or removed once written (a task only in the columns each side works out for itself).</summary>
    public static bool IsAppendOnly(Type clr) => clr == typeof(WorklistTask) || clr == typeof(WorklistEntry)
        || clr == typeof(RunningHoursReading) || clr == typeof(JobCompletion) || clr == typeof(JobVerification)
        || clr == typeof(JobDeferralRequest) || clr == typeof(JobDeferralDecision) || clr == typeof(PlanChangeRequest) || clr == typeof(PlanChangeDecision)
        || clr == typeof(TankInspectionReview) || clr == typeof(RaSignOff)
        || clr == typeof(ChatMessage)
        || clr == typeof(IncidentEvent) || clr == typeof(IncidentInvestigation) || clr == typeof(IncidentExposure)
        || clr == typeof(ProjectStatusEvent) || clr == typeof(ProjectNote) || clr == typeof(ProjectLink) || clr == typeof(ProjectItemRequest) || clr == typeof(ProjectItemDecision)
        || clr == typeof(RestEntry) || clr == typeof(RestSignOff) || clr == typeof(RestBreachNote) || clr == typeof(VesselDayStatus) || clr == typeof(WorkArrangementTable) || clr == typeof(WorkArrangementSignOff);

    private static readonly Dictionary<string, AggregateType> ByName = Types.ToDictionary(t => t.DocType);
    private static readonly Dictionary<Type, AggregateType> ByClr = Types.ToDictionary(t => t.Clr);

    public static AggregateType? Find(string docType) => ByName.GetValueOrDefault(docType);
    public static AggregateType? Find(Type clr) => ByClr.GetValueOrDefault(clr);

    /// <summary>Columns each side keeps for itself: the delivery status is recomputed from the deliveries held, and the invoice lock exists only at the office.</summary>
    private static readonly Dictionary<Type, string[]> LocalOnly = new()
    {
        [typeof(PurchaseOrder)] = [nameof(PurchaseOrder.DeliveryStatus), nameof(PurchaseOrder.ReviewedAt), nameof(PurchaseOrder.ReviewedByName)],
        [typeof(DeliveryOrder)] = [nameof(DeliveryOrder.InvoiceId)],
        [typeof(PlannedJob)] =
        [
            nameof(PlannedJob.LastDoneDate), nameof(PlannedJob.LastDoneHours), nameof(PlannedJob.NextDueDate), nameof(PlannedJob.DueBasis), nameof(PlannedJob.HoursEstimated),
            nameof(PlannedJob.HoursUnknown), nameof(PlannedJob.LastReadingDate), nameof(PlannedJob.GraceUntil), nameof(PlannedJob.DeferredUntil),
        ],
        [typeof(WorklistTask)] =
        [
            nameof(WorklistTask.Title), nameof(WorklistTask.Description), nameof(WorklistTask.Priority), nameof(WorklistTask.Status),
            nameof(WorklistTask.PlannedDate), nameof(WorklistTask.TargetDate), nameof(WorklistTask.AssigneeName), nameof(WorklistTask.PercentComplete),
            nameof(WorklistTask.DeferralCount), nameof(WorklistTask.CompletedAt), nameof(WorklistTask.VerifiedAt), nameof(WorklistTask.LastEntryAt),
        ],
    };

    public static string[] LocalColumns(Type clr) => LocalOnly.GetValueOrDefault(clr, []);

    // ---- who owns it ----

    public static string StageOf(object root) => root switch
    {
        PurchaseOrder p => p.OwnerStage,
        DeliveryOrder d => d.OwnerStage,
        Rfq r => r.OwnerStage,
        Quote q => q.OwnerStage,
        WorklistTask t => t.OwnerStage,
        WorklistEntry e => e.OwnerStage,
        MachineryItem mi => mi.OwnerStage,
        PlannedJob pj => pj.OwnerStage,
        RunningHoursReading rr => rr.OwnerStage,
        JobCompletion jc => jc.OwnerStage,
        JobVerification jv => jv.OwnerStage,
        JobDeferralRequest dq => dq.OwnerStage,
        JobDeferralDecision dd => dd.OwnerStage,
        PlanChangeRequest pq => pq.OwnerStage,
        PlanChangeDecision pd => pd.OwnerStage,
        MachinerySpare ms => ms.OwnerStage,
        Tank tk => tk.OwnerStage,
        TankInspection ti => ti.OwnerStage,
        TankInspectionReview tr => tr.OwnerStage,
        RestEntry re => re.OwnerStage,
        RestSignOff rs => rs.OwnerStage,
        RestBreachNote rb => rb.OwnerStage,
        VesselDayStatus vs => vs.OwnerStage,
        WorkArrangementTable wa => wa.OwnerStage,
        WorkArrangementSignOff ws => ws.OwnerStage,
        RiskAssessment ra => ra.OwnerStage,
        Incident inc => inc.OwnerStage,
        IncidentEvent incEvent => incEvent.OwnerStage,
        IncidentInvestigation incInvestigation => incInvestigation.OwnerStage,
        IncidentExposure incExposure => incExposure.OwnerStage,
        RaSignOff rs => rs.OwnerStage,
        Project pr => pr.OwnerStage,
        ProjectStatusEvent pe => pe.OwnerStage,
        ProjectNote pn => pn.OwnerStage,
        ProjectLink pl => pl.OwnerStage,
        ProjectItemRequest pq => pq.OwnerStage,
        ProjectItemDecision pd => pd.OwnerStage,
        ChatMessage cm => cm.OwnerStage,
        _ => SyncStages.Office,
    };

    public static void SetStage(object root, string stage)
    {
        if (root is PurchaseOrder p) p.OwnerStage = stage;
        else if (root is DeliveryOrder d) d.OwnerStage = stage;
        else if (root is Rfq r) r.OwnerStage = stage;
        else if (root is Quote q) q.OwnerStage = stage;
        else if (root is WorklistTask t) t.OwnerStage = stage;
        else if (root is WorklistEntry e) e.OwnerStage = stage;
        else if (root is MachineryItem item) item.OwnerStage = stage;
        else if (root is PlannedJob job) job.OwnerStage = stage;
        else if (root is RunningHoursReading reading) reading.OwnerStage = stage;
        else if (root is JobCompletion completion) completion.OwnerStage = stage;
        else if (root is JobVerification verification) verification.OwnerStage = stage;
        else if (root is JobDeferralRequest deferralRequest) deferralRequest.OwnerStage = stage;
        else if (root is JobDeferralDecision deferralDecision) deferralDecision.OwnerStage = stage;
        else if (root is PlanChangeRequest changeRequest) changeRequest.OwnerStage = stage;
        else if (root is PlanChangeDecision changeDecision) changeDecision.OwnerStage = stage;
        else if (root is MachinerySpare spare) spare.OwnerStage = stage;
        else if (root is Tank tank) tank.OwnerStage = stage;
        else if (root is TankInspection inspection) inspection.OwnerStage = stage;
        else if (root is TankInspectionReview tankReview) tankReview.OwnerStage = stage;
        else if (root is RestEntry restEntry) restEntry.OwnerStage = stage;
        else if (root is RestSignOff restSignOff) restSignOff.OwnerStage = stage;
        else if (root is RestBreachNote restNote) restNote.OwnerStage = stage;
        else if (root is VesselDayStatus dayStatus) dayStatus.OwnerStage = stage;
        else if (root is WorkArrangementTable arrangement) arrangement.OwnerStage = stage;
        else if (root is WorkArrangementSignOff arrangementSign) arrangementSign.OwnerStage = stage;
        else if (root is RiskAssessment assessment) assessment.OwnerStage = stage;
        else if (root is Incident incident) incident.OwnerStage = stage;
        else if (root is IncidentEvent incidentEvent) incidentEvent.OwnerStage = stage;
        else if (root is IncidentInvestigation investigation) investigation.OwnerStage = stage;
        else if (root is IncidentExposure exposure) exposure.OwnerStage = stage;
        else if (root is RaSignOff signOff) signOff.OwnerStage = stage;
        else if (root is Project project) project.OwnerStage = stage;
        else if (root is ProjectStatusEvent projectEvent) projectEvent.OwnerStage = stage;
        else if (root is ProjectNote projectNote) projectNote.OwnerStage = stage;
        else if (root is ProjectLink projectLink) projectLink.OwnerStage = stage;
        else if (root is ProjectItemRequest itemRequest) itemRequest.OwnerStage = stage;
        else if (root is ProjectItemDecision itemDecision) itemDecision.OwnerStage = stage;
        else if (root is ChatMessage chatMessage) chatMessage.OwnerStage = stage;
    }

    /// <summary>True when the only changes to this saved root are columns each side keeps for itself.</summary>
    public static bool OnlyLocalChanged(EntityEntry root)
        => root.State == EntityState.Modified
           && root.Properties.Where(p => p.IsModified).All(p => LocalColumns(root.Metadata.ClrType).Contains(p.Metadata.Name));

    // ---- reading ----

    public static JsonObject ToJson(ChandleryDbContext db, object root)
    {
        var entry = db.Entry(root);
        var json = MasterData.ToJson(entry);
        foreach (var local in LocalColumns(root.GetType())) json.Remove(local);

        foreach (var nav in entry.Metadata.GetNavigations().Where(n => n.IsCollection && n.PropertyInfo is not null))
        {
            var children = new JsonArray();
            foreach (var child in (System.Collections.IEnumerable?)nav.PropertyInfo!.GetValue(root) ?? Array.Empty<object>())
            {
                var item = new JsonObject();
                foreach (var p in ChildProperties(nav))
                    item[p.Name] = MasterData.ToNode(p.PropertyInfo!.GetValue(child), p.ClrType);
                children.Add(item);
            }
            json[nav.Name] = children;
        }
        return json;
    }

    /// <summary>The root as it was before a save, without children: enough to tell where a removal must go.</summary>
    public static JsonObject SnapshotOf(EntityEntry root)
    {
        var json = MasterData.ToJson(root, original: true);
        foreach (var local in LocalColumns(root.Metadata.ClrType)) json.Remove(local);
        return json;
    }

    private static IEnumerable<IProperty> ChildProperties(INavigation nav)
        => nav.TargetEntityType.GetProperties().Where(p => !p.IsForeignKey() && p.PropertyInfo is not null);   // a child with a real id of its own keeps it

    // ---- writing ----

    /// <summary>Copies a document onto a tracked root: its own columns, then its children replaced by the document's.</summary>
    public static void Apply(EntityEntry entry, JsonObject payload)
    {
        var local = LocalColumns(entry.Metadata.ClrType);
        foreach (var property in entry.Metadata.GetProperties())
            if (!local.Contains(property.Name) && MasterData.TryRead(property, payload, out var value))
                entry.Property(property.Name).CurrentValue = value;

        foreach (var nav in entry.Metadata.GetNavigations().Where(n => n.IsCollection && n.PropertyInfo is not null))
        {
            var list = (System.Collections.IList)nav.PropertyInfo!.GetValue(entry.Entity)!;
            var items = (payload[nav.Name] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var keyProperty = nav.TargetEntityType.FindPrimaryKey() is { Properties.Count: 1 } key && key.Properties[0].PropertyInfo is not null ? key.Properties[0] : null;

            if (keyProperty is null)
            {
                // Children without an id of their own (order lines, delivery lines) are simply replaced.
                list.Clear();
                foreach (var item in items) list.Add(Build(nav, item, Activator.CreateInstance(nav.TargetEntityType.ClrType)!));
                continue;
            }

            // Children with an id (RFQ lines are referred to by quotes) are matched by it and updated in place.
            var held = list.Cast<object>().ToDictionary(c => keyProperty.PropertyInfo!.GetValue(c)!);
            var keep = new HashSet<object>();
            foreach (var item in items)
            {
                MasterData.TryRead(keyProperty, item, out var id);
                if (id is null) continue;
                keep.Add(id);
                if (held.TryGetValue(id, out var current)) Build(nav, item, current);
                else
                {
                    // A new child that arrives with its id already set (a risk assessment hazard) must be told it is new, or the save would try to update a row that is not there.
                    var fresh = Build(nav, item, Activator.CreateInstance(nav.TargetEntityType.ClrType)!);
                    list.Add(fresh);
                    if (id is Guid newKey && newKey != Guid.Empty) entry.Context.Entry(fresh).State = EntityState.Added;
                }
            }
            for (var i = list.Count - 1; i >= 0; i--)
                if (!keep.Contains(keyProperty.PropertyInfo!.GetValue(list[i])!)) list.RemoveAt(i);
        }
    }

    private static object Build(INavigation nav, JsonObject item, object child)
    {
        foreach (var p in ChildProperties(nav))
            if (MasterData.TryRead(p, item, out var value))
                p.PropertyInfo!.SetValue(child, value);
        return child;
    }

    /// <summary>Loads a root with all its children (owned or not).</summary>
    public static async Task<object?> LoadAsync(ChandleryDbContext db, AggregateType type, Guid id, CancellationToken ct = default)
    {
        var root = await db.FindAsync(type.Clr, [id], ct);
        if (root is null) return null;
        var entry = db.Entry(root);
        foreach (var nav in entry.Metadata.GetNavigations().Where(n => n.IsCollection))
            await entry.Collection(nav.Name).LoadAsync(ct);
        return root;
    }

    // ---- what a save leaves behind ----

    public static SyncDirty NoteFor(EntityEntry root, AggregateType type, DateTime now)
    {
        var deleted = root.State == EntityState.Deleted;
        var note = new SyncDirty { DocType = type.DocType, DocId = (Guid)(deleted ? root.Property("Id").OriginalValue! : root.Property("Id").CurrentValue!), At = now };
        if (deleted)
        {
            note.Deleted = true;
            note.Version = (int)root.Property(ChandleryDbContext.SyncVersion).OriginalValue! + 1;
            note.Snapshot = SnapshotOf(root).ToJsonString();
        }
        return note;
    }

    public static SyncDocument Document(AggregateType type, Guid docId, long version, JsonObject payload, string stage, long hlc, Guid? origin, bool deleted = false)
        => new(type.DocType, docId, version, origin?.ToString("N") ?? "", hlc, stage, SchemaVersion, payload.ToJsonString(), deleted);
}
