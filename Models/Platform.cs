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

/// <summary>This installation's node id, loaded once at startup and stamped onto every record that is saved.</summary>
public static class NodeContext
{
    public static Guid? NodeId { get; private set; }
    public static string Profile { get; private set; } = "Shore";

    public static void Set(NodeInfo node)
    {
        NodeId = node.Id;
        Profile = node.Profile;
    }
}
