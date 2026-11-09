using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>The pure part of signing and breaches: signing periods, gaps, whether a signature still matches, and where a breach stands as people write about it.</summary>
public class RestReviewTests
{
    private static readonly Guid Crew = Guid.NewGuid();
    private static DateTime D(int day) => new(2026, 3, day, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int day, int hour = 12) => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);
    private const string Normal = "08:00-12:00,13:00-17:00";
    private const string TooLong = "05:00-12:00,13:00-20:15";       // 14 h 15 of work: 9 h 45 of rest, 15 minutes short, and no other rule broken

    private static RestEntry Entry(int day, string spans, DateTime enteredAt) => new() { Id = Guid.CreateVersion7(), CrewMemberId = Crew, Date = D(day), WorkSpans = spans, EnteredAt = enteredAt };

    private static RestBreachNote Note(int day, RestNoteKind kind, DateTime at, string text = "because", RestExceptionKind exception = RestExceptionKind.None, string rule = "Rest24")
        => new() { Id = Guid.CreateVersion7(), CrewMemberId = Crew, Date = D(day), Rule = rule, Kind = kind, ExceptionKind = exception, Text = text, At = at };

    private static CrewBook Book(List<RestEntry> entries, List<RestBreachNote>? notes = null, List<RestSignOff>? signs = null, DateTime? first = null)
        => new(Crew, "Test Crew", "Third Officer", null, first ?? entries.Min(e => e.Date), entries, notes ?? [], signs ?? []);

    /// <summary>The first nine days of March as ordinary days entered on the day itself, then the tenth as given.</summary>
    private static List<RestEntry> Nine(string tenth) => [.. Enumerable.Range(1, 9).Select(d => Entry(d, Normal, At(d, 18))), Entry(10, tenth, At(10, 22))];

    private static List<BreachView> BreachesOf(CrewBook book) => RestReview.Breaches(book, RestRules.Evaluate(RestLimits.Default, book.Inputs, D(1), D(10), null));

    // ---- periods and gaps ----

    [Fact]
    public void AMonthIsCutIntoRunsOfTheInterval_TheLastOneShorter_AndALongIntervalMakesItOnePeriod()
    {
        var weeks = RestReview.Periods(2026, 2, 7);
        Assert.Equal([(1, 7), (8, 14), (15, 21), (22, 28)], weeks.Select(p => (p.From.Day, p.To.Day)));
        Assert.Equal([(1, 7), (8, 14), (15, 21), (22, 28), (29, 31)], RestReview.Periods(2026, 3, 7).Select(p => (p.From.Day, p.To.Day)));
        Assert.Equal(31, RestReview.Periods(2026, 3, 1).Count);
        var whole = Assert.Single(RestReview.Periods(2026, 3, 31));
        Assert.Equal((1, 31), (whole.From.Day, whole.To.Day));
        Assert.Single(RestReview.Periods(2026, 3, 99));            // an interval beyond a month is held to a month
    }

    [Fact]
    public void ADayBeforeTheSeafarersFirstRecordIsNotAGap_ADayLeftOutAfterItIs_AndTheFutureIsNeither()
    {
        var days = new[] { 3, 4, 6, 7 }.Select(d => new RestDayInput(D(d), RestSpans.Parse(Normal))).ToList();
        Assert.Equal([D(5)], RestReview.Gaps(D(1), D(7), D(3), days, today: D(20)));
        Assert.Equal([D(1), D(2), D(5)], RestReview.Gaps(D(1), D(7), D(1), days, today: D(20)));
        Assert.Empty(RestReview.Gaps(D(5), D(7), D(3), days.Where(d => d.Date.Day != 5).Concat([new RestDayInput(D(5), [])]).ToList(), today: D(20)));       // a day recorded as all rest is recorded
        Assert.Equal([D(5)], RestReview.Gaps(D(1), D(10), D(3), days, today: D(5)));                                                                         // 8 to 10 have not happened yet
    }

    // ---- signatures ----

    [Fact]
    public void ASignatureMatchesTheDaysItWasGivenFor_UntilOneOfThemIsRevised_OrAGapIsFilled()
    {
        var entries = Enumerable.Range(1, 3).Select(d => Entry(d, Normal, At(d))).ToList();
        var sign = new RestSignOff { Id = Guid.CreateVersion7(), CrewMemberId = Crew, FromDate = D(1), ToDate = D(4), Role = RestRole.Seafarer, Fingerprint = RestRules.Fingerprint(RestHoursService.Inputs(entries), D(1), D(4)) };
        Assert.True(RestReview.IsCurrent(sign, RestHoursService.Inputs(entries)));

        var corrected = entries.Append(Entry(2, "08:00-12:00,13:00-18:00", At(5))).ToList();
        Assert.False(RestReview.IsCurrent(sign, RestHoursService.Inputs(corrected)));

        var reverted = corrected.Append(Entry(2, Normal, At(6))).ToList();
        Assert.True(RestReview.IsCurrent(sign, RestHoursService.Inputs(reverted)));                       // the same days again, the same signature

        var filled = entries.Append(Entry(4, Normal, At(7))).ToList();
        Assert.False(RestReview.IsCurrent(sign, RestHoursService.Inputs(filled)));                        // the gap on the 4th was part of what was signed
        Assert.True(RestReview.IsCurrent(sign, RestHoursService.Inputs(entries.Append(Entry(9, Normal, At(7))))));       // a day outside the period changes nothing
    }

    [Fact]
    public void ThePeriodShowsWhoHasSigned_AndWhetherTheirSignatureStillHolds()
    {
        var entries = Enumerable.Range(1, 7).Select(d => Entry(d, Normal, At(d))).ToList();
        var inputs = RestHoursService.Inputs(entries);
        RestSignOff Sign(RestRole role, DateTime at, IEnumerable<RestDayInput>? over = null) => new() { Id = Guid.CreateVersion7(), CrewMemberId = Crew, FromDate = D(1), ToDate = D(7), Role = role, Fingerprint = RestRules.Fingerprint(over ?? inputs, D(1), D(7)), SignedAt = at };

        var none = RestReview.PeriodOf(Book(entries), D(1), D(7), today: D(8), []);
        Assert.Equal((7, 7, true, false, false), (none.Days, none.Recorded, none.Ended, none.SeafarerSigned, none.MasterSigned));

        var signed = RestReview.PeriodOf(Book(entries, signs: [Sign(RestRole.Seafarer, At(8)), Sign(RestRole.Master, At(9))]), D(1), D(7), D(10), []);
        Assert.True(signed.Complete);
        Assert.False(signed.OutOfDate);

        var changed = entries.Append(Entry(3, "08:00-12:00,13:00-19:00", At(11))).ToList();
        var stale = RestReview.PeriodOf(Book(changed, signs: [Sign(RestRole.Seafarer, At(8)), Sign(RestRole.Master, At(9))]), D(1), D(7), D(12), []);
        Assert.False(stale.Complete);
        Assert.True(stale.OutOfDate);
        Assert.Equal((false, false), (stale.SeafarerSigned, stale.MasterSigned));

        // A later signature of the same role is the one that counts; a period not yet over is not "ended".
        var again = RestReview.PeriodOf(Book(changed, signs: [Sign(RestRole.Seafarer, At(8)), Sign(RestRole.Seafarer, At(12), RestHoursService.Inputs(changed))]), D(1), D(7), D(12), []);
        Assert.True(again.SeafarerSigned);
        Assert.False(RestReview.PeriodOf(Book(entries), D(1), D(7), today: D(5), []).Ended);
    }

    // ---- breaches ----

    [Fact]
    public void ABreachGoesFromUnexplained_ToExplained_ToAcknowledged_AndBackToTheMasterWhenTheDayIsChanged()
    {
        var entries = Nine(TooLong);
        var breach = Assert.Single(BreachesOf(Book(entries)));
        Assert.Equal((RestRule.Rest24, D(10), 585, 15), (breach.Finding.Rule, breach.Finding.Date, breach.Finding.Actual, breach.OwedMinutes));
        Assert.Equal(BreachStatus.Unexplained, breach.Status);

        var explained = new List<RestBreachNote> { Note(10, RestNoteKind.Explanation, At(11, 8), "Cargo operation ran over") };
        Assert.Equal(BreachStatus.Explained, Assert.Single(BreachesOf(Book(entries, explained))).Status);

        var acknowledged = explained.Append(Note(10, RestNoteKind.MasterAcknowledged, At(11, 9))).ToList();
        var ack = Assert.Single(BreachesOf(Book(entries, acknowledged)));
        Assert.Equal(BreachStatus.Acknowledged, ack.Status);
        Assert.True(ack.Resolved);

        // Another day of the week is revised afterwards: the 24 hours the finding rests on are the 9th and 10th, so that is what matters.
        var later = entries.Append(Entry(10, "06:00-20:00", At(12))).ToList();                    // the 10th is corrected and no longer breaks the rule
        Assert.Empty(BreachesOf(Book(later, acknowledged)));
        var stillBreach = entries.Append(Entry(9, "08:00-12:00,13:00-17:30", At(12))).ToList();   // the 9th changes; the 10th still breaches
        Assert.Equal(BreachStatus.Stale, Assert.Single(BreachesOf(Book(stillBreach, acknowledged))).Status);
        var elsewhere = entries.Append(Entry(3, "08:00-12:00,13:00-17:30", At(12))).ToList();     // a week earlier day: not what a 24-hour finding rests on
        Assert.Equal(BreachStatus.Acknowledged, Assert.Single(BreachesOf(Book(elsewhere, acknowledged))).Status);
    }

    [Fact]
    public void AnExceptionResolvesTheBreach_OwesCompensatoryRestUntilTheMasterConfirmsIt_AndCanBeWithdrawn()
    {
        var entries = Nine(TooLong);
        var notes = new List<RestBreachNote>
        {
            Note(10, RestNoteKind.Explanation, At(11, 8), "Mooring in bad weather"),
            Note(10, RestNoteKind.Exception, At(11, 9), "Overriding operational condition", RestExceptionKind.OperationalNeed),
        };
        var excepted = Assert.Single(BreachesOf(Book(entries, notes)));
        Assert.Equal(BreachStatus.Excepted, excepted.Status);
        Assert.True(excepted.Resolved);
        Assert.True(excepted.CompensatoryOutstanding);
        Assert.Equal(15, excepted.OwedMinutes);

        notes.Add(Note(10, RestNoteKind.CompensatoryRestGiven, At(12), "Given on the 11th, 12:00 to 12:15"));
        var done = Assert.Single(BreachesOf(Book(entries, notes)));
        Assert.Equal(BreachStatus.Excepted, done.Status);
        Assert.False(done.CompensatoryOutstanding);

        notes.Add(Note(10, RestNoteKind.Exception, At(13), "Declared in error", RestExceptionKind.None));
        var withdrawn = Assert.Single(BreachesOf(Book(entries, notes)));
        Assert.Equal(BreachStatus.Explained, withdrawn.Status);                                       // the explanation is still there; the Master has to look again
        Assert.False(withdrawn.CompensatoryOutstanding);

        // An exception declared again starts the question of compensatory rest again.
        notes.Add(Note(10, RestNoteKind.Exception, At(14), "Declared again", RestExceptionKind.Emergency));
        Assert.True(Assert.Single(BreachesOf(Book(entries, notes))).CompensatoryOutstanding);
    }

    [Fact]
    public void OneLongSpellOfWork_IsOneBreach_NotOneOnEveryDateItsWindowsTouch()
    {
        // The 24 hours ending at 00:15 the next day hold the same long spell. It is not a second breach on the 11th, whether the 11th is worked or not.
        var worked = Nine(TooLong).Append(Entry(11, Normal, At(11, 20))).ToList();
        var days = RestRules.Evaluate(RestLimits.Default, Book(worked).Inputs, D(1), D(11), null);
        Assert.Equal([D(10)], days.SelectMany(d => d.Findings).Select(f => f.Date));
        Assert.Equal(RestDayState.Ok, days[^1].State);

        var restDay = Nine(TooLong).Append(Entry(11, "", At(11, 20))).ToList();
        var rested = RestRules.Evaluate(RestLimits.Default, Book(restDay).Inputs, D(1), D(11), null);
        Assert.Equal([D(10)], rested.SelectMany(d => d.Findings).Select(f => f.Date));
        Assert.Equal(1440, rested[^1].Worst24);                                        // a day with no work still shows the rest it had
    }

    [Fact]
    public void WhatIsSaidAboutOneRuleOrOneDayDoesNotAnswerAnother()
    {
        var entries = Nine(TooLong);
        var notes = new List<RestBreachNote>
        {
            Note(10, RestNoteKind.Explanation, At(11), rule: "Rest7"),             // another rule, same day
            Note(9, RestNoteKind.Explanation, At(11)),                             // same rule, another day
        };
        Assert.Equal(BreachStatus.Unexplained, Assert.Single(BreachesOf(Book(entries, notes))).Status);
    }
}
