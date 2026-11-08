using System.Diagnostics;
using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The scheduling rules of spec 07, one rule per test. Pure, so the whole behaviour can be pinned down without a database.</summary>
public class PmsSchedulerTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = D(2026, 1, 1);
    private static long _tick;

    private static PmsJobInput Job(int? calendar = null, PmsIntervalUnit unit = PmsIntervalUnit.Months, int? hours = null, PmsAnchor anchor = PmsAnchor.FromCompletion,
        DateTime? fixedAnchor = null, int? grace = null, DateTime? start = null, DateTime? baselineDate = null, decimal? baselineHours = null)
        => new(calendar, unit, hours, anchor, fixedAnchor, grace, start ?? Start, baselineDate, baselineHours);

    private static PmsCompletionInput Done(DateTime date, decimal? hours = null) => new(date, hours, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_tick++), Guid.CreateVersion7());
    private static PmsReadingInput Read(DateTime date, decimal hours, bool correction = false) => new(date, hours, correction, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_tick++), Guid.CreateVersion7());

    // ---- calendar ----

    [Fact]
    public void ANewCalendarJob_IsDueOneIntervalAfterItsStart_AndOverdueAfterTheGrace()
    {
        var s = PmsScheduler.Compute(Job(calendar: 3), [], []);
        Assert.Equal((D(2026, 4, 1), "calendar"), (s.NextDue, s.DueBasis));
        Assert.Equal(D(2026, 4, 10), s.GraceUntil);                                                    // a tenth of about 90 days
        Assert.Equal(PmsStatus.NotDue, PmsScheduler.StatusOf(s, D(2026, 3, 31)));
        Assert.Equal(PmsStatus.Due, PmsScheduler.StatusOf(s, D(2026, 4, 1)));
        Assert.Equal(PmsStatus.Due, PmsScheduler.StatusOf(s, D(2026, 4, 10)));
        Assert.Equal(PmsStatus.Overdue, PmsScheduler.StatusOf(s, D(2026, 4, 11)));
    }

    [Fact]
    public void AFloatingJob_CountsFromWhenItWasLastDone()
    {
        var s = PmsScheduler.Compute(Job(calendar: 3), [Done(D(2026, 4, 15))], []);
        Assert.Equal((D(2026, 4, 15), D(2026, 7, 15)), (s.LastDoneDate, s.NextDue));
    }

    [Fact]
    public void GraceCanBeSet_AndAJobWithNoIntervalHasNoSchedule()
    {
        var strict = PmsScheduler.Compute(Job(calendar: 1, grace: 0), [], []);
        Assert.Equal(PmsStatus.Due, PmsScheduler.StatusOf(strict, D(2026, 2, 1)));
        Assert.Equal(PmsStatus.Overdue, PmsScheduler.StatusOf(strict, D(2026, 2, 2)));

        var none = PmsScheduler.Compute(Job(), [], []);
        Assert.Null(none.NextDue);
        Assert.Equal(PmsStatus.NoSchedule, PmsScheduler.StatusOf(none, D(2030, 1, 1)));
    }

    [Fact]
    public void MonthEndsDoNotDrift_OnEitherKindOfAnchor()
    {
        Assert.Equal(D(2026, 2, 28), PmsScheduler.Compute(Job(calendar: 1, start: D(2026, 1, 31)), [], []).NextDue);

        // A fixed monthly grid from 31 January goes 28 Feb, 31 Mar (not 28 Mar).
        var fixedJob = Job(calendar: 1, anchor: PmsAnchor.FixedCalendar, fixedAnchor: D(2026, 1, 31), start: D(2026, 1, 31));
        Assert.Equal(D(2026, 3, 31), PmsScheduler.Compute(fixedJob, [Done(D(2026, 2, 28))], []).NextDue);
    }

    // ---- fixed calendar ----

    [Fact]
    public void AFixedCalendarJob_StaysOnItsGrid_AndAnEarlyCompletionCoversTheNextDate()
    {
        var annual = Job(calendar: 12, anchor: PmsAnchor.FixedCalendar, fixedAnchor: D(2026, 3, 1), start: D(2026, 1, 15));
        Assert.Equal(D(2026, 3, 1), PmsScheduler.Compute(annual, [], []).NextDue);                         // nothing done yet: the first date on the grid

        Assert.Equal(D(2027, 3, 1), PmsScheduler.Compute(annual, [Done(D(2026, 2, 20))], []).NextDue);     // 9 days early: counts for 1 March
        Assert.Equal(D(2027, 3, 1), PmsScheduler.Compute(annual, [Done(D(2026, 3, 5))], []).NextDue);      // a little late: the next 1 March
        Assert.Equal(D(2027, 3, 1), PmsScheduler.Compute(annual, [Done(D(2026, 9, 1))], []).NextDue);      // six months early does not count for 2027
        Assert.Equal(D(2028, 3, 1), PmsScheduler.Compute(annual, [Done(D(2026, 12, 15))], []).NextDue);    // 76 days before 2027-03-01: counts for it
    }

    // ---- running hours ----

    [Fact]
    public void AnHoursJob_IsProjectedFromTheAverageOverTheLastThirtyDays()
    {
        var job = Job(hours: 500, baselineDate: D(2026, 1, 1), baselineHours: 1000);
        var s = PmsScheduler.Compute(job, [], [Read(D(2026, 1, 1), 1000), Read(D(2026, 1, 31), 1300)]);
        Assert.Equal(10m, s.AverageDailyHours);                                                           // 300 hours in 30 days
        Assert.Equal((D(2026, 2, 20), "hours", true, false), (s.NextDue, s.DueBasis, s.HoursEstimated, s.HoursUnknown));   // 200 hours to go at 10 a day
    }

    [Fact]
    public void WhenTheMeterHasReachedTheInterval_ItIsDueFromThatDay_NotEstimated()
    {
        var job = Job(hours: 500, baselineDate: D(2026, 1, 1), baselineHours: 1000);
        var s = PmsScheduler.Compute(job, [], [Read(D(2026, 1, 1), 1000), Read(D(2026, 1, 20), 1400), Read(D(2026, 2, 10), 1510), Read(D(2026, 2, 20), 1600)]);
        Assert.Equal((D(2026, 2, 10), false), (s.NextDue, s.HoursEstimated));
    }

    [Fact]
    public void WithoutEnoughReadings_HoursAreUnknown_AndTheCalendarAloneDecides()
    {
        var onlyHours = PmsScheduler.Compute(Job(hours: 500, baselineHours: 1000), [], [Read(D(2026, 1, 31), 1100)]);
        Assert.Equal((null, true), (onlyHours.NextDue, onlyHours.HoursUnknown));

        var both = PmsScheduler.Compute(Job(calendar: 6, hours: 500, baselineHours: 1000), [], [Read(D(2026, 1, 31), 1100)]);
        Assert.Equal((D(2026, 7, 1), "calendar", true), (both.NextDue, both.DueBasis, both.HoursUnknown));

        var parked = PmsScheduler.Compute(Job(hours: 500, baselineDate: D(2026, 1, 1), baselineHours: 1000), [], [Read(D(2026, 1, 1), 1100), Read(D(2026, 1, 31), 1100)]);
        Assert.True(parked.HoursUnknown);                                                                  // a meter that is not running gives no date
    }

    [Fact]
    public void Whichever_ComesFirst_DecidesTheDueDate()
    {
        var readings = new[] { Read(D(2026, 1, 1), 1000), Read(D(2026, 1, 31), 1300) };                    // hours date 2026-02-20
        var calendarFirst = PmsScheduler.Compute(Job(calendar: 1, hours: 500, baselineDate: D(2026, 1, 1), baselineHours: 1000), [], readings);
        Assert.Equal((D(2026, 2, 1), "calendar"), (calendarFirst.NextDue, calendarFirst.DueBasis));
        var hoursFirst = PmsScheduler.Compute(Job(calendar: 6, hours: 500, baselineDate: D(2026, 1, 1), baselineHours: 1000), [], readings);
        Assert.Equal((D(2026, 2, 20), "hours"), (hoursFirst.NextDue, hoursFirst.DueBasis));
    }

    [Fact]
    public void ACompletionRestartsTheHoursCount_FromTheHoursAtDone_OrTheReadingOnOrBeforeTheDay()
    {
        var readings = new[] { Read(D(2026, 1, 1), 1000), Read(D(2026, 1, 31), 1300), Read(D(2026, 2, 10), 1400) };
        var given = PmsScheduler.Compute(Job(hours: 500), [Done(D(2026, 2, 1), hours: 1320)], readings);
        Assert.Equal(1320m, given.LastDoneHours);                                                          // the crew's own figure

        var derived = PmsScheduler.Compute(Job(hours: 500), [Done(D(2026, 2, 5))], readings);
        Assert.Equal(1300m, derived.LastDoneHours);                                                        // the reading on 31 January
    }

    [Fact]
    public void HoursOnlyJobsGetAWeekOfGrace_AndStaleReadingsAreFlagged()
    {
        var s = PmsScheduler.Compute(Job(hours: 500, baselineDate: D(2026, 1, 1), baselineHours: 1000), [], [Read(D(2026, 1, 1), 1000), Read(D(2026, 1, 31), 1300)]);
        Assert.Equal(D(2026, 2, 27), s.GraceUntil);
        Assert.False(PmsScheduler.HoursStale(s, usesHours: true, D(2026, 2, 7)));
        Assert.True(PmsScheduler.HoursStale(s, usesHours: true, D(2026, 2, 8)));                          // the last reading is 8 days old
        Assert.False(PmsScheduler.HoursStale(s, usesHours: false, D(2026, 6, 1)));                        // a calendar job never goes stale
        Assert.True(PmsScheduler.HoursStale(PmsScheduler.Compute(Job(hours: 500), [], []), usesHours: true, D(2026, 2, 1)));
    }

    // ---- corrections and order ----

    [Fact]
    public void ACorrectionBeatsAnOrdinaryReading_ForItsDate_EvenWhenAnOrdinaryOneIsRecordedLater()
    {
        var wrong = Read(D(2026, 1, 31), 1900);
        var fix = Read(D(2026, 1, 31), 1300, correction: true);
        var laterOrdinary = Read(D(2026, 1, 31), 1950);
        var effective = PmsScheduler.EffectiveReadings([wrong, fix, laterOrdinary]);
        Assert.Equal([1300m], effective.Select(r => r.Hours));

        var twoCorrections = PmsScheduler.EffectiveReadings([Read(D(2026, 1, 31), 1300, true), Read(D(2026, 1, 31), 1310, true)]);
        Assert.Equal([1310m], twoCorrections.Select(r => r.Hours));                                       // the latest correction wins
    }

    [Fact]
    public void CorrectingAReading_RecomputesTheDueDate_WithoutLosingTheOriginal()
    {
        var job = Job(hours: 500, baselineDate: D(2026, 1, 1), baselineHours: 1000);
        var early = Read(D(2026, 1, 1), 1000);
        var wrong = Read(D(2026, 1, 31), 1600);
        Assert.Equal(D(2026, 1, 31), PmsScheduler.Compute(job, [], [early, wrong]).NextDue);              // 1600 would mean the interval is already reached

        var corrected = Read(D(2026, 1, 31), 1300, correction: true);
        Assert.Equal(D(2026, 2, 20), PmsScheduler.Compute(job, [], [early, wrong, corrected]).NextDue);
    }

    [Fact]
    public void ABackDatedCompletion_NeverMovesTheScheduleBackwards()
    {
        var job = Job(calendar: 3);
        var march = Done(D(2026, 3, 1));
        var january = Done(D(2026, 1, 10));                                                                // entered later, but older
        Assert.Equal(D(2026, 6, 1), PmsScheduler.Compute(job, [march, january], []).NextDue);
        Assert.Equal(D(2026, 6, 1), PmsScheduler.Compute(job, [january, march], []).NextDue);
    }

    [Fact]
    public void TheOrderRecordsArriveIn_NeverChangesTheResult()
    {
        var job = Job(calendar: 6, hours: 750, baselineDate: D(2026, 1, 1), baselineHours: 1000);
        var completions = new[] { Done(D(2026, 2, 1), 1100), Done(D(2026, 5, 1), 1400), Done(D(2026, 4, 1)) };
        var readings = Enumerable.Range(0, 40).Select(i => Read(D(2026, 1, 1).AddDays(i * 4), 1000 + i * 40)).Append(Read(D(2026, 3, 1), 9999, true)).Append(Read(D(2026, 3, 1), 1200)).ToArray();

        var expected = PmsScheduler.Compute(job, completions, readings);
        var random = new Random(7);
        for (var i = 0; i < 50; i++)
            Assert.Equal(expected, PmsScheduler.Compute(job, completions.OrderBy(_ => random.Next()), readings.OrderBy(_ => random.Next())));
    }

    // ---- size ----

    [Fact]
    public void AVesselPlanOfFiveThousandJobs_IsWorkedOutQuickly()
    {
        var readings = Enumerable.Range(0, 400).Select(i => Read(D(2025, 1, 1).AddDays(i), 1000 + i * 22)).ToList();
        var completions = Enumerable.Range(0, 20).Select(i => Done(D(2025, 1, 1).AddDays(i * 15), 1000 + i * 330)).ToList();
        var job = Job(calendar: 6, hours: 500, start: D(2025, 1, 1));

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++) PmsScheduler.Compute(job, completions, readings);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }
}
