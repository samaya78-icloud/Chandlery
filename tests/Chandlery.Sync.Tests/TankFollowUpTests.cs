using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>What submitting a tank inspection sets in motion: worklist tasks for exceptions and the completion of a linked maintenance job.</summary>
public class TankFollowUpTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private TankService _shoreTanks = null!;
    private TankInspectionService _shore = null!, _vessel = null!;
    private PmsService _shorePms = null!, _vesselPms = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreTanks = new TankService(_s.ShoreFactory);
        _shorePms = new PmsService(_s.ShoreFactory);
        _vesselPms = new PmsService(_s.VesselFactory);
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shore = new TankInspectionService(_s.ShoreFactory, _shoreWorklist, _shorePms);
        _vessel = new TankInspectionService(_s.VesselFactory, _vesselWorklist, _vesselPms);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(params string[] modules)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in modules) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<Tank> BallastTankAsync(string code = "2P")
    {
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput(code, "No. 2 port ballast", TankType.Ballast, 10, 5, 12, 8), _s.User);
        await _s.SettleUntilQuietAsync();
        return tank;
    }

    private async Task<Guid> BallastTemplateAsync() => (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).Single(t => t.Template.TankType == TankType.Ballast).Template.Id;

    private async Task AnswerAsync(TankInspectionService service, Guid inspectionId, params (string Criterion, string Value)[] except)
    {
        var detail = (await service.GetAsync(_s.CompanyId, inspectionId))!;
        var answers = detail.Inspection.Results.Select(r => new InspectionAnswer(r.Id,
            except.Any(e => e.Criterion == r.CriterionName) ? except.First(e => e.Criterion == r.CriterionName).Value : r.Scale == ScaleKind.PercentBucket ? "76-100" : "Good",
            except.Any(e => e.Criterion == r.CriterionName) ? "Found at inspection" : "")).ToList();
        await service.SaveAsync(_s.CompanyId, inspectionId, new InspectionEdit(detail.Inspection.InspectedOn, "At sea", "Internal", "", "Chief Officer", answers), _s.User);
    }

    // ---- exceptions raise worklist tasks ----

    [Fact]
    public async Task SubmittingRaisesOneWorklistTaskPerException_ABadRatingUrgent_AndAResubmissionRaisesNothingTwice()
    {
        await SwitchOnAsync(PolicyModules.TankInspection, PolicyModules.Worklist);
        var tank = await BallastTankAsync();
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, await BallastTemplateAsync(), Today, "At sea"), _s.User);

        await AnswerAsync(_vessel, started.Id, ("Condition of coating", "Fair"), ("Cracks", "Bad"));
        var first = await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        Assert.Equal(2, first.TasksRaised.Count);
        Assert.Empty(first.Notes);
        Assert.False(first.JobCompleted);

        var tasks = await _vesselWorklist.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.TankInspection, started.Id);
        Assert.Equal(2, tasks.Count);
        Assert.Equal([WorklistPriority.Normal, WorklistPriority.Urgent], tasks.Select(t => t.Priority).Order());
        Assert.Contains(tasks, t => t.Title.Contains("Cracks") && t.Priority == WorklistPriority.Urgent);
        Assert.Equal(2, (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Inspection.Results.Count(r => r.WorklistTaskId is not null));

        // The office returns it; the vessel corrects the crack, finds a new fault and submits again.
        await _s.SettleUntilQuietAsync();
        await _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Returned, "Check the pitting again", _s.User);
        await _s.SettleUntilQuietAsync();
        await AnswerAsync(_vessel, started.Id, ("Condition of coating", "Fair"), ("Pitting", "Fair"));
        var second = await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        Assert.Single(second.TasksRaised);                                                                                  // only the new fault; the coating already has its task
        Assert.Contains("Pitting", second.TasksRaised[0].Title);
        Assert.Equal(3, (await _vesselWorklist.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.TankInspection, started.Id)).Count);

        // The tasks and the inspection reach the office, and the task opens the inspection.
        await _s.SettleUntilQuietAsync();
        Assert.Equal(3, (await _shoreWorklist.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.TankInspection, started.Id)).Count);
        Assert.Equal($"/tanks/inspections/{started.Id}", WorklistLinkTypes.Href(WorklistLinkTypes.TankInspection, started.Id, onVessel: false));
    }

    [Fact]
    public async Task WithoutTheWorklist_TheInspectionStillSubmits_AndSaysNoTasksWereRaised()
    {
        await SwitchOnAsync(PolicyModules.TankInspection);
        var tank = await BallastTankAsync();
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, await BallastTemplateAsync(), Today, "At sea"), _s.User);
        await AnswerAsync(_vessel, started.Id, ("Cracks", "Bad"));
        var outcome = await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        Assert.Empty(outcome.TasksRaised);
        Assert.Contains("worklist is off", outcome.Notes.Single());
        Assert.Equal(InspectionStatus.Submitted, (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Status);
    }

    // ---- the maintenance job ----

    private async Task<PlannedJob> InspectionJobAsync(Tank tank, string code = "BT-1")
    {
        var item = await _shorePms.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Ballast system"), _s.User);
        var job = await _shorePms.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: code, Title: "Inspect ballast tank", Type: PmsJobType.Inspection, CalendarInterval: 6, StartDate: Today.AddDays(-40)), _s.User);
        await _shorePms.SetJobTankAsync(_s.CompanyId, job.Id, tank.Id);
        await _s.SettleUntilQuietAsync();
        return job;
    }

    [Fact]
    public async Task ATankJobIsCompletedBySubmittingTheInspection_AndCannotBeCompletedAnyOtherWay()
    {
        await SwitchOnAsync(PolicyModules.TankInspection, PolicyModules.Pms);
        var tank = await BallastTankAsync();
        var job = await InspectionJobAsync(tank);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselPms.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Today), _s.User));
        Assert.Contains("inspecting the tank", refused.Message);
        Assert.Empty((await _vesselPms.GetJobAsync(_s.CompanyId, job.Id))!.Completions);

        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, await BallastTemplateAsync(), Today.AddDays(-1), "At sea", PlannedJobId: job.Id), _s.User);
        await AnswerAsync(_vessel, started.Id);
        var outcome = await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        Assert.True(outcome.JobCompleted);

        var after = (await _vesselPms.GetJobAsync(_s.CompanyId, job.Id))!;
        var completion = Assert.Single(after.Completions);
        Assert.Contains(started.Code, completion.Comments);
        Assert.Equal(Today.AddDays(-1), after.Job.LastDoneDate);                                                          // the schedule counts from the inspection date
        Assert.True(after.Job.NextDueDate > Today);

        // Returned and submitted again: the job is not completed a second time.
        await _s.SettleUntilQuietAsync();
        await _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Returned, "Add the work done", _s.User);
        await _s.SettleUntilQuietAsync();
        await AnswerAsync(_vessel, started.Id);
        Assert.False((await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User)).JobCompleted);
        Assert.Single((await _vesselPms.GetJobAsync(_s.CompanyId, job.Id))!.Completions);

        // The completion reaches the office.
        await _s.SettleUntilQuietAsync();
        Assert.Single((await _shorePms.GetJobAsync(_s.CompanyId, job.Id))!.Completions);
    }

    [Fact]
    public async Task AJobCanOnlyNameATankOfItsOwnVessel_AndOnlyAnInspectionJobCanNameOne()
    {
        await SwitchOnAsync(PolicyModules.TankInspection, PolicyModules.Pms);
        var tank = await BallastTankAsync();
        var item = await _shorePms.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Pump"), _s.User);
        var check = await _shorePms.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "P-1", Title: "Greasing", Type: PmsJobType.Check, CalendarInterval: 1, StartDate: Today), _s.User);
        var notInspection = await Assert.ThrowsAsync<InvalidOperationException>(() => _shorePms.SetJobTankAsync(_s.CompanyId, check.Id, tank.Id));
        Assert.Contains("inspection job", notInspection.Message);

        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, Module = PolicyModules.TankInspection, Enabled = true });
            await db.SaveChangesAsync();
        }
        var otherTank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.OtherVesselId, new TankInput("1P", "Their tank", TankType.Ballast, 10, 5, 12, 8), _s.User);
        var job = await _shorePms.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "P-2", Title: "Inspect", Type: PmsJobType.Inspection, CalendarInterval: 6, StartDate: Today), _s.User);
        var elsewhere = await Assert.ThrowsAsync<InvalidOperationException>(() => _shorePms.SetJobTankAsync(_s.CompanyId, job.Id, otherTank.Id));
        Assert.Contains("another vessel", elsewhere.Message);

        // An inspection cannot claim a job that is not for its tank.
        await _shorePms.SetJobTankAsync(_s.CompanyId, job.Id, tank.Id);
        var second = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput("2S", "No. 2 starboard ballast", TankType.Ballast, 30, 5, 12, 8), _s.User);
        await _s.SettleUntilQuietAsync();
        var template = await BallastTemplateAsync();
        var wrongTank = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.StartAsync(_s.CompanyId, new NewInspection(second.Id, template, Today, "At sea", PlannedJobId: job.Id), _s.User));
        Assert.Contains("not an inspection of this tank", wrongTank.Message);

        // Unlinking frees the job for ordinary completion.
        await _shorePms.SetJobTankAsync(_s.CompanyId, job.Id, null);
        await _shorePms.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Today), _s.User);
    }
}
