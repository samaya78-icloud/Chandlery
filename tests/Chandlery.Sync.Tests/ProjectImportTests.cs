using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class ProjectImportRulesTests
{
    [Fact]
    public void TheDrydockNatureIsTheOneChosen_ElseTheOneWhoseNameSaysSo_ElseNone()
    {
        var deferred = (Guid.CreateVersion7(), "Deferred");
        var yard = (Guid.CreateVersion7(), "Deferred to Dry-Dock");
        var planned = (Guid.CreateVersion7(), "Planned");

        Assert.Null(ProjectImportRules.ResolveNature(null, [planned, deferred]));                              // nothing says drydock: the company must choose
        Assert.Equal(yard.Item1, ProjectImportRules.ResolveNature(null, [planned, deferred, yard]));            // "Dry-Dock", "drydock" and "dry dock" all count
        Assert.Equal(deferred.Item1, ProjectImportRules.ResolveNature(deferred.Item1, [planned, deferred, yard]));   // a choice wins over the name
        Assert.Equal(yard.Item1, ProjectImportRules.ResolveNature(Guid.NewGuid(), [planned, deferred, yard]));       // a choice that is no longer in use is ignored
        Assert.Null(ProjectImportRules.ResolveNature(null, []));
    }

    [Fact]
    public void AJobIsOfferedWhenItIsDueWithinTheWindow_OrOverdue_ButNeverWithoutADueDate()
    {
        var today = new DateTime(2026, 11, 10);
        Assert.True(ProjectImportRules.JobIsDue(today.AddDays(-30), today, 90));       // overdue
        Assert.True(ProjectImportRules.JobIsDue(today, today, 0));
        Assert.True(ProjectImportRules.JobIsDue(today.AddDays(90), today, 90));        // the last day of the window
        Assert.False(ProjectImportRules.JobIsDue(today.AddDays(91), today, 90));
        Assert.False(ProjectImportRules.JobIsDue(null, today, 3650));
    }
}

