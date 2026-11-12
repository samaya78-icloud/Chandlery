using System.Text.Json;
using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The rules of incident reporting, with no database: severity and band, urgency, lateness, the days to investigate, repeats, shared lessons, status and closing.</summary>
public class IncidentRulesTests
{
    private static readonly RiskMatrix Matrix = RiskMatrix.Default;                         // Low 1, Medium 5, Medium High 10, High 15, Extreme 20
    private static readonly IncidentLimits Limits = IncidentLimits.Default;
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    private static Incident Report(IncidentKind kind = IncidentKind.NearMiss, int actual = 0, int potential = 4, int likelihood = 3, DateTime? occurred = null, DateTime? submitted = null, string place = "Engine room")
        => new()
        {
            Id = Guid.NewGuid(), Code = "INC-X-1", Kind = kind, OccurredAt = occurred ?? Now.AddHours(-3), SubmittedAt = submitted ?? Now.AddHours(-2), Place = place, Description = "Something happened",
            ActualConsequence = actual, PotentialConsequence = potential, Likelihood = likelihood, Band = IncidentRules.BandOf(Matrix, actual, potential, likelihood), ReporterName = "A. Reporter",
            ShareLesson = false,
        };

    private static IncidentEvent Ev(Incident i, IncidentEventKind kind, int minutes) => new() { Id = Guid.CreateVersion7(), IncidentId = i.Id, Kind = kind, At = Now.AddMinutes(minutes), Text = "x" };

    private static IncidentInvestigation Inv(Incident i, bool finished, int version = 1) => new() { Id = Guid.CreateVersion7(), IncidentId = i.Id, Version = version, Finished = finished, Facts = "f", RootCauses = "Fatigue", Lessons = "Check the grating" };

    private static IncidentFacts Facts(Incident i, IEnumerable<IncidentEvent>? events = null, IncidentInvestigation? inv = null, int open = 0)
        => new(i, (events ?? []).ToList(), inv, open, IncidentRules.InvestigationRequired(i.Band, Matrix, Limits));

    // ---- severity and band ----

    [Theory]
    [InlineData(0, 1, 1, null)]
    [InlineData(5, 5, 5, null)]
    [InlineData(-1, 3, 3, "What actually happened")]
    [InlineData(6, 3, 3, "What actually happened")]
    [InlineData(0, 0, 3, "What it could have been")]
    [InlineData(0, 6, 3, "What it could have been")]
    [InlineData(0, 3, 0, "How likely")]
    [InlineData(0, 3, 6, "How likely")]
    public void TheSeverityEdgesAreChecked(int actual, int potential, int likelihood, string? problem)
    {
        var result = IncidentRules.SeverityProblem(actual, potential, likelihood);
        if (problem is null) Assert.Null(result); else Assert.StartsWith(problem, result);
    }

    [Theory]
    [InlineData(0, 1, 1, "Low")]                 // 1
    [InlineData(0, 3, 2, "Medium")]              // 6
    [InlineData(0, 4, 3, "Medium High")]         // 12
    [InlineData(0, 3, 5, "High")]                // 15, the line itself
    [InlineData(4, 2, 5, "Extreme")]             // the higher consequence counts: 4 x 5
    [InlineData(5, 1, 4, "Extreme")]             // 20
    [InlineData(2, 4, 1, "Low")]                 // the higher consequence is the potential one: 4 x 1 = 4
    [InlineData(3, 4, 2, "Medium")]              // 4 x 2 = 8
    public void TheBandIsThatOfTheHigherConsequenceTimesTheLikelihood(int actual, int potential, int likelihood, string band)
        => Assert.Equal(band, IncidentRules.BandOf(Matrix, actual, potential, likelihood));

    [Fact]
    public void AnInvalidScoreGivesNoBand() => Assert.Equal("", IncidentRules.BandOf(Matrix, 0, 9, 3));

    // ---- urgency ----

