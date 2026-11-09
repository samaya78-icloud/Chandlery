using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>The office's view of the fleet, in its pure part: one seafarer's month summed up, the vessel's tone, and the CSV it can be exported as.</summary>
public class RestFleetTests
{
    private static readonly Guid Crew = Guid.NewGuid();
    private static readonly Guid Vessel = Guid.NewGuid();
    private static DateTime D(int day) => new(2026, 3, day, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int day, int hour = 12) => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);
    private const string Normal = "08:00-12:00,13:00-17:00";
    private const string TooLong = "05:00-12:00,13:00-20:15";       // 14 h 15 of work: 9 h 45 of rest on the day, 15 minutes short, and no other rule broken

    private static RestEntry Entry(int day, string spans, DateTime enteredAt) => new() { Id = Guid.CreateVersion7(), CrewMemberId = Crew, Date = D(day), WorkSpans = spans, EnteredAt = enteredAt };

    private static CrewBook Book(List<RestBreachNote>? notes = null, List<RestSignOff>? signs = null)
    {
        var entries = Enumerable.Range(1, 9).Select(d => Entry(d, Normal, At(d, 18))).Append(Entry(10, TooLong, At(10, 22))).ToList();
        return new CrewBook(Crew, "Test Crew", "Third Officer", null, D(1), entries, notes ?? [], signs ?? []);
    }

    private static RestCrewMonth March(CrewBook book, DateTime today) => RestFleet.CrewMonth(Vessel, "MV Test", book, RestLimits.Default, 7, 2026, 3, today);

    private static RestSignOff Sign(CrewBook book, RestRole role, int from, int to, string? fingerprint = null) => new()
    {
        Id = Guid.CreateVersion7(), CrewMemberId = Crew, FromDate = D(from), ToDate = D(to), Role = role, SignedByName = role.ToString(), SignedAt = At(11),
        Fingerprint = fingerprint ?? RestRules.Fingerprint(book.Inputs, D(from), D(to)),
    };

    [Fact]
    public void AMonthIsSummedUp_DaysThatHavePassedRecordedAndLeftOut_TheBreach_AndTheLeastRestFound()
    {
        var month = March(Book(), today: D(20));
        Assert.Equal((20, 10, 10), (month.Elapsed, month.Recorded, month.Missing));
        var breach = Assert.Single(month.Breaches);
        Assert.Equal((RestRule.Rest24, D(10), BreachStatus.Unexplained), (breach.Finding.Rule, breach.Finding.Date, breach.Status));
        Assert.Equal(1, month.Unresolved);
        Assert.Equal(585, month.Worst24);                    // 9 h 45
        Assert.False(month.Clean);
    }

    [Fact]
    public void PeriodsThatAreOverAreToSignUntilBothHaveSigned_AndASignatureForDaysThatChangedIsOutOfDate()
    {
        var book = Book();
        Assert.Equal(2, March(book, D(20)).ToSign);                                         // 1-7 and 8-14 are over and hold records; 15-21 is not over

        var signed = book with { Signs = [Sign(book, RestRole.Seafarer, 1, 7), Sign(book, RestRole.Master, 1, 7)] };
        var after = March(signed, D(20));
        Assert.Equal(1, after.ToSign);
        Assert.Equal(0, after.OutOfDate);

        // The ninth is corrected after the signatures were given for 8-14 on the old figures.
        var stale = book with { Signs = [Sign(book, RestRole.Seafarer, 8, 14, fingerprint: "not what was signed")] };
        var month = March(stale, D(20));
        Assert.Equal(1, month.OutOfDate);
        Assert.Equal(2, month.ToSign);
    }

    [Fact]
    public void ABreachTheMasterHasDealtWithIsNotCounted_AndAnExceptionOwesRestUntilItIsConfirmed()
    {
        RestBreachNote Note(RestNoteKind kind, int hour, RestExceptionKind exception = RestExceptionKind.None)
            => new() { Id = Guid.CreateVersion7(), CrewMemberId = Crew, Date = D(10), Rule = "Rest24", Kind = kind, ExceptionKind = exception, Text = "because", At = At(11, hour) };

        var explained = March(Book([Note(RestNoteKind.Explanation, 8)]), D(20));
        Assert.Equal((1, 0), (explained.Unresolved, explained.CompensatoryOutstanding));

        var excepted = March(Book([Note(RestNoteKind.Explanation, 8), Note(RestNoteKind.Exception, 9, RestExceptionKind.Emergency)]), D(20));
        Assert.Equal((0, 1, 15), (excepted.Unresolved, excepted.CompensatoryOutstanding, excepted.OwedMinutes));

        var given = March(Book([Note(RestNoteKind.Explanation, 8), Note(RestNoteKind.Exception, 9, RestExceptionKind.Emergency), Note(RestNoteKind.CompensatoryRestGiven, 10)]), D(20));
        Assert.Equal(0, given.CompensatoryOutstanding);
    }

    [Fact]
    public void AVesselIsRedWhenSomethingIsUnresolved_AmberWhenOnlyCatchingUp_AndGreenWhenNothingIsWaiting()
    {
        var first = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("danger", new RestVesselMonth(Vessel, "MV Test", first, [March(Book(), D(20))]).Tone);

        var catchingUp = March(new CrewBook(Crew, "Test Crew", "Third Officer", null, D(1), Enumerable.Range(1, 5).Select(d => Entry(d, Normal, At(d, 18))).ToList(), [], []), D(6));
        Assert.Equal(0, catchingUp.Unresolved);
        Assert.Equal("warning", new RestVesselMonth(Vessel, "MV Test", first, [catchingUp with { Missing = 1 }]).Tone);

        var nothingYet = March(Book(), today: new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc));      // March has not begun
        Assert.Equal(0, nothingYet.Elapsed);
        Assert.Equal("success", new RestVesselMonth(Vessel, "MV Test", first, [nothingYet with { Breaches = [], Periods = [] }]).Tone);
        Assert.Equal(100, new RestVesselMonth(Vessel, "MV Test", first, [nothingYet]).RecordedPercent);
    }

    [Fact]
    public void TheBreachReportQuotesWhatPeopleWrote_AndStatesTheRestOwedInHours()
    {
        var note = new RestBreachNote { Id = Guid.CreateVersion7(), CrewMemberId = Crew, Date = D(10), Rule = "Rest24", Kind = RestNoteKind.Explanation, Text = "Cargo ran over, the \"berth\" slipped", ByName = "Ravi", At = At(11) };
        var vessel = new RestVesselMonth(Vessel, "MV Test", D(1), [March(Book([note]), D(20))]);

        var csv = RestReports.BreachesCsv([vessel]);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("\"Vessel\",\"Seafarer\"", lines[0]);
        Assert.Contains("\"Cargo ran over, the \"\"berth\"\" slipped\"", lines[1]);
        Assert.Contains("\"MV Test\",\"Test Crew\",\"Third Officer\",\"2026-03-10\",\"Rest24\",\"9.75\",\"10.00\",\"Explained\"", lines[1]);
        Assert.Contains("\"0.25\"", lines[1]);

        var fleet = RestReports.FleetCsv([vessel]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("\"MV Test\",\"2026-03\",\"1\"", fleet[1]);

        var unsigned = RestReports.UnsignedCsv([vessel]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, unsigned.Length);                    // the header and the two periods that are over
        Assert.Contains("\"Not signed\"", unsigned[1]);
    }
}
