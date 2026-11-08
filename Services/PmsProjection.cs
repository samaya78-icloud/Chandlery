using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>
/// Writes each job's worked-out schedule onto the job row so lists and boards are fast. The columns are local to each side and are never sent;
/// both sides get the same values by working them out from the same completions and readings.
/// </summary>
public static class PmsProjection
{
    public static PmsJobInput InputOf(PlannedJob job) => new(job.CalendarInterval, job.CalendarUnit, job.HoursInterval, job.Anchor, job.FixedAnchorDate, job.GraceDays,
        job.StartDate, job.BaselineDoneDate, job.BaselineDoneHours);

    public static PmsCompletionInput InputOf(JobCompletion c) => new(c.DoneDate, c.HoursAtDone, c.At, c.Id);

    public static PmsReadingInput InputOf(RunningHoursReading r) => new(r.ReadingDate, r.Hours, r.IsCorrection, r.At, r.Id);

    /// <summary>Where a job stands today, from its stored schedule (the same rule as <see cref="PmsScheduler.StatusOf"/>).</summary>
    public static PmsStatus StatusOf(PlannedJob job, DateTime today)
    {
        if (job.NextDueDate is not { } due) return PmsStatus.NoSchedule;
        if (today.Date < due.Date) return PmsStatus.NotDue;
        return job.GraceUntil is { } until && today.Date > until.Date ? PmsStatus.Overdue : PmsStatus.Due;
    }

    /// <summary>An hours-driven job whose latest reading is old, or missing: its hours date should not be trusted.</summary>
    public static bool HoursStale(PlannedJob job, DateTime today, int staleDays = PmsScheduler.DefaultStaleDays)
        => job.HoursInterval is > 0 && (job.LastReadingDate is not { } read || (today.Date - read.Date).Days > staleDays);

    public static void Apply(PlannedJob job, PmsSchedule s)
    {
        job.LastDoneDate = s.LastDoneDate;
        job.LastDoneHours = s.LastDoneHours;
        job.NextDueDate = s.NextDue;
        job.DueBasis = s.DueBasis;
        job.HoursEstimated = s.HoursEstimated;
        job.HoursUnknown = s.HoursUnknown;
        job.LastReadingDate = s.LastReadingDate;
        job.GraceUntil = s.GraceUntil;
        job.DeferredUntil = s.DeferredTo;
    }

    /// <summary>Re-works one job's schedule from what is stored. Used after the job itself, or one of its completions, arrives or is saved.</summary>
    public static async Task RefreshJobAsync(ChandleryDbContext db, Guid jobId, CancellationToken ct = default)
    {
        var job = await db.PlannedJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        var completions = await db.JobCompletions.AsNoTracking().Where(c => c.PlannedJobId == jobId).ToListAsync(ct);
        var readings = await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == job.MachineryItemId).ToListAsync(ct);
        var deferrals = await DeferralsAsync(db, [jobId], ct);
        Apply(job, PmsScheduler.Compute(InputOf(job), completions.Select(InputOf), readings.Select(InputOf), deferrals[jobId]));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The approved deferrals of each job. On a request, the latest decision is the one that counts; a rejection after an approval cancels it.</summary>
    public static async Task<ILookup<Guid, PmsDeferralInput>> DeferralsAsync(ChandleryDbContext db, IReadOnlyCollection<Guid> jobIds, CancellationToken ct = default)
    {
        var requests = await db.JobDeferralRequests.AsNoTracking().Where(r => jobIds.Contains(r.PlannedJobId)).ToListAsync(ct);
        if (requests.Count == 0) return Enumerable.Empty<(Guid, PmsDeferralInput)>().ToLookup(x => x.Item1, x => x.Item2);
        var requestIds = requests.Select(r => r.Id).ToList();
        var latest = (await db.JobDeferralDecisions.AsNoTracking().Where(d => requestIds.Contains(d.RequestId)).ToListAsync(ct))
            .GroupBy(d => d.RequestId).Select(g => g.OrderByDescending(d => d.At).ThenByDescending(d => d.Id).First())
            .Where(d => d.Approved).ToDictionary(d => d.RequestId);
        return requests.Where(r => latest.ContainsKey(r.Id))
            .Select(r => (Job: r.PlannedJobId, Input: new PmsDeferralInput(latest[r.Id].ApprovedDueDate ?? r.RequestedDueDate, r.LastDoneAtRequest, latest[r.Id].At, latest[r.Id].Id)))
            .ToLookup(x => x.Job, x => x.Input);
    }

    /// <summary>Re-works every job on a machinery item that is driven by hours. Used after a reading arrives or is saved.</summary>
    public static async Task RefreshMachineryAsync(ChandleryDbContext db, Guid machineryItemId, CancellationToken ct = default)
    {
        var jobs = await db.PlannedJobs.Where(j => j.MachineryItemId == machineryItemId && j.HoursInterval > 0).ToListAsync(ct);
        if (jobs.Count == 0) return;
        var readings = (await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == machineryItemId).ToListAsync(ct)).Select(InputOf).ToList();
        var ids = jobs.Select(j => j.Id).ToList();
        var completions = (await db.JobCompletions.AsNoTracking().Where(c => ids.Contains(c.PlannedJobId)).ToListAsync(ct)).ToLookup(c => c.PlannedJobId);
        var deferrals = await DeferralsAsync(db, ids, ct);
        foreach (var job in jobs)
            Apply(job, PmsScheduler.Compute(InputOf(job), completions[job.Id].Select(InputOf), readings, deferrals[job.Id]));
        await db.SaveChangesAsync(ct);
    }
}
