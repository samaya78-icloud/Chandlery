using Chandlery.Models;

namespace Chandlery.Services;

public record PmsJobInput(
    int? CalendarInterval, PmsIntervalUnit Unit, int? HoursInterval, PmsAnchor Anchor, DateTime? FixedAnchorDate, int? GraceDays,
    DateTime StartDate, DateTime? BaselineDoneDate, decimal? BaselineDoneHours);

public record PmsCompletionInput(DateTime DoneDate, decimal? HoursAtDone, DateTime At, Guid Id);

public record PmsReadingInput(DateTime Date, decimal Hours, bool IsCorrection, DateTime At, Guid Id);

/// <summary>A job's schedule at one moment: what was last done, when it is next due and why.</summary>
public record PmsSchedule(
    DateTime? LastDoneDate, decimal? LastDoneHours, DateTime? CalendarDue, DateTime? HoursDue, DateTime? NextDue, string DueBasis,
    bool HoursEstimated, bool HoursUnknown, DateTime? LastReadingDate, decimal? AverageDailyHours, DateTime? GraceUntil);

/// <summary>
/// Works out when a job is next due. Pure: the same job, completions and readings always give the same answer, whatever order they arrived
/// in, so the vessel and the office agree. Due is whichever comes first, the calendar date or the date the running hours are projected to
/// reach the interval; the job is overdue once the grace window after that has passed.
/// </summary>
public static class PmsScheduler
{
    /// <summary>Running hours are averaged over this many days before the latest reading.</summary>
    public const int AverageWindowDays = 30;

    /// <summary>Hours-driven jobs are flagged "hours out of date" when the latest reading is older than this.</summary>
    public const int DefaultStaleDays = 7;

    /// <summary>A fixed-calendar job done within this share of an interval before its next date counts for that date (done a little early is still on time; done much earlier is not).</summary>
    private const double CoversNextDateShare = 0.25;

    public static int ApproxDays(int interval, PmsIntervalUnit unit) => unit switch
    {
        PmsIntervalUnit.Days => interval,
        PmsIntervalUnit.Weeks => interval * 7,
        PmsIntervalUnit.Months => interval * 30,
        _ => interval * 365,
    };

    /// <summary>The date <paramref name="steps"/> intervals after <paramref name="from"/>. Counted from the start each time, so month ends do not drift.</summary>
    public static DateTime Add(DateTime from, int interval, PmsIntervalUnit unit, int steps = 1)
    {
        var n = checked(interval * steps);
        return unit switch
        {
            PmsIntervalUnit.Days => from.AddDays(n),
            PmsIntervalUnit.Weeks => from.AddDays(7 * n),
            PmsIntervalUnit.Months => from.AddMonths(n),
            _ => from.AddYears(n),
        };
    }

    /// <summary>One reading per date: a correction beats an ordinary reading, and among those the latest recorded wins. Oldest date first.</summary>
    public static List<PmsReadingInput> EffectiveReadings(IEnumerable<PmsReadingInput> readings) => readings
        .GroupBy(r => r.Date.Date)
        .Select(g => g.OrderByDescending(r => r.IsCorrection).ThenByDescending(r => r.At).ThenByDescending(r => r.Id).First())
        .OrderBy(r => r.Date).ToList();

