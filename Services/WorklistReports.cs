using System.Text;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record VesselWorklistRow(Guid VesselId, string Vessel, int Open, int Overdue, int DueThisWeek, int AwaitingVerification, int Urgent, int NewFromVessel);

public record AgeingBucket(string Label, int Count);

public record SourceCompletion(string Source, int Total, int Completed, int Open, int Overdue)
{
    public int RatePercent => Total == 0 ? 0 : (int)Math.Round(100.0 * Completed / Total);
}

/// <summary>Everything the fleet views and reports need, loaded once for the vessels the worklist is switched on for.</summary>
public record WorklistSnapshot(
    List<WorklistTask> Tasks,
    List<(Guid Id, string Name, Guid? GroupId)> Vessels,
    Dictionary<Guid, string> Sources,
    Dictionary<Guid, string> Departments,
    List<VesselGroup> Groups);

/// <summary>The numbers behind the fleet view, reports and tiles. Pure functions of the tasks, so each can be checked on its own.</summary>
public static class WorklistReports
{
    /// <summary>A task counts as "new from a vessel" for this many days after the vessel raised it.</summary>
    public const int NewDays = 7;

    public static bool IsOpen(WorklistTask t) => !t.IsClosed;

    public static int DaysOverdue(WorklistTask t, DateTime today) =>
        t.IsOverdue(today) && t.TargetDate is { } d ? (today - d.Date).Days : 0;

    public static List<VesselWorklistRow> PerVessel(IEnumerable<WorklistTask> tasks, IEnumerable<(Guid Id, string Name, Guid? GroupId)> vessels, DateTime today)
    {
        var list = tasks.ToList();
        return vessels.Select(v =>
        {
            var mine = list.Where(t => t.VesselId == v.Id).ToList();
            var open = mine.Where(IsOpen).ToList();
            return new VesselWorklistRow(v.Id, v.Name, open.Count, open.Count(t => t.IsOverdue(today)),
                open.Count(t => t.TargetDate is { } d && d.Date >= today && d.Date <= today.AddDays(7)),
                mine.Count(t => t.AwaitingVerification), open.Count(t => t.Priority == WorklistPriority.Urgent),
                mine.Count(t => IsNewFromVessel(t, today)));
        }).OrderByDescending(r => r.Overdue).ThenBy(r => r.Vessel).ToList();
    }

    public static bool IsNewFromVessel(WorklistTask t, DateTime today) =>
        t.OwnerStage == SyncStages.Vessel && t.CreatedAt.Date >= today.AddDays(-NewDays);

    /// <summary>Open tasks overdue by more than <paramref name="days"/> days, the longest overdue first.</summary>
    public static List<WorklistTask> Escalations(IEnumerable<WorklistTask> tasks, DateTime today, int days) =>
        tasks.Where(t => IsOpen(t) && DaysOverdue(t, today) > days).OrderByDescending(t => DaysOverdue(t, today)).ThenBy(t => t.Code).ToList();

    public static List<AgeingBucket> Ageing(IEnumerable<WorklistTask> tasks, DateTime today)
    {
        var ages = tasks.Where(IsOpen).Select(t => (today - t.CreatedAt.Date).Days).ToList();
        return
        [
            new("Up to 30 days", ages.Count(a => a <= 30)),
            new("31 to 90 days", ages.Count(a => a is > 30 and <= 90)),
            new("91 to 180 days", ages.Count(a => a is > 90 and <= 180)),
            new("Over 180 days", ages.Count(a => a > 180)),
        ];
    }

    /// <summary>Per source: how many tasks there are, how many are completed (not cancelled) and the share. Cancelled tasks are left out.</summary>
    public static List<SourceCompletion> CompletionBySource(IEnumerable<WorklistTask> tasks, IReadOnlyDictionary<Guid, string> sources, DateTime today)
    {
        return tasks.Where(t => t.Status != WorklistStatus.Cancelled)
            .GroupBy(t => t.SourceId is { } s && sources.ContainsKey(s) ? sources[s] : "No source")
            .Select(g => new SourceCompletion(g.Key, g.Count(), g.Count(t => t.Status == WorklistStatus.Completed), g.Count(IsOpen), g.Count(t => IsOpen(t) && t.IsOverdue(today))))
            .OrderByDescending(r => r.Total).ThenBy(r => r.Source).ToList();
    }

