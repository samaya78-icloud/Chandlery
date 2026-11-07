namespace Chandlery.Models;

/// <summary>
/// One row describing this installation: its permanent node id (the shore hub, or a vessel) and its profile.
/// Every record stamps the node that created it, which is what later lets sync tell where a record was born.
/// </summary>
public class NodeInfo
{
    /// <summary>The permanent node id. Never changes, even if a vessel is renamed.</summary>
    public Guid Id { get; set; }

    /// <summary>"Shore" or "Vessel".</summary>
    public string Profile { get; set; } = "Shore";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>The last number given out for one (scope, document type, year, lane). Incremented atomically.</summary>
public class NumberSequence
{
    /// <summary>The vessel short code, or the company's office code for documents without a vessel.</summary>
    public string Scope { get; set; } = "";
    public string Type { get; set; } = "";
    public int Year { get; set; }

    /// <summary>"" for documents created onboard, "H" for documents created ashore, so both sides can number offline without colliding.</summary>
    public string Lane { get; set; } = "";
    public int LastValue { get; set; }
}

public enum DocType
{
    Requisition,
    Rfq,
    PurchaseOrder,
    Delivery,
    Invoice
}

/// <summary>
/// Who this installation is: its node id and profile, and whether sync is switched on. One instance per installation,
/// handed to every database context. (Tests run a shore and a vessel side by side, so it is not a static.)
/// </summary>
public sealed class NodeIdentity
{
    public Guid? Id { get; private set; }
    public string Profile { get; private set; } = "Shore";
    public bool IsVessel => Profile == "Vessel";

    /// <summary>When on, every change to a synced document leaves a note for the sync publisher.</summary>
    public bool SyncEnabled { get; set; }

    /// <summary>The sync id of this node: how the hub knows it.</summary>
    public string? SyncNodeId => Id?.ToString("N");

    public void Set(NodeInfo node)
    {
        Id = node.Id;
        Profile = node.Profile;
    }
}

/// <summary>The installation-wide identity used by the running application.</summary>
public static class NodeContext
{
    public static NodeIdentity Current { get; } = new();
    public static Guid? NodeId => Current.Id;
    public static string Profile => Current.Profile;
    public static void Set(NodeInfo node) => Current.Set(node);
}

/// <summary>Which side of a synced document may change it right now.</summary>
public static class SyncStages
{
    public const string Vessel = "vessel";
    public const string Office = "office";
}

/// <summary>
/// A note that a synced document changed and has not yet been handed to the sync engine. Written in the same transaction as the
/// change, so a crash can never lose a change; the publisher turns notes into messages and removes them.
/// </summary>
public class SyncDirty
{
    public Guid Id { get; set; }
    public string DocType { get; set; } = "";
    public Guid DocId { get; set; }
    public DateTime At { get; set; }

    /// <summary>The record was removed. <see cref="Version"/> is the version the removal travels under.</summary>
    public bool Deleted { get; set; }
    public int Version { get; set; }

    /// <summary>
    /// The record as it was before the save, as JSON. Kept for removals and for changes that move a record between groups or vessels
    /// (to tell which vessels must drop it), and for records keyed by several columns (to find them again).
    /// </summary>
    public string? Snapshot { get; set; }
}
