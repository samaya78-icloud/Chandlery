using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The edges of the incident rules and figures. Each test here was written because a deliberate change to the code (a mutation check, 2026-11-12) got past the
/// first set of tests: who counts as injured, what counts as opened, which reports are repeats, which hours are known, and which reports a figure is made from.
/// </summary>
public class IncidentBoundaryTests
{
    private static readonly RiskMatrix Matrix = RiskMatrix.Default;
    private static readonly IncidentLimits Limits = IncidentLimits.Default;
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static Incident Report(IncidentKind kind = IncidentKind.NearMiss, int potential = 4, int likelihood = 3, DateTime? occurred = null)
        => new()
        {
            Id = Guid.NewGuid(), Code = "INC-X-1", Kind = kind, OccurredAt = occurred ?? Now.AddHours(-3), SubmittedAt = Now.AddHours(-2), Place = "Engine room",
            ActualConsequence = 0, PotentialConsequence = potential, Likelihood = likelihood, Band = IncidentRules.BandOf(Matrix, 0, potential, likelihood), ReporterName = "A. Reporter",
        };

    private static IncidentEvent Ev(Incident i, IncidentEventKind kind, int minutes) => new() { Id = Guid.CreateVersion7(), IncidentId = i.Id, Kind = kind, At = Now.AddMinutes(minutes), Text = "x" };

    private static IncidentCase Case(Guid vessel, DateTime occurred, IncidentKind kind = IncidentKind.NearMiss, IncidentStatus status = IncidentStatus.Reported, DateTime? closedAt = null, DateTime? submitted = null, IncidentPerson? person = null)
        => new(new Incident { Id = Guid.NewGuid(), VesselId = vessel, Kind = kind, OccurredAt = occurred, SubmittedAt = submitted ?? occurred.AddHours(2), Band = "Low", Persons = person is null ? [] : [person] }, status, closedAt, false);

    // ---- urgency ----

    [Fact]
    public void ADaysLostBelongsToTheInjuredOnly_AnInvolvedPersonWithDaysLostDoesNotMakeAReportUrgent()
    {
        var report = Report(IncidentKind.InjuryIllness, 1, 1);
        report.Persons = [new IncidentPerson { Name = "N", Role = IncidentPersonRole.Involved, DaysLost = 9, Treatment = IncidentTreatment.Evacuation }];
        Assert.False(IncidentRules.IsUrgent(report, Matrix, Limits));
        report.Persons[0].Role = IncidentPersonRole.Injured;
        Assert.True(IncidentRules.IsUrgent(report, Matrix, Limits));
    }

    // ---- opened ----

    [Fact]
    public void AnInvestigationThatArrivedWithoutItsOpeningEventStillCountsAsOpen()
    {
        // Events and investigations travel as separate records and may arrive in either order.
        var report = Report(potential: 4, likelihood: 3);                                     // Medium High: an investigation is required
        var saved = new IncidentInvestigation { Id = Guid.CreateVersion7(), IncidentId = report.Id, Version = 1, Finished = false, Facts = "f" };
        var blockers = IncidentRules.CloseBlockers(new IncidentFacts(report, [Ev(report, IncidentEventKind.Reviewed, 1)], saved, 0, true));
        Assert.Contains("The investigation is not finished.", blockers);
        Assert.DoesNotContain(blockers, b => b.Contains("is required"));
    }

    // ---- repeats ----

    [Fact]
    public void ALaterReportIsNotARepeatOfAnEarlierOne()
    {
        var earlier = Report(occurred: Now.AddMonths(-2));
        var later = Report(occurred: Now.AddMonths(-1));
        Assert.Empty(IncidentRules.Repeats(earlier, [later], 12));
        Assert.Equal([earlier.Id], IncidentRules.Repeats(later, [earlier], 12).Select(r => r.Id));
    }

    // ---- hours ----

