using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record PmsFleetRow(Guid VesselId, string Vessel, int Jobs, int Overdue, int CriticalOverdue, int DueThisWeek, int HoursOutOfDate)
{
    /// <summary>The share of the vessel's jobs that are not overdue.</summary>
    public int OnSchedulePercent => Jobs == 0 ? 100 : (int)Math.Round(100.0 * (Jobs - Overdue) / Jobs);
}

public record PmsCounts(int Overdue, int CriticalOverdue, int DueThisWeek, int HoursOutOfDate, int PendingDecisions = 0);

public record PmsJobRow(PlannedJob Job, string Vessel, string Machinery, int DaysOverdue);

public record PmsDoneRow(JobCompletion Completion, PlannedJob Job, string Vessel, string Machinery);

public record PmsHoursRow(string Vessel, string Machinery, decimal? LatestHours, DateTime? LatestDate, int Readings, decimal? AverageDaily, int? DaysSinceReading);

public record PmsVesselOption(Guid Id, string Name, Guid? GroupId);

/// <summary>The fleet-wide figures for the office: counted in the database, so they stay quick with thousands of jobs per vessel.</summary>
public class PmsReportService(IDbContextFactory<ChandleryDbContext> factory)
{
    private static (DateTime Today, DateTime WeekEnd, DateTime StaleBefore) Dates()
    {
        var today = DateTime.UtcNow.Date;
        return (today, today.AddDays(7), today.AddDays(-PmsScheduler.DefaultStaleDays));
    }

