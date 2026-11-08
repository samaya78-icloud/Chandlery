using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>How a tank stands according to its latest submitted inspection. Worked out, never stored.</summary>
public enum TankHealth { NotInspected, Clean, Exceptions, Bad }

/// <summary>Names and colours for showing tanks, kept in one place so the plan, the list and the reports agree.</summary>
public static class TankLook
{
    public static string Label(TankType type) => type switch
    {
        TankType.FreshWater => "Fresh water",
        TankType.VoidSpace => "Void space",
        _ => type.ToString(),
    };

    public static string Label(CriterionGroup group) => group switch
    {
        CriterionGroup.VariableData => "Variable data",
        CriterionGroup.InternalFittings => "Internal fittings",
        CriterionGroup.Reports => "Reports",
        CriterionGroup.TypeSpecific => "Type-specific",
        _ => "Structure",
    };

    /// <summary>What an answer on a checklist means, for a tooltip.</summary>
    public static string Meaning(string value) => value switch
    {
        "NI" => "Not inspected",
        "NA" => "Not applicable",
        _ => value,
    };

    public static string Label(TankHealth health) => health switch
    {
        TankHealth.NotInspected => "Not inspected",
        TankHealth.Clean => "No exceptions",
        TankHealth.Exceptions => "Exceptions",
        _ => "Bad rating",
    };

    /// <summary>Fill colour for a tank drawn by type (readable in light and dark themes).</summary>
    public static string Colour(TankType type) => type switch
    {
        TankType.Cargo => "#6366f1",
        TankType.Ballast => "#0ea5e9",
        TankType.Slops => "#f59e0b",
        TankType.FreshWater => "#14b8a6",
        TankType.VoidSpace => "#94a3b8",
        _ => "#a855f7",
    };

    public static string Colour(TankHealth health) => health switch
    {
        TankHealth.NotInspected => "#94a3b8",
        TankHealth.Clean => "#10b981",
        TankHealth.Exceptions => "#f59e0b",
        _ => "#f43f5e",
    };

    public static TankHealth HealthOf(bool inspected, int exceptionCount, bool hasBadRating)
        => !inspected ? TankHealth.NotInspected : hasBadRating ? TankHealth.Bad : exceptionCount > 0 ? TankHealth.Exceptions : TankHealth.Clean;
}

/// <summary>A tank with what its latest submitted inspection said, for the plan and the list.</summary>
public record TankPlanRow(Tank Tank, int InspectionCount, DateTime? LastInspectedOn, int ExceptionCount, bool HasBadRating)
{
    public TankHealth Health => TankLook.HealthOf(LastInspectedOn is not null, ExceptionCount, HasBadRating);
}

public record TankCopyResult(string Vessel, int Added, int Skipped, string? Problem = null);

/// <summary>The words a reviewer should notice in a comment (rust, crack, leak...). The company can change the list; blank means the standard one.</summary>
public static class TankKeywords
{
    public static List<string> Parse(string? text)
        => (text ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();

    /// <summary>The company's list, or the standard one when it has none.</summary>
    public static List<string> Effective(string? text) => Parse(text) is { Count: > 0 } own ? own : TankScales.ReviewKeywords.ToList();

    public static bool Contains(string? text, IReadOnlyList<string> keywords) => Highlight(text, keywords).Any(s => s.Match);

    /// <summary>
    /// Cuts the text into pieces, marking those that are a keyword (any case, also inside a longer word, so "crack" finds "cracking"). The pieces always
    /// join back into the original text.
    /// </summary>
    public static List<(string Text, bool Match)> Highlight(string? text, IReadOnlyList<string> keywords)
    {
        text ??= "";
        var spans = new List<(int Start, int End)>();
        foreach (var keyword in keywords.Where(k => k.Length > 0))
            for (var at = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(keyword, at + keyword.Length, StringComparison.OrdinalIgnoreCase))
                spans.Add((at, at + keyword.Length));
        var merged = new List<(int Start, int End)>();
        foreach (var span in spans.OrderBy(s => s.Start).ThenByDescending(s => s.End))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            else merged.Add(span);
        }
        var pieces = new List<(string, bool)>();
        var cursor = 0;
        foreach (var (start, end) in merged)
        {
            if (start > cursor) pieces.Add((text[cursor..start], false));
            pieces.Add((text[start..end], true));
            cursor = end;
        }
        if (cursor < text.Length) pieces.Add((text[cursor..], false));
        return pieces;
    }
}

/// <summary>An inspection waiting for the office, with the vessel it is from.</summary>
public record ReviewQueueRow(InspectionRow Row, string VesselName);

public record ReviewWaiting(int Count, int WithBadRating);