    // ---- CSV (UTF-8, Excel-friendly: every text field quoted) ----

    public static string Csv(IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows)
    {
        var sb = new StringBuilder();
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        sb.AppendLine(string.Join(",", header.Select(Q)));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(Q)));
        return sb.ToString();
    }

    public static string TasksCsv(IEnumerable<WorklistTask> tasks, WorklistSnapshot snap, DateTime today) => Csv(
        ["Number", "Vessel", "Title", "Source", "Department", "Priority", "Status", "Target date", "Days overdue", "Assigned to", "Percent", "Deferrals", "Raised", "Awaiting verification"],
        tasks.Select(t => new[]
        {
            t.Code, snap.Vessels.FirstOrDefault(v => v.Id == t.VesselId).Name ?? "", t.Title,
            t.SourceId is { } s && snap.Sources.TryGetValue(s, out var sn) ? sn : "",
            t.DepartmentId is { } d && snap.Departments.TryGetValue(d, out var dn) ? dn : "",
            t.Priority.ToString(), t.Status.ToString(), t.TargetDate?.ToString("yyyy-MM-dd") ?? "", DaysOverdue(t, today).ToString(),
            t.AssigneeName, t.PercentComplete.ToString(), t.DeferralCount.ToString(), t.CreatedAt.ToString("yyyy-MM-dd"), t.AwaitingVerification ? "yes" : "",
        }));

    public static string CompletionCsv(IEnumerable<SourceCompletion> rows) => Csv(
        ["Source", "Tasks", "Completed", "Open", "Open and overdue", "Completion %"],
        rows.Select(r => new[] { r.Source, r.Total.ToString(), r.Completed.ToString(), r.Open.ToString(), r.Overdue.ToString(), r.RatePercent.ToString() }));

    public static string AgeingCsv(IEnumerable<AgeingBucket> rows) => Csv(["Age of open tasks", "Tasks"], rows.Select(r => new[] { r.Label, r.Count.ToString() }));
}

/// <summary>Loads the worklist data for the office's fleet-wide views.</summary>
public class WorklistReportService(IDbContextFactory<ChandleryDbContext> factory, WorklistService worklist)
{
    public async Task<WorklistSnapshot> SnapshotAsync(Guid companyId)
    {
        var enabled = (await worklist.EnabledVesselsAsync(companyId)).Select(v => v.Id).ToHashSet();
        var tasks = (await worklist.ListAsync(companyId)).Where(t => enabled.Contains(t.VesselId)).ToList();
        var lists = await worklist.GetListsAsync(companyId, includeInactive: true);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var vessels = (await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).OrderBy(v => v.Name).ToListAsync())
            .Where(v => enabled.Contains(v.Id)).Select(v => (v.Id, v.Name, v.GroupId)).ToList();
        var departments = await db.Departments.AsNoTracking().Where(d => d.CompanyId == companyId).ToDictionaryAsync(d => d.Id, d => d.Name);
        var groups = await db.VesselGroups.AsNoTracking().Where(g => g.CompanyId == companyId).OrderBy(g => g.Level).ThenBy(g => g.Name).ToListAsync();
        return new WorklistSnapshot(tasks, vessels, lists.Where(l => l.Kind == WorklistListKind.Source).ToDictionary(l => l.Id, l => l.Name), departments, groups);
    }

    /// <summary>Keeps the vessels that belong to the group or any group below it.</summary>
    public static WorklistSnapshot InGroup(WorklistSnapshot snap, Guid? groupId)
    {
        if (groupId is not { } g) return snap;
        var vessels = snap.Vessels.Where(v => PolicyResolver.GroupChain(snap.Groups, v.GroupId).Contains(g)).ToList();
        var ids = vessels.Select(v => v.Id).ToHashSet();
        return snap with { Vessels = vessels, Tasks = snap.Tasks.Where(t => ids.Contains(t.VesselId)).ToList() };
    }
}