    [Fact]
    public void ASeriousBandIsUrgent_AMiddleOneIsNot()
    {
        Assert.True(IncidentRules.IsUrgent(Report(potential: 5, likelihood: 3), Matrix, Limits));       // 15: High
        Assert.True(IncidentRules.IsUrgent(Report(potential: 5, likelihood: 4), Matrix, Limits));       // 20: Extreme
        Assert.False(IncidentRules.IsUrgent(Report(potential: 4, likelihood: 3), Matrix, Limits));      // 12: Medium High
    }

    [Fact]
    public void PollutionIsUrgentWhereTheSettingsSayAndOnlyThere()
    {
        var pollution = Report(IncidentKind.Pollution, potential: 2, likelihood: 2);
        Assert.True(IncidentRules.IsUrgent(pollution, Matrix, Limits));
        Assert.False(IncidentRules.IsUrgent(pollution, Matrix, Limits with { UrgentForPollution = false }));
    }

    [Fact]
    public void AnInjuryWithDaysLostOrAnEvacuationIsUrgent_FirstAidAndWitnessesAreNot()
    {
        IncidentPerson Person(IncidentPersonRole role, int lost = 0, IncidentTreatment treatment = IncidentTreatment.None) => new() { Role = role, DaysLost = lost, Treatment = treatment, Name = "N" };
        Incident With(params IncidentPerson[] people) { var i = Report(IncidentKind.InjuryIllness, 1, 1, 1); i.Persons = [.. people]; return i; }

        Assert.True(IncidentRules.IsUrgent(With(Person(IncidentPersonRole.Injured, 1)), Matrix, Limits));
        Assert.True(IncidentRules.IsUrgent(With(Person(IncidentPersonRole.Injured, 0, IncidentTreatment.Evacuation)), Matrix, Limits));
        Assert.False(IncidentRules.IsUrgent(With(Person(IncidentPersonRole.Injured, 0, IncidentTreatment.FirstAid)), Matrix, Limits));
        Assert.False(IncidentRules.IsUrgent(With(Person(IncidentPersonRole.Witness, 9)), Matrix, Limits));
        Assert.False(IncidentRules.IsUrgent(With(Person(IncidentPersonRole.Injured, 1)), Matrix, Limits with { UrgentForLostTime = false }));
    }

    // ---- late ----

    [Fact]
    public void ALateReportIsOneEnteredMoreThanTheAllowedHoursAfter()
    {
        Assert.False(IncidentRules.ReportedLate(Report(occurred: Now.AddHours(-24), submitted: Now), Limits));                  // exactly 24 h
        Assert.True(IncidentRules.ReportedLate(Report(occurred: Now.AddHours(-24).AddMinutes(-1), submitted: Now), Limits));
        Assert.False(IncidentRules.ReportedLate(Report(occurred: Now.AddHours(-30), submitted: Now), Limits with { LateReportHours = 48 }));
        var draft = Report(); draft.SubmittedAt = null;
        Assert.False(IncidentRules.ReportedLate(draft, Limits));
    }

    // ---- the days to investigate ----

    [Theory]
    [InlineData("Extreme", 7)]
    [InlineData("High", 14)]
    [InlineData("Medium High", 30)]
    [InlineData("Medium", 60)]
    public void TheDaysRunFromTheHighestBandDown(string band, int days) => Assert.Equal(days, IncidentRules.InvestigationDays(band, Matrix, Limits));

    [Fact]
    public void TheLowestBandNeedsNone_AndAnUnknownBandNeedsNone()
    {
        Assert.Null(IncidentRules.InvestigationDays("Low", Matrix, Limits));
        Assert.Null(IncidentRules.InvestigationDays("", Matrix, Limits));
        Assert.Null(IncidentRules.InvestigationDays("Catastrophic", Matrix, Limits));
    }