    public static PmsSchedule Compute(PmsJobInput job, IEnumerable<PmsCompletionInput> completions, IEnumerable<PmsReadingInput> readings)
    {
        var effective = EffectiveReadings(readings);
        var last = completions.OrderByDescending(c => c.DoneDate.Date).ThenByDescending(c => c.At).ThenByDescending(c => c.Id).FirstOrDefault();

        var lastDate = (last?.DoneDate ?? job.BaselineDoneDate ?? job.StartDate).Date;
        decimal? lastHours = last is not null
            ? last.HoursAtDone ?? AtOrBefore(effective, lastDate)?.Hours
            : job.BaselineDoneHours ?? AtOrBefore(effective, lastDate)?.Hours ?? effective.FirstOrDefault()?.Hours;   // a new job counts from the first reading it has

        var calendarDue = CalendarDue(job, lastDate, doneForReal: last is not null);

        DateTime? hoursDue = null;
        decimal? average = null;
        var estimated = false;
        var hoursUnknown = false;
        var latest = effective.LastOrDefault();
        if (job.HoursInterval is > 0)
        {
            if (lastHours is null || latest is null) hoursUnknown = true;
            else
            {
                var target = lastHours.Value + job.HoursInterval.Value;
                if (latest.Hours >= target)
                    hoursDue = effective.First(r => r.Hours >= target).Date.Date;            // already reached: due from the day the meter got there
                else
                {
                    average = AverageDaily(effective, latest);
                    if (average is > 0)
                    {
                        hoursDue = latest.Date.Date.AddDays((int)Math.Ceiling((double)((target - latest.Hours) / average.Value)));
                        estimated = true;
                    }
                    else hoursUnknown = true;
                }
            }
        }

        DateTime? next = (calendarDue, hoursDue) switch
        {
            ({ } c, { } h) => c <= h ? c : h,
            ({ } c, null) => c,
            (null, { } h) => h,
            _ => null,
        };
        var basis = next is null ? "" : calendarDue is { } cd && next == cd ? "calendar" : "hours";
        DateTime? graceUntil = next is { } n ? n.AddDays(GraceDaysFor(job, average)) : null;

        return new PmsSchedule(last is null && job.BaselineDoneDate is null ? null : lastDate, last is null && job.BaselineDoneDate is null ? null : lastHours,
            calendarDue, hoursDue, next, basis, estimated, hoursUnknown, latest?.Date.Date, average, graceUntil);
    }

    private static DateTime? CalendarDue(PmsJobInput job, DateTime lastDate, bool doneForReal)
    {
        if (job.CalendarInterval is not > 0) return null;
        var interval = job.CalendarInterval.Value;
        if (job.Anchor == PmsAnchor.FromCompletion) return Add(lastDate, interval, job.Unit);

        // Fixed calendar: due dates sit on a grid from the anchor. The next one is the first grid date after the last done date, unless the
        // job was done well ahead of it, in which case that date is already covered and the one after counts.
        var anchor = (job.FixedAnchorDate ?? job.StartDate).Date;
        var approx = Math.Max(1, ApproxDays(interval, job.Unit));
        var k = Math.Max(0, (lastDate - anchor).Days / approx - 1);
        while (Add(anchor, interval, job.Unit, k) <= lastDate) k++;
        var next = Add(anchor, interval, job.Unit, k);
        if (doneForReal && (next - lastDate).Days < approx * CoversNextDateShare) next = Add(anchor, interval, job.Unit, k + 1);
        return next;
    }

    private static PmsReadingInput? AtOrBefore(List<PmsReadingInput> effective, DateTime date)
        => effective.LastOrDefault(r => r.Date.Date <= date.Date);

    /// <summary>Hours per day over the window ending at the latest reading. Null when there are not two readings to compare.</summary>
    private static decimal? AverageDaily(List<PmsReadingInput> effective, PmsReadingInput latest)
    {
        var from = latest.Date.Date.AddDays(-AverageWindowDays);
        var earliest = effective.FirstOrDefault(r => r.Date.Date >= from && r.Date.Date < latest.Date.Date);
        if (earliest is null) return null;
        var days = (latest.Date.Date - earliest.Date.Date).Days;
        return days <= 0 ? null : (latest.Hours - earliest.Hours) / days;
    }

    private static int GraceDaysFor(PmsJobInput job, decimal? averageDaily)
    {
        if (job.GraceDays is { } set) return Math.Max(0, set);
        if (job.CalendarInterval is > 0) return Math.Max(1, (int)Math.Round(0.1 * ApproxDays(job.CalendarInterval.Value, job.Unit)));
        return 7;                                                                                                                      // hours-only jobs: a week
    }

    public static PmsStatus StatusOf(PmsSchedule schedule, DateTime today)
    {
        if (schedule.NextDue is not { } due) return PmsStatus.NoSchedule;
        if (today.Date < due.Date) return PmsStatus.NotDue;
        return schedule.GraceUntil is { } until && today.Date > until.Date ? PmsStatus.Overdue : PmsStatus.Due;
    }

    /// <summary>An hours-driven job whose latest reading is older than <paramref name="staleDays"/> (or that has none): its hours date cannot be trusted.</summary>
    public static bool HoursStale(PmsSchedule schedule, bool usesHours, DateTime today, int staleDays = DefaultStaleDays)
        => usesHours && (schedule.LastReadingDate is not { } read || (today.Date - read.Date).Days > staleDays);
}