    public async Task<(List<PmsVesselOption> Vessels, List<VesselGroup> Groups)> VesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Pms)).Select(v => v.Id).ToHashSet();
        var vessels = (await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).OrderBy(v => v.Name).ToListAsync())
            .Where(v => enabled.Contains(v.Id)).Select(v => new PmsVesselOption(v.Id, v.Name, v.GroupId)).ToList();
        var groups = await db.VesselGroups.AsNoTracking().Where(g => g.CompanyId == companyId).OrderBy(g => g.Level).ThenBy(g => g.Name).ToListAsync();
        return (vessels, groups);
    }

    /// <summary>One row per vessel with planned maintenance switched on, worst first. A group keeps its vessels and those of the groups below it.</summary>
    public async Task<List<PmsFleetRow>> FleetAsync(Guid companyId, Guid? groupId = null)
    {
        var (vessels, groups) = await VesselsAsync(companyId);
        if (groupId is { } g) vessels = vessels.Where(v => PolicyResolver.GroupChain(groups, v.GroupId).Contains(g)).ToList();
        if (vessels.Count == 0) return [];

        var (today, weekEnd, staleBefore) = Dates();
        var ids = vessels.Select(v => v.Id).ToList();
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var counts = (await db.PlannedJobs.AsNoTracking().Where(j => j.CompanyId == companyId && j.IsActive && ids.Contains(j.VesselId))
            .GroupBy(j => j.VesselId)
            .Select(x => new
            {
                VesselId = x.Key,
                Jobs = x.Count(),
                Overdue = x.Count(j => j.GraceUntil != null && j.GraceUntil < today),
                CriticalOverdue = x.Count(j => j.IsCritical && j.GraceUntil != null && j.GraceUntil < today),
                Week = x.Count(j => j.NextDueDate != null && j.NextDueDate >= today && j.NextDueDate <= weekEnd),
                Stale = x.Count(j => j.HoursInterval > 0 && (j.LastReadingDate == null || j.LastReadingDate < staleBefore)),
            }).ToListAsync()).ToDictionary(x => x.VesselId);

        return vessels.Select(v => counts.TryGetValue(v.Id, out var c)
                ? new PmsFleetRow(v.Id, v.Name, c.Jobs, c.Overdue, c.CriticalOverdue, c.Week, c.Stale)
                : new PmsFleetRow(v.Id, v.Name, 0, 0, 0, 0, 0))
            .OrderByDescending(r => r.CriticalOverdue).ThenByDescending(r => r.Overdue).ThenBy(r => r.Vessel).ToList();
    }

    /// <summary>The totals for the dashboard tiles. Null when planned maintenance is off for every vessel.</summary>
    public async Task<PmsCounts?> CountsAsync(Guid companyId)
    {
        var rows = await FleetAsync(companyId);
        if (rows.Count == 0) return null;
        var ids = rows.Select(r => r.VesselId).ToList();
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var waiting = await db.JobDeferralRequests.AsNoTracking().CountAsync(r => r.CompanyId == companyId && ids.Contains(r.VesselId) && !db.JobDeferralDecisions.Any(d => d.RequestId == r.Id));
        return new PmsCounts(rows.Sum(r => r.Overdue), rows.Sum(r => r.CriticalOverdue), rows.Sum(r => r.DueThisWeek), rows.Sum(r => r.HoursOutOfDate), waiting);
    }

    // ---- lists behind the reports ----

    /// <summary>Overdue jobs, critical first, then the longest overdue.</summary>
    public async Task<List<PmsJobRow>> OverdueAsync(Guid companyId, Guid? vesselId = null, int take = 1000)
    {
        var (today, _, _) = Dates();
        return await RowsAsync(companyId, vesselId, take, q => q.Where(j => j.GraceUntil != null && j.GraceUntil < today).OrderByDescending(j => j.IsCritical).ThenBy(j => j.NextDueDate), today);
    }

    /// <summary>Jobs falling due in the next <paramref name="days"/> days (not yet overdue), soonest first.</summary>
    public async Task<List<PmsJobRow>> DueWithinAsync(Guid companyId, int days, Guid? vesselId = null, int take = 2000)
    {
        var (today, _, _) = Dates();
        var until = today.AddDays(Math.Max(0, days));
        return await RowsAsync(companyId, vesselId, take, q => q.Where(j => j.NextDueDate != null && j.NextDueDate <= until && (j.GraceUntil == null || j.GraceUntil >= today)).OrderBy(j => j.NextDueDate).ThenBy(j => j.Code), today);
    }

    private async Task<List<PmsJobRow>> RowsAsync(Guid companyId, Guid? vesselId, int take, Func<IQueryable<PlannedJob>, IQueryable<PlannedJob>> shape, DateTime today)
    {
        var (vessels, _) = await VesselsAsync(companyId);
        var ids = vessels.Select(v => v.Id).ToList();
        if (vesselId is { } only) ids = ids.Where(i => i == only).ToList();
        if (ids.Count == 0) return [];

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var jobs = await shape(db.PlannedJobs.AsNoTracking().Where(j => j.CompanyId == companyId && j.IsActive && ids.Contains(j.VesselId))).Take(take).ToListAsync();
        var itemIds = jobs.Select(j => j.MachineryItemId).Distinct().ToList();
        var items = await db.MachineryItems.AsNoTracking().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        var names = vessels.ToDictionary(v => v.Id, v => v.Name);
        return jobs.Select(j => new PmsJobRow(j, names.GetValueOrDefault(j.VesselId, ""), items.GetValueOrDefault(j.MachineryItemId, ""),
            j.NextDueDate is { } due && due.Date < today ? (today - due.Date).Days : 0)).ToList();
    }

    /// <summary>Jobs done between two dates (imported history is not work done on this system and is left out), newest first.</summary>
    public async Task<List<PmsDoneRow>> DoneAsync(Guid companyId, DateTime from, DateTime to, Guid? vesselId = null, int take = 5000)
    {
        var (vessels, _) = await VesselsAsync(companyId);
        var ids = vessels.Select(v => v.Id).ToList();
        if (vesselId is { } only) ids = ids.Where(i => i == only).ToList();
        if (ids.Count == 0) return [];

        var start = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var done = await db.JobCompletions.AsNoTracking().Where(c => c.CompanyId == companyId && !c.IsImported && ids.Contains(c.VesselId) && c.DoneDate >= start && c.DoneDate <= end)
            .OrderByDescending(c => c.DoneDate).ThenByDescending(c => c.At).Take(take).ToListAsync();
        var jobIds = done.Select(c => c.PlannedJobId).Distinct().ToList();
        var jobs = await db.PlannedJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id);
        var itemIds = jobs.Values.Select(j => j.MachineryItemId).Distinct().ToList();
        var items = await db.MachineryItems.AsNoTracking().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        var names = vessels.ToDictionary(v => v.Id, v => v.Name);
        return done.Where(c => jobs.ContainsKey(c.PlannedJobId)).Select(c =>
        {
            var job = jobs[c.PlannedJobId];
            return new PmsDoneRow(c, job, names.GetValueOrDefault(c.VesselId, ""), items.GetValueOrDefault(job.MachineryItemId, ""));
        }).ToList();
    }

    /// <summary>Each hour meter's latest reading, the average hours per day over the last 30 days, and how long since it was read.</summary>
    public async Task<List<PmsHoursRow>> HoursSummaryAsync(Guid companyId)
    {
        var (vessels, _) = await VesselsAsync(companyId);
        var ids = vessels.Select(v => v.Id).ToList();
        if (ids.Count == 0) return [];

        var today = DateTime.UtcNow.Date;
        var since = today.AddDays(-90);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var meters = await db.MachineryItems.AsNoTracking().Where(m => m.CompanyId == companyId && m.IsActive && m.HasHourMeter && ids.Contains(m.VesselId)).ToListAsync();
        var meterIds = meters.Select(m => m.Id).ToList();
        var recent = (await db.RunningHoursReadings.AsNoTracking().Where(r => meterIds.Contains(r.MachineryItemId) && r.ReadingDate >= since).ToListAsync()).ToLookup(r => r.MachineryItemId);
        // A meter not read in 90 days still shows its latest reading.
        var lastEver = await db.RunningHoursReadings.AsNoTracking().Where(r => meterIds.Contains(r.MachineryItemId))
            .GroupBy(r => r.MachineryItemId).Select(g => new { Id = g.Key, Date = g.Max(r => r.ReadingDate) }).ToListAsync();
        var names = vessels.ToDictionary(v => v.Id, v => v.Name);

        var rows = new List<PmsHoursRow>();
        foreach (var meter in meters)
        {
            var effective = PmsScheduler.EffectiveReadings(recent[meter.Id].Select(PmsProjection.InputOf));
            var latest = effective.LastOrDefault();
            DateTime? latestDate = latest?.Date.Date ?? lastEver.FirstOrDefault(x => x.Id == meter.Id)?.Date.Date;
            decimal? average = null;
            if (latest is not null && effective.FirstOrDefault(r => r.Date.Date >= latest.Date.Date.AddDays(-PmsScheduler.AverageWindowDays) && r.Date.Date < latest.Date.Date) is { } earliest)
                average = (latest.Hours - earliest.Hours) / Math.Max(1, (latest.Date.Date - earliest.Date.Date).Days);
            rows.Add(new PmsHoursRow(names.GetValueOrDefault(meter.VesselId, ""), meter.Name, latest?.Hours, latestDate, effective.Count, average, latestDate is { } d ? (today - d).Days : null));
        }
        return rows.OrderBy(r => r.Vessel).ThenBy(r => r.Machinery).ToList();
    }
}