    [Fact]
    public void AShorterListOrASmallerMatrixStillReadsFromTheTop()
    {
        var one = Limits with { InvestigationDays = IncidentLimits.ParseDays("10") };
        Assert.Equal(10, IncidentRules.InvestigationDays("Extreme", Matrix, one));
        Assert.Null(IncidentRules.InvestigationDays("High", Matrix, one));
        var three = new RiskMatrix([("Minor", 1), ("Major", 8), ("Critical", 16)]);
        Assert.Equal(7, IncidentRules.InvestigationDays("Critical", three, Limits));
        Assert.Equal(14, IncidentRules.InvestigationDays("Major", three, Limits));
        Assert.Equal(30, IncidentRules.InvestigationDays("Minor", three, Limits));
    }

    [Theory]
    [InlineData("7,14,30,60", new[] { 7, 14, 30, 60 })]
    [InlineData(" 7 , x, 0, -3, 14,", new[] { 7, 14 })]
    [InlineData("", new int[0])]
    public void TheDaysAreReadFromText(string text, int[] expected) => Assert.Equal(expected, IncidentLimits.ParseDays(text));

    [Fact]
    public void TheDueDateIsTheDaySubmittedPlusTheDays_AndOverdueNeedsAnUnfinishedOpenReport()
    {
        var i = Report(potential: 5, likelihood: 4, submitted: new DateTime(2026, 11, 1, 15, 30, 0, DateTimeKind.Utc));          // Extreme: 7 days
        Assert.Equal(new DateTime(2026, 11, 8), IncidentRules.InvestigationDue(i, Matrix, Limits));
        var facts = Facts(i, [Ev(i, IncidentEventKind.Reviewed, 0)]);
        var status = IncidentStatusRules.Of(facts);

        Assert.False(IncidentRules.InvestigationOverdue(facts, status, Matrix, Limits, new DateTime(2026, 11, 8)));                // the day itself is not late
        Assert.True(IncidentRules.InvestigationOverdue(facts, status, Matrix, Limits, new DateTime(2026, 11, 9)));
        Assert.False(IncidentRules.InvestigationOverdue(Facts(i, [Ev(i, IncidentEventKind.Reviewed, 0)], Inv(i, finished: true)), IncidentStatus.ActionsOpen, Matrix, Limits, new DateTime(2026, 11, 20)));
        Assert.False(IncidentRules.InvestigationOverdue(facts, IncidentStatus.Closed, Matrix, Limits, new DateTime(2026, 11, 20)));
        var low = Report(potential: 1, likelihood: 1);
        Assert.Null(IncidentRules.InvestigationDue(low, Matrix, Limits));
    }

    [Fact]
    public void AnObservationOrALowReportCanBeClosedWithoutAnInvestigation_ASeriousOneCannot()
    {
        Assert.True(IncidentRules.CanSkipInvestigation(Report(potential: 1, likelihood: 1), Matrix, Limits));
        Assert.True(IncidentRules.CanSkipInvestigation(Report(IncidentKind.Observation, potential: 5, likelihood: 5), Matrix, Limits));
        Assert.False(IncidentRules.CanSkipInvestigation(Report(potential: 5, likelihood: 4), Matrix, Limits));
        Assert.False(IncidentRules.CanSkipInvestigation(Report(IncidentKind.NearMiss, potential: 3, likelihood: 3), Matrix, Limits));   // 9: Medium, 60 days
    }

    // ---- status ----

