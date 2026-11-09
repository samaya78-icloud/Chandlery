using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>What an assessment sets in motion in the worklist (control actions, review reminders), and the counts for tiles, on a real office and a real vessel.</summary>
public class RiskFollowUpTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private RiskAssessmentService _shore = null!, _vessel = null!;
    private RiskLibraryService _library = null!;
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
        _crew = await RiskTestSupport.MakeReviewersAsync(_s);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(bool worklist, params Guid[] vessels)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var vessel in vessels.Length == 0 ? [_s.VesselId] : vessels)
            {
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.RiskAssessment, Enabled = true });
                if (worklist) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.Worklist, Enabled = true });
            }
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private static RaHazardInput Control(string hazard, string additional = "Install a second guard", string owner = "Chief Officer", int after = 4, DateTime? due = null, Guid? id = null)
        => new(id, hazard, "", 5, 5, "Permit to work", additional, after, after, "", owner, due ?? Today.AddDays(20));

    private async Task<(Guid Id, RaSubmitOutcome Outcome)> WriteAndSubmitAsync(RiskAssessmentService side, Guid vesselId, Actor writer, params RaHazardInput[] hazards)
    {
        var category = (await _library.ListCategoriesAsync(_s.CompanyId)).First();
        var started = await side.StartAsync(_s.CompanyId, new NewAssessment(vesselId, "Hot work in the workshop", Today, category.Id), writer);
        await side.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Hot work in the workshop", category.Id, Today, "", [new RaTeamInput("Bosun")], [new RaActivityInput(null, "Welding", hazards)]), writer);
        var outcome = await side.SubmitAsync(_s.CompanyId, started.Id, writer);
        await _s.SettleUntilQuietAsync();
        return (started.Id, outcome);
    }

    private async Task<List<WorklistTask>> TasksAsync(WorklistService worklist, Guid assessmentId) => await worklist.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.RiskAssessment, assessmentId);

    [Fact]
    public async Task EachControlActionRaisesOneTask_WhenSubmitted_ASeriousRiskIsUrgent_AndAResubmissionRaisesOnlyTheNewOnes()
    {
        await SwitchOnAsync(worklist: true);
        var (id, outcome) = await WriteAndSubmitAsync(_vessel, _s.VesselId, _crew,
            Control("Fire", "Fire blanket and a second extinguisher"),                                  // 16 left: High, with owner and date: a control action
            Control("Sparks", "Screens around the bench", after: 2),                                    // 4 left: a control action, not urgent
            Control("Fumes", "Extraction fan", owner: "", after: 2),                                           // no owner: not a control action yet
            new RaHazardInput(null, "Noise", "", 3, 3, "Ear defenders", "", 2, 2));                    // no additional control

        Assert.Equal(2, outcome.TasksRaised.Count);
        Assert.Empty(outcome.Notes);
        var onboard = await TasksAsync(_vesselWorklist, id);
        Assert.Equal(2, onboard.Count);
        var fire = onboard.Single(t => t.Title.Contains("fire blanket", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((WorklistPriority.Urgent, "Chief Officer", Today.AddDays(20)), (fire.Priority, fire.AssigneeName, fire.TargetDate!.Value.Date));
        Assert.Equal(WorklistPriority.Normal, onboard.Single(t => t.Id != fire.Id).Priority);
        Assert.Equal(2, (await TasksAsync(_shoreWorklist, id)).Count);                                  // the office sees them too

        // Returned, corrected with one more action, and submitted again: the two already raised are not raised twice.
        await _shore.SignOffAsync(_s.CompanyId, id, RaDecision.Returned, "Add a fire watch", _s.User);
        await _s.SettleUntilQuietAsync();
        var detail = (await _vessel.GetAsync(_s.CompanyId, id))!;
        var existing = detail.Assessment.Hazards.OrderBy(h => h.SortOrder)
            .Select(h => new RaHazardInput(h.Id, h.Hazard, h.Effect, h.ConsequenceBefore, h.ProbabilityBefore, h.ExistingControls, h.AdditionalControls, h.ConsequenceAfter, h.ProbabilityAfter, h.ResidualReason, h.ResponsibleName, h.DueDate, h.Contingency)).ToList();
        existing[2] = existing[2] with { ResponsibleName = "Second Engineer" };                          // the fumes action now has an owner
        existing.Add(Control("Hot slag", "Slag trays"));
        await _vessel.SaveAsync(_s.CompanyId, id, new AssessmentEdit("Hot work in the workshop", detail.Assessment.CategoryId, Today, "", [new RaTeamInput("Bosun")],
            [new RaActivityInput(detail.Assessment.Activities.Single().Id, "Welding", existing)]), _crew);
        var second = await _vessel.SubmitAsync(_s.CompanyId, id, _crew);
        Assert.Equal(2, second.TasksRaised.Count);                                                       // fumes and slag
        await _s.SettleUntilQuietAsync();
        Assert.Equal(4, (await TasksAsync(_vesselWorklist, id)).Count);
        Assert.Equal(4, (await TasksAsync(_shoreWorklist, id)).Count);
        Assert.Equal(4, (await _vessel.GetAsync(_s.CompanyId, id))!.Assessment.Hazards.Count(h => h.WorklistTaskId is not null));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task WithTheWorklistOff_NoTasksAreRaised_TheSubmissionStands_AndTheWriterIsToldWhy()
    {
        await SwitchOnAsync(worklist: false);
        var (id, outcome) = await WriteAndSubmitAsync(_vessel, _s.VesselId, _crew, Control("Fire"));
        Assert.Empty(outcome.TasksRaised);
        Assert.Contains("worklist is off", outcome.Notes.Single());
        var detail = (await _vessel.GetAsync(_s.CompanyId, id))!;
        Assert.Equal(RaStatus.Submitted, detail.Status);
        Assert.All(detail.Assessment.Hazards, h => Assert.Null(h.WorklistTaskId));

        await _shore.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "", _s.User);                    // signing with the worklist off is fine too: no reminder
        await _s.SettleUntilQuietAsync();
        Assert.Empty(await TasksAsync(_shoreWorklist, id));
    }

    [Fact]
    public async Task SigningRaisesAReminderForTheReviewDate_AndTheNextSignedVersionCancelsTheOldOne()
    {
        await SwitchOnAsync(worklist: true);
        var (id, _) = await WriteAndSubmitAsync(_shore, _s.VesselId, _s.User, Control("Fire", after: 2));
        Assert.Empty((await TasksAsync(_shoreWorklist, id)).Where(t => t.Title.StartsWith(RiskAssessmentService.ReminderTitle)));      // nothing until it is signed

        await _shore.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "", _s.User, "Superintendent");
        await _s.SettleUntilQuietAsync();
        var first = (await _shore.GetAsync(_s.CompanyId, id))!;
        var reminder = Assert.Single((await TasksAsync(_vesselWorklist, id)).Where(t => t.Title.StartsWith(RiskAssessmentService.ReminderTitle)));
        Assert.Equal((first.NextReview!.Value.Date, first.NextReview.Value.Date.AddDays(-RaReview.SoonDays)), (reminder.TargetDate!.Value.Date, reminder.PlannedDate!.Value.Date));
        Assert.Contains(first.Assessment.Code, reminder.Title);

        // An annual review, signed, replaces it: the old reminder is cancelled and the new version has its own.
        var annual = await _shore.NewVersionAsync(_s.CompanyId, id, RaKind.Annual, _s.User);
        await _shore.SubmitAsync(_s.CompanyId, annual.Id, _s.User);
        await _shore.SignOffAsync(_s.CompanyId, annual.Id, RaDecision.Signed, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(WorklistStatus.Cancelled, (await TasksAsync(_shoreWorklist, id)).Single(t => t.Title.StartsWith(RiskAssessmentService.ReminderTitle)).Status);
        var current = Assert.Single((await TasksAsync(_vesselWorklist, annual.Id)).Where(t => t.Title.StartsWith(RiskAssessmentService.ReminderTitle)));
        Assert.NotEqual(WorklistStatus.Cancelled, current.Status);
        Assert.Contains("version 2", current.Title);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ASharedCopyGetsItsOwnReminder_RaisedForItsVessel()
    {
        await SwitchOnAsync(worklist: true, _s.VesselId, _s.OtherVesselId);
        var (id, _) = await WriteAndSubmitAsync(_shore, _s.OtherVesselId, _s.User, Control("Fire", after: 2));
        await _shore.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "", _s.User);
        var copy = Assert.Single(await _shore.ShareAsync(_s.CompanyId, id, [_s.VesselId], _s.User));
        await _s.SettleUntilQuietAsync();

        var reminder = Assert.Single(await TasksAsync(_vesselWorklist, copy.Id));                        // raised ashore for the vessel, so the vessel sees it
        Assert.StartsWith(RiskAssessmentService.ReminderTitle, reminder.Title);
        Assert.Equal(_s.VesselId, reminder.VesselId);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TheCountsFollowTheWork_AndAreNullWhenTheModuleIsOff()
    {
        Assert.Null(await _shore.CountsAsync(_s.CompanyId));
        await SwitchOnAsync(worklist: false);
        Assert.Equal(new RiskCounts(0, 0, 0, 0, 0), await _shore.CountsAsync(_s.CompanyId));

        var (waiting, _) = await WriteAndSubmitAsync(_shore, _s.VesselId, _s.User, Control("Fire", after: 2));
        var (serious, _) = await WriteAndSubmitAsync(_shore, _s.VesselId, _s.User, Control("Gas"));      // 16 left: High
        await _shore.SignOffAsync(_s.CompanyId, serious, RaDecision.Signed, "", _s.User);
        var (overdue, _) = await WriteAndSubmitAsync(_shore, _s.VesselId, _s.User, Control("Slips", after: 2));
        await _shore.SignOffAsync(_s.CompanyId, overdue, RaDecision.Signed, "", _s.User);
        var stale = (await _shore.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Forgotten draft", Today), _s.User)).Id;
        await _shore.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Fresh draft", Today), _s.User);

        // Make one signed assessment overdue for review (signed two months ago, reviewed monthly) and one draft old.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            await db.RaSignOffs.Where(x => x.AssessmentId == overdue).ExecuteUpdateAsync(u => u.SetProperty(x => x.At, DateTime.UtcNow.AddMonths(-2)));
            await db.RiskAssessments.Where(x => x.Id == overdue).ExecuteUpdateAsync(u => u.SetProperty(x => x.ReviewPeriodMonths, 1));
            await db.RiskAssessments.Where(x => x.Id == stale).ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-30)));
        }

        var counts = (await _shore.CountsAsync(_s.CompanyId))!;
        Assert.Equal(new RiskCounts(Waiting: 1, DueForReview: 1, Overdue: 1, HighResidual: 1, OldDrafts: 1), counts);
        Assert.NotEqual(Guid.Empty, waiting);

        var rows = await _shore.InForceAsync(_s.CompanyId, _s.VesselId);
        Assert.Equal(2, rows.Count);                                                                      // the two signed ones
        Assert.Empty(await _shore.InForceAsync(_s.CompanyId, _s.OtherVesselId));
    }
}

