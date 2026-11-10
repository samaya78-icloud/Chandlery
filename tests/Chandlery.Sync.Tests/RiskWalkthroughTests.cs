using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The whole risk assessment story on a real office and a real vessel: the ship writes and submits while the office changes the library,
/// the office returns one and signs another, a signed one gets a new version and the old is Superseded, a control action reaches the
/// Worklist once, and both sides end with the same assessments, statuses and review dates.
/// </summary>
public class RiskWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RiskAssessmentService _shore = null!, _vessel = null!;
    private RiskLibraryService _library = null!, _vesselLibrary = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private Actor _crew = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shore = new RiskAssessmentService(_s.ShoreFactory, _shoreWorklist);
        _vessel = new RiskAssessmentService(_s.VesselFactory, _vesselWorklist);
        _library = new RiskLibraryService(_s.ShoreFactory);
        _vesselLibrary = new RiskLibraryService(_s.VesselFactory);
        _crew = await RiskTestSupport.MakeReviewersAsync(_s);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RiskAssessment, Enabled = true });
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Worklist, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    /// <summary>Everything in the document as the writer left it, with the residual score and (optionally) a control action on every hazard.</summary>
    private static AssessmentEdit Fill(RiskAssessment a, Guid category, string description, int after, string additional = "", string owner = "")
        => new(description, category, Today, "Stop work", [new RaTeamInput("Chief Officer", "C/O")],
            a.Activities.OrderBy(x => x.SortOrder).Select(act => new RaActivityInput(act.Id, act.Title,
                a.Hazards.Where(h => h.ActivityId == act.Id).OrderBy(h => h.SortOrder).Select(h => new RaHazardInput(h.Id, h.Hazard, h.Effect, h.ConsequenceBefore, h.ProbabilityBefore,
                    h.ExistingControls, additional, after, after, "", owner, owner == "" ? null : Today.AddDays(20))).ToList())).ToList());

    /// <summary>Starts from the template onboard, fills it in and submits.</summary>
    private async Task<(Guid Id, RaSubmitOutcome Outcome)> WriteAndSubmitAsync(Guid template, Guid category, string description, int after, string additional = "", string owner = "")
    {
        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "", Today.AddDays(-1), TemplateId: template), _crew);
        var draft = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        await _vessel.SaveAsync(_s.CompanyId, started.Id, Fill(draft.Assessment, category, description, after, additional, owner), _crew);
        var outcome = await _vessel.SubmitAsync(_s.CompanyId, started.Id, _crew);
        return (started.Id, outcome);
    }

    private static bool IsReminder(WorklistTask t) => t.Title.StartsWith(RiskAssessmentService.ReminderTitle);

    private async Task<List<WorklistTask>> TasksAsync(WorklistService worklist, Guid id) => await worklist.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.RiskAssessment, id);

    /// <summary>One line per assessment version: its code, status, whether it is replaced, review date, size, and the tasks hanging off it.</summary>
    private async Task<string> StoryAsync(RiskAssessmentService risk, WorklistService worklist)
    {
        var lines = new List<string>();
        foreach (var row in (await risk.ListAsync(_s.CompanyId)).OrderBy(r => r.Assessment.Code).ThenBy(r => r.Assessment.Version))
        {
            var detail = (await risk.GetAsync(_s.CompanyId, row.Assessment.Id))!;
            var tasks = (await worklist.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.RiskAssessment, row.Assessment.Id))
                .OrderBy(t => t.Title).Select(t => $"{t.Title}={t.Status}@{t.TargetDate:yyyy-MM-dd}");
            lines.Add($"{row.Assessment.Code} v{row.Assessment.Version} {row.Status} superseded={row.Superseded} review={row.NextReview:yyyy-MM-dd} " +
                      $"hazards={detail.Assessment.Hazards.Count} signoffs={detail.SignOffs.Count} [{string.Join("; ", tasks)}]");
        }
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task TheVesselWritesAndSubmits_TheOfficeEditsTheLibraryReturnsOneAndSignsAnother_AndBothSidesEndWithTheSameStory()
    {
        // The office sets up a template; it reaches the vessel read-only.
        var hot = (await _library.ListCategoriesAsync(_s.CompanyId)).Single(c => c.Code == "HOT");
        var template = await _library.SaveTemplateAsync(_s.CompanyId, null, "Hot work", hot.Id, "Hot work outside the workshop", "Stop work, raise the alarm");
        var fire = new RaTemplateHazardInput(null, "Fire", "Burns, damage", "Fire watch", "", 5, 5, 2, 2);                // 25 before; the vessel leaves 16 (High) or 4 (Low) after controls
        await _library.SaveTemplateContentAsync(_s.CompanyId, template.Id, [new RaTemplateActivityInput(null, "Welding on deck", [fire])]);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(1, Assert.Single(await _vesselLibrary.ListTemplatesAsync(_s.CompanyId)).Hazards);

        // The vessel writes two assessments from it and submits both: one with a control action (a serious risk left), one without.
        var (a, aOutcome) = await WriteAndSubmitAsync(template.Id, hot.Id, "Hot work on the main deck", after: 4, additional: "Fire blanket and a second extinguisher", owner: "Chief Officer");
        var (b, bOutcome) = await WriteAndSubmitAsync(template.Id, hot.Id, "Hot work in the paint locker", after: 2);
        var control = Assert.Single(aOutcome.TasksRaised);
        Assert.Equal((WorklistPriority.Urgent, "Chief Officer"), (control.Priority, control.AssigneeName));       // 16 left: High, so urgent
        Assert.Empty(bOutcome.TasksRaised);
        await _s.SettleUntilQuietAsync();

        // Meanwhile the office adds a hazard to the template. The two assessments already made keep the template as it was.
        var held = (await _library.GetTemplateAsync(_s.CompanyId, template.Id))!;
        await _library.SaveTemplateContentAsync(_s.CompanyId, template.Id, [new RaTemplateActivityInput(held.Activities.Single().Id, "Welding on deck",
            [fire with { Id = held.Hazards.Single().Id }, new RaTemplateHazardInput(null, "Sparks", "Ignites cargo residue", "Screens", "", 3, 3, 2, 2)])]);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, Assert.Single(await _vesselLibrary.ListTemplatesAsync(_s.CompanyId)).Hazards);
        foreach (var side in new[] { _shore, _vessel })
            foreach (var id in new[] { a, b })
                Assert.Single((await side.GetAsync(_s.CompanyId, id))!.Assessment.Hazards);
        var later = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "", Today, TemplateId: template.Id), _crew);       // a third, left as a draft
        Assert.Equal(2, (await _vessel.GetAsync(_s.CompanyId, later.Id))!.Assessment.Hazards.Count);                                         // started after the change: it has the new hazard
        await _s.SettleUntilQuietAsync();

        // The office returns the first for a correction and signs the second.
        await _shore.SignOffAsync(_s.CompanyId, a, RaDecision.Returned, "Name the fire watch", _s.User);
        await _shore.SignOffAsync(_s.CompanyId, b, RaDecision.Signed, "Approved", _s.User, "Superintendent");
        await _s.SettleUntilQuietAsync();
        Assert.Equal((RaStatus.Returned, RaStatus.Signed), ((await _vessel.GetAsync(_s.CompanyId, a))!.Status, (await _vessel.GetAsync(_s.CompanyId, b))!.Status));
        Assert.Single((await TasksAsync(_vesselWorklist, b)).Where(IsReminder));                                                             // signing set up the review reminder

        // The vessel corrects and resubmits the first: the control action it already raised is not raised again. The office signs it.
        var corrected = (await _vessel.GetAsync(_s.CompanyId, a))!;
        await _vessel.SaveAsync(_s.CompanyId, a, Fill(corrected.Assessment, hot.Id, "Hot work on the main deck, fire watch posted", 4, "Fire blanket and a second extinguisher", "Chief Officer"), _crew);
        Assert.Empty((await _vessel.SubmitAsync(_s.CompanyId, a, _crew)).TasksRaised);
        await _s.SettleUntilQuietAsync();
        await _shore.SignOffAsync(_s.CompanyId, a, RaDecision.Signed, "", _s.User, "Superintendent");
        await _s.SettleUntilQuietAsync();

        // The second, already signed, gets an interim version onboard; once signed it replaces the first and moves the reminder on.
        var interim = await _vessel.NewVersionAsync(_s.CompanyId, b, RaKind.Interim, _crew);
        Assert.False((await _vessel.GetAsync(_s.CompanyId, b))!.Superseded);                                                                // a draft does not replace anything yet
        await _vessel.SubmitAsync(_s.CompanyId, interim.Id, _crew);
        await _s.SettleUntilQuietAsync();
        await _shore.SignOffAsync(_s.CompanyId, interim.Id, RaDecision.Signed, "", _s.User, "Superintendent");
        await _s.SettleUntilQuietAsync();

        // Both sides now tell the same story: the same versions, statuses, review dates, sign-offs and tasks.
        var atOffice = await StoryAsync(_shore, _shoreWorklist);
        Assert.Equal(atOffice, await StoryAsync(_vessel, _vesselWorklist));
        Assert.Equal(4, atOffice.Split('\n').Length);                                                                                       // a, b v1, b v2 and the draft

        foreach (var side in new[] { _shore, _vessel })
        {
            var rows = (await side.ListAsync(_s.CompanyId)).ToDictionary(r => r.Assessment.Id);
            Assert.Equal((RaStatus.Signed, false), (rows[a].Status, rows[a].Superseded));
            Assert.Equal((RaStatus.Signed, true), (rows[b].Status, rows[b].Superseded));                                                    // replaced by the interim version
            Assert.Equal((RaStatus.Signed, false, 2), (rows[interim.Id].Status, rows[interim.Id].Superseded, rows[interim.Id].Assessment.Version));
            Assert.Equal((RaStatus.Draft, false), (rows[later.Id].Status, rows[later.Id].Superseded));
            Assert.Equal(Today.AddMonths(11), rows[a].NextReview!.Value.Date);
            Assert.Equal(Today.AddMonths(11), rows[interim.Id].NextReview!.Value.Date);
            Assert.Null(rows[later.Id].NextReview);
            Assert.Equal([RaStatus.Signed, RaStatus.Signed], (await side.VersionsAsync(_s.CompanyId, rows[b].Assessment.Code)).Select(v => v.Status));
        }

        // The Worklist: one control action in all (never twice), a reminder for each version signed, the replaced one's cancelled.
        foreach (var worklist in new[] { _shoreWorklist, _vesselWorklist })
        {
            var onA = await TasksAsync(worklist, a);
            Assert.Single(onA.Where(t => !IsReminder(t)));
            Assert.Single(onA.Where(IsReminder), t => t.Status != WorklistStatus.Cancelled);
            Assert.Equal(WorklistStatus.Cancelled, Assert.Single((await TasksAsync(worklist, b)).Where(IsReminder)).Status);
            Assert.Single((await TasksAsync(worklist, interim.Id)).Where(IsReminder), t => t.Status != WorklistStatus.Cancelled);
            Assert.Empty(await TasksAsync(worklist, later.Id));
        }

        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }
}