    [Fact]
    public void TheStatusFollowsTheEventsInOrder()
    {
        var i = Report(potential: 4, likelihood: 3);                                          // Medium High: an investigation is required
        var draft = Report(); draft.SubmittedAt = null;
        Assert.Equal(IncidentStatus.Draft, IncidentStatusRules.Of(Facts(draft)));
        Assert.Equal(IncidentStatus.Reported, IncidentStatusRules.Of(Facts(i)));

        var reviewed = Ev(i, IncidentEventKind.Reviewed, 1);
        Assert.Equal(IncidentStatus.Reviewed, IncidentStatusRules.Of(Facts(i, [reviewed])));

        var opened = Ev(i, IncidentEventKind.InvestigationOpened, 2);
        Assert.Equal(IncidentStatus.Investigating, IncidentStatusRules.Of(Facts(i, [reviewed, opened])));
        Assert.Equal(IncidentStatus.Investigating, IncidentStatusRules.Of(Facts(i, [reviewed, opened], Inv(i, finished: false))));

        Assert.Equal(IncidentStatus.ActionsOpen, IncidentStatusRules.Of(Facts(i, [reviewed, opened], Inv(i, true), open: 2)));
        Assert.Equal(IncidentStatus.ReadyToClose, IncidentStatusRules.Of(Facts(i, [reviewed, opened], Inv(i, true), open: 0)));

        var closed = Ev(i, IncidentEventKind.Closed, 10);
        Assert.Equal(IncidentStatus.Closed, IncidentStatusRules.Of(Facts(i, [reviewed, opened, closed], Inv(i, true))));
        var reopened = Ev(i, IncidentEventKind.Reopened, 11);
        Assert.Equal(IncidentStatus.ReadyToClose, IncidentStatusRules.Of(Facts(i, [reviewed, opened, closed, reopened], Inv(i, true))));
        Assert.Equal(IncidentStatus.Closed, IncidentStatusRules.Of(Facts(i, [reviewed, opened, closed, reopened, Ev(i, IncidentEventKind.Closed, 12)], Inv(i, true))));

        Assert.Equal(IncidentStatus.Withdrawn, IncidentStatusRules.Of(Facts(i, [Ev(i, IncidentEventKind.Withdrawn, 3)])));
        Assert.Equal(IncidentStatus.Withdrawn, IncidentStatusRules.Of(Facts(draft, [Ev(draft, IncidentEventKind.Withdrawn, 3)])));
    }

    [Fact]
    public void AReportThatNeedsNoInvestigationGoesStraightFromReviewToReadyToClose_UnlessActionsAreOpen()
    {
        var low = Report(potential: 1, likelihood: 1);
        var reviewed = Ev(low, IncidentEventKind.Reviewed, 1);
        Assert.Equal(IncidentStatus.ReadyToClose, IncidentStatusRules.Of(Facts(low, [reviewed])));
        Assert.Equal(IncidentStatus.ActionsOpen, IncidentStatusRules.Of(Facts(low, [reviewed], open: 1)));
    }

    [Fact]
    public void WaivingTheInvestigationCountsAsFinishingIt()
    {
        var i = Report(potential: 4, likelihood: 3);
        var events = new[] { Ev(i, IncidentEventKind.Reviewed, 1), Ev(i, IncidentEventKind.NoInvestigationNeeded, 2) };
        Assert.Equal(IncidentStatus.ReadyToClose, IncidentStatusRules.Of(Facts(i, events)));
    }

    [Fact]
    public void TheEventsAreReadInTimeOrder_WhateverOrderTheyArriveIn()
    {
        var i = Report(potential: 1, likelihood: 1);
        var reviewed = Ev(i, IncidentEventKind.Reviewed, 1);
        var closed = Ev(i, IncidentEventKind.Closed, 5);
        var reopened = Ev(i, IncidentEventKind.Reopened, 9);
        Assert.Equal(IncidentStatus.ReadyToClose, IncidentStatusRules.Of(Facts(i, [reopened, reviewed, closed])));
    }

    [Fact]
    public void OnlyADraftOrAReportedReportHasTextThatCanStillBeChanged()
    {
        Assert.False(IncidentStatusRules.TextIsLocked(IncidentStatus.Draft));
        Assert.False(IncidentStatusRules.TextIsLocked(IncidentStatus.Reported));
        foreach (var s in new[] { IncidentStatus.Reviewed, IncidentStatus.Investigating, IncidentStatus.ActionsOpen, IncidentStatus.ReadyToClose, IncidentStatus.Closed, IncidentStatus.Withdrawn })
            Assert.True(IncidentStatusRules.TextIsLocked(s), s.ToString());
    }

