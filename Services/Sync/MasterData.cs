using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Chandlery.Data;
using Chandlery.Models;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Chandlery.Services.Sync;

/// <summary>One kind of master record that the office sends to vessels. The document type is the class name.</summary>
public sealed record MasterType(string DocType, Type Clr);

/// <summary>
/// Master data: what the office maintains and every vessel holds a read-only copy of. The list below is the whole definition, in
/// dependency order (a record's parents come before it), and the rest is generic: a record travels as its stored properties
/// (read from the model, so adding a column needs no sync change), keyed by its id.
/// </summary>
public static class MasterData
{
    public const int SchemaVersion = 1;

    public static readonly IReadOnlyList<MasterType> Types =
    [
        Of<Company>(), Of<Supplier>(), Of<CompanySupplier>(), Of<Port>(), Of<Currency>(), Of<CompanyCurrency>(), Of<ExchangeRate>(), Of<VesselGroup>(), Of<Vessel>(), Of<CrewMember>(),
        Of<Department>(), Of<Category>(), Of<WorklistListItem>(), Of<MachineryClass>(), Of<JobTemplate>(), Of<TankChecklistTemplate>(), Of<TankCriterion>(), Of<DepartmentCategory>(), Of<Item>(), Of<Uom>(), Of<UomConversion>(),
        Of<VesselPolicy>(), Of<VesselModuleSwitch>(),
        Of<RestRuleSet>(),
    ];

    private static readonly Dictionary<string, MasterType> ByName = Types.ToDictionary(t => t.DocType);
    private static readonly Dictionary<Type, MasterType> ByClr = Types.ToDictionary(t => t.Clr);

    private static MasterType Of<T>() => new(typeof(T).Name, typeof(T));

    public static MasterType? Find(string docType) => ByName.GetValueOrDefault(docType);
    public static MasterType? Find(Type clr) => ByClr.GetValueOrDefault(clr);

