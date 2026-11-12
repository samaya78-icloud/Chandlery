using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public enum CapacityVerdict { Unknown, Fits, Tight, Exceeds }

/// <summary>What the planned jobs of the coming days add up to.</summary>
public sealed record CapacityJobs(int Count, int Minutes, int WithoutEstimate, int Overdue);

/// <summary>One position of the working table: how many people hold it onboard, what the table has them work on this kind of day, and what is left of their working day and week.</summary>
public sealed record CapacityLine(string Position, int People, int WorkMinutesPerDay, int SpareMinutesPerPerson)
{
    public int SpareMinutes => People * SpareMinutesPerPerson;
}

public sealed record RestCapacityView(DateTime From, int Days, bool InPort, bool HasTable, IReadOnlyList<CapacityLine> Lines, IReadOnlyList<string> Unmatched,
    CapacityJobs Jobs, int SpareMinutes, CapacityVerdict Verdict)
{
    public double? Ratio => SpareMinutes > 0 ? (double)Jobs.Minutes / SpareMinutes : null;
}

/// <summary>
/// Sets the planned maintenance of the coming days against the work time the crew has left under the rules, so a heavy week is seen before it starts.
/// It is a coarse check by design: the time a position has left is the working day and week the rule set allows less what the working table already
/// has that position doing, and it ignores the rules about splitting rest. It says "this cannot fit" with confidence and "this fits" with care.
/// </summary>
public static class RestCapacity
{
    /// <summary>Percent of the spare time the planned jobs may take before the week is called tight. Whole numbers, so the line itself is exact.</summary>
    public const int TightFromPercent = 60;

    /// <summary>The planned jobs due in the window: everything on the plan that is due by its last day, including what is overdue already.</summary>
    public static CapacityJobs JobsIn(IEnumerable<(DateTime? NextDue, int? EstimatedMinutes, bool Active)> jobs, DateTime today, int days)
    {
        var last = today.Date.AddDays(days - 1);
        var due = jobs.Where(j => j.Active && j.NextDue is { } d && d.Date <= last).ToList();
        return new CapacityJobs(due.Count, due.Sum(j => j.EstimatedMinutes ?? 0), due.Count(j => j.EstimatedMinutes is null or 0), due.Count(j => j.NextDue!.Value.Date < today.Date));
    }

    /// <summary>The work time one person in a position has left over <paramref name="days"/> days, never more than the day or the week allows.</summary>
    public static int SparePerPerson(ArrangementRow row, RestLimits limits, bool inPort, int days)
    {
        var worked = inPort ? row.PortWorkMinutes : row.SeaWorkMinutes;
        var dayRoom = Math.Max(0, RestSpans.DayMinutes - limits.MinRest24 - worked);
        var weekRoom = Math.Max(0, days * RestSpans.DayMinutes - limits.MinRest7 * days / 7 - days * worked);
        return Math.Min(days * dayRoom, weekRoom);
    }

    public static RestCapacityView Compute(DateTime from, int days, bool inPort, IReadOnlyList<ArrangementRow>? table, IEnumerable<string> crewRanks, RestLimits limits, CapacityJobs jobs)
    {
        if (table is null) return new(from, days, inPort, false, [], [], jobs, 0, CapacityVerdict.Unknown);

        var byRank = crewRanks.Select(r => (r ?? "").Trim()).GroupBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        var lines = new List<CapacityLine>();
        var unmatched = new List<string>();
        foreach (var group in byRank)
        {
            var row = table.FirstOrDefault(r => string.Equals(r.Position.Trim(), group.Key, StringComparison.OrdinalIgnoreCase));
            if (row is null) unmatched.Add(group.Key == "" ? "(no rank)" : group.Key);
            else lines.Add(new CapacityLine(row.Position, group.Count(), inPort ? row.PortWorkMinutes : row.SeaWorkMinutes, SparePerPerson(row, limits, inPort, days)));
        }

        var spare = lines.Sum(l => l.SpareMinutes);
        var verdict = lines.Count == 0 ? CapacityVerdict.Unknown
            : jobs.Minutes == 0 ? CapacityVerdict.Fits
            : spare == 0 || jobs.Minutes > spare ? CapacityVerdict.Exceeds
            : jobs.Minutes * 100L > spare * (long)TightFromPercent ? CapacityVerdict.Tight
            : CapacityVerdict.Fits;
        return new(from, days, inPort, true, lines.OrderBy(l => l.Position).ToList(), unmatched.Order().ToList(), jobs, spare, verdict);
    }
}

public class RestCapacityService(IDbContextFactory<ChandleryDbContext> factory)
{
    public const int Days = 7;

    /// <summary>The view for a vessel and the jobs on its plan, or null when Rest Hours is not switched on for the vessel.</summary>
    public async Task<RestCapacityView?> ForVesselAsync(Guid companyId, Guid vesselId, IEnumerable<PlannedJob> jobs, DateTime? today = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await RestHoursService.IsEnabledForVesselAsync(db, companyId, vesselId)) return null;

        var day = DateTime.SpecifyKind((today ?? DateTime.UtcNow).Date, DateTimeKind.Utc);
        var (_, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        var table = (await db.WorkArrangementTables.AsNoTracking().Where(t => t.VesselId == vesselId && t.EffectiveFrom <= day).ToListAsync()).OrderBy(t => t.VersionNo).LastOrDefault();
        var ranks = await db.CrewMembers.AsNoTracking().Where(c => c.VesselId == vesselId && c.CompanyId == companyId && c.IsActive).Select(c => c.Rank).ToListAsync();

        // The vessel's latest status on or before today: a ship stays as it was until it is told otherwise.
        var statuses = await db.VesselDayStatuses.AsNoTracking().Where(s => s.VesselId == vesselId && s.Date <= day && s.Date >= day.AddDays(-30)).ToListAsync();
        var latest = statuses.OrderBy(s => s.Date).ThenBy(s => s.At).ThenBy(s => s.Id).LastOrDefault();
        var inPort = latest?.Kind == VesselDayKind.InPort;

        var planned = RestCapacity.JobsIn(jobs.Select(j => ((DateTime?)j.NextDueDate, j.EstimatedMinutes, j.IsActive)), day, Days);
        return RestCapacity.Compute(day, Days, inPort, table is null ? null : WorkArrangementService.RowsOf(table), ranks, limits, planned);
    }
}