    // ---- closing ----

    [Fact]
    public void TheBlockersSayWhatIsMissing_AndCloseWhenNothingIs()
    {
        var i = Report(potential: 4, likelihood: 3);
        var all = IncidentRules.CloseBlockers(Facts(i, open: 2));
        Assert.Contains(all, b => b.Contains("Master has not reviewed"));
        Assert.Contains(all, b => b.Contains("An investigation is required") && b.Contains("Medium High"));
        Assert.Contains(all, b => b.Contains("2 actions are still open"));

        var reviewed = Ev(i, IncidentEventKind.Reviewed, 1);
        var opened = Ev(i, IncidentEventKind.InvestigationOpened, 2);
        Assert.Contains("The investigation is not finished.", IncidentRules.CloseBlockers(Facts(i, [reviewed, opened], Inv(i, false))));
        Assert.Contains("1 action is still open.", IncidentRules.CloseBlockers(Facts(i, [reviewed, opened], Inv(i, true), open: 1)));
        Assert.Empty(IncidentRules.CloseBlockers(Facts(i, [reviewed, opened], Inv(i, true))));
        Assert.Empty(IncidentRules.CloseBlockers(Facts(i, [reviewed, Ev(i, IncidentEventKind.NoInvestigationNeeded, 2)])));
    }

    [Fact]
    public void AClosedOrWithdrawnOrUnsubmittedReportCannotBeClosed()
    {
        var i = Report(potential: 1, likelihood: 1);
        var reviewed = Ev(i, IncidentEventKind.Reviewed, 1);
        Assert.Equal(["The report is already closed."], IncidentRules.CloseBlockers(Facts(i, [reviewed, Ev(i, IncidentEventKind.Closed, 2)])));
        Assert.Equal(["The report was withdrawn."], IncidentRules.CloseBlockers(Facts(i, [Ev(i, IncidentEventKind.Withdrawn, 2)])));
        var draft = Report(); draft.SubmittedAt = null;
        Assert.Equal(["The report has not been submitted."], IncidentRules.CloseBlockers(Facts(draft)));
    }

    // ---- who is named ----

    [Fact]
    public void TheOfficeSeesACrewMemberOnlyWhereTheReporterAskedAndTheCompanyAllows()
    {
        var i = Report(); i.AnonymousToOffice = true;
        var allowed = Limits with { AllowAnonymousToOffice = true };
        Assert.Equal("A crew member", IncidentRules.ReporterNameFor(i, viewerIsOffice: true, allowed));
        Assert.Equal("A. Reporter", IncidentRules.ReporterNameFor(i, viewerIsOffice: false, allowed));      // the vessel always sees the name
        Assert.Equal("A. Reporter", IncidentRules.ReporterNameFor(i, viewerIsOffice: true, Limits));        // the company does not allow it
        i.AnonymousToOffice = false;
        Assert.Equal("A. Reporter", IncidentRules.ReporterNameFor(i, viewerIsOffice: true, allowed));
    }

    // ---- repeats ----

    private static Incident Earlier(Incident subject, int monthsBefore, string? place = null, IncidentKind? kind = null, Guid? equipment = null, string equipmentName = "")
        => new()
        {
            Id = Guid.NewGuid(), Kind = kind ?? subject.Kind, OccurredAt = subject.OccurredAt.AddMonths(-monthsBefore), SubmittedAt = subject.OccurredAt.AddMonths(-monthsBefore).AddHours(1),
            Place = place ?? subject.Place, EquipmentId = equipment, EquipmentName = equipmentName,
        };

