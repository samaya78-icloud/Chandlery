using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The rules of seafarers' hours of rest (ILO 180 art. 5, STCW A-VIII/1), as pure functions on recorded days.</summary>
public class RestRulesTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime Day(int n) => Start.AddDays(n);
    private static RestDayInput D(int n, string spans) => new(Day(n), RestSpans.Parse(spans));
    private static List<RestDayInput> Repeat(int from, int to, string spans) => Enumerable.Range(from, to - from + 1).Select(n => D(n, spans)).ToList();

    private static RestDayResult On(List<RestDayResult> results, int n) => results.Single(r => r.Date == Day(n));

    private static List<RestDayResult> Check(IEnumerable<RestDayInput> days, int from, int to, RestLimits? limits = null, Func<DateTime, bool>? minor = null)
        => RestRules.Evaluate(limits ?? RestLimits.Default, days, Day(from), Day(to), minor);

    /// <summary>The shorter rest of STCW A-VIII/1.4 is off by default (it needs the flag state's permission); a rule set turns it on.</summary>
    private static readonly RestLimits WithReduction = RestLimits.Default with { ReductionAllowed = true };

    // The classic 4-on, 8-off watch, and an ordinary day worker with a lunch break.
    private const string Watch48 = "04:00-08:00,16:00-20:00";
    private const string DayWork = "08:00-12:00,13:00-17:00";

    // ---- the text form of a day ----

    [Fact]
    public void WorkPeriodsAreReadAndWrittenAsText_ChecksCatchWhatIsNotAPeriod()
    {
        var spans = RestSpans.Parse("16:00-20:00, 00:00-04:00;04:00-05:30");
        Assert.Equal("00:00-05:30,16:00-20:00", RestSpans.Format(RestSpans.Normalize(spans)));         // sorted, touching periods joined
        Assert.Equal("00:00-24:00", RestSpans.Format(RestSpans.Parse("0000-2400")));
        Assert.Empty(RestSpans.Parse(""));
        Assert.Equal(570, RestSpans.Minutes(RestSpans.Normalize(spans)));

        Assert.Null(RestSpans.Validate(RestSpans.Parse("00:00-04:00,04:00-06:00")));
        Assert.Contains("overlap", RestSpans.Validate(RestSpans.Parse("00:00-05:00,04:00-06:00")));
        Assert.Contains("quarter hour", RestSpans.Validate(RestSpans.Parse("00:10-04:00")));
        Assert.Contains("within one day", RestSpans.Validate(RestSpans.Parse("22:00-02:00")));          // over midnight is entered on both days
        Assert.Throws<FormatException>(() => RestSpans.Parse("0600"));
        Assert.Throws<FormatException>(() => RestSpans.Parse("25:00-26:00"));
        Assert.Throws<FormatException>(() => RestSpans.Parse("12:60-13:00"));
    }

    // ---- ordinary patterns comply ----

    [Fact]
    public void AFourOnEightOffWatchComplies_ARestPeriodOverMidnightIsOnePeriod()
    {
        var results = Check(Repeat(0, 14, Watch48), 8, 14);
        Assert.All(results, r => Assert.Equal(RestDayState.Ok, r.State));
        Assert.All(results, r => Assert.Empty(r.Findings));
        Assert.Equal(480, On(results, 10).WorkMinutes);
        Assert.Equal(960, On(results, 10).RestMinutes);
        Assert.Equal(960, On(results, 10).Worst24);                      // 16 hours of rest in the worst 24 hours
        Assert.Equal(7 * 960, On(results, 10).Worst7);
    }

    [Fact]
    public void AnOrdinaryWorkingDayComplies()
    {
        Assert.All(Check(Repeat(0, 14, DayWork), 8, 14), r => Assert.Equal(RestDayState.Ok, r.State));
    }

    // ---- rest in any 24 hours ----

    [Fact]
    public void TenHoursOfRestIsEnough_NineAndThreeQuarterIsNot()
    {
        // 14 hours of work leaves exactly 10 hours: allowed.
        var exact = Check(Repeat(0, 14, "06:00-20:00"), 8, 8);
        Assert.DoesNotContain(exact[0].Findings, f => f.Rule == RestRule.Rest24);                // (the week is another matter, see below)
        Assert.Equal(600, exact[0].Worst24);

        // 14 hours 15 minutes of work on one day leaves 9 hours 45.
        var days = Repeat(0, 7, DayWork);
        days.Add(D(8, "06:00-20:15"));
        var results = Check(days, 8, 8);
        var breach = Assert.Single(results[0].Findings, f => f.Rule == RestRule.Rest24);
        Assert.Equal(RestDayState.Breach, results[0].State);
        Assert.Equal((585, 600), (breach.Actual, breach.Limit));
        Assert.Contains("9h 45m", breach.Message);

        // Where the flag state allows the shorter rest, 9 hours 45 with a long unbroken period passes as a reduced day instead.
        Assert.Equal(RestDayState.OkByReduction, Check(days, 8, 8, WithReduction)[0].State);
    }

    [Fact]
    public void TheWindowIsAnyTwentyFourHours_NotJustACalendarDay()
    {
        // Day 8 ends with 10 hours of work, day 9 starts with 5: each calendar day has plenty of rest, but the 24 hours
        // ending at 05:00 on day 9 hold 15 hours of work and only 9 of rest.
        var days = Repeat(0, 7, DayWork);
        days.Add(D(8, "14:00-24:00"));
        days.Add(D(9, "00:00-05:00"));
        var results = Check(days, 8, 9);

        Assert.Equal(RestDayState.Ok, results[0].State);
        Assert.Equal(840, results[0].RestMinutes);
        var breach = Assert.Single(results[1].Findings, f => f.Rule == RestRule.Rest24);
        Assert.Equal(540, breach.Actual);
        Assert.Equal(Day(9).AddHours(5), breach.WindowEnd);
        Assert.Equal(1140, results[1].RestMinutes);                       // an easy calendar day by itself
    }

    // ---- how rest is divided ----

    [Fact]
    public void RestInThreePeriodsIsOneTooMany()
    {
        var results = Check(Repeat(0, 10, "06:00-08:00,12:00-14:00,18:00-20:00"), 8, 9);
        var f = Assert.Single(results[0].Findings);
        Assert.Equal((RestRule.Periods, 3, 2), (f.Rule, f.Actual, f.Limit));
    }

    [Fact]
    public void OneRestPeriodMustBeAtLeastSixHours()
    {
        // Ten hours of rest, but as two periods of five.
        var results = Check(Repeat(0, 10, "05:00-10:00,15:00-24:00"), 8, 9);
        var f = Assert.Single(results[0].Findings, x => x.Rule == RestRule.LongestPeriod);
        Assert.Equal((RestRule.LongestPeriod, 300, 360), (f.Rule, f.Actual, f.Limit));
    }

    [Fact]
    public void MoreThanFourteenHoursBetweenRestPeriodsIsFlagged()
    {
        var days = Repeat(0, 7, DayWork);
        days.Add(D(8, "05:00-20:00"));                                   // 15 hours of work in a row
        var results = Check(days, 8, 9);
        Assert.Contains(results.SelectMany(r => r.Findings), f => f.Rule == RestRule.Interval && f.Actual == 900 && f.Limit == 840);
    }

    // ---- the shorter rest of STCW A-VIII/1.4 ----

    [Fact]
    public void ADayWithSixConsecutiveHoursOfRestPassesAsReduced_WhenTheWeekStillHolds70Hours()
    {
        // Four days of 14 hours of work, two of 13 and a 16 hour day: the week holds exactly 70 hours of rest.
        var days = Repeat(0, 4, "06:00-20:00");
        days.AddRange(Repeat(5, 6, "06:00-19:00"));
        days.Add(D(7, "06:00-22:00"));                                   // only 8 hours of rest in the worst 24
        var results = Check(days, 7, 7, WithReduction);
        Assert.Equal(RestDayState.OkByReduction, results[0].State);
        Assert.True(results[0].ReducedDay);
        Assert.Empty(results[0].Findings);
        Assert.Equal(480, results[0].Worst24);
        Assert.Equal(4200, results[0].Worst7);
    }

    [Fact]
    public void TheReductionBreaksWhenTheWeekFallsBelow70Hours()
    {
        var days = new List<RestDayInput>();
        days.AddRange(Repeat(0, 6, "06:00-20:00"));                      // 6 x 14 hours + 16 hours = 100 hours of work: 68 of rest
        days.Add(D(7, "06:00-22:00"));
        var results = Check(days, 7, 7, WithReduction);
        var f = Assert.Single(results[0].Findings);
        Assert.Equal((RestRule.Rest7, 4080, 4200), (f.Rule, f.Actual, f.Limit));
    }

    [Fact]
    public void WithoutThePermissionOfTheRuleSet_ASixteenHourDayIsABreach()
    {
        var days = Repeat(0, 6, DayWork);
        days.Add(D(7, "06:00-22:00"));
        var results = Check(days, 7, 7);                                  // the default rule set does not allow the shorter rest
        Assert.Equal(RestDayState.Breach, results[0].State);
        var f = Assert.Single(results[0].Findings, x => x.Rule == RestRule.Rest24);
        Assert.Equal((480, 600), (f.Actual, f.Limit));
        Assert.Contains(results[0].Findings, x => x.Rule == RestRule.Interval && x.Actual == 960);      // and 16 hours is more than 14 between rests
        Assert.False(results[0].ReducedDay);

        Assert.Equal(RestDayState.OkByReduction, Check(days, 7, 7, WithReduction)[0].State);              // where the rule set allows it, it passes
    }

    [Fact]
    public void TheReductionMayBeUsedOnTwoDaysOnly()
    {
        var days = Repeat(0, 4, DayWork);
        days.AddRange(Repeat(5, 7, "06:00-22:00"));                      // three 16-hour days in a row
        var results = Check(days, 5, 7, WithReduction);
        Assert.Empty(On(results, 5).Findings);
        Assert.Empty(On(results, 6).Findings);
        Assert.Equal(RestDayState.OkByReduction, On(results, 5).State);
        Assert.Equal(RestDayState.OkByReduction, On(results, 6).State);
        var third = Assert.Single(On(results, 7).Findings);
        Assert.Equal((RestRule.ReductionLimit, 3, 2), (third.Rule, third.Actual, third.Limit));
    }

    [Fact]
    public void ADayWithLessThanSixConsecutiveHoursIsABreachWhateverTheReduction()
    {
        var days = Repeat(0, 7, DayWork);
        days.Add(D(8, "05:00-24:00"));                                   // 5 hours of rest
        var f = Assert.Single(Check(days, 8, 8, WithReduction)[0].Findings, x => x.Rule == RestRule.Rest24);
        Assert.Equal(300, f.Actual);
    }

    // ---- rest in any 7 days ----

    [Fact]
    public void SeventySevenHoursInAWeekIsRequired_EvenWhenEveryDayHasTen()
    {
        var results = Check(Repeat(0, 14, "06:00-20:00"), 8, 14);        // 10 hours every day: 70 hours a week
        var f = results[0].Findings.Single(x => x.Rule == RestRule.Rest7);
        Assert.Equal((4200, 4620), (f.Actual, f.Limit));
        Assert.DoesNotContain(results[0].Findings, x => x.Rule == RestRule.Rest24);
    }

    // ---- young seafarers ----

    [Fact]
    public void ASeafarerUnder18MayNotWorkAtNight()
    {
        var days = Repeat(0, 9, "07:00-12:00,13:00-17:00");
        days.Add(D(10, "07:00-12:00,21:00-22:30"));                      // 90 minutes in the protected night (21:00 to 06:00)
        var teenager = Check(days, 10, 10, minor: _ => true);
        var f = Assert.Single(teenager[0].Findings);
        Assert.Equal((RestRule.MinorNight, 90, 0), (f.Rule, f.Actual, f.Limit));

        Assert.Empty(Check(days, 10, 10, minor: _ => false)[0].Findings);                  // an adult may
        Assert.Empty(Check(Repeat(0, 10, "07:00-12:00,13:00-17:00"), 10, 10, minor: _ => true)[0].Findings);
        Assert.Contains(Check(Repeat(0, 10, "05:30-06:30"), 10, 10, minor: _ => true)[0].Findings, x => x.Rule == RestRule.MinorNight && x.Actual == 30);   // before 06:00 counts too
    }

    // ---- gaps ----

    [Fact]
    public void ADayThatWasNotRecordedIsNotADayOfRest()
    {
        var days = Repeat(0, 14, DayWork).Where(d => d.Date != Day(10)).ToList();
        var results = Check(days, 9, 12);
        Assert.Equal(RestDayState.NotRecorded, On(results, 10).State);
        Assert.Equal(RestDayState.Ok, On(results, 9).State);
        Assert.Null(On(results, 11).Worst7);                              // a week that includes the gap is not tested
        Assert.Equal(RestDayState.Ok, On(results, 11).State);

        Assert.Equal(RestDayState.NotRecorded, Check([], 3, 3)[0].State);
    }

    [Fact]
    public void TheLastDayRecordedDoesNotCountItsOpenEndAgainstTheSeafarer()
    {
        // The record stops at the end of day 8, which closes with a rest that may well run on: not judged as a short rest period.
        var days = Repeat(0, 7, DayWork);
        days.Add(D(8, "08:00-22:00"));
        Assert.DoesNotContain(Check(days, 8, 8)[0].Findings, f => f.Rule == RestRule.LongestPeriod);
    }

    // ---- fingerprints ----

    [Fact]
    public void AFingerprintChangesWithAnyRevisedDay_AndSeparatesNotRecordedFromNoWork()
    {
        var days = Repeat(0, 6, DayWork);
        var one = RestRules.Fingerprint(days, Day(0), Day(6));
        Assert.Equal(one, RestRules.Fingerprint(days.AsEnumerable().Reverse(), Day(0), Day(6)));                 // order does not matter
        Assert.Equal(one, RestRules.Fingerprint(Repeat(0, 6, "13:00-17:00,08:00-12:00"), Day(0), Day(6)));      // the same day written differently

        var revised = Repeat(0, 6, DayWork);
        revised[3] = D(3, "08:00-12:00,13:00-17:15");
        Assert.NotEqual(one, RestRules.Fingerprint(revised, Day(0), Day(6)));

        var noWork = Repeat(0, 6, DayWork);
        noWork[3] = D(3, "");
        var gap = Repeat(0, 6, DayWork);
        gap.RemoveAt(3);
        Assert.NotEqual(RestRules.Fingerprint(noWork, Day(0), Day(6)), RestRules.Fingerprint(gap, Day(0), Day(6)));

        Assert.Equal(one, RestRules.Fingerprint(days.Append(D(20, "01:00-02:00")), Day(0), Day(6)));             // days outside the period are not part of it
    }

    [Fact]
    public void AnEngineThatIsGivenAMisalignedTimeRefusesIt()
    {
        Assert.Throws<ArgumentException>(() => Check([D(5, "08:10-12:00")], 5, 5));
    }
}
