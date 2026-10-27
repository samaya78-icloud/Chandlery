namespace Chandlery.Models;

/// <summary>A unit of measure a company buys in (Pcs, Drum, Litre...). Items and document lines refer to it by its code.</summary>
public class Uom
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Disabled units stay on old documents but cannot be chosen on new ones.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// "1 <see cref="FromUomId"/> = <see cref="Factor"/> <see cref="ToUomId"/>". The reverse direction is implied, and chains work
/// (Drum to Litre and Litre to mL gives Drum to mL). A conversion with an item only applies to that item (a paint drum is not an oil drum).
/// </summary>
public class UomConversion
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int FromUomId { get; set; }
    public int ToUomId { get; set; }
    public decimal Factor { get; set; }

    /// <summary>Null for a general conversion; set when the factor is only true for one item.</summary>
    public int? ItemId { get; set; }
}

/// <summary>Works out how many of one unit make another, from a company's conversion table.</summary>
public class UomConverter
{
    private readonly Dictionary<string, Dictionary<string, decimal>> _general = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Dictionary<string, Dictionary<string, decimal>>> _byItem = new();

    public static UomConverter Empty { get; } = new();

    /// <summary>Adds one stored conversion (and its reverse). Codes are matched ignoring case.</summary>
    public void Add(string from, string to, decimal factor, int? itemId)
    {
        if (factor <= 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        var graph = itemId is null ? _general : _byItem.TryGetValue(itemId.Value, out var g) ? g : _byItem[itemId.Value] = new(StringComparer.OrdinalIgnoreCase);
        Link(graph, from, to, factor);
        Link(graph, to, from, 1m / factor);
    }

    private static void Link(Dictionary<string, Dictionary<string, decimal>> graph, string from, string to, decimal factor)
    {
        if (!graph.TryGetValue(from, out var edges)) graph[from] = edges = new(StringComparer.OrdinalIgnoreCase);
        edges[to] = factor;
    }

    /// <summary>How many <paramref name="to"/> make one <paramref name="from"/>, or null when no chain of conversions connects them.</summary>
    public decimal? Factor(string? from, string? to, int? itemId = null)
    {
        from = (from ?? "").Trim();
        to = (to ?? "").Trim();
        if (from == "" || to == "") return null;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 1m;

        // The item's own conversions win over a general one for the same pair; everything else falls back to the general table.
        var item = itemId.HasValue && _byItem.TryGetValue(itemId.Value, out var own) ? own : null;
        IEnumerable<KeyValuePair<string, decimal>> Edges(string at)
        {
            var itemEdges = item is not null && item.TryGetValue(at, out var ie) ? ie : null;
            if (itemEdges is not null)
                foreach (var e in itemEdges) yield return e;
            if (_general.TryGetValue(at, out var ge))
                foreach (var e in ge)
                    if (itemEdges is null || !itemEdges.ContainsKey(e.Key)) yield return e;
        }

        // Fewest hops first, so a direct conversion is always preferred to a longer chain.
        var best = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [from] = 1m };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var (next, factor) in Edges(at))
            {
                if (best.ContainsKey(next)) continue;
                best[next] = best[at] * factor;
                if (string.Equals(next, to, StringComparison.OrdinalIgnoreCase)) return Math.Round(best[next], 8);
                queue.Enqueue(next);
            }
        }
        return null;
    }
}

/// <summary>The starter units and conversions every new company gets (all editable).</summary>
public static class StarterUoms
{
    public static readonly (string Code, string Name)[] Units =
    {
        ("Pcs", "Pieces"), ("Set", "Set"), ("Pair", "Pair"), ("Box", "Box"), ("Carton", "Carton"), ("Bag", "Bag"), ("Can", "Can"),
        ("Coil", "Coil"), ("Roll", "Roll"), ("Drum", "Drum"), ("Litre", "Litre"), ("mL", "Millilitre"), ("Gallon", "Gallon (US)"),
        ("kg", "Kilogram"), ("g", "Gram"), ("MT", "Metric tonne"), ("m", "Metre")
    };

    /// <summary>1 From = Factor To. The Drum is the common 205 litre oil drum; override it per item where a drum holds something else.</summary>
    public static readonly (string From, string To, decimal Factor)[] Conversions =
    {
        ("Drum", "Litre", 205m), ("Litre", "mL", 1000m), ("Gallon", "Litre", 3.78541m), ("kg", "g", 1000m), ("MT", "kg", 1000m)
    };
}