/// <summary>CSV forms of the maintenance reports (UTF-8 with every field quoted, as the worklist reports).</summary>
public static class PmsReports
{
    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

    public static string JobsCsv(IEnumerable<PmsJobRow> rows) => WorklistReports.Csv(
        ["Vessel", "Machinery", "Code", "Job", "Critical", "Type", "Due", "Overdue after", "Days overdue", "Due by", "Last done", "Every"],
        rows.Select(r => new[]
        {
            r.Vessel, r.Machinery, r.Job.Code, r.Job.Title, r.Job.IsCritical ? "yes" : "", r.Job.Type.ToString(), Date(r.Job.NextDueDate), Date(r.Job.GraceUntil),
            r.DaysOverdue.ToString(), r.Job.DueBasis, Date(r.Job.LastDoneDate), Interval(r.Job),
        }));

    public static string DoneCsv(IEnumerable<PmsDoneRow> rows) => WorklistReports.Csv(
        ["Vessel", "Machinery", "Code", "Job", "Done", "Running hours", "Done by", "Minutes", "Comments"],
        rows.Select(r => new[]
        {
            r.Vessel, r.Machinery, r.Job.Code, r.Job.Title, Date(r.Completion.DoneDate), r.Completion.HoursAtDone?.ToString("0.0") ?? "", r.Completion.PerformedBy,
            r.Completion.TimeSpentMinutes?.ToString() ?? "", r.Completion.Comments,
        }));

    public static string HoursCsv(IEnumerable<PmsHoursRow> rows) => WorklistReports.Csv(
        ["Vessel", "Machinery", "Latest hours", "Read on", "Days since read", "Readings (90 days)", "Hours per day (30 days)"],
        rows.Select(r => new[]
        {
            r.Vessel, r.Machinery, r.LatestHours?.ToString("0.0") ?? "", Date(r.LatestDate), r.DaysSinceReading?.ToString() ?? "", r.Readings.ToString(), r.AverageDaily?.ToString("0.0") ?? "",
        }));

    public static string FleetCsv(IEnumerable<PmsFleetRow> rows) => WorklistReports.Csv(
        ["Vessel", "Jobs", "Overdue", "Critical overdue", "Due in 7 days", "Hours out of date", "On schedule %"],
        rows.Select(r => new[] { r.Vessel, r.Jobs.ToString(), r.Overdue.ToString(), r.CriticalOverdue.ToString(), r.DueThisWeek.ToString(), r.HoursOutOfDate.ToString(), r.OnSchedulePercent.ToString() }));

    private static string Interval(PlannedJob job)
    {
        var parts = new List<string>();
        if (job.CalendarInterval is > 0 and var n) parts.Add($"{n} {job.CalendarUnit.ToString().ToLowerInvariant()}");
        if (job.HoursInterval is > 0) parts.Add($"{job.HoursInterval} h");
        return string.Join(" / ", parts);
    }
}
