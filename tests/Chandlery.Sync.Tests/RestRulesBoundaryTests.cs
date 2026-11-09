using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The edges of the rules: exactly on a limit, one quarter hour either side, and the places where what is not known must not be judged as if it were.
/// Each test here was written because a deliberate change to the engine (a mutation check, 2026-11-09) got past the first set of tests.
/// </summary>
public class RestRulesBoundaryTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime Day(int n) => Start.AddDays(n);
    private static RestDayInput D(int n, string spans) => new(Day(n), RestSpans.Parse(spans));
    private static List<RestDayInput> Repeat(int from, int to, string spans) => Enumerable.Range(from, to - from + 1).Select(n => D(n, spans)).ToList();

    private static List<RestDayResult> Check(IEnumerable<RestDayInput> days, int from, int to, RestLimits? limits = null, Func<DateTime, bool>? minor = null)
        => RestRules.Evaluate(limits ?? RestLimits.Default, days, Day(from), Day(to), minor);

    private static readonly RestLimits WithReduction = RestLimits.Default with { ReductionAllowed = true };

    // ---- exactly on a limit is enough; a quarter hour under is not ----

    [Fact]
    public void SeventySevenHoursInAWeekIsEnough_SeventySixAndThreeQuartersIsNot()
    {
        var exact = Repeat(0, 6, "06:00-19:00");                                      // 13 h of work a day: 11 h of rest, 77 h in the week
        Assert.Empty(Check(exact, 6, 6)[0].Findings);

        var days = Repeat(0, 5, "06:00-19:00");
        days.Add(D(6, "06:00-19:15"));                                                  // a quarter hour more on the last day
        var f = Assert.Single(Check(days, 6, 6)[0].Findings);
        Assert.Equal((RestRule.Rest7, 4605, 4620), (f.Rule, f.Actual, f.Limit));
    }

    [Fact]
    public void ALongestRestOfExactlySixHoursIsEnough()
    {
        var results = Check(Repeat(0, 14, "06:00-12:00,18:00-24:00"), 8, 14);           // two rests of 6 h a day
        Assert.All(results, r => Assert.Empty(r.Findings));
        Assert.All(results, r => Assert.Equal(RestDayState.Ok, r.State));
    }

    [Fact]
    public void ARestThatMayContinueIntoDaysNotRecordedIsNotJudgedTooShort()
    {
        // Alone, the day shows rests of 4 h at each end, but the day before and the day after are unknown: they may go on for hours.
        var result = Check([D(0, "04:00-10:00,14:00-20:00")], 0, 0)[0];
        Assert.Empty(result.Findings);
        Assert.Equal(RestDayState.Ok, result.State);
    }

    [Fact]
    public void TwentyHoursOfWorkFromTheStartOfTheRecordIsAnIntervalBreach()
    {
        var findings = Check([D(0, "00:00-20:00")], 0, 0)[0].Findings;
        var interval = Assert.Single(findings, f => f.Rule == RestRule.Interval);
        Assert.Equal((1200, 840), (interval.Actual, interval.Limit));                    // measured from the first record: at least 20 h without rest
    }

    // ---- the shorter rest ----

    [Fact]
    public void ARestOfExactlySixConsecutiveHoursQualifiesForTheShorterRest()
    {
        var days = Repeat(0, 5, "06:00-19:00");
        days.Add(D(6, "06:00-24:00"));                                                  // 6 h of rest, all of it in one piece; the week still holds 72 h
        var result = Check(days, 6, 6, WithReduction)[0];
        Assert.True(result.ReducedDay);
        Assert.Empty(result.Findings);
        Assert.Equal(RestDayState.OkByReduction, result.State);
    }

    [Fact]
    public void TheShorterRestIsCountedOverTheLastSevenDaysIncludingTheFirstOfThem()
    {
        const string Short = "06:00-24:00";                                              // 6 h of rest: the shorter rest
        const string Easy = "10:00-17:00";
        var days = new List<RestDayInput> { D(0, Easy), D(1, Short), D(2, Easy), D(3, Easy), D(4, Short), D(5, Easy), D(6, Easy), D(7, Short) };
        var findings = Check(days, 7, 7, WithReduction)[0].Findings;
        var f = Assert.Single(findings);
        Assert.Equal((RestRule.ReductionLimit, 3, 2), (f.Rule, f.Actual, f.Limit));      // days 1, 4 and 7 are seven days from first to last

        // Two of them are allowed.
        Assert.Empty(Check(days.Take(7), 6, 6, WithReduction)[0].Findings);
        Assert.Empty(Check(days, 4, 4, WithReduction)[0].Findings);
    }

    [Fact]
    public void WhenSeveralWindowsShareTheLeastRest_TheOneWithTheLongestUnbrokenRestDecidesTheShorterRest()
    {
        // The day's least rest is 8 h in more than one 24-hour window; in the first of them it is broken into pieces of at most 5 h, in a later one there is a piece of 6 h.
        var days = new List<RestDayInput> { D(0, "07:00-09:00,12:00-17:00"), D(1, "07:00-16:00,17:00-22:00"), D(2, "00:00-02:00,08:00-21:00") };
        var result = Check(days, 2, 2, WithReduction)[0];
        Assert.Equal(480, result.Worst24);
        Assert.True(result.ReducedDay);
        Assert.DoesNotContain(result.Findings, f => f.Rule == RestRule.Rest24);
    }

    [Fact]
    public void RestThatMayContinueIntoADayNotRecordedIsNotCountedAsAnUnbrokenRestForTheShorterRest()
    {
        // Alone, 04:00-20:00 leaves 8 h in two pieces of 4 h. A window that started the evening before would join them, but that day is not recorded.
        var result = Check([D(0, "04:00-20:00")], 0, 0, WithReduction)[0];
        Assert.False(result.ReducedDay);
        Assert.Contains(result.Findings, f => f.Rule == RestRule.Rest24 && f.Actual == 480);
        Assert.Equal(RestDayState.Breach, result.State);
    }

    // ---- windows and days ----

    [Fact]
    public void AWeekShortOfRestIsReportedOnTheDayItEndsOn_NotAgainOnTheDayAfter()
    {
        var days = Repeat(0, 5, "06:00-19:00");
        days.Add(D(6, "06:00-19:15"));
        days.Add(D(7, ""));                                                              // a whole day of rest
        var results = Check(days, 6, 7);
        Assert.Contains(results[0].Findings, f => f.Rule == RestRule.Rest7);
        Assert.Empty(results[1].Findings);
    }

    // ---- young seafarers ----

    [Fact]
    public void TheProtectionOfSeafarersUnder18CanBeSwitchedOffInTheRuleSet()
    {
        var night = new[] { D(0, "00:00-04:00,12:00-16:00") };
        Assert.Contains(Check(night, 0, 0, RestLimits.Default, _ => true)[0].Findings, f => f.Rule == RestRule.MinorNight);
        var off = RestLimits.Default with { MinorNightProtection = false };
        Assert.DoesNotContain(Check(night, 0, 0, off, _ => true)[0].Findings, f => f.Rule == RestRule.MinorNight);
    }
}