/// <summary>Putting off a critical maintenance job with a signed risk assessment named on the request.</summary>
public class RiskPmsLinkTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _pmsShore = null!, _pmsVessel = null!;
    private RiskAssessmentService _risk = null!;
    private RiskLibraryService _library = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _pmsShore = new PmsService(_s.ShoreFactory);
        _pmsVessel = new PmsService(_s.VesselFactory);
        _risk = new RiskAssessmentService(_s.ShoreFactory);
        _library = new RiskLibraryService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Pms, Enabled = true });
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RiskAssessment, Enabled = true });
        var user = await db.Users.FirstAsync(u => u.Id == _s.User.UserId);
        user.IsRiskReviewer = true;
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<PlannedJob> JobAsync(bool critical)
    {
        var item = (await _pmsShore.ListMachineryAsync(_s.CompanyId, _s.VesselId)).FirstOrDefault()
            ?? await _pmsShore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true), _s.User);
        var job = await _pmsShore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: critical ? "JC" : "JN", Title: "Overhaul", CalendarInterval: 1, IsCritical: critical, StartDate: Today.AddDays(-40)), _s.User);
        await _s.SettleUntilQuietAsync();
        return job;
    }

    private async Task<Guid> SignedAssessmentAsync(string code = "CRE")
    {
        var category = (await _library.ListCategoriesAsync(_s.CompanyId)).Single(c => c.Code == code);
        var started = await _risk.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Deferring the engine overhaul", Today, category.Id), _s.User);
        await _risk.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Deferring the engine overhaul", category.Id, Today, "", [new RaTeamInput("Chief Engineer")],
            [new RaActivityInput(null, "Running on", [new RaHazardInput(null, "Wear", "", 4, 3, "Oil analysis monthly", "", 2, 2)])]), _s.User);
        await _risk.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _risk.SignOffAsync(_s.CompanyId, started.Id, RaDecision.Signed, "", _s.User);
        await _s.SettleUntilQuietAsync();
        return started.Id;
    }

    private async Task<DateTime> DueAsync(Guid jobId)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        return (await db.PlannedJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).NextDueDate!.Value;
    }

    [Fact]
    public async Task ARequestMayNameASignedAssessment_AndTheLinkIsKept()
    {
        var job = await JobAsync(critical: false);
        var assessment = await SignedAssessmentAsync();
        var due = await DueAsync(job.Id);

        var request = await _pmsVessel.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(7), "Waiting for a part", _s.User, assessment);
        Assert.Equal(assessment, request.RiskAssessmentId);
        Assert.Matches(@"^MVSF-RA-\d{4}-H0001 v1$", request.RiskAssessmentLabel);
        await _s.SettleUntilQuietAsync();
        var seen = await _pmsShore.DeferralsOfJobAsync(_s.CompanyId, job.Id);
        Assert.Equal(assessment, seen.Single().Request.RiskAssessmentId);                                // the office reads the same link

    }

    [Fact]
    public async Task OnlyASignedAssessmentInForceForThisVesselCanBeNamed()
    {
        var job = await JobAsync(critical: false);
        var due = await DueAsync(job.Id);
        var category = (await _library.ListCategoriesAsync(_s.CompanyId)).First();
        var draft = (await _risk.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Not signed", Today, category.Id), _s.User)).Id;
        Assert.Contains("signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _pmsShore.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(7), "Why", _s.User, draft))).Message);
        Assert.Contains("signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _pmsShore.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(7), "Why", _s.User, Guid.NewGuid()))).Message);

        // A replaced version is no longer acceptable; its successor is.
        var first = await SignedAssessmentAsync();
        var annual = await _risk.NewVersionAsync(_s.CompanyId, first, RaKind.Annual, _s.User);
        await _risk.SubmitAsync(_s.CompanyId, annual.Id, _s.User);
        await _risk.SignOffAsync(_s.CompanyId, annual.Id, RaDecision.Signed, "", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _pmsShore.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(7), "Why", _s.User, first));
        Assert.NotNull(await _pmsShore.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(7), "Why", _s.User, annual.Id));
    }

    [Fact]
    public async Task WhenTheCompanyInsists_ACriticalJobNeedsAnAssessment_ButAnOrdinaryJobDoesNot()
    {
        var critical = await JobAsync(critical: true);
        var ordinary = await JobAsync(critical: false);
        var dueCritical = await DueAsync(critical.Id);
        var dueOrdinary = await DueAsync(ordinary.Id);

        // Off by default.
        var allowed = await _pmsShore.RequestDeferralAsync(_s.CompanyId, critical.Id, dueCritical.AddDays(7), "Part delayed", _s.User);
        Assert.Null(allowed.RiskAssessmentId);
        await _pmsShore.DecideDeferralAsync(_s.CompanyId, allowed.Id, false, null, "No", _s.User);

        await _library.SetRequiredForCriticalDeferralAsync(_s.CompanyId, true);
        Assert.True(await _library.GetRequiredForCriticalDeferralAsync(_s.CompanyId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RiskLibraryService(_s.VesselFactory).SetRequiredForCriticalDeferralAsync(_s.CompanyId, false));   // the office's setting
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _pmsShore.RequestDeferralAsync(_s.CompanyId, critical.Id, dueCritical.AddDays(8), "Part delayed", _s.User));
        Assert.Contains("risk assessment", refused.Message);
        Assert.NotNull(await _pmsShore.RequestDeferralAsync(_s.CompanyId, ordinary.Id, dueOrdinary.AddDays(7), "Part delayed", _s.User));   // not critical: no assessment needed

        var assessment = await SignedAssessmentAsync();
        Assert.Equal(assessment, (await _pmsShore.RequestDeferralAsync(_s.CompanyId, critical.Id, dueCritical.AddDays(8), "Part delayed", _s.User, assessment)).RiskAssessmentId);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }
}
