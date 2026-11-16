using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>Certificates and surveys, the pure rules: where a date stands (90, 60 and 30 days, the survey window), how events move the date, and what an event must carry.</summary>
public class CertificateRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 16);

    private static ShipCertificate Cert(CertificateKind kind = CertificateKind.Statutory, int? dueInDays = null, int before = 0, int after = 0)
        => new() { Kind = kind, DueDate = dueInDays is { } d ? Today.AddDays(d) : null, WindowBeforeDays = before, WindowAfterDays = after };

    private static CertificateEvent Ev(CertificateEventType type, int dayOffset, int? newDueInDays = null, int recordedHour = 9)
        => new() { Type = type, OccurredOn = Today.AddDays(dayOffset), NewDueDate = newDueInDays is { } d ? Today.AddDays(d) : null, At = Today.AddDays(dayOffset).AddHours(recordedHour) };

    private static CertificateState State(ShipCertificate c, params CertificateEvent[] events) => CertificateRules.StatusOf(c, events, Today).State;

    [Fact]
    public void WithoutADateThereIsNothingToWarnAbout()
    {
        var status = CertificateRules.StatusOf(Cert(), [], Today);
        Assert.Equal(CertificateState.NoDate, status.State);
        Assert.Null(status.DaysLeft);
        Assert.False(status.NeedsAttention);
    }

    [Theory]
    [InlineData(200, CertificateState.Valid)]
    [InlineData(91, CertificateState.Valid)]
    [InlineData(90, CertificateState.Due90)]
    [InlineData(61, CertificateState.Due90)]
    [InlineData(60, CertificateState.Due60)]
    [InlineData(31, CertificateState.Due60)]
    [InlineData(30, CertificateState.Due30)]
    [InlineData(0, CertificateState.Due30)]
    [InlineData(-1, CertificateState.Overdue)]
    public void ACertificateWarnsAtNinetySixtyAndThirtyDays_AndIsOverdueTheDayAfterItExpires(int daysLeft, CertificateState expected)
        => Assert.Equal(expected, State(Cert(dueInDays: daysLeft)));

    [Fact]
    public void OnlyTheLastThirtyDaysAndOverdueNeedAttention()
    {
        Assert.False(CertificateRules.StatusOf(Cert(dueInDays: 31), [], Today).NeedsAttention);
        Assert.True(CertificateRules.StatusOf(Cert(dueInDays: 30), [], Today).NeedsAttention);
        Assert.True(CertificateRules.StatusOf(Cert(dueInDays: -5), [], Today).NeedsAttention);
    }

    [Fact]
    public void ASurveyIsOverdueOnlyWhenItsWindowHasClosed_AndShowsWhenTheWindowOpens()
    {
        // Annual survey, window 90 days either side of the anniversary.
        Assert.Equal(CertificateState.Valid, State(Cert(CertificateKind.Survey, 200, 90, 90)));        // window not open yet
        Assert.Equal(CertificateState.Window, State(Cert(CertificateKind.Survey, 60, 90, 90)));         // open, plenty of time to its last day (day 150)
        Assert.Equal(CertificateState.Due60, State(Cert(CertificateKind.Survey, -30, 90, 90)));         // past the anniversary but 60 days left in the window
        Assert.Equal(CertificateState.Overdue, State(Cert(CertificateKind.Survey, -100, 90, 90)));      // window closed ten days ago
        Assert.Equal(Today.AddDays(150), CertificateRules.StatusOf(Cert(CertificateKind.Survey, 60, 90, 90), [], Today).Deadline);
    }

    [Fact]
    public void ACertificateHasNoWindow_EvenIfOneIsStored()
        => Assert.Equal(CertificateState.Overdue, State(Cert(CertificateKind.Statutory, -1, 90, 90)));

    [Fact]
    public void ARenewalMovesTheDate_AndTheLatestDayWinsNotTheLatestRecording()
    {
        var cert = Cert(dueInDays: 10);
        Assert.Equal(CertificateState.Due30, State(cert));

        var renewed = Ev(CertificateEventType.Issued, -1, newDueInDays: 1800);
        Assert.Equal(CertificateState.Valid, State(cert, renewed));
        Assert.Equal(Today.AddDays(1800), CertificateRules.EffectiveDue(cert, [renewed]));

        // An extension for an earlier day, but recorded after: the later day still counts.
        var extension = Ev(CertificateEventType.Extended, -20, newDueInDays: 40, recordedHour: 23);
        Assert.Equal(Today.AddDays(1800), CertificateRules.EffectiveDue(cert, [renewed, extension]));
    }

    [Fact]
    public void AnExtensionRescuesAnExpiredCertificate()
    {
        var cert = Cert(dueInDays: -3);
        Assert.Equal(CertificateState.Overdue, State(cert));
        Assert.Equal(CertificateState.Due30, State(cert, Ev(CertificateEventType.Extended, 0, newDueInDays: 14)));
    }

    [Fact]
    public void EventsWithoutADateLeaveTheDateAlone()
        => Assert.Equal(Today.AddDays(70), CertificateRules.EffectiveDue(Cert(dueInDays: 70), [Ev(CertificateEventType.Note, 0), Ev(CertificateEventType.Document, 0)]));

    [Theory]
    [InlineData(CertificateEventType.Issued, false, "x", "x", true)]
    [InlineData(CertificateEventType.Issued, true, "", "", false)]
    [InlineData(CertificateEventType.Extended, true, "", "", true)]
    [InlineData(CertificateEventType.Extended, true, "Letter 12/2026", "", false)]
    [InlineData(CertificateEventType.SurveyDone, false, "", "", true)]
    [InlineData(CertificateEventType.Note, false, "", "", true)]
    [InlineData(CertificateEventType.Note, false, "", "Checked the stamp", false)]
    [InlineData(CertificateEventType.Endorsed, false, "", "", false)]
    public void AnEventMustCarryWhatItsKindNeeds(CertificateEventType type, bool hasDate, string reference, string note, bool problem)
        => Assert.Equal(problem, CertificateRules.Problem(type, hasDate ? Today.AddDays(30) : null, reference, note) is not null);

    [Fact]
    public void TheFleetOverviewCountsPerVesselAndPutsTheWorstFirst()
    {
        var calm = Guid.NewGuid();
        var worried = Guid.NewGuid();
        CertificateRow Row(Guid vessel, string name, int? due)
        {
            var cert = new ShipCertificate { Id = Guid.NewGuid(), VesselId = vessel, DueDate = due is { } d ? Today.AddDays(d) : null };
            return new CertificateRow(cert, name, CertificateRules.StatusOf(cert, [], Today), 0, null);
        }

        var rows = new[] { Row(calm, "MV Calm", 400), Row(calm, "MV Calm", null), Row(worried, "MV Worried", -2), Row(worried, "MV Worried", 20), Row(worried, "MV Worried", 75) };
        var fleet = CertificateFleet.Of(rows);

        Assert.Equal(["MV Worried", "MV Calm"], fleet.Select(r => r.Vessel));
        var w = fleet[0];
        Assert.Equal((3, 1, 1, 0, 1), (w.Total, w.Overdue, w.Due30, w.Due60, w.Due90));
        Assert.Equal(1, fleet[1].NoDate);

        var counts = CertificateFleet.CountsOf(rows);
        Assert.Equal((5, 3, 1, 1), (counts.Total, counts.Due90, counts.Due30, counts.Overdue));
        Assert.Equal(2, counts.NeedingAttention);
    }
}