    [Fact]
    public void AMonthWithZeroHoursIsNotAMonthWithKnownHours()
    {
        var row = IncidentStats.ForVessel(A, "A", [Case(A, Jan.AddDays(1), IncidentKind.InjuryIllness, person: new IncidentPerson { Role = IncidentPersonRole.Injured, DaysLost = 3 })], [new MonthHours(Jan, 0m, "Typed")], Jan, 1);
        Assert.Equal(0, row.MonthsWithHours);
        Assert.False(row.HoursComplete);
        Assert.Null(row.Ltif);
    }

    [Fact]
    public void TheFleetRateReadsOnlyTheCasesInMonthsWhoseHoursAreKnown_ThoughTheCountHasThemAll()
    {
        var lostTimeWithoutHours = Case(A, Jan.AddMonths(1).AddDays(3), IncidentKind.InjuryIllness, person: new IncidentPerson { Role = IncidentPersonRole.Injured, DaysLost = 5 });
        var one = IncidentStats.ForVessel(A, "A", [lostTimeWithoutHours], [new MonthHours(Jan, 100_000m, "Typed")], Jan, 2);        // January has hours, the case is in February
        var fleet = IncidentStats.Combine("Fleet", [one], 2);
        Assert.Equal(1, fleet.LostTimeInjuries);
        Assert.Equal(0, fleet.LostTimeForRate);
        Assert.Equal(0.0, fleet.Ltif);                                                                                                // not 10.0: that case is outside the hours
    }

    // ---- which reports a figure is made from ----

    [Fact]
    public void OnlyNearMissesAreNearMisses_PollutionAndObservationsAreNot()
    {
        var cases = new[]
        {
            Case(A, Jan.AddDays(1)), Case(A, Jan.AddDays(2), IncidentKind.Pollution), Case(A, Jan.AddDays(3), IncidentKind.Observation),
            Case(A, Jan.AddDays(4), IncidentKind.InjuryIllness, person: new IncidentPerson { Role = IncidentPersonRole.Injured }),
        };
        var row = IncidentStats.ForVessel(A, "A", cases, [], Jan, 1);
        Assert.Equal(1, row.NearMisses);
        Assert.Equal(1.0, row.NearMissesPerInjury);
    }

    [Fact]
    public void DaysToCloseCountsClosedReportsOnly_AndRunsFromTheDayTheReportWasSubmitted()
    {
        var closed = Case(A, Jan.AddDays(1), status: IncidentStatus.Closed, submitted: Jan.AddDays(5), closedAt: Jan.AddDays(15));       // 10 days from submitted, 14 from happening
        var reopened = Case(A, Jan.AddDays(2), status: IncidentStatus.ReadyToClose, submitted: Jan.AddDays(2), closedAt: Jan.AddDays(40));  // was closed once, is open again
        var row = IncidentStats.ForVessel(A, "A", [closed, reopened], [], Jan, 2);
        Assert.Equal(10.0, row.MeanDaysToClose);
    }

    [Fact]
    public void OpenCountsTheOpenOnesNotTheClosed()
    {
        var cases = new[] { Case(A, Jan.AddDays(1)), Case(A, Jan.AddDays(2)), Case(A, Jan.AddDays(3), status: IncidentStatus.Closed, closedAt: Jan.AddDays(9)) };
        Assert.Equal(2, IncidentStats.ForVessel(A, "A", cases, [], Jan, 1).Open);
    }

    // ---- quiet vessels ----

    [Fact]
    public void AVesselThatReportedExactlyOnTheLineOfTheWindowIsNotQuiet()
    {
        var today = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
        var onTheLine = Case(A, today.AddMonths(-3), submitted: today.AddMonths(-3));                 // exactly three months ago
        var justBefore = Case(B, today.AddMonths(-3).AddDays(-1), submitted: today.AddMonths(-3).AddDays(-1));
        var quiet = IncidentStats.Quiet([(A, "On the line"), (B, "Just before")], [onTheLine, justBefore], today, 3);
        Assert.Equal(["Just before"], quiet.Select(q => q.Vessel));
    }
}
