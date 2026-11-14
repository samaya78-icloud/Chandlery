using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>What one yard quoted for each item, with its total. Missing items are null: the yard did not price them.</summary>
public record TenderColumn(Guid TenderId, Guid YardId, string YardName, TenderStatus Status, Dictionary<Guid, decimal> ByItem, decimal OtherCharges, decimal Total, int Unpriced, bool IsLowest);

/// <summary>One specification item across the yards: what each quoted, and which yard is cheapest for it.</summary>
public record TenderRow(Guid ItemId, string Group, string Description, Dictionary<Guid, decimal> ByTender, Guid? CheapestTenderId);

public record TenderComparison(List<TenderRow> Rows, List<TenderColumn> Columns);

/// <summary>Pure rules for tenders, phases and the documents checklist, so the screens and the tests read the same numbers.</summary>
public static class ProjectPlanningRules
{
    // ---- tenders ----

    /// <summary>
    /// Lays the quotes side by side. Only yards that have quoted are shown. A yard's total is its priced items plus its other charges; the lowest is picked only
    /// from yards that priced every item, since a yard that left things out is not comparable. An item's cheapest yard is the lowest price among those that priced it.
    /// </summary>
    public static TenderComparison Compare(IReadOnlyList<ProjectItem> items, IReadOnlyList<(ProjectTender Tender, string YardName, List<ProjectTenderLine> Lines)> tenders)
    {
        var quoted = tenders.Where(t => t.Tender.Status is TenderStatus.Quoted or TenderStatus.Awarded or TenderStatus.NotAwarded).OrderBy(t => t.YardName, StringComparer.OrdinalIgnoreCase).ToList();
        var live = items.Where(i => i.Status != ProjectItemStatus.Dropped).OrderBy(i => i.SortOrder).ToList();
        var liveIds = live.Select(i => i.Id).ToHashSet();

        var columns = quoted.Select(t =>
        {
            var byItem = t.Lines.Where(l => liveIds.Contains(l.ItemId)).ToDictionary(l => l.ItemId, l => l.AmountUsd);
            return new TenderColumn(t.Tender.Id, t.Tender.YardId, t.YardName, t.Tender.Status, byItem, t.Tender.OtherChargesUsd, byItem.Values.Sum() + t.Tender.OtherChargesUsd, live.Count - byItem.Count, false);
        }).ToList();

        var complete = columns.Where(c => c.Unpriced == 0).ToList();
        var lowest = complete.Count == 0 ? (decimal?)null : complete.Min(c => c.Total);
        columns = columns.Select(c => c with { IsLowest = lowest is { } l && c.Unpriced == 0 && c.Total == l }).ToList();

        var rows = live.Select(i =>
        {
            var by = columns.Where(c => c.ByItem.ContainsKey(i.Id)).ToDictionary(c => c.TenderId, c => c.ByItem[i.Id]);
            var cheapest = by.Count < 2 ? (Guid?)null : by.OrderBy(kv => kv.Value).First().Key;
            return new TenderRow(i.Id, i.Group, i.Description, by, cheapest);
        }).ToList();
        return new TenderComparison(rows, columns);
    }

    // ---- phases ----

    /// <summary>The standard stages of a drydock stay, in order.</summary>
    public static readonly string[] DrydockPhases = ["Arrival and berthing", "Docking", "Work in dock", "Undocking", "Sea trial"];

    public static readonly string[] TechnicalPhases = ["Mobilisation", "Work", "Testing and handover"];

    public static string[] StandardPhases(ProjectType type) => type == ProjectType.Drydock ? DrydockPhases : TechnicalPhases;

    /// <summary>
    /// Days a phase has slipped: how many days after plan it ended (or, if it has not ended, how many days past its planned end today is). Null while there is
    /// nothing to compare, and never negative: finishing early is not a delay.
    /// </summary>
    public static int? DelayDays(ProjectPhase p, DateTime today)
    {
        if (p.PlannedEnd is not { } planned) return null;
        if (p.ActualEnd is { } ended) return Math.Max((ended.Date - planned.Date).Days, 0);
        if (p.ActualStart is not null) return Math.Max((today.Date - planned.Date).Days, 0);
        return null;
    }

    /// <summary>The first and last day any phase touches, for the chart's axis; null with no dates at all.</summary>
    public static (DateTime Start, DateTime End)? Range(IEnumerable<ProjectPhase> phases, DateTime today)
    {
        var days = phases.SelectMany(p => new[] { p.PlannedStart, p.PlannedEnd, p.ActualStart, p.ActualEnd ?? (p.ActualStart is null ? null : today) }).OfType<DateTime>().Select(d => d.Date).ToList();
        return days.Count == 0 ? null : (days.Min(), days.Max());
    }

