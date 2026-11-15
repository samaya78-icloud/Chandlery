using Chandlery.Models;
using Chandlery.Services;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Incident reports between a vessel and the office: who writes each step, what travels, and that both sides read the same status.</summary>
public class IncidentTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private IncidentService _shore = null!, _vessel = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private CrewMember _reporter = null!, _master = null!, _cook = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shore = new IncidentService(_s.ShoreFactory, _shoreWorklist);
        _vessel = new IncidentService(_s.VesselFactory, _vesselWorklist);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Incidents, PolicyModules.Worklist })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        _reporter = await SignOnAsync("I1001", "Third Officer");
        _master = await SignOnAsync("I1002", "Master", master: true);
        _cook = await SignOnAsync("I1003", "Cook");
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static IncidentInput Input(IncidentKind kind = IncidentKind.NearMiss, int actual = 0, int potential = 4, int likelihood = 3, bool share = false, bool anonymous = false,
        IReadOnlyList<IncidentPersonInput>? persons = null, DateTime? occurred = null)
        => new(kind, occurred ?? DateTime.UtcNow.AddHours(-3), "Engine room, second platform", IncidentShipStatus.AtSea, "Calm", "A wrench fell from the grating above the purifier.", "Area cleared and the wrench secured.",
            null, "Purifier", "", anonymous, share, actual, potential, likelihood, persons ?? []);

    private async Task<Incident> SubmittedAsync(IncidentInput? input = null)
    {
        var draft = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, input ?? Input());
        var submitted = await _vessel.SubmitAsync(_s.CompanyId, draft.Id, As(_reporter));
        await _s.SettleUntilQuietAsync();
        return submitted;
    }

    private async Task<IncidentStatus> StatusAsync(IncidentService side, Guid id, Actor who) => (await side.GetAsync(_s.CompanyId, id, who))!.Status;

    // ---- asking for a drill ----

    [Fact]
    public async Task TheOfficeCanAskTheVesselToHoldADrill_AsALinkedAction_ButOnlyForADrillTypeInUse()
    {
        var report = await SubmittedAsync();
        DrillType drill;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync()) drill = await db.DrillTypes.AsNoTracking().FirstAsync(t => t.CompanyId == _s.CompanyId && t.IsActive);

        var task = await _shore.AskForDrillAsync(_s.CompanyId, report.Id, _s.User, drill.Id, "Nobody knew where the gas detector was kept.");
        await _s.SettleUntilQuietAsync();
        Assert.Equal($"Hold a drill: {drill.Name}", task.Title);
        var seen = (await _vessel.GetAsync(_s.CompanyId, report.Id, As(_master)))!.Actions.Single();           // the action reached the vessel, linked to the report
        Assert.Equal(task.Id, seen.Id);
        Assert.Contains("gas detector", seen.Description);

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AskForDrillAsync(_s.CompanyId, report.Id, _s.User, Guid.NewGuid(), ""));
        Assert.Contains("drill type", unknown.Message);
        var crew = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AskForDrillAsync(_s.CompanyId, report.Id, As(_cook), drill.Id, ""));
        Assert.Contains("office or by the Master", crew.Message);                                                // the same rule as any action
    }

    // ---- the whole way ----

    [Fact]
    public async Task AReportGoesFromDraftToClosed_AndBothSidesAgreeAtEveryStep()
    {
        var draft = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, Input());
        Assert.Contains("-INC-", draft.Code);
        Assert.Equal(IncidentStatus.Draft, await StatusAsync(_vessel, draft.Id, As(_reporter)));

        await _vessel.SubmitAsync(_s.CompanyId, draft.Id, As(_reporter));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.Reported, await StatusAsync(_shore, draft.Id, _s.User));

        await _vessel.ReviewAsync(_s.CompanyId, draft.Id, As(_master), "Facts confirmed with the Chief Engineer.");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.Reviewed, await StatusAsync(_shore, draft.Id, _s.User));                // Medium High: an investigation is required, none opened yet

        await _shore.OpenInvestigationAsync(_s.CompanyId, draft.Id, _s.User, "Superintendent to visit");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.Investigating, await StatusAsync(_vessel, draft.Id, As(_master)));

        var cause = (await _shore.CausesAsync(_s.CompanyId)).First();
        await _shore.SaveInvestigationAsync(_s.CompanyId, draft.Id, _s.User, new InvestigationInput("Grating clip missing.", "Tool left unsecured.", [], "", "", IncidentAssessmentFinding.NotLooked, "", "Superintendent", false));
        var finished = await _shore.SaveInvestigationAsync(_s.CompanyId, draft.Id, _s.User, new InvestigationInput("Grating clip missing.", "Tool left unsecured.", [cause.Name], "Why 1: no tool lanyard.", "", IncidentAssessmentFinding.Inadequate, "Lanyards for tools at height.", "Superintendent", true));
        Assert.Equal(2, finished.Version);

        var action = await _shore.AddActionAsync(_s.CompanyId, draft.Id, _s.User, new NewIncidentAction("Fit lanyards to the engine room tool kits", "All kits.", DateTime.UtcNow.AddDays(14)));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.ActionsOpen, await StatusAsync(_shore, draft.Id, _s.User));
        Assert.Equal(IncidentStatus.ActionsOpen, await StatusAsync(_vessel, draft.Id, As(_master)));
        Assert.Single((await _vessel.GetAsync(_s.CompanyId, draft.Id, As(_master)))!.Actions);              // the task reached the vessel, linked to the report

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.CloseAsync(_s.CompanyId, draft.Id, _s.User, "Done"));
        Assert.Contains("1 action is still open", blocked.Message);

        await _shoreWorklist.AddEntryAsync(_s.CompanyId, action.Id, new WorklistEntryInput(WorklistEntryType.Status, Status: WorklistStatus.Cancelled, Text: "Done another way"), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.ReadyToClose, await StatusAsync(_shore, draft.Id, _s.User));
        await _shore.RecordNotificationAsync(_s.CompanyId, draft.Id, _s.User, "Class told 12 Nov, ref C-4411");
        await _shore.CloseAsync(_s.CompanyId, draft.Id, _s.User, "Lanyards fitted and checked.");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.Closed, await StatusAsync(_shore, draft.Id, _s.User));
        Assert.Equal(IncidentStatus.Closed, await StatusAsync(_vessel, draft.Id, As(_master)));

        await _shore.ReopenAsync(_s.CompanyId, draft.Id, _s.User, "Another tool fell: check the lanyard fitting.");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.ReadyToClose, await StatusAsync(_vessel, draft.Id, As(_master)));

        // Both sides hold the same history, in the same order.
        var onboard = (await _vessel.GetAsync(_s.CompanyId, draft.Id, As(_master)))!;
        var ashore = (await _shore.GetAsync(_s.CompanyId, draft.Id, _s.User))!;
        Assert.Equal(ashore.Events.Select(e => e.Kind), onboard.Events.Select(e => e.Kind));
        Assert.Equal([IncidentEventKind.Reviewed, IncidentEventKind.InvestigationOpened, IncidentEventKind.NotificationRecorded, IncidentEventKind.Closed, IncidentEventKind.Reopened], ashore.Events.Select(e => e.Kind));
        Assert.Equal(2, onboard.Investigations.Count);
        Assert.Equal(finished.Id, onboard.Investigation!.Id);                                              // the newest version counts
    }

    // ---- who may do what ----

    [Fact]
    public async Task OnlyTheRightPeopleDoEachStep()
    {
        var draft = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, Input());

        Assert.Contains("reporter or the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_cook), draft.Id, Input()))).Message);
        Assert.Contains("reporter or the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, draft.Id, As(_cook)))).Message);
        Assert.Null(await _vessel.GetAsync(_s.CompanyId, draft.Id, As(_cook)));                              // someone else's report is not theirs to read
        Assert.NotNull(await _vessel.GetAsync(_s.CompanyId, draft.Id, As(_master)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SubmitAsync(_s.CompanyId, draft.Id, _s.User));                          // the office does not write a vessel's report

        await _vessel.SubmitAsync(_s.CompanyId, draft.Id, As(_reporter));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, draft.Id, As(_reporter), "ok"));          // the reporter is not the Master
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, draft.Id, As(_cook), "ok"));
        await _s.SettleUntilQuietAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, draft.Id, _s.User, "ok"));                   // the office does not review for the Master

        // Until the Master reviews, the reporter can still correct the text; after, only a supplement is added.
        var corrected = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), draft.Id, Input(potential: 3));
        Assert.Equal(3, corrected.PotentialConsequence);
        await _vessel.ReviewAsync(_s.CompanyId, draft.Id, As(_master), "Reviewed");
        Assert.Contains("Add a supplement", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), draft.Id, Input()))).Message);
        Assert.Contains("already", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, draft.Id, As(_master), "again"))).Message);
        var supplement = await _vessel.AddSupplementAsync(_s.CompanyId, draft.Id, As(_reporter), "The clip was found in the bilge.");
        Assert.Equal(_reporter.FullName, supplement.ByName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddSupplementAsync(_s.CompanyId, draft.Id, As(_cook), "Not mine to add"));
        await _s.SettleUntilQuietAsync();
        Assert.Single((await _shore.GetAsync(_s.CompanyId, draft.Id, _s.User))!.Incident.Supplements);

        // The office's steps are the office's.
        Assert.Contains("office", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.OpenInvestigationAsync(_s.CompanyId, draft.Id, As(_master)))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CloseAsync(_s.CompanyId, draft.Id, As(_master), "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveSettingsAsync(_s.CompanyId, new IncidentSettings()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveCauseAsync(_s.CompanyId, null, "Own cause", "", true));
    }

    [Fact]
    public async Task WithdrawingNeedsAReason_StaysInTheHistory_AndIsFinal()
    {
        var report = await SubmittedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.WithdrawAsync(_s.CompanyId, report.Id, As(_cook), "Not mine"));
        Assert.Contains("why", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.WithdrawAsync(_s.CompanyId, report.Id, As(_reporter), " "))).Message);

        await _vessel.WithdrawAsync(_s.CompanyId, report.Id, As(_reporter), "Entered twice");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.Withdrawn, await StatusAsync(_shore, report.Id, _s.User));
        Assert.Equal(IncidentStatus.Withdrawn, await StatusAsync(_vessel, report.Id, As(_master)));
        Assert.Contains("withdrawn", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, report.Id, As(_master), "x"))).Message);
        Assert.Contains("withdrawn", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.WithdrawAsync(_s.CompanyId, report.Id, As(_master), "again"))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.OpenInvestigationAsync(_s.CompanyId, report.Id, _s.User));
    }

    [Fact]
    public async Task SubmittingNeedsTheFactsTheSeverityAndForAnInjuryTheInjuredPerson()
    {
        var empty = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, Input() with { Description = "  " });
        Assert.Contains("Say what happened", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, empty.Id, As(_reporter)))).Message);

        var noScore = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, Input(potential: 0));
        Assert.Contains("could have been", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, noScore.Id, As(_reporter)))).Message);

        var future = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, Input(occurred: DateTime.UtcNow.AddDays(3)));
        Assert.Contains("future", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, future.Id, As(_reporter)))).Message);

        var injury = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, Input(IncidentKind.InjuryIllness, actual: 2));
        Assert.Contains("injured", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, injury.Id, As(_reporter)))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), injury.Id, Input(IncidentKind.InjuryIllness, persons: [new IncidentPersonInput(null, " ", "", "", IncidentPersonRole.Injured)])));
        await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), injury.Id, Input(IncidentKind.InjuryIllness, actual: 2, persons: [new IncidentPersonInput(_cook.Id, _cook.FullName, "Cook", "", IncidentPersonRole.Injured, "Burn", "Left hand", IncidentTreatment.FirstAid)]));
        var submitted = await _vessel.SubmitAsync(_s.CompanyId, injury.Id, As(_reporter));
        Assert.NotNull(submitted.SubmittedAt);
        Assert.Contains("already", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, injury.Id, As(_reporter)))).Message);
    }

    // ---- urgent, late, band ----

    [Fact]
    public async Task ASeriousReportIsMarkedUrgent_AndGoesUpOnTheUrgentLane_AnOrdinaryOneDoesNot()
    {
        var ordinary = await SubmittedAsync(Input(potential: 3, likelihood: 2));
        Assert.False(ordinary.IsUrgent);
        Assert.Equal("Medium", ordinary.Band);
        Assert.Equal(Lane.Transaction, SyncPublisher.LaneFor(ordinary));

        var serious = await SubmittedAsync(Input(IncidentKind.InjuryIllness, actual: 3, potential: 4, likelihood: 5, persons: [new IncidentPersonInput(_cook.Id, _cook.FullName, "Cook", "", IncidentPersonRole.Injured, "Fracture", "Wrist", IncidentTreatment.Hospital, 20)]));
        Assert.True(serious.IsUrgent);
        Assert.Equal("Extreme", serious.Band);
        Assert.Equal(Lane.Urgent, SyncPublisher.LaneFor(serious));
        Assert.True((await _shore.GetAsync(_s.CompanyId, serious.Id, _s.User))!.Incident.IsUrgent);

        var draft = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, Input(IncidentKind.Pollution, potential: 2, likelihood: 2));
        Assert.Equal(Lane.Transaction, SyncPublisher.LaneFor(draft));                                         // not submitted: nothing is urgent yet
    }

    [Fact]
    public async Task TheMasterMayChangeTheSeverityAtReview_WhichRescoresTheBandAndTheLane()
    {
        var report = await SubmittedAsync(Input(potential: 2, likelihood: 2));
        Assert.Equal(("Low", false), (report.Band, report.IsUrgent));
        await _vessel.ReviewAsync(_s.CompanyId, report.Id, As(_master), "Worse than first thought.", (Actual: 2, Potential: 5, Likelihood: 4));
        await _s.SettleUntilQuietAsync();
        var seen = (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!;
        Assert.Equal(("Extreme", true), (seen.Incident.Band, seen.Incident.IsUrgent));
        Assert.Equal(IncidentStatus.Reviewed, seen.Status);
        Assert.True(seen.InvestigationRequired);
        Assert.Equal(report.SubmittedAt!.Value.Date.AddDays(7), seen.InvestigationDue);
    }

    [Fact]
    public async Task ALateReportIsFlagged_OnBothSides()
    {
        var late = await SubmittedAsync(Input(occurred: DateTime.UtcNow.AddHours(-30)));
        var fresh = await SubmittedAsync(Input(occurred: DateTime.UtcNow.AddHours(-2)));
        Assert.True((await _shore.GetAsync(_s.CompanyId, late.Id, _s.User))!.ReportedLate);
        Assert.True((await _vessel.GetAsync(_s.CompanyId, late.Id, As(_master)))!.ReportedLate);
        Assert.False((await _shore.GetAsync(_s.CompanyId, fresh.Id, _s.User))!.ReportedLate);
    }

    // ---- the investigation ----

    [Fact]
    public async Task TheInvestigationNeedsFactsAndARootCause_FromTheCompanysList_AndIsAppendedNotEdited()
    {
        var report = await SubmittedAsync();
        await _vessel.ReviewAsync(_s.CompanyId, report.Id, As(_master), "ok");
        await _s.SettleUntilQuietAsync();
        var cause = (await _shore.CausesAsync(_s.CompanyId)).First().Name;

        InvestigationInput Make(string facts, string[] causes, bool finished) => new(facts, "", causes, "", "", IncidentAssessmentFinding.NotLooked, "", "", finished);
        Assert.Contains("facts", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveInvestigationAsync(_s.CompanyId, report.Id, _s.User, Make("", [cause], true)))).Message);
        Assert.Contains("root cause", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveInvestigationAsync(_s.CompanyId, report.Id, _s.User, Make("Facts", [], true)))).Message);
        Assert.Contains("not in the list", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveInvestigationAsync(_s.CompanyId, report.Id, _s.User, Make("Facts", ["Gremlins"], true)))).Message);

        var first = await _shore.SaveInvestigationAsync(_s.CompanyId, report.Id, _s.User, Make("First look", [], false));
        var second = await _shore.SaveInvestigationAsync(_s.CompanyId, report.Id, _s.User, Make("Second look", [cause.ToUpperInvariant()], false));
        Assert.Equal((1, 2), (first.Version, second.Version));
        Assert.Equal(cause, second.RootCauses);                                                               // the company's own spelling, whatever case was asked for
        await _s.SettleUntilQuietAsync();
        var onboard = (await _vessel.GetAsync(_s.CompanyId, report.Id, As(_master)))!;
        Assert.Equal(["First look", "Second look"], onboard.Investigations.Select(v => v.Facts));
        Assert.Equal(IncidentStatus.Investigating, onboard.Status);
        Assert.Single(onboard.Events, e => e.Kind == IncidentEventKind.InvestigationOpened);                  // the first save opened it, once
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.OpenInvestigationAsync(_s.CompanyId, report.Id, _s.User));
    }

    [Fact]
    public async Task NoInvestigationIsNeededForALowReport_ButNotForASeriousOne()
    {
        var low = await SubmittedAsync(Input(potential: 1, likelihood: 1));
        var serious = await SubmittedAsync(Input(potential: 5, likelihood: 4));
        foreach (var r in new[] { low, serious }) await _vessel.ReviewAsync(_s.CompanyId, r.Id, As(_master), "ok");
        await _s.SettleUntilQuietAsync();

        Assert.Contains("needs an investigation", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SkipInvestigationAsync(_s.CompanyId, serious.Id, _s.User, "Not worth it"))).Message);
        Assert.Contains("why", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SkipInvestigationAsync(_s.CompanyId, low.Id, _s.User, ""))).Message);
        Assert.Equal(IncidentStatus.ReadyToClose, await StatusAsync(_shore, low.Id, _s.User));               // nothing to investigate, nothing open
        await _shore.SkipInvestigationAsync(_s.CompanyId, low.Id, _s.User, "A tool dropped on an empty deck; the lanyard rule was repeated at the toolbox talk.");
        await _shore.CloseAsync(_s.CompanyId, low.Id, _s.User, "Closed");
        Assert.Contains("investigation is required", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.CloseAsync(_s.CompanyId, serious.Id, _s.User, "Closed"))).Message);
    }

    [Fact]
    public async Task ARepeatAtTheSamePlaceIsShownWithTheReport_AndTheThirdSuggestsAReviewOfTheAssessment()
    {
        var first = await SubmittedAsync(Input(occurred: DateTime.UtcNow.AddMonths(-3)));
        var second = await SubmittedAsync(Input(occurred: DateTime.UtcNow.AddMonths(-1)));
        var third = await SubmittedAsync();
        var shown = (await _shore.GetAsync(_s.CompanyId, third.Id, _s.User))!;
        Assert.Equal([second.Id, first.Id], shown.Repeats.Select(r => r.Id));
        Assert.True(shown.SuggestAssessmentReview);
        Assert.False((await _shore.GetAsync(_s.CompanyId, second.Id, _s.User))!.SuggestAssessmentReview);       // only one before it

        await _vessel.WithdrawAsync(_s.CompanyId, second.Id, As(_reporter), "Entered twice");
        await _s.SettleUntilQuietAsync();
        Assert.Equal([first.Id], (await _shore.GetAsync(_s.CompanyId, third.Id, _s.User))!.Repeats.Select(r => r.Id));      // a withdrawn report is not a repeat
    }

    // ---- who sees what ----

    [Fact]
    public async Task ACrewMemberListsTheirOwnReports_TheMasterTheVessels_TheOfficeAll()
    {
        var mine = await SubmittedAsync();
        var cooks = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_cook), null, Input());
        await _vessel.SubmitAsync(_s.CompanyId, cooks.Id, As(_cook));
        await _s.SettleUntilQuietAsync();

        Assert.Equal([mine.Id], (await _vessel.ListAsync(_s.CompanyId, As(_reporter))).Select(r => r.Incident.Id));
        Assert.Equal([cooks.Id], (await _vessel.ListAsync(_s.CompanyId, As(_cook))).Select(r => r.Incident.Id));
        Assert.Equal(2, (await _vessel.ListAsync(_s.CompanyId, As(_master))).Count);
        Assert.Equal(2, (await _shore.ListAsync(_s.CompanyId, _s.User)).Count);
        Assert.Equal(2, (await _shore.ListAsync(_s.CompanyId, _s.User, new IncidentFilter(Status: IncidentStatus.Reported, Text: "INC"))).Count);
        Assert.Empty(await _shore.ListAsync(_s.CompanyId, _s.User, new IncidentFilter(Status: IncidentStatus.Closed)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ListAsync(_s.CompanyId, _s.User));                  // the office user is no crew member onboard
    }

    [Fact]
    public async Task TheOfficeSeesACrewMemberWhereTheReporterAskedAndTheCompanyAllows_TheVesselAlwaysSeesTheName()
    {
        var report = await SubmittedAsync(Input(anonymous: true));
        Assert.Equal(_reporter.FullName, (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.ReporterName);                // the company has not allowed it

        var settings = await _shore.SettingsAsync(_s.CompanyId);
        settings.AllowAnonymousToOffice = true;
        await _shore.SaveSettingsAsync(_s.CompanyId, settings);
        await _s.SettleUntilQuietAsync();
        Assert.Equal("A crew member", (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.ReporterName);
        Assert.Equal("A crew member", (await _shore.ListAsync(_s.CompanyId, _s.User)).Single().ReporterName);
        Assert.Equal(_reporter.FullName, (await _vessel.GetAsync(_s.CompanyId, report.Id, As(_master)))!.ReporterName);
    }

    [Fact]
    public async Task ASharedLessonReachesEveryoneWithoutNames_OnlyOnceReviewed_AndNotWhenWithdrawn()
    {
        var injured = new IncidentPersonInput(_cook.Id, _cook.FullName, "Cook", "", IncidentPersonRole.Injured, "Burn", "Left hand", IncidentTreatment.FirstAid);
        var shared = await SubmittedAsync(Input(IncidentKind.InjuryIllness, actual: 1, potential: 3, likelihood: 2, share: true, persons: [injured]));
        var notShared = await SubmittedAsync(Input());

        Assert.Empty(await _vessel.LessonsAsync(_s.CompanyId, As(_cook)));                                       // not reviewed yet
        await _vessel.ReviewAsync(_s.CompanyId, shared.Id, As(_master), "ok");
        await _vessel.ReviewAsync(_s.CompanyId, notShared.Id, As(_master), "ok");
        await _s.SettleUntilQuietAsync();

        foreach (var (side, who) in new[] { (_vessel, As(_cook)), (_shore, _s.User) })
        {
            var lesson = Assert.Single(await side.LessonsAsync(_s.CompanyId, who));
            Assert.Equal(shared.Id, lesson.IncidentId);
            var text = System.Text.Json.JsonSerializer.Serialize(lesson);
            Assert.DoesNotContain(_cook.FullName, text);
            Assert.DoesNotContain(_reporter.FullName, text);
            Assert.DoesNotContain("Burn", text);
        }

        await _vessel.WithdrawAsync(_s.CompanyId, shared.Id, As(_master), "Wrong vessel");
        Assert.Empty(await _vessel.LessonsAsync(_s.CompanyId, As(_cook)));
    }

    // ---- reports for another vessel ----

    [Fact]
    public async Task AVesselNeverHoldsAnotherVesselsReports_AndAReportTheOfficeEntersIsTheOfficesToChange()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, Module = PolicyModules.Incidents, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        var other = await _shore.SaveDraftAsync(_s.CompanyId, _s.OtherVesselId, _s.User, null, Input());
        await _shore.SubmitAsync(_s.CompanyId, other.Id, _s.User);
        var ours = await _shore.SaveDraftAsync(_s.CompanyId, _s.VesselId, _s.User, null, Input());
        await _shore.SubmitAsync(_s.CompanyId, ours.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.CurrentCompanyId = _s.CompanyId;
            var held = await db.Incidents.IgnoreQueryFilters().Select(i => i.Id).ToListAsync();
            Assert.Contains(ours.Id, held);
            Assert.DoesNotContain(other.Id, held);
        }

        // The vessel can review what the office entered, but cannot rewrite it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_master), ours.Id, Input()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddSupplementAsync(_s.CompanyId, ours.Id, As(_master), "Changed my mind"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, ours.Id, As(_master), "Reviewed", (Actual: 1, Potential: 1, Likelihood: 1)));
        await _vessel.ReviewAsync(_s.CompanyId, ours.Id, As(_master), "Reviewed");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.Reviewed, await StatusAsync(_shore, ours.Id, _s.User));
    }

    // ---- settings and lists ----

    [Fact]
    public async Task TheSettingsAndTheCausesAreTheOfficesAndReachTheVessel()
    {
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveSettingsAsync(_s.CompanyId, new IncidentSettings { LateReportHours = 0 }));
        Assert.Contains("late", bad.Message);
        await _shore.SaveSettingsAsync(_s.CompanyId, new IncidentSettings { LateReportHours = 12, RepeatWindowMonths = 6, InvestigationDays = "5, 10", UrgentForLostTime = false, UrgentForPollution = true, AllowAnonymousToOffice = true });
        var added = await _shore.SaveCauseAsync(_s.CompanyId, null, "Crane signalling", "Operations", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveCauseAsync(_s.CompanyId, null, "Crane signalling", "Operations", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveCauseAsync(_s.CompanyId, null, "A;B", "", true));
        await _s.SettleUntilQuietAsync();

        var onboard = await _vessel.SettingsAsync(_s.CompanyId);
        Assert.Equal((12, 6, "5,10", false, true), (onboard.LateReportHours, onboard.RepeatWindowMonths, onboard.InvestigationDays, onboard.UrgentForLostTime, onboard.AllowAnonymousToOffice));
        Assert.Contains(await _vessel.CausesAsync(_s.CompanyId), c => c.Id == added.Id);

        await _shore.SaveCauseAsync(_s.CompanyId, added.Id, "Crane signalling", "Operations", false);
        await _s.SettleUntilQuietAsync();
        Assert.DoesNotContain(await _vessel.CausesAsync(_s.CompanyId), c => c.Id == added.Id);
        Assert.Contains(await _vessel.CausesAsync(_s.CompanyId, includeInactive: true), c => c.Id == added.Id);
    }

    [Fact]
    public async Task ANewCompanyStartsWithTheDefaultCausesAndSettings()
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.CurrentCompanyId = _s.CompanyId;
        Assert.True(await db.IncidentCauses.CountAsync() >= 15);
        Assert.Equal("7,14,30,60", (await db.IncidentSettingsRows.SingleAsync()).InvestigationDays);
    }

    // ---- what each side is offered ----

    [Fact]
    public async Task EachStepIsOfferedOnlyWhenItWouldBeAccepted_OnEachSide()
    {
        var report = await SubmittedAsync();                                                                       // Medium High: an investigation is required
        var office = (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.Can;
        Assert.True(office.IsOffice && office.OpenInvestigation && office.Notify && office.WriteInvestigation);
        Assert.False(office.SkipInvestigation);                                                                    // a report of this band needs one
        Assert.False(office.Close || office.Reopen || office.Review || office.Edit || office.Submit);
        var master = (await _vessel.GetAsync(_s.CompanyId, report.Id, As(_master)))!.Can;
        Assert.True(master.Review && master.Edit && master.AddAction && master.Withdraw && master.Findings);
        Assert.False(master.OpenInvestigation || master.Close || master.Notify || master.WriteInvestigation);
        var reporter = (await _vessel.GetAsync(_s.CompanyId, report.Id, As(_reporter)))!.Can;
        Assert.True(reporter.Edit && reporter.Supplement && reporter.Withdraw);
        Assert.False(reporter.Review || reporter.AddAction);

        await _vessel.ReviewAsync(_s.CompanyId, report.Id, As(_master), "ok");
        await _s.SettleUntilQuietAsync();
        Assert.False((await _vessel.GetAsync(_s.CompanyId, report.Id, As(_reporter)))!.Can.Edit);                  // locked after the review
        await _shore.OpenInvestigationAsync(_s.CompanyId, report.Id, _s.User);
        var opened = (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.Can;
        Assert.False(opened.OpenInvestigation);
        Assert.True(opened.WriteInvestigation && !opened.Close);                                                   // not finished: closing is not offered

        var cause = (await _shore.CausesAsync(_s.CompanyId)).First().Name;
        await _shore.SaveInvestigationAsync(_s.CompanyId, report.Id, _s.User, new InvestigationInput("Facts", "", [cause], "", "", IncidentAssessmentFinding.Adequate, "", "", true));
        Assert.True((await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.Can.Close);
        await _shore.CloseAsync(_s.CompanyId, report.Id, _s.User, "Done");
        var closed = (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.Can;
        Assert.True(closed.Reopen);
        Assert.False(closed.Close || closed.WriteInvestigation || closed.OpenInvestigation || closed.AddAction || closed.Withdraw);
    }

    [Fact]
    public async Task ALowReportCanHaveItsInvestigationWaived_AndThenNoInvestigationIsWritten()
    {
        var low = await SubmittedAsync(Input(potential: 1, likelihood: 1));
        Assert.True((await _shore.GetAsync(_s.CompanyId, low.Id, _s.User))!.Can.SkipInvestigation);
        await _shore.SkipInvestigationAsync(_s.CompanyId, low.Id, _s.User, "Nothing to learn beyond the toolbox talk.");
        var can = (await _shore.GetAsync(_s.CompanyId, low.Id, _s.User))!.Can;
        Assert.False(can.OpenInvestigation || can.SkipInvestigation || can.WriteInvestigation);
        Assert.Contains("No investigation was needed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveInvestigationAsync(_s.CompanyId, low.Id, _s.User,
            new InvestigationInput("Facts", "", [], "", "", IncidentAssessmentFinding.NotLooked, "", "", false)))).Message);
    }

    // ---- the queue ----

    [Fact]
    public async Task TheQueueHoldsWhatWaitsForTheOffice_OverdueFirst_AndLeavesOutWhatIsDealtWith()
    {
        var unreviewed = await SubmittedAsync(Input(potential: 3, likelihood: 3));                                 // Medium, 60 days
        var reviewed = await SubmittedAsync(Input(potential: 4, likelihood: 3));                                   // Medium High, 30 days
        var investigating = await SubmittedAsync(Input(potential: 4, likelihood: 4));                              // High, 14 days
        var late = await SubmittedAsync(Input(potential: 5, likelihood: 4));                                       // Extreme, 7 days
        var done = await SubmittedAsync(Input(potential: 1, likelihood: 1));
        foreach (var r in new[] { reviewed, investigating, late, done }) await _vessel.ReviewAsync(_s.CompanyId, r.Id, As(_master), "ok");
        await _s.SettleUntilQuietAsync();
        await _shore.OpenInvestigationAsync(_s.CompanyId, investigating.Id, _s.User);
        await _shore.OpenInvestigationAsync(_s.CompanyId, late.Id, _s.User);
        await _shore.SkipInvestigationAsync(_s.CompanyId, done.Id, _s.User, "Nothing to investigate");
        await _shore.CloseAsync(_s.CompanyId, done.Id, _s.User, "Closed");

        // The extreme report was submitted a month ago, so its investigation is overdue (the vessel owns the report, so the date is moved there and travels up).
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.CurrentCompanyId = _s.CompanyId;
            var row = await db.Incidents.SingleAsync(i => i.Id == late.Id);
            row.SubmittedAt = DateTime.UtcNow.AddDays(-30);
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();

        var queue = await _shore.QueueAsync(_s.CompanyId, _s.User);
        Assert.Equal([late.Id, investigating.Id, reviewed.Id, unreviewed.Id], queue.Select(r => r.Incident.Id));       // the overdue first, then by the date due
        Assert.True(queue[0].InvestigationOverdue);
        Assert.All(queue.Skip(1), r => Assert.False(r.InvestigationOverdue));
        Assert.Equal([IncidentStatus.Investigating, IncidentStatus.Investigating, IncidentStatus.Reviewed, IncidentStatus.Reported], queue.Select(r => r.Status));
        Assert.Equal(DateTime.UtcNow.AddDays(-30).Date.AddDays(7), queue[0].Due!.Value.Date);                                  // submitted a month ago, 7 days for an Extreme report

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.QueueAsync(_s.CompanyId, As(_master)));      // the queue is the office's
    }

    // ---- the printed report ----

    [Fact]
    public async Task TheReportMakesAPdf_WithAFileNameFromItsNumber()
    {
        var injured = new IncidentPersonInput(_cook.Id, _cook.FullName, "Cook", "", IncidentPersonRole.Injured, "Burn", "Left hand", IncidentTreatment.FirstAid, 2);
        var report = await SubmittedAsync(Input(IncidentKind.InjuryIllness, actual: 2, potential: 3, likelihood: 3, persons: [injured]));
        await _vessel.AddSupplementAsync(_s.CompanyId, report.Id, As(_reporter), "Treated by the Chief Officer.");
        await _vessel.ReviewAsync(_s.CompanyId, report.Id, As(_master), "ok");
        await _s.SettleUntilQuietAsync();
        var detail = (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!;

        var pdf = IncidentPdf.Build(detail, "Test Shipping");
        Assert.True(pdf.Length > 1500);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var name = IncidentPdf.FileName(detail);
        Assert.StartsWith("incident-", name);
        Assert.EndsWith(".pdf", name);
        Assert.DoesNotContain(" ", name);
    }

    // ---- changes after the report has already travelled ----

    [Fact]
    public async Task ChangesToThePeopleAndAdditionsMadeAfterTheReportHasAlreadyTravelled_TravelToo()
    {
        var injured = new IncidentPersonInput(_cook.Id, _cook.FullName, "Cook", "", IncidentPersonRole.Injured, "Burn", "Hand", IncidentTreatment.FirstAid, 0);
        var report = await SubmittedAsync(Input(IncidentKind.InjuryIllness, actual: 1, potential: 3, likelihood: 2, persons: [injured]));      // submitted and settled: the office holds it
        Assert.Single((await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.Incident.Persons);

        // The reporter corrects it before the Master reviews: the injury was worse, and a witness is added.
        var worse = injured with { Treatment = IncidentTreatment.Hospital, DaysLost = 4, BodyPart = "Left forearm" };
        await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), report.Id,
            Input(IncidentKind.InjuryIllness, actual: 1, potential: 3, likelihood: 2, persons: [worse, new IncidentPersonInput(_reporter.Id, _reporter.FullName, "Third Officer", "", IncidentPersonRole.Witness)]));
        await _s.SettleUntilQuietAsync();
        var seen = (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!;
        Assert.Equal(2, seen.Incident.Persons.Count);
        Assert.Equal((IncidentTreatment.Hospital, 4, true), (seen.Incident.Persons.Single(p => p.Role == IncidentPersonRole.Injured).Treatment, seen.Incident.Persons.Single(p => p.Role == IncidentPersonRole.Injured).DaysLost, seen.Incident.IsUrgent));   // four days lost makes it urgent

        // After the review, a supplement travels as well.
        await _vessel.ReviewAsync(_s.CompanyId, report.Id, As(_master), "Reviewed");
        await _s.SettleUntilQuietAsync();
        await _vessel.AddSupplementAsync(_s.CompanyId, report.Id, As(_reporter), "Treated by the Chief Officer, then landed at the next port.");
        await _s.SettleUntilQuietAsync();
        Assert.Single((await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.Incident.Supplements);
        Assert.Equal(2, (await _shore.GetAsync(_s.CompanyId, report.Id, _s.User))!.Incident.Persons.Count);
    }

    // ---- hours worked and the statistics ----

    private static readonly DateTime LastMonth = IncidentStats.MonthOf(DateTime.UtcNow).AddMonths(-1);

    [Fact]
    public async Task OnlyTheMasterOrTheOfficeTypesTheHoursWorked_TheNewestForAMonthCounts_AndItTravels()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_cook), LastMonth, 5000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_reporter), LastMonth, 5000));
        Assert.Contains("more than 0", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_master), LastMonth, 0))).Message);
        Assert.Contains("not begun", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_master), LastMonth.AddMonths(3), 5000))).Message);

        await _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_master), LastMonth.AddDays(12), 5000);                     // any day of the month is the month
        await Task.Delay(20);
        await _shore.SetExposureAsync(_s.CompanyId, _s.VesselId, _s.User, LastMonth, 6000, "Corrected from the crew list");
        await _s.SettleUntilQuietAsync();

        foreach (var side in new[] { _shore, _vessel })
        {
            var view = await side.StatsAsync(_s.CompanyId, side == _shore ? _s.User : As(_master), LastMonth, 1);
            var hours = Assert.Single(view.Hours[_s.VesselId]);
            Assert.Equal((LastMonth, 6000m, "Typed"), (hours.Month, hours.Hours, hours.Source));
        }
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.CurrentCompanyId = _s.CompanyId;
        Assert.Equal(2, await db.IncidentExposures.CountAsync());                                                                // both kept; the newest counts
    }

    [Fact]
    public async Task TheStatisticsAddUpRealReports_AndTheRatesUseTheHoursTypedIn()
    {
        var injured = new IncidentPersonInput(_cook.Id, _cook.FullName, "Cook", "", IncidentPersonRole.Injured, "Fracture", "Wrist", IncidentTreatment.Hospital, 5);
        var when = LastMonth.AddDays(10);
        await SubmittedAsync(Input(IncidentKind.InjuryIllness, actual: 3, potential: 4, likelihood: 3, persons: [injured], occurred: when));
        await SubmittedAsync(Input(occurred: when.AddDays(1)));
        await SubmittedAsync(Input(occurred: when.AddDays(2)));
        await _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_master), LastMonth, 400_000);
        await _s.SettleUntilQuietAsync();

        var view = await _shore.StatsAsync(_s.CompanyId, _s.User, LastMonth, 1);
        var fleet = view.Fleet;
        Assert.Equal((3, 2, 1, 1, 5, 1), (fleet.Reports, fleet.NearMisses, fleet.InjuryReports, fleet.LostTimeInjuries, fleet.LostDays, fleet.Recordable));
        Assert.Equal(2.5, fleet.Ltif);                                                                                           // 1 case in 400,000 hours
        Assert.Equal(2.5, fleet.Trcf);
        Assert.Equal(2.0, fleet.NearMissesPerInjury);
        Assert.Equal((400_000m, 1, true), (fleet.Hours, fleet.MonthsWithHours, fleet.HoursComplete));
        Assert.Equal(fleet.Reports, view.Vessels.Single().Reports);
        Assert.Equal(3, view.Trend.Single().Reports);
        Assert.Empty(view.Quiet);                                                                                                // the vessel has reported

        // The same figures onboard, for the Master.
        var onboard = (await _vessel.StatsAsync(_s.CompanyId, As(_master), LastMonth, 1)).Fleet;
        Assert.Equal((fleet.Reports, fleet.LostTimeInjuries, fleet.Ltif), (onboard.Reports, onboard.LostTimeInjuries, onboard.Ltif));
    }

    [Fact]
    public async Task AWithdrawnReportIsLeftOutOfTheStatistics_AndAMonthWithoutHoursGivesNoRate()
    {
        var when = LastMonth.AddDays(4);
        var kept = await SubmittedAsync(Input(occurred: when));
        var gone = await SubmittedAsync(Input(occurred: when.AddDays(1)));
        await _vessel.WithdrawAsync(_s.CompanyId, gone.Id, As(_reporter), "Entered twice");
        await _s.SettleUntilQuietAsync();
        var fleet = (await _shore.StatsAsync(_s.CompanyId, _s.User, LastMonth, 1)).Fleet;
        Assert.Equal(1, fleet.Reports);
        Assert.Null(fleet.Ltif);
        Assert.Equal(0, fleet.MonthsWithHours);
        Assert.NotEqual(Guid.Empty, kept.Id);
    }

    [Fact]
    public async Task TheHoursComeFromHoursOfRestWhereTheVesselRecordsThem_AndOverrideWhatWasTyped()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RestHours, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        var rest = new RestHoursService(_s.VesselFactory);
        var day = LastMonth.AddDays(6);
        await rest.RecordAsync(_s.CompanyId, _s.VesselId, As(_reporter), _reporter.Id, day, "08:00-12:00,13:00-17:00");        // 8 h
        await rest.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), _master.Id, day, "06:00-12:00");                         // 6 h
        await rest.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), _master.Id, day, "06:00-10:00", "Corrected: only four hours");   // the correction counts: 4 h
        await _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_master), LastMonth, 100_000);
        await _s.SettleUntilQuietAsync();

        var view = await _shore.StatsAsync(_s.CompanyId, _s.User, LastMonth, 2);
        var hours = view.Hours[_s.VesselId].Single();
        Assert.Equal((LastMonth, 12m, "Hours of rest"), (hours.Month, hours.Hours, hours.Source));
        Assert.Equal(1, view.Fleet.MonthsWithHours);
        Assert.Equal(2, view.Fleet.Months);
    }

    [Fact]
    public async Task TheMastersStatisticsAreTheirVesselsOnly_ACrewMemberHasNone_AndTheOfficeCanChooseAVessel()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StatsAsync(_s.CompanyId, As(_cook), LastMonth, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StatsAsync(_s.CompanyId, _s.User, LastMonth, 1));      // the office user is no crew member onboard
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.StatsAsync(_s.CompanyId, _s.User, LastMonth, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.StatsAsync(_s.CompanyId, _s.User, LastMonth, 61));

        var onboard = await _vessel.StatsAsync(_s.CompanyId, As(_master), LastMonth, 3);
        Assert.Equal([_s.VesselId], onboard.Vessels.Select(v => v.VesselId));
        Assert.Empty(onboard.Quiet);                                                                                             // the quiet list is the office's
        var chosen = await _shore.StatsAsync(_s.CompanyId, _s.User, LastMonth, 3, _s.VesselId);
        Assert.Equal([_s.VesselId], chosen.Vessels.Select(v => v.VesselId));
        Assert.Equal(chosen.Vessels.Single().Vessel, chosen.Fleet.Vessel);
        Assert.Equal([_s.VesselId], (await _shore.StatsAsync(_s.CompanyId, _s.User, LastMonth, 3)).Quiet.Select(q => q.VesselId));   // no report yet: a quiet vessel
    }

    [Fact]
    public async Task TheCountsForTheHomePage_FollowWhoIsAsking()
    {
        var urgent = await SubmittedAsync(Input(IncidentKind.Pollution, potential: 2, likelihood: 2));
        var needsInvestigation = await SubmittedAsync(Input(potential: 4, likelihood: 3));
        var unreviewed = await SubmittedAsync(Input(potential: 1, likelihood: 1));
        foreach (var r in new[] { urgent, needsInvestigation }) await _vessel.ReviewAsync(_s.CompanyId, r.Id, As(_master), "ok");
        await _s.SettleUntilQuietAsync();

        var master = (await _vessel.CountsAsync(_s.CompanyId, As(_master)))!;
        Assert.Equal((3, 1, 1, 0, 3), (master.Open, master.ToReview, master.UrgentOpen, master.Overdue, master.Mine));
        Assert.True(master.IsMaster);
        var office = (await _shore.CountsAsync(_s.CompanyId, _s.User))!;
        Assert.Equal((3, 1, 1, 1), (office.Open, office.ToReview, office.ToInvestigate, office.UrgentOpen));       // only the reviewed report that needs an investigation waits for one; the pollution report was urgent
        Assert.False(office.IsMaster);

        var mine = (await _vessel.CountsAsync(_s.CompanyId, As(_reporter)))!;
        Assert.Equal((3, 3), (mine.Open, mine.Mine));                                                              // the reporter made all three
        var cook = (await _vessel.CountsAsync(_s.CompanyId, As(_cook)))!;
        Assert.Equal(0, cook.Open);
        Assert.Null(await _vessel.CountsAsync(_s.CompanyId, _s.User));                                             // not a crew member onboard
        Assert.NotEqual(Guid.Empty, unreviewed.Id);
    }
}