/// <summary>Adding Worklist tasks and maintenance jobs to a project's specification, and closing the task when its item is done.</summary>
public class ProjectImportTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _projects = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private PmsService _pms = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _projects = new ProjectService(_s.ShoreFactory, _shoreWorklist);
        _pms = new PmsService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApprovalRules.RemoveRange(db.ApprovalRules);      // the fixture seeds approval rules; the plain approval step needs none to apply
        foreach (var module in new[] { PolicyModules.Projects, PolicyModules.Worklist, PolicyModules.Pms })
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Guid> NatureAsync(string name) => (await _shoreWorklist.GetListsAsync(_s.CompanyId)).Single(i => i.Kind == WorklistListKind.Nature && i.Name == name).Id;

    private Task<WorklistTask> TaskAsync(string title, Guid? nature) => _shoreWorklist.CreateTaskAsync(_s.CompanyId, new NewWorklistTask(_s.VesselId, title, NatureId: nature), _s.User);

    private async Task<PlannedJob> JobAsync(string code, int months, int startedDaysAgo, bool critical = false)
    {
        var machinery = (await _pms.ListMachineryAsync(_s.CompanyId, _s.VesselId)).FirstOrDefault() ?? await _pms.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true), _s.User);
        return await _pms.AddJobAsync(_s.CompanyId, new NewPlannedJob(machinery.Id, Code: code, Title: $"Overhaul {code}", CalendarInterval: months, IsCritical: critical, StartDate: Today.AddDays(-startedDaysAgo)), _s.User);
    }

    private async Task<Actor> ClerkAsync()
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"clerk{Guid.NewGuid():N}@test.example.com", PasswordHash = "x", Role = UserRole.CompanyUser, DisplayName = "Clerk", CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, "Clerk");
    }

    [Fact]
    public async Task TheCompanyNamesTheDrydockNature_ThenOpenTasksAndDueJobsAreOffered_AndAddingThemTwiceAddsNothing()
    {
        var project = await _projects.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027"), _s.User);

        // The standard natures say nothing about drydock, so nothing is offered until the company chooses one.
        Assert.Null(await _projects.DrydockNatureAsync(_s.CompanyId));
        var deferred = await NatureAsync("Deferred");
        var planned = await NatureAsync("Planned");
        var open = await TaskAsync("Renew the ballast tank coating", deferred);
        var urgent = await _shoreWorklist.CreateTaskAsync(_s.CompanyId, new NewWorklistTask(_s.VesselId, "Repair the rudder stock seal", NatureId: deferred, Priority: WorklistPriority.Urgent), _s.User);
        var finished = await TaskAsync("Already done at sea", deferred);
        await _shoreWorklist.AddEntryAsync(_s.CompanyId, finished.Id, new WorklistEntryInput(WorklistEntryType.Completed, "Done"), _s.User);
        var other = await TaskAsync("A planned job", planned);
        var soon = await JobAsync("J-SOON", months: 1, startedDaysAgo: 40);                       // due about now
        var far = await JobAsync("J-FAR", months: 24, startedDaysAgo: 0);                         // due in two years

        var none = await _projects.ImportCandidatesAsync(_s.CompanyId, project.Id);
        Assert.Null(none.NatureName);
        Assert.Empty(none.Tasks);
        Assert.Equal(["J-SOON"], none.Jobs.Select(j => j.Code));                                  // jobs do not depend on the nature

        // Only a company admin chooses it.
        var clerk = await ClerkAsync();
        Assert.Contains("company admin", (await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.SetDrydockNatureAsync(_s.CompanyId, deferred, clerk))).Message);
        await _projects.SetDrydockNatureAsync(_s.CompanyId, deferred, _s.User);
        Assert.Equal("Deferred", (await _projects.DrydockNatureAsync(_s.CompanyId))!.Value.Name);

        var offered = await _projects.ImportCandidatesAsync(_s.CompanyId, project.Id);
        Assert.Equal(new[] { "Repair the rudder stock seal", "Renew the ballast tank coating" }.Order(), offered.Tasks.Select(t => t.Title).Order());   // open tasks of the nature only
        Assert.True(offered.Tasks.Single(t => t.Id == urgent.Id).Urgent);
        Assert.DoesNotContain(offered.Tasks, t => t.Id == finished.Id || t.Id == other.Id);
        Assert.Equal(["J-SOON", "J-FAR"], (await _projects.ImportCandidatesAsync(_s.CompanyId, project.Id, dueWithinDays: 800)).Jobs.Select(j => j.Code));

        // Not just anyone: importing is a project manager's act.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.ImportAsync(_s.CompanyId, project.Id, [open.Id], [], clerk));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _projects.ImportAsync(_s.CompanyId, project.Id, [], [], _s.User));

        var outcome = await _projects.ImportAsync(_s.CompanyId, project.Id, [open.Id, urgent.Id], [soon.Id], _s.User);
        Assert.Equal((3, 0), (outcome.Added, outcome.Notes.Count));
        var items = (await _projects.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        Assert.Equal(3, items.Count);
        var task = items.Single(i => i.SourceId == open.Id);
        Assert.Equal((ProjectItemSource.WorklistTask, open.Code, "Renew the ballast tank coating", ProjectItemStatus.Open), (task.Source, task.SourceRef, task.Description, task.Status));
        var job = items.Single(i => i.SourceId == soon.Id);
        Assert.Equal((ProjectItemSource.PmsJob, "J-SOON", "Main engine"), (job.Source, job.SourceRef, job.Group));            // grouped under its machinery

        // The tasks know they are on a project, and the same ones are no longer offered.
        var entries = (await _shoreWorklist.GetAsync(_s.CompanyId, open.Id))!.Entries;
        Assert.Contains(entries, e => e.Type == WorklistEntryType.Link && e.Payload.Contains(project.Id.ToString()) && e.Payload.Contains("project"));
        var after = await _projects.ImportCandidatesAsync(_s.CompanyId, project.Id, dueWithinDays: 800);
        Assert.Empty(after.Tasks);
        Assert.Equal(["J-FAR"], after.Jobs.Select(j => j.Code));

        // Adding them again (a second click, another page) adds nothing, and says so.
        var again = await _projects.ImportAsync(_s.CompanyId, project.Id, [open.Id], [soon.Id], _s.User);
        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.Notes.Count(n => n.Contains("already on the specification")));
        Assert.Equal(3, (await _projects.GetAsync(_s.CompanyId, project.Id))!.Project.Items.Count);
        Assert.Contains("could not be found", (await _projects.ImportAsync(_s.CompanyId, project.Id, [Guid.NewGuid()], [], _s.User)).Notes.Single());

        // With no choice made, the nature whose name says drydock is used.
        await _shoreWorklist.AddListItemAsync(_s.CompanyId, WorklistListKind.Nature, "Deferred to drydock");
        await _projects.SetDrydockNatureAsync(_s.CompanyId, null, _s.User);
        Assert.Equal("Deferred to drydock", (await _projects.DrydockNatureAsync(_s.CompanyId))!.Value.Name);

        // The link reaches the vessel's copy of the task.
        await _s.SettleUntilQuietAsync();
        Assert.Contains((await _vesselWorklist.GetAsync(_s.CompanyId, open.Id))!.Entries, e => e.Type == WorklistEntryType.Link && e.Payload.Contains(project.Id.ToString()));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task MarkingAnItemDoneClosesItsTaskOnce_AndAMaintenanceJobWaitsForMaintenanceToRecordIt()
    {
        var project = await _projects.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027", PlannedStart: Today.AddDays(-5)), _s.User);
        var task = await TaskAsync("Renew the ballast tank coating", await NatureAsync("Deferred"));
        var job = await JobAsync("J-SOON", months: 1, startedDaysAgo: 40);
        await _projects.ImportAsync(_s.CompanyId, project.Id, [task.Id], [job.Id], _s.User);

        List<ProjectItemInput> Inputs(ProjectItemStatus status) => (_projects.GetAsync(_s.CompanyId, project.Id).Result!).Project.Items
            .Select(i => new ProjectItemInput(i.Id, i.Group, i.Description, i.Source, i.SourceId, i.SourceRef, status)).ToList();

        var notes = await _projects.SaveItemsAsync(_s.CompanyId, project.Id, Inputs(ProjectItemStatus.Done), _s.User);
        Assert.Equal($"Worklist task {task.Code} is completed.", Assert.Single(notes));
        var closed = (await _shoreWorklist.GetAsync(_s.CompanyId, task.Id))!;
        Assert.Equal(WorklistStatus.Completed, closed.Task.Status);
        Assert.Single(closed.Entries, e => e.Type == WorklistEntryType.Completed && e.Text.Contains(project.Code));

        // Saving it Done again, or reopening and marking it Done again, never completes the task a second time.
        Assert.Empty(await _projects.SaveItemsAsync(_s.CompanyId, project.Id, Inputs(ProjectItemStatus.Done), _s.User));
        await _projects.SaveItemsAsync(_s.CompanyId, project.Id, Inputs(ProjectItemStatus.Open), _s.User);
        Assert.Empty(await _projects.SaveItemsAsync(_s.CompanyId, project.Id, Inputs(ProjectItemStatus.Done), _s.User));
        Assert.Single((await _shoreWorklist.GetAsync(_s.CompanyId, task.Id))!.Entries, e => e.Type == WorklistEntryType.Completed);

        // A job that is Done here waits for maintenance to record it; recording it there (not here) clears the reminder.
        var items = (await _projects.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        var jobItem = items.Single(i => i.SourceId == job.Id);
        Assert.Equal([jobItem.Id], await _projects.PmsWaitingAsync(_s.CompanyId, project.Id));
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.JobCompletions.Add(new JobCompletion { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, PlannedJobId = job.Id, DoneDate = Today, AuthorName = "Chief Engineer", At = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Empty(await _projects.PmsWaitingAsync(_s.CompanyId, project.Id));

        // A task that needs the office to verify it is completed, and the note says it still waits for that.
        var sourceId = (await _shoreWorklist.GetListsAsync(_s.CompanyId)).First(i => i.Kind == WorklistListKind.Source && i.RequiresVerification).Id;
        var strict = await _shoreWorklist.CreateTaskAsync(_s.CompanyId, new NewWorklistTask(_s.VesselId, "Class item: repair the hatch cover", SourceId: sourceId, NatureId: await NatureAsync("Deferred")), _s.User);
        await _projects.ImportAsync(_s.CompanyId, project.Id, [strict.Id], [], _s.User);
        var strictInputs = Inputs(ProjectItemStatus.Done);
        var verifyNote = Assert.Single(await _projects.SaveItemsAsync(_s.CompanyId, project.Id, strictInputs, _s.User));
        Assert.Contains("still has to verify", verifyNote);
        Assert.True((await _shoreWorklist.GetAsync(_s.CompanyId, strict.Id))!.Task.AwaitingVerification);
    }
}
