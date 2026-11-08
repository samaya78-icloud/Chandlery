using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>The answers each scale allows, and which of them are exceptions unless a criterion says otherwise.</summary>
public static class TankScales
{
    public static readonly IReadOnlyList<string> Rating = ["Good", "Fair", "Bad", "NI", "NA"];
    public static readonly IReadOnlyList<string> YesNoNa = ["Yes", "No", "NA"];
    public static readonly IReadOnlyList<string> PercentBucket = ["0-25", "26-50", "51-75", "76-100", "NA"];

    public static IReadOnlyList<string> Values(ScaleKind scale) => scale switch
    {
        ScaleKind.Rating => Rating, ScaleKind.YesNoNa => YesNoNa, _ => PercentBucket,
    };

    public static bool IsValid(ScaleKind scale, string? value) => value is not null && Values(scale).Contains(value);

    /// <summary>Fair and Bad are exceptions on a rating; the other scales have none unless a criterion lists some.</summary>
    public static string DefaultExceptionValues(ScaleKind scale) => scale == ScaleKind.Rating ? "Fair,Bad" : "";

    public static HashSet<string> ExceptionSet(string? exceptionValues)
        => (exceptionValues ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsException(ScaleKind scale, string? exceptionValues, string? value)
        => !string.IsNullOrEmpty(value) && ExceptionSet(exceptionValues).Contains(value);

    /// <summary>A number for charts: higher is better. Not-inspected and not-applicable answers have none.</summary>
    public static int? Score(ScaleKind scale, string? value) => (scale, value) switch
    {
        (ScaleKind.Rating, "Good") => 3, (ScaleKind.Rating, "Fair") => 2, (ScaleKind.Rating, "Bad") => 1,
        (ScaleKind.PercentBucket, "76-100") => 4, (ScaleKind.PercentBucket, "51-75") => 3, (ScaleKind.PercentBucket, "26-50") => 2, (ScaleKind.PercentBucket, "0-25") => 1,
        (ScaleKind.YesNoNa, "Yes") => 2, (ScaleKind.YesNoNa, "No") => 1,
        _ => null,
    };

    /// <summary>Words in a comment the reviewer should notice.</summary>
    public static readonly IReadOnlyList<string> ReviewKeywords = ["rust", "anode", "crack", "pitting", "corrosion", "leak", "buckling", "hole", "deform"];
}

/// <summary>Where an inspection stands, worked out from its submission count and its reviews (the same on both sides).</summary>
public static class TankStatus
{
    /// <summary>
    /// Draft until first submitted. Then the latest review of the current submission decides: accepted means Reviewed, returned means Returned, started means InReview;
    /// with none it is Submitted. A review of an earlier submission is ignored.
    /// </summary>
    public static InspectionStatus Of(int submissionCount, IEnumerable<TankInspectionReview> reviews)
    {
        if (submissionCount <= 0) return InspectionStatus.Draft;
        var latest = reviews.Where(r => r.ForSubmission == submissionCount).OrderByDescending(r => r.At).ThenByDescending(r => r.Id).FirstOrDefault();
        return latest?.Decision switch
        {
            ReviewDecision.Accepted => InspectionStatus.Reviewed,
            ReviewDecision.Returned => InspectionStatus.Returned,
            ReviewDecision.Started => InspectionStatus.InReview,
            _ => InspectionStatus.Submitted,
        };
    }

    /// <summary>Only a draft or a returned inspection can be changed; anything submitted or reviewed is as it was left.</summary>
    public static bool IsEditable(InspectionStatus status) => status is InspectionStatus.Draft or InspectionStatus.Returned;

    /// <summary>The results with an exception answer.</summary>
    public static List<TankInspectionResult> Exceptions(IEnumerable<TankInspectionResult> results)
        => results.Where(r => TankScales.IsException(r.Scale, r.ExceptionValues, r.Value)).ToList();

    /// <summary>The problems that stop a submission: every answer must be one of its scale's values (NI and NA are answers).</summary>
    public static List<string> SubmissionProblems(IEnumerable<TankInspectionResult> results)
    {
        var problems = new List<string>();
        foreach (var r in results.OrderBy(r => r.SortOrder))
        {
            if (string.IsNullOrEmpty(r.Value)) problems.Add($"{r.CriterionName}: not answered.");
            else if (!TankScales.IsValid(r.Scale, r.Value)) problems.Add($"{r.CriterionName}: \"{r.Value}\" is not an answer on this scale.");
        }
        return problems;
    }
}

/// <summary>The starter checklists, built from the criteria lists in spec 08. The office edits them freely afterwards.</summary>
public static class TankStandardTemplates
{
    private static readonly string[] Fittings = ["Ladders and railings", "Cargo and ballast pipes", "Stripping lines", "Draft gauge", "Hydraulic clamps", "Heating coils", "Reach rods", "Tank-cleaning machines", "Suction valves", "Couplings"];
    private static readonly string[] Structure = ["Buckling", "Side shell", "Bottom plate", "Bellmouth", "Deckhead", "Longitudinals", "Bulkheads", "Crossbeams", "Stringers", "Web frames", "Girders"];
    private static readonly string[] Reports = ["Thickness loss", "Pitting", "Cracks"];

    /// <summary>The internal fittings that make sense for a kind of tank.</summary>
    public static IReadOnlyList<string> FittingsFor(TankType type) => type switch
    {
        TankType.Cargo => Fittings,
        TankType.Slops => Fittings,
        TankType.Ballast => Fittings.Where(f => f is not ("Heating coils" or "Tank-cleaning machines" or "Hydraulic clamps")).ToList(),
        TankType.FreshWater => ["Ladders and railings", "Stripping lines", "Draft gauge", "Suction valves", "Couplings"],
        TankType.VoidSpace => ["Ladders and railings"],
        _ => ["Ladders and railings", "Suction valves", "Couplings"],
    };

    public static IReadOnlyList<string> TypeSpecificFor(TankType type) => type == TankType.Cargo
        ? ["Insulation (where fitted)", "Cold spots", "Anti-float, anti-pitching and anti-rolling devices (where fitted)", "Dome space (gas carriers)"]
        : [];

    public static string NameFor(TankType type) => type switch
    {
        TankType.Cargo => "Cargo tank inspection", TankType.Ballast => "Ballast tank inspection", TankType.Slops => "Slop tank inspection",
        TankType.FreshWater => "Fresh water tank inspection", TankType.VoidSpace => "Void space and cofferdam inspection", _ => "Tank inspection",
    };

    /// <summary>The criteria of a standard template, in order: (group, name, scale).</summary>
    public static List<(CriterionGroup Group, string Name, ScaleKind Scale)> CriteriaFor(TankType type)
    {
        var list = new List<(CriterionGroup, string, ScaleKind)>
        {
            (CriterionGroup.VariableData, "Condition of coating", ScaleKind.Rating),
            (CriterionGroup.VariableData, "Cathodic condition", ScaleKind.PercentBucket),
        };
        list.AddRange(FittingsFor(type).Select(f => (CriterionGroup.InternalFittings, f, ScaleKind.Rating)));
        list.AddRange(Structure.Select(s => (CriterionGroup.Structure, s, ScaleKind.Rating)));
        list.AddRange(Reports.Select(r => (CriterionGroup.Reports, r, ScaleKind.Rating)));
        list.AddRange(TypeSpecificFor(type).Select(t => (CriterionGroup.TypeSpecific, t, ScaleKind.Rating)));
        return list;
    }
}