    [Fact]
    public void RepeatsAreTheSameKindAtTheSamePlaceWithinTheWindow_NewestFirst()
    {
        var subject = Report(place: "Engine room");
        var a = Earlier(subject, 2, "engine room ");                      // the same place, whatever the case and spaces
        var b = Earlier(subject, 11);
        var tooOld = Earlier(subject, 13);
        var edge = Earlier(subject, 12);                                  // exactly the window: counts
        var otherPlace = Earlier(subject, 1, "Bridge");
        var otherKind = Earlier(subject, 1, kind: IncidentKind.UnsafeAct);
        var unsent = Earlier(subject, 1); unsent.SubmittedAt = null;

        var repeats = IncidentRules.Repeats(subject, [tooOld, b, otherPlace, a, otherKind, unsent, edge, subject], 12);
        Assert.Equal([a.Id, b.Id, edge.Id], repeats.Select(r => r.Id));
    }

    [Fact]
    public void ARepeatOnTheSameEquipmentCountsWhateverThePlace()
    {
        var machine = Guid.NewGuid();
        var subject = Report(place: "Engine room"); subject.EquipmentId = machine;
        var onMachine = Earlier(subject, 3, "Workshop", equipment: machine);
        var otherMachine = Earlier(subject, 3, "Workshop", equipment: Guid.NewGuid());
        Assert.Equal([onMachine.Id], IncidentRules.Repeats(subject, [onMachine, otherMachine], 12).Select(r => r.Id));

        var typed = Report(place: ""); typed.EquipmentName = "Purifier";
        var sameName = Earlier(typed, 3, "Anywhere", equipmentName: "purifier");
        Assert.Single(IncidentRules.Repeats(typed, [sameName], 12));
    }

    [Fact]
    public void ABlankPlaceNeverMatchesABlankPlace()
    {
        var subject = Report(place: "");
        Assert.Empty(IncidentRules.Repeats(subject, [Earlier(subject, 1, "")], 12));
    }

    [Fact]
    public void ThirdTimeLuckyIsTheTriggerForAssessmentReview()
    {
        Assert.False(IncidentRules.SuggestsAssessmentReview(0));
        Assert.False(IncidentRules.SuggestsAssessmentReview(1));
        Assert.True(IncidentRules.SuggestsAssessmentReview(2));
    }

    // ---- shared lessons ----

    [Fact]
    public void AReportIsALessonOnlyWhenSharedAndReviewed()
    {
        var i = Report(); i.ShareLesson = true;
        Assert.Null(IncidentRules.LessonOf(i, null, reviewed: false));
        i.ShareLesson = false;
        Assert.Null(IncidentRules.LessonOf(i, null, reviewed: true));
        i.ShareLesson = true;
        var lesson = IncidentRules.LessonOf(i, Inv(i, true), reviewed: true)!;
        Assert.Equal(("Fatigue", "Check the grating", i.Code), (lesson.RootCauses, lesson.Lessons, lesson.Code));
    }

    [Fact]
    public void ALessonHasNoPlaceForANameOrAnInjury()
    {
        var i = Report(IncidentKind.InjuryIllness); i.ShareLesson = true; i.ReporterName = "Zed Zebra";
        i.Persons = [new IncidentPerson { Name = "Yan Yak", Role = IncidentPersonRole.Injured, InjuryType = "Fracture", BodyPart = "Left wrist", Treatment = IncidentTreatment.Hospital, DaysLost = 20 }];
        var lesson = IncidentRules.LessonOf(i, null, reviewed: true)!;

        var json = JsonSerializer.Serialize(lesson);
        foreach (var secret in new[] { "Zed Zebra", "Yan Yak", "Fracture", "Left wrist", "Hospital" }) Assert.DoesNotContain(secret, json);
        var fields = typeof(IncidentLesson).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(fields, f => f.Contains("Person") || f.Contains("Reporter") || f.Contains("Injur") || f.Contains("Name") && f != "EquipmentName");
        Assert.DoesNotContain("VesselId", fields);
    }
}
