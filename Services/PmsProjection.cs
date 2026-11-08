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
    }

    /// <summary>Re-works one job's schedule from what is stored. Used after the job itself, or one of its completions, arrives or is saved.</summary>
    public static async Task RefreshJobAsync(ChandleryDbContext db, Guid jobId, CancellationToken ct = default)
    {
        var job = await db.PlannedJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        var completions = await db.JobCompletions.AsNoTracking().Where(c => c.PlannedJobId == jobId).ToListAsync(ct);
        var readings = await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == job.MachineryItemId).ToListAsync(ct);
        Apply(job, PmsScheduler.Compute(InputOf(job), completions.Select(InputOf), readings.Select(InputOf)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-works every job on a machinery item that is driven by hours. Used after a reading arrives or is saved.</summary>
    public static async Task RefreshMachineryAsync(ChandleryDbContext db, Guid machineryItemId, CancellationToken ct = default)
    {
        var jobs = await db.PlannedJobs.Where(j => j.MachineryItemId == machineryItemId && j.HoursInterval > 0).ToListAsync(ct);
        if (jobs.Count == 0) return;
        var readings = (await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == machineryItemId).ToListAsync(ct)).Select(InputOf).ToList();
        var ids = jobs.Select(j => j.Id).ToList();
        var completions = (await db.JobCompletions.AsNoTracking().Where(c => ids.Contains(c.PlannedJobId)).ToListAsync(ct)).ToLookup(c => c.PlannedJobId);
        foreach (var job in jobs)
            Apply(job, PmsScheduler.Compute(InputOf(job), completions[job.Id].Select(InputOf), readings));
        await db.SaveChangesAsync(ct);
    }
}