    // Plain numbers travel as text so no database or language rounds a decimal; dates as UTC ISO-8601.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = null,
    };

    /// <summary>Columns that decide which vessel a record belongs to. When one changes, vessels that lost the record are told to drop it.</summary>
    private static readonly string[] ScopeColumns = ["GroupId", "VesselId"];

    // ---- identity ----

    public static bool HasSingleGuidKey(IEntityType type)
    {
        var key = type.FindPrimaryKey()!.Properties;
        return key.Count == 1 && key[0].ClrType == typeof(Guid);
    }

    /// <summary>The document id: the record's own id, or for records keyed by several columns a fixed id derived from the key.</summary>
    public static Guid DocIdOf(IEntityType type, Func<IProperty, object?> value)
    {
        var key = type.FindPrimaryKey()!.Properties;
        if (HasSingleGuidKey(type)) return (Guid)value(key[0])!;
        var text = type.ClrType.Name + "|" + string.Join("|", key.Select(k => Convert.ToString(value(k), CultureInfo.InvariantCulture)));
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    }

    // ---- reading and writing the stored properties ----

    /// <summary>All stored properties of a tracked record, including the sync columns, as a JSON object.</summary>
    public static JsonObject ToJson(EntityEntry entry, bool original = false)
    {
        var json = new JsonObject();
        foreach (var property in entry.Metadata.GetProperties())
        {
            var p = entry.Property(property.Name);
            json[property.Name] = JsonSerializer.SerializeToNode(original ? p.OriginalValue : p.CurrentValue, property.ClrType, Json);
        }
        return json;
    }

    /// <summary>Reads one property from a document. False when it is absent (or null for a column that cannot be null), so the stored value stays.</summary>
    public static bool TryRead(IProperty property, JsonObject payload, out object? value)
    {
        value = null;
        if (!payload.TryGetPropertyValue(property.Name, out var node)) return false;
        if (node is null) return property.IsNullable;
        value = node.Deserialize(property.ClrType, Json);
        if (value is DateTime date && date.Kind == DateTimeKind.Unspecified) value = DateTime.SpecifyKind(date, DateTimeKind.Utc);
        return true;
    }

    public static System.Text.Json.Nodes.JsonNode? ToNode(object? value, Type type) => JsonSerializer.SerializeToNode(value, type, Json);

    public static Guid? ReadGuid(JsonObject payload, string name)
        => payload.TryGetPropertyValue(name, out var node) && node is not null && Guid.TryParse(node.GetValue<string>(), out var id) ? id : null;

    // ---- what a save leaves behind for the publisher ----

    /// <summary>
    /// The note that goes with a saved master record. A removal also keeps the record as it was (so the publisher can tell which
    /// vessels held it), and so does a change that moves a record between groups or vessels; records keyed by several columns keep
    /// their key, since their document id alone cannot be turned back into it.
    /// </summary>
    public static SyncDirty NoteFor(EntityEntry entry, MasterType type, DateTime now)
    {
        var deleted = entry.State == EntityState.Deleted;
        var docId = DocIdOf(entry.Metadata, p => deleted ? entry.Property(p.Name).OriginalValue : entry.Property(p.Name).CurrentValue);
        var note = new SyncDirty { DocType = type.DocType, DocId = docId, At = now };

        if (deleted)
        {
            note.Deleted = true;
            note.Version = (int)entry.Property(ChandleryDbContext.SyncVersion).OriginalValue! + 1;
            note.Snapshot = ToJson(entry, original: true).ToJsonString();
        }
        else if (entry.State == EntityState.Modified && MovedBetweenScopes(entry))
        {
            note.Snapshot = ToJson(entry, original: true).ToJsonString();
        }
        else if (!HasSingleGuidKey(entry.Metadata))
        {
            note.Snapshot = ToJson(entry, original: entry.State == EntityState.Modified).ToJsonString();
        }
        return note;
    }

    private static bool MovedBetweenScopes(EntityEntry entry)
        => ScopeColumns.Any(name => entry.Metadata.FindProperty(name) is not null
            && !Equals(entry.Property(name).OriginalValue, entry.Property(name).CurrentValue));

    // ---- reading everything (the baseline) ----

    /// <summary>Every record of a type, tracked (the sync columns live in the tracker, not on the class).</summary>
    public static async Task<List<object>> LoadAllAsync(ChandleryDbContext db, MasterType type, CancellationToken ct = default)
    {
        var set = (IQueryable<object>)typeof(DbContext).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == nameof(DbContext.Set) && m.GetParameters().Length == 0 && m.IsGenericMethodDefinition)
            .MakeGenericMethod(type.Clr).Invoke(db, null)!;
        return await set.ToListAsync(ct);
    }

    /// <summary>Records that point at a record of the same type (categories, vessel groups) are listed parents first, so a vessel can store them as they arrive.</summary>
    public static List<JsonObject> ParentsFirst(IEntityType type, List<JsonObject> records)
    {
        var selfKey = type.GetForeignKeys().FirstOrDefault(fk => fk.PrincipalEntityType == type && fk.Properties.Count == 1);
        if (selfKey is null) return records;
        var parentOf = selfKey.Properties[0].Name;
        var byId = records.ToDictionary(r => ReadGuid(r, "Id")!.Value);

        int Depth(JsonObject record)
        {
            var depth = 0;
            for (var parent = ReadGuid(record, parentOf); parent is { } id && byId.TryGetValue(id, out var next) && depth < 50; parent = ReadGuid(next, parentOf))
                depth++;
            return depth;
        }

        return records.OrderBy(Depth).ToList();
    }

    // ---- who gets what ----

    /// <summary>What one vessel installation is entitled to hold: its company, itself, and the chain of vessel groups above it.</summary>
    public sealed record VesselScope(string NodeId, Guid CompanyId, Guid VesselId, Guid? GroupId, IReadOnlySet<Guid> Groups, IReadOnlySet<Guid> Suppliers);

    public static async Task<List<VesselScope>> LoadScopesAsync(ChandleryDbContext db, CancellationToken ct = default)
    {
        var vessels = await db.Vessels.Where(v => v.NodeId != null).Select(v => new { v.Id, v.CompanyId, v.GroupId, v.NodeId }).ToListAsync(ct);
        if (vessels.Count == 0) return [];
        var parents = await db.VesselGroups.Select(g => new { g.Id, g.ParentId }).ToDictionaryAsync(g => g.Id, g => g.ParentId, ct);
        var links = (await db.CompanySuppliers.Select(l => new { l.CompanyId, l.SupplierId }).ToListAsync(ct))
            .ToLookup(l => l.CompanyId, l => l.SupplierId);
        return vessels.Select(v => new VesselScope(v.NodeId!.Value.ToString("N"), v.CompanyId, v.Id, v.GroupId, ChainOf(parents, v.GroupId),
            links[v.CompanyId].ToHashSet())).ToList();
    }

    /// <summary>The group and every group above it.</summary>
    public static HashSet<Guid> ChainOf(IReadOnlyDictionary<Guid, Guid?> parents, Guid? group)
    {
        var chain = new HashSet<Guid>();
        for (var at = group; at is { } id && chain.Add(id); at = parents.GetValueOrDefault(id)) { }
        return chain;
    }

    /// <summary>
    /// Should this vessel hold this record? Company-wide catalogue data goes to every vessel of the company; the vessel itself, its
    /// groups and the policies written for them go only to it; nothing about another vessel ever does.
    /// </summary>
    public static bool Includes(VesselScope scope, string docType, JsonObject d)
    {
        var company = ReadGuid(d, "CompanyId");
        switch (docType)
        {
            case nameof(Port) or nameof(Currency): return true;
            case nameof(Company): return ReadGuid(d, "Id") == scope.CompanyId;
            case nameof(Supplier): return ReadGuid(d, "Id") is { } supplier && scope.Suppliers.Contains(supplier);
            case nameof(ExchangeRate): return company is null || company == scope.CompanyId;
            case nameof(VesselGroup): return ReadGuid(d, "Id") is { } g && scope.Groups.Contains(g);
            case nameof(Vessel): return ReadGuid(d, "Id") == scope.VesselId;
            case nameof(CrewMember): return company == scope.CompanyId && ReadGuid(d, "VesselId") == scope.VesselId;
            case nameof(VesselPolicy) or nameof(VesselModuleSwitch):
                if (company != scope.CompanyId) return false;
                return ReadGuid(d, "VesselId") is { } vessel
                    ? vessel == scope.VesselId
                    : ReadGuid(d, "GroupId") is { } group && scope.Groups.Contains(group);
            case nameof(RestRuleSet):      // the company's default, and the set written for this vessel; never another vessel's
                return company == scope.CompanyId && (ReadGuid(d, "VesselId") is not { } ruleVessel || ruleVessel == scope.VesselId);
            default: return company == scope.CompanyId;
        }
    }

    public static SyncDocument Document(MasterType type, Guid docId, long version, JsonObject payload, long hlc, Guid? origin, bool deleted = false)
        => new(type.DocType, docId, version, origin?.ToString("N") ?? "", hlc, SyncStages.Office, SchemaVersion, payload.ToJsonString(), deleted);
}
