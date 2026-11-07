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
    ];

    private static readonly Dictionary<string, AggregateType> ByName = Types.ToDictionary(t => t.DocType);
    private static readonly Dictionary<Type, AggregateType> ByClr = Types.ToDictionary(t => t.Clr);

    public static AggregateType? Find(string docType) => ByName.GetValueOrDefault(docType);
    public static AggregateType? Find(Type clr) => ByClr.GetValueOrDefault(clr);

    /// <summary>Columns each side keeps for itself: the delivery status is recomputed from the deliveries held, and the invoice lock exists only at the office.</summary>
    private static readonly Dictionary<Type, string[]> LocalOnly = new()
    {
        [typeof(PurchaseOrder)] = [nameof(PurchaseOrder.DeliveryStatus)],
        [typeof(DeliveryOrder)] = [nameof(DeliveryOrder.InvoiceId)],
    };

    public static string[] LocalColumns(Type clr) => LocalOnly.GetValueOrDefault(clr, []);

    // ---- who owns it ----

    public static string StageOf(object root) => root switch
    {
        PurchaseOrder p => p.OwnerStage,
        DeliveryOrder d => d.OwnerStage,
        Rfq r => r.OwnerStage,
        Quote q => q.OwnerStage,
        _ => SyncStages.Office,
    };

    public static void SetStage(object root, string stage)
    {
        if (root is PurchaseOrder p) p.OwnerStage = stage;
        else if (root is DeliveryOrder d) d.OwnerStage = stage;
        else if (root is Rfq r) r.OwnerStage = stage;
        else if (root is Quote q) q.OwnerStage = stage;
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
                else list.Add(Build(nav, item, Activator.CreateInstance(nav.TargetEntityType.ClrType)!));
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
