using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>A project placed on the timeline: from when it started (or is planned to start) to when it ended (or is planned to end, or today while it is still going).</summary>
public record FleetSpan(ProjectListRow Row, DateTime Start, DateTime End, bool Actual);

/// <summary>One month of workload: how many projects were running in it, how many drydocks among them, and how many project days fell in it.</summary>
public record FleetMonth(DateTime Month, int Projects, int Drydocks, int Days);

/// <summary>One superintendent's load: projects in the window, total project days, and the days in each month of the window.</summary>
public record SuperintendentLoad(string Name, int Projects, int Days, int[] DaysByMonth);

/// <summary>What a vessel's projects add up to: how many, the budget set, and the cost to date. Over is the cost in excess of the budget for projects that have one.</summary>
public record FleetSpend(string Vessel, int Projects, decimal Budget, decimal Cost, decimal Over);

/// <summary>
/// The fleet view of projects: which vessel is in the yard when, how busy each month is, and what is being spent. Pure rules over the list rows,
/// so the page only draws what these give. Money is the office's: this is never built onboard.
/// </summary>
public static class ProjectFleetRules
{
    /// <summary>
    /// Where a project sits on the timeline, or null when it has no date to place it by. Real dates win over planned ones. A project that has started but not ended runs to today
    /// (or to its planned end if that is later), and one that ended before it was planned to start is still drawn as one day.
    /// </summary>
    public static FleetSpan? SpanOf(ProjectListRow row, DateTime today)
    {
        var p = row.Row.Project;
        var start = row.Row.ActualStart ?? p.PlannedStart;
        if (start is not { } s) return null;
        var actual = row.Row.ActualStart is not null;
        DateTime end;
        if (row.Row.ActualEnd is { } ae) end = ae;
        else if (actual) end = p.PlannedEnd is { } pe && pe > today ? pe : today;
        else end = p.PlannedEnd ?? s;
        if (end < s) end = s;
        return new FleetSpan(row, s.Date, end.Date, actual);
    }

    public static List<FleetSpan> Spans(IEnumerable<ProjectListRow> rows, DateTime today)
        => rows.Select(r => SpanOf(r, today)).OfType<FleetSpan>().OrderBy(s => s.Row.VesselName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Start).ToList();

    /// <summary>The first day of the month a date is in.</summary>
    public static DateTime MonthStart(DateTime d) => new(d.Year, d.Month, 1);

    /// <summary>The window to draw: from the month of the earliest span (or this month, if sooner) to the month of the latest, with at least <paramref name="minMonths"/> months.</summary>
    public static (DateTime From, int Months) Window(IReadOnlyCollection<FleetSpan> spans, DateTime today, int minMonths = 6)
    {
        var from = MonthStart(today);
        var to = MonthStart(today);
        foreach (var s in spans)
        {
            if (s.Start < from) from = MonthStart(s.Start);
            if (s.End > to) to = MonthStart(s.End);
        }
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        return (from, Math.Max(months, minMonths));
    }

    /// <summary>How far along the window a day falls, from 0 to 1, counting the whole of the last day.</summary>
    public static double Position(DateTime day, DateTime from, int months)
    {
        var total = (from.AddMonths(months) - from).TotalDays;
        return Math.Clamp((day.Date - from).TotalDays / total, 0, 1);
    }

    /// <summary>Workload by month over the window: a project counts in every month it has at least one day in, and its days are split by month.</summary>
    public static List<FleetMonth> Workload(IReadOnlyCollection<FleetSpan> spans, DateTime from, int months)
    {
        var result = new List<FleetMonth>();
        for (var i = 0; i < months; i++)
        {
            var first = from.AddMonths(i);
            var last = first.AddMonths(1).AddDays(-1);
            var inMonth = spans.Where(s => s.Start <= last && s.End >= first).ToList();
            var days = inMonth.Sum(s => (Min(s.End, last) - Max(s.Start, first)).Days + 1);
            result.Add(new FleetMonth(first, inMonth.Count, inMonth.Count(s => s.Row.Row.Project.Type == ProjectType.Drydock), days));
        }
        return result;
    }

    /// <summary>The days of a span that fall inside one month.</summary>
    private static int DaysIn(FleetSpan s, DateTime first)
    {
        var last = first.AddMonths(1).AddDays(-1);
        return s.Start <= last && s.End >= first ? (Min(s.End, last) - Max(s.Start, first)).Days + 1 : 0;
    }

    /// <summary>
    /// How much of each superintendent's time the projects take: for each, the projects they have in the window and the project days in each month. Projects with no
    /// superintendent are grouped as "Not assigned" so they are not lost. Busiest first.
    /// </summary>
    public static List<SuperintendentLoad> WorkloadBySuperintendent(IReadOnlyCollection<FleetSpan> spans, DateTime from, int months)
        => spans.GroupBy(s => s.Row.Row.Project.SuperintendentName.Trim() is { Length: > 0 } n ? n : "Not assigned")
            .Select(g =>
            {
                var byMonth = Enumerable.Range(0, months).Select(i => g.Sum(s => DaysIn(s, from.AddMonths(i)))).ToArray();
                return new SuperintendentLoad(g.Key, g.Count(s => Enumerable.Range(0, months).Any(i => DaysIn(s, from.AddMonths(i)) > 0)), byMonth.Sum(), byMonth);
            })
            .Where(l => l.Projects > 0).OrderByDescending(l => l.Days).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Spend by vessel, most expensive first. Closed and live projects both count: it is what each ship has cost.</summary>
    public static List<FleetSpend> SpendByVessel(IEnumerable<ProjectListRow> rows)
        => rows.GroupBy(r => r.VesselName)
            .Select(g => new FleetSpend(g.Key, g.Count(), g.Sum(r => r.Budget), g.Sum(r => r.Cost.ToDate), g.Where(r => r.OverBudget).Sum(r => r.Cost.ToDate - r.Budget)))
            .OrderByDescending(s => s.Cost).ThenBy(s => s.Vessel, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Spend by year of the project's own date, newest first, for the same totals.</summary>
    public static List<(int Year, FleetSpend Spend)> SpendByYear(IEnumerable<ProjectListRow> rows)
        => rows.GroupBy(r => (r.Row.ActualStart ?? r.Row.Project.PlannedStart ?? r.Row.Project.CreatedAt).Year)
            .OrderByDescending(g => g.Key)
            .Select(g => (g.Key, new FleetSpend(g.Key.ToString(), g.Count(), g.Sum(r => r.Budget), g.Sum(r => r.Cost.ToDate), g.Where(r => r.OverBudget).Sum(r => r.Cost.ToDate - r.Budget)))).ToList();

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
