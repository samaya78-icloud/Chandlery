using System.Text.Json;
using System.Text.Json.Serialization;
using Chandlery.Models;

namespace Chandlery.Services.Sync;

public static class SyncDocTypes
{
    public const string Requisition = "Requisition";

    /// <summary>A crew member's login as changed onboard, on its way to the office.</summary>
    public const string CrewCredential = "CrewCredential";
}

/// <summary>
/// A requisition and its lines as they travel between vessel and office. Canonical form: camelCase names, enums by name,
/// numbers written as strings (so no database or language rounds a decimal), dates as UTC ISO-8601.
/// </summary>
public sealed record RequisitionDocument(
    Guid Id,
    Guid CompanyId,
    string ReqnNumber,
    RfqType Type,
    Guid VesselId,
    Guid? DepartmentId,
    Guid? DeliveryPortId,
    Urgency Urgency,
    DateTime ReqnDate,
    DateTime? VesselEta,
    DateTime? VesselEtd,
    DateTime? EstDeliveryDate,
    bool IsBreakdown,
    string Remarks,
    RequisitionStatus Status,
    Guid? CreatedByUserId,
    string CreatedByName,
    DateTime CreatedAt,
    string OwnerStage,
    Guid? OriginNodeId,
    DateTime SyncCreatedAt,
    DateTime SyncUpdatedAt,
    int SyncVersion,
    IReadOnlyList<RequisitionLineDocument> Lines)
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static RequisitionDocument FromJson(string json)
        => JsonSerializer.Deserialize<RequisitionDocument>(json, Json) ?? throw new JsonException("Empty requisition document.");

    /// <summary>Wrap for the sync engine. The version is the requisition's own version counter.</summary>
    public Marine.Sync.SyncDocument ToSyncDocument(long hlc) => new(
        SyncDocTypes.Requisition, Id, SyncVersion, OriginNodeId?.ToString("N") ?? "", hlc, OwnerStage, SchemaVersion, ToJson());

    public static RequisitionDocument From(Requisition r, Guid? originNodeId, DateTime syncCreatedAt, DateTime syncUpdatedAt, int syncVersion)
        => new(r.Id, r.CompanyId, r.ReqnNumber, r.Type, r.VesselId, r.DepartmentId, r.DeliveryPortId, r.Urgency,
            Utc(r.ReqnDate), Utc(r.VesselEta), Utc(r.VesselEtd), Utc(r.EstDeliveryDate), r.IsBreakdown, r.Remarks, r.Status,
            r.CreatedByUserId, r.CreatedByName, Utc(r.CreatedAt), r.OwnerStage, originNodeId,
            Utc(syncCreatedAt), Utc(syncUpdatedAt), syncVersion,
            r.Lines.Select(l => new RequisitionLineDocument(l.Id, l.ItemId, l.Quantity, l.ApprovedQuantity, l.Uom, l.Description, l.DrawingNo)).ToList());

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? d) => d is null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);
}

public sealed record RequisitionLineDocument(
    Guid Id, Guid ItemId, decimal Quantity, decimal ApprovedQuantity, string Uom, string? Description, string? DrawingNo);

/// <summary>Who may change a requisition: the pure rules, and the guard the services call before changing one.</summary>
public static class RequisitionOwnership
{
    /// <summary>A vessel may not change a requisition the office now owns, and the office may not change one still being prepared onboard.</summary>
    public static void EnsureEditable(Data.ChandleryDbContext db, Requisition requisition)
    {
        if (!db.Node.SyncEnabled) return;
        if (db.Node.IsVessel && requisition.OwnerStage == SyncStages.Office)
            throw new InvalidOperationException("This requisition has been sent to the office; only the office can change it now.");
        if (!db.Node.IsVessel && requisition.OwnerStage == SyncStages.Vessel)
            throw new InvalidOperationException("This requisition is still with the vessel; it can be changed once the vessel has sent it.");
    }

    /// <summary>
    /// Should a document arriving from the other side replace what is held here?
    /// At the hub, only a vessel's first submission counts (a vessel edit after the office took over is ignored).
    /// On the vessel, anything the office sends wins over a requisition still in vessel hands, and otherwise a newer office version wins.
    /// </summary>
    public static bool ShouldApply(bool receivingAtHub, string? localStage, int? localVersion, string incomingStage, int incomingVersion)
    {
        if (receivingAtHub)
        {
            if (incomingStage != SyncStages.Vessel) return false;
            return localStage is null || (localStage == SyncStages.Vessel && incomingVersion > localVersion);
        }
        if (incomingStage != SyncStages.Office) return false;
        return localStage is null || localStage == SyncStages.Vessel || incomingVersion > localVersion;
    }
}