    public static string? CheckPhase(DateTime? start, DateTime? end)
        => start is { } s && end is { } e && e.Date < s.Date ? "A phase cannot end before it starts." : null;

    // ---- documents ----

    public static string[] StandardDocuments(ProjectType type) => type == ProjectType.Drydock
        ? ["Drydock specification issued to the yards", "Docking plan and capacity plan", "Class and flag survey schedule", "Yard quotations", "Signed yard contract", "Paint scheme and manufacturers' data sheets",
           "Hot work and safety arrangements", "Class and statutory certificates after docking", "Final yard invoices and cost reconciliation"]
        : ["Scope of work", "Quotation or purchase order", "Completion or service report", "Cost reconciliation"];

    /// <summary>A document is missing while it is required and has not been received. Overdue means missing and past its due date.</summary>
    public static bool IsMissing(ProjectDocumentCheck d) => d.Status == DocumentCheckStatus.Required;

    public static bool IsOverdue(ProjectDocumentCheck d, DateTime today) => IsMissing(d) && d.DueDate is { } due && due.Date < today.Date;
}

/// <summary>What a group of projects (one drydock across sister ships) adds up to.</summary>
public record GroupSummary(int Projects, int Vessels, decimal Budget, decimal Contingency, decimal Cost, int ItemsDone, int ItemsTotal, DateTime? Start, DateTime? End, ProjectStatus? Overall)
{
    public decimal Variance => ProjectCostRules.Variance(Budget, Cost);
    public bool OverBudget => Budget > 0 && Cost > Budget;
}

public static class ProjectGroupRules
{
    /// <summary>
    /// Adds a group's projects up: budget, contingency and cost to date, items done of all items (dropped ones left out), and the span from the earliest start to the latest end.
    /// The group's overall status is the most active member's: ongoing if any is, else complete or closed once all are, else planning if any is still being planned, else approved.
    /// </summary>
    public static GroupSummary Summarise(IReadOnlyCollection<ProjectListRow> rows, DateTime today)
    {
        if (rows.Count == 0) return new GroupSummary(0, 0, 0, 0, 0, 0, 0, null, null, null);
        var spans = rows.Select(r => ProjectFleetRules.SpanOf(r, today)).OfType<FleetSpan>().ToList();
        ProjectStatus overall;
        if (rows.Any(r => r.Row.Status == ProjectStatus.Ongoing)) overall = ProjectStatus.Ongoing;
        else if (rows.All(r => r.Row.Status == ProjectStatus.Closed)) overall = ProjectStatus.Closed;
        else if (rows.All(r => r.Row.Status is ProjectStatus.Complete or ProjectStatus.Closed)) overall = ProjectStatus.Complete;
        else if (rows.Any(r => r.Row.Status == ProjectStatus.Planning)) overall = ProjectStatus.Planning;
        else overall = ProjectStatus.Approved;
        return new GroupSummary(rows.Count, rows.Select(r => r.Row.Project.VesselId).Distinct().Count(), rows.Sum(r => r.Budget), rows.Sum(r => r.Contingency), rows.Sum(r => r.Cost.ToDate),
            rows.Sum(r => r.Row.ItemsDone), rows.Sum(r => r.Row.ItemsDone + r.Row.ItemsOpen), spans.Count == 0 ? null : spans.Min(s => s.Start), spans.Count == 0 ? null : spans.Max(s => s.End), overall);
    }
}

/// <summary>One yard across the whole group: how many member projects it has quoted, what those quotes add up to, and whether it can be compared and awarded as a whole.</summary>
public record GroupYardColumn(Guid YardId, string YardName, int Members, int Quoted, decimal Total, bool Complete, bool IsLowest, bool Awarded);

public static class ProjectGroupTenderRules
{
    /// <summary>
    /// Adds each yard's quotes up across the group's projects. A yard is complete only when it has quoted every member and priced every job of each; the lowest total
    /// is picked among complete yards only, as for one project. A yard is awarded when it holds the award on every member.
    /// </summary>
    public static List<GroupYardColumn> CompareYards(IReadOnlyList<TenderComparison> members)
    {
        var yards = members.SelectMany(m => m.Columns).GroupBy(c => c.YardId).ToList();
        var columns = yards.Select(g =>
        {
            var cols = g.ToList();
            var complete = cols.Count == members.Count && cols.All(c => c.Unpriced == 0);
            return new GroupYardColumn(g.Key, cols[0].YardName, members.Count, cols.Count, cols.Sum(c => c.Total), complete, false, cols.Count == members.Count && cols.All(c => c.Status == TenderStatus.Awarded));
        }).ToList();
        var lowest = columns.Where(c => c.Complete).Select(c => (decimal?)c.Total).DefaultIfEmpty().Min();
        return columns.Select(c => c with { IsLowest = lowest is { } l && c.Complete && c.Total == l }).OrderBy(c => c.YardName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
