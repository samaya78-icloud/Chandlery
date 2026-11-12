using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// Incident reporting as one story between a real office database and a real vessel database: the office sets its rules, the vessel reports a near miss, an injury,
/// a pollution report and an observation, the Master reviews, the office investigates, sets actions, closes and reopens, and the numbers are typed in. At the end
/// both sides must read the same status, history and statistics for every report, a shared lesson must carry no names, and the vessel must hold nothing of another
/// vessel's reports, even after a full baseline.
/// </summary>
public class IncidentWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private IncidentService _shore = null!, _vessel = null!;
    private WorklistService _shoreWorklist = null!;
    private CrewMember _reporter = null!, _master = null!, _cook = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _shore = new IncidentService(_s.ShoreFactory, _shoreWorklist);
        _vessel = new IncidentService(_s.VesselFactory, new WorklistService(_s.VesselFactory));
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(Guid vesselId, params string[] modules)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in modules) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<CrewMember> SignOnAsync(string code, string rank, Guid vesselId, bool master = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", vesselId, null, true, false, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static IncidentInput Input(IncidentKind kind, string place, string description, int actual, int potential, int likelihood, DateTime occurred, bool share = false, bool anonymous = false,
        IReadOnlyList<IncidentPersonInput>? persons = null)
        => new(kind, occurred, place, IncidentShipStatus.AtSea, "Moderate sea", description, "Made safe and reported.", null, "", "", anonymous, share, actual, potential, likelihood, persons ?? []);

    private async Task<Incident> ReportAsync(IncidentInput input)
    {
        var draft = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, input);
        return await _vessel.SubmitAsync(_s.CompanyId, draft.Id, As(_reporter));
    }

    /// <summary>One report as a line that can be compared between the two sides. The reporter's name is left out: the office may be shown "a crew member".</summary>
    private static string Line(IncidentDetail d)
        => $"{d.Incident.Code}|{d.Status}|{d.Incident.Band}|urgent {d.Incident.IsUrgent}|late {d.ReportedLate}|due {d.InvestigationDue:yyyy-MM-dd}|overdue {d.InvestigationOverdue}|"
           + $"events {string.Join(",", d.Events.Select(e => e.Kind))}|supp {d.Incident.Supplements.Count}|persons {d.Incident.Persons.Count}|inv {d.Investigations.Count}/{d.Investigation?.Finished}|"
           + $"actions {d.Actions.Count}/{d.OpenActions}|blockers {d.CloseBlockers.Count}|repeats {d.Repeats.Count}";

    private static string Stats(IncidentStatsRow r)
        => $"{r.Reports}|{r.NearMisses}|{r.InjuryReports}|{r.LostTimeInjuries}|{r.LostDays}|{r.Recordable}|{r.Hours}|{r.MonthsWithHours}/{r.Months}|{r.Ltif:0.####}|{r.Trcf:0.####}|{r.Open}|{r.Urgent}|{r.Overdue}|"
           + $"{r.MeanDaysToClose:0.#}|{string.Join(",", r.ByKind.Where(p => p.Value > 0).OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))}|{string.Join(",", r.ByBand.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))}";

    [Fact]
    public async Task TheOfficeSetsItsRules_TheVesselReportsAndTheMasterReviews_TheOfficeInvestigatesAndCloses_AndBothSidesEndWithTheSameStatusesHistoryAndNumbers()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.Incidents, PolicyModules.Worklist);
        _reporter = await SignOnAsync("K1001", "Third Officer", _s.VesselId);
        _master = await SignOnAsync("K1002", "Master", _s.VesselId, master: true);
        _cook = await SignOnAsync("K1003", "Cook", _s.VesselId);

        // The office decides how it wants reports handled.
        var settings = await _shore.SettingsAsync(_s.CompanyId);
        (settings.AllowAnonymousToOffice, settings.InvestigationDays, settings.LateReportHours) = (true, "5, 10, 20, 40", 24);
        await _shore.SaveSettingsAsync(_s.CompanyId, settings);
        var cause = await _shore.SaveCauseAsync(_s.CompanyId, null, "Crane signalling", "Operations", true);
        await _s.SettleUntilQuietAsync();
        Assert.Equal("5,10,20,40", (await _vessel.SettingsAsync(_s.CompanyId)).InvestigationDays);

        // The vessel reports: a near miss (late, anonymous, to be shared), an injury, a pollution report and an observation.
        var now = DateTime.UtcNow;
        var near = await ReportAsync(Input(IncidentKind.NearMiss, "Main deck, forward", "A lashing bar slipped from a stack during a roll and landed beside a deckhand.", 0, 3, 3, now.AddHours(-30), share: true, anonymous: true));
        var injury = await ReportAsync(Input(IncidentKind.InjuryIllness, "Galley", "A pan of hot oil tipped during a roll.", 3, 3, 3, now.AddHours(-3),
            persons: [new IncidentPersonInput(_cook.Id, _cook.FullName, "Cook", "", IncidentPersonRole.Injured, "Burn", "Left forearm", IncidentTreatment.Hospital, 6)]));
        var spill = await ReportAsync(Input(IncidentKind.Pollution, "Deck, port side", "A small hydraulic leak reached the scuppers.", 1, 2, 2, now.AddHours(-3)));
        var idea = await ReportAsync(Input(IncidentKind.Observation, "Bridge", "The new checklist for pilot boarding works well.", 0, 1, 1, now.AddHours(-3)));
        Assert.False(near.IsUrgent);
        Assert.True(spill.IsUrgent);
        await _s.SettleUntilQuietAsync();

        // The Master reviews all four; the injury is worse than first thought.
        foreach (var r in new[] { near, spill, idea }) await _vessel.ReviewAsync(_s.CompanyId, r.Id, As(_master), "Reviewed.");
        await _vessel.ReviewAsync(_s.CompanyId, injury.Id, As(_master), "Hospital treatment: scored again.", (Actual: 3, Potential: 5, Likelihood: 4));
        await _vessel.AddSupplementAsync(_s.CompanyId, near.Id, As(_reporter), "The bar was a different length from the stack's others.");
        await _vessel.AddVesselFindingsAsync(_s.CompanyId, injury.Id, As(_master), "The galley rail was removed for repair and not replaced.");
        await _vessel.WithdrawAsync(_s.CompanyId, spill.Id, As(_reporter), "Entered twice: the leak was already reported as a defect.");
        await _s.SettleUntilQuietAsync();

        // Ashore: the injury is urgent and Extreme, so an investigation is due in 5 days.
        var seen = (await _shore.GetAsync(_s.CompanyId, injury.Id, _s.User))!;
        Assert.Equal(("Extreme", true, IncidentStatus.Reviewed), (seen.Incident.Band, seen.Incident.IsUrgent, seen.Status));
        Assert.Equal(seen.Incident.SubmittedAt!.Value.Date.AddDays(5), seen.InvestigationDue);
        Assert.Equal("A crew member", (await _shore.GetAsync(_s.CompanyId, near.Id, _s.User))!.ReporterName);                       // asked for, and allowed
        Assert.Equal(_reporter.FullName, (await _vessel.GetAsync(_s.CompanyId, near.Id, As(_master)))!.ReporterName);                // the vessel sees the name

        await _shore.OpenInvestigationAsync(_s.CompanyId, injury.Id, _s.User, "Superintendent to visit");
        await _shore.SaveInvestigationAsync(_s.CompanyId, injury.Id, _s.User, new InvestigationInput("A pan tipped when the ship rolled.", "", [], "", "", IncidentAssessmentFinding.NotLooked, "", "Superintendent", false));
        await _shore.SaveInvestigationAsync(_s.CompanyId, injury.Id, _s.User, new InvestigationInput("A pan tipped when the ship rolled; the galley rail was missing.", "Rail removed for repair.", [cause.Name], "No check that the rail was back.", "", IncidentAssessmentFinding.Inadequate, "Check guards are fitted before the galley is used.", "Superintendent", true));
        var action = await _shore.AddActionAsync(_s.CompanyId, injury.Id, _s.User, new NewIncidentAction("Fit and check the galley rail", "Before the next meal service.", DateTime.UtcNow.AddDays(3)));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(IncidentStatus.ActionsOpen, (await _vessel.GetAsync(_s.CompanyId, injury.Id, As(_master)))!.Status);
        Assert.Single((await _vessel.GetAsync(_s.CompanyId, injury.Id, As(_master)))!.Actions);

        await _shoreWorklist.AddEntryAsync(_s.CompanyId, action.Id, new WorklistEntryInput(WorklistEntryType.Status, Status: WorklistStatus.Cancelled, Text: "Done by the ship's carpenter"), _s.User);
        await _shore.RecordNotificationAsync(_s.CompanyId, injury.Id, _s.User, "Flag state told, ref F-2201");
        await _shore.SkipInvestigationAsync(_s.CompanyId, idea.Id, _s.User, "A good-practice note: nothing to investigate.");
        await _shore.CloseAsync(_s.CompanyId, idea.Id, _s.User, "Shared with the fleet.");
        await _shore.CloseAsync(_s.CompanyId, injury.Id, _s.User, "Rail fitted and checked.");
        await _shore.ReopenAsync(_s.CompanyId, injury.Id, _s.User, "A second pan nearly fell: check the stove guard too.");
        await _s.SettleUntilQuietAsync();

        // The hours worked: typed in onboard for last month, and by the office for this one.
        await _vessel.SetExposureAsync(_s.CompanyId, _s.VesselId, As(_master), IncidentStats.MonthOf(now).AddMonths(-1), 300_000);
        await _shore.SetExposureAsync(_s.CompanyId, _s.VesselId, _s.User, IncidentStats.MonthOf(now), 300_000, "From the crew list");
        await _s.SettleUntilQuietAsync();

        // ---- both sides read the same ----
        foreach (var r in new[] { near, injury, spill, idea })
        {
            var ashore = (await _shore.GetAsync(_s.CompanyId, r.Id, _s.User))!;
            var onboard = (await _vessel.GetAsync(_s.CompanyId, r.Id, As(_master)))!;
            Assert.Equal(Line(ashore), Line(onboard));
        }
        Assert.Equal(IncidentStatus.ReadyToClose, (await _shore.GetAsync(_s.CompanyId, injury.Id, _s.User))!.Status);          // reopened, investigation finished, the action done
        Assert.Equal(IncidentStatus.Closed, (await _vessel.GetAsync(_s.CompanyId, idea.Id, As(_master)))!.Status);
        Assert.Equal(IncidentStatus.Withdrawn, (await _shore.GetAsync(_s.CompanyId, spill.Id, _s.User))!.Status);
        Assert.Equal(IncidentStatus.Reviewed, (await _vessel.GetAsync(_s.CompanyId, near.Id, As(_master)))!.Status);            // a Medium report with no investigation opened yet
        Assert.Equal([IncidentEventKind.Reviewed, IncidentEventKind.VesselFindings, IncidentEventKind.InvestigationOpened, IncidentEventKind.NotificationRecorded, IncidentEventKind.Closed, IncidentEventKind.Reopened],
            (await _vessel.GetAsync(_s.CompanyId, injury.Id, As(_master)))!.Events.Select(e => e.Kind));

        // The statistics: the same figures from the office (for the vessel) and from the Master, and the right ones.
        var from = IncidentStats.MonthOf(now).AddMonths(-1);
        var office = (await _shore.StatsAsync(_s.CompanyId, _s.User, from, 2)).Vessels.Single();
        var master = (await _vessel.StatsAsync(_s.CompanyId, As(_master), from, 2)).Fleet;
        Assert.Equal(Stats(office), Stats(master));
        Assert.Equal((3, 1, 1, 1, 6, 1, 600_000m, 2), (office.Reports, office.NearMisses, office.InjuryReports, office.LostTimeInjuries, office.LostDays, office.Recordable, office.Hours, office.MonthsWithHours));
        Assert.Equal(1.6667, Math.Round(office.Ltif!.Value, 4));                                                                // one case in 600,000 hours
        Assert.Equal((2, 1), (office.Open, office.Urgent));                                                                    // the withdrawn spill is not counted; the injury is the urgent one
        Assert.Equal(IncidentReports.StatisticsCsv([office]), IncidentReports.StatisticsCsv([master with { Vessel = office.Vessel }]));

        // The shared lesson reaches the whole crew, without a name or an injury.
        var lesson = Assert.Single(await _vessel.LessonsAsync(_s.CompanyId, As(_cook)));
        Assert.Equal(near.Id, lesson.IncidentId);
        Assert.Equal(lesson, Assert.Single(await _shore.LessonsAsync(_s.CompanyId, _s.User)));
        var text = System.Text.Json.JsonSerializer.Serialize(lesson);
        foreach (var secret in new[] { _reporter.FullName, _cook.FullName, "Burn" }) Assert.DoesNotContain(secret, text);

        // What waits for each side.
        Assert.Equal([near.Id], (await _shore.QueueAsync(_s.CompanyId, _s.User)).Select(r => r.Incident.Id));
        var counts = (await _vessel.CountsAsync(_s.CompanyId, As(_master)))!;
        Assert.Equal((2, 0), (counts.Open, counts.ToReview));
    }

    [Fact]
    public async Task TheVesselHoldsOnlyItsOwnReports_NotEvenAfterAFullBaseline_WhileTheOfficeKeepsBothApart()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.Incidents);
        await SwitchOnAsync(_s.OtherVesselId, PolicyModules.Incidents);
        _reporter = await SignOnAsync("K2001", "Third Officer", _s.VesselId);
        _master = await SignOnAsync("K2002", "Master", _s.VesselId, master: true);
        var ours = await ReportAsync(Input(IncidentKind.NearMiss, "Engine room", "A hose clip came loose.", 0, 3, 3, DateTime.UtcNow.AddHours(-3)));
        await _vessel.ReviewAsync(_s.CompanyId, ours.Id, As(_master), "Reviewed.");
        await _s.SettleUntilQuietAsync();

        // The office has a report of its own for another vessel, with an investigation and typed hours.
        var cause = (await _shore.CausesAsync(_s.CompanyId)).First().Name;
        var theirs = await _shore.SaveDraftAsync(_s.CompanyId, _s.OtherVesselId, _s.User, null, Input(IncidentKind.NearMiss, "Bridge", "A chart table lamp fell.", 0, 4, 3, DateTime.UtcNow.AddHours(-5)));
        await _shore.SubmitAsync(_s.CompanyId, theirs.Id, _s.User);
        await _shore.OpenInvestigationAsync(_s.CompanyId, theirs.Id, _s.User);
        await _shore.SaveInvestigationAsync(_s.CompanyId, theirs.Id, _s.User, new InvestigationInput("The lamp bracket was loose.", "", [cause], "", "", IncidentAssessmentFinding.Adequate, "Check brackets.", "", true));
        await _shore.SetExposureAsync(_s.CompanyId, _s.OtherVesselId, _s.User, IncidentStats.MonthOf(DateTime.UtcNow), 120_000);
        await _s.SettleUntilQuietAsync();
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        await using var vessel = await _s.VesselFactory.CreateDbContextAsync();
        Assert.Equal([ours.Id], await vessel.Incidents.IgnoreQueryFilters().Select(i => i.Id).ToListAsync());
        Assert.All(await vessel.IncidentEvents.IgnoreQueryFilters().ToListAsync(), e => Assert.Equal(_s.VesselId, e.VesselId));
        Assert.Empty(await vessel.IncidentInvestigations.IgnoreQueryFilters().Where(v => v.VesselId == _s.OtherVesselId).ToListAsync());
        Assert.Empty(await vessel.IncidentExposures.IgnoreQueryFilters().Where(x => x.VesselId == _s.OtherVesselId).ToListAsync());
        Assert.Empty(await vessel.IncidentPersons.IgnoreQueryFilters().Where(p => p.IncidentId == theirs.Id).ToListAsync());

        // The office has both, apart.
        var all = await _shore.ListAsync(_s.CompanyId, _s.User);
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { _s.VesselId, _s.OtherVesselId }.Order(), all.Select(r => r.Incident.VesselId).Order());
        var stats = await _shore.StatsAsync(_s.CompanyId, _s.User, IncidentStats.MonthOf(DateTime.UtcNow).AddMonths(-1), 2);
        Assert.Equal(2, stats.Vessels.Count);
        Assert.All(stats.Vessels, v => Assert.Equal(1, v.Reports));
        Assert.Equal(2, stats.Fleet.Reports);
        Assert.Single((await _vessel.StatsAsync(_s.CompanyId, As(_master), IncidentStats.MonthOf(DateTime.UtcNow).AddMonths(-1), 2)).Vessels);
        Assert.Single(await _vessel.ListAsync(_s.CompanyId, As(_master)));
    }
}
