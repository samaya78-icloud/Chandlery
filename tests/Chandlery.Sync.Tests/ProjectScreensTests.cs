using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Who may plan and run projects, and the figures the project list and the dashboard card show.</summary>
public class ProjectScreensTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync()
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApprovalRules.RemoveRange(db.ApprovalRules);      // the fixture seeds approval rules; the plain approval step needs none to apply
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
        await db.SaveChangesAsync();
    }

    private async Task<Actor> AddUserAsync(string name, UserRole role = UserRole.CompanyUser, bool enabled = true)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name.ToLowerInvariant().Replace(' ', '.')}@test.example.com", PasswordHash = "x", Role = role, DisplayName = name, CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow, IsEnabled = enabled };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, name);
    }

    [Fact]
    public async Task OnlyACompanyAdminOrANamedProjectManagerCanPlanAndRunProjects_EveryoneElseCanReadAndAddNotes()
    {
        await SwitchOnAsync();
        var clerk = await AddUserAsync("Office Clerk");
        var manager = await AddUserAsync("Pat Manager");
        var gone = await AddUserAsync("Left Company", enabled: false);

        Assert.True(await _shore.MayManageAsync(_s.CompanyId, _s.User.UserId));                                         // a company admin always may
        Assert.False(await _shore.MayManageAsync(_s.CompanyId, clerk.UserId));
        Assert.False(await _shore.MayManageAsync(_s.CompanyId, null));

        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027"), _s.User);
        foreach (var attempt in new Func<Task>[]
        {
            () => _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "By the clerk"), clerk),
            () => _shore.UpdateAsync(_s.CompanyId, project.Id, new ProjectEdit(ProjectType.Drydock, "Renamed", "", null, "", null, null, "", false), clerk),
            () => _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast the hull")], clerk),
            () => _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 100m)], clerk),
            () => _shore.AddCostEntryAsync(_s.CompanyId, project.Id, Today, "Bill", "", 10m, clerk),
            () => _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", clerk),
            () => _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "By nobody"), Actor.System),
        })
            Assert.Contains("project manager", (await Assert.ThrowsAsync<InvalidOperationException>(attempt)).Message);

        // Reading, and adding notes, needs no special right.
        Assert.NotNull(await _shore.GetAsync(_s.CompanyId, project.Id));
        await _shore.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "Checked the yard's berth"), clerk);

        // Naming the clerk a project manager lets them work; taking it away stops them again; a disabled account never may.
        await _shore.SetProjectManagerAsync(_s.CompanyId, clerk.UserId!.Value, true);
        Assert.True(await _shore.MayManageAsync(_s.CompanyId, clerk.UserId));
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast the hull")], clerk);
        await _shore.SetProjectManagerAsync(_s.CompanyId, clerk.UserId!.Value, false);
        Assert.False(await _shore.MayManageAsync(_s.CompanyId, clerk.UserId));
        await _shore.SetProjectManagerAsync(_s.CompanyId, gone.UserId!.Value, true);
        Assert.False(await _shore.MayManageAsync(_s.CompanyId, gone.UserId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SetProjectManagerAsync(_s.CompanyId, Guid.NewGuid(), true));

        // A vessel installation never plans projects, whoever asks.
        Assert.False(await new ProjectService(_s.VesselFactory).MayManageAsync(_s.CompanyId, _s.User.UserId));

        Assert.Contains(manager.Name, (await _shore.OfficeUsersAsync(_s.CompanyId)).Select(u => u.Name));                // for the superintendent choice
        Assert.DoesNotContain(gone.Name, (await _shore.OfficeUsersAsync(_s.CompanyId)).Select(u => u.Name));
    }

    [Fact]
    public async Task TheListAndTheDashboardCardShowBudgetCostDaysAndWhatIsOverBudget_AndTheModuleOffShowsNothing()
    {
        Assert.Null(await _shore.CountsAsync(_s.CompanyId));                                                              // module off: no card
        await SwitchOnAsync();
        Assert.Equal(new ProjectCounts(0, 0, 0), await _shore.CountsAsync(_s.CompanyId));

        var ongoing = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "In the yard", PlannedStart: Today.AddDays(-4), PlannedEnd: Today.AddDays(5)), _s.User);
        await _shore.SaveBudgetAsync(_s.CompanyId, ongoing.Id, [new("Yard", 10_000m, 10m), new("Paint", 2_000m)], _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, ongoing.Id, Today, "Surveyor", "", 3_000m, _s.User);
        await _shore.RecordAsync(_s.CompanyId, ongoing.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, ongoing.Id, ProjectEventKind.Started, Today.AddDays(-2), "", _s.User);

        var over = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.NonDrydock, "Pump replacement"), _s.User);
        await _shore.SaveBudgetAsync(_s.CompanyId, over.Id, [new("Pumps", 1_000m)], _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, over.Id, Today, "Pump set", "", 1_500m, _s.User);

        var noBudget = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.NonDrydock, "Not yet budgeted"), _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, noBudget.Id, Today, "Early cost", "", 500m, _s.User);                // cost with no budget is not "over budget"

        var closed = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.NonDrydock, "Finished long ago"), _s.User);
        await _shore.SaveBudgetAsync(_s.CompanyId, closed.Id, [new("Work", 100m)], _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, closed.Id, Today, "Work", "", 900m, _s.User);
        foreach (var kind in new[] { ProjectEventKind.Approved, ProjectEventKind.Started, ProjectEventKind.Completed, ProjectEventKind.Closed })
            await _shore.RecordAsync(_s.CompanyId, closed.Id, kind, Today, "Reconciled", _s.User);

        var rows = (await _shore.ListWithMoneyAsync(_s.CompanyId)).ToDictionary(r => r.Row.Project.Title);
        var yard = rows["In the yard"];
        Assert.Equal((12_000m, 1_000m, 3_000m, ProjectStatus.Ongoing), (yard.Budget, yard.Contingency, yard.Cost.ToDate, yard.Row.Status));
        Assert.Equal((9_000m, false), (yard.Variance, yard.OverBudget));
        Assert.False(string.IsNullOrEmpty(yard.VesselName));
        Assert.Equal((10, 3), (yard.PlannedDays, yard.ActualDays));                                                        // planned 10 days; started two days ago, so day 3 today
        Assert.True(rows["Pump replacement"].OverBudget);
        Assert.Equal(-500m, rows["Pump replacement"].Variance);
        Assert.False(rows["Not yet budgeted"].OverBudget);
        Assert.Null(rows["Not yet budgeted"].PlannedDays);

        // The card counts live projects only: the closed one is over its budget but is history.
        Assert.True(rows["Finished long ago"].OverBudget);
        Assert.Equal(new ProjectCounts(Ongoing: 1, NotStarted: 2, OverBudget: 1), await _shore.CountsAsync(_s.CompanyId));

        // Onboard the same list has no money in it: the vessel holds none, and reads none.
        await _s.SettleUntilQuietAsync();
        var onboard = await new ProjectService(_s.VesselFactory).ListWithMoneyAsync(_s.CompanyId);
        Assert.Equal(4, onboard.Count);
        Assert.All(onboard, r => Assert.Equal((0m, 0m, 0m, false), (r.Budget, r.Contingency, r.Cost.ToDate, r.OverBudget)));
        Assert.Equal(new ProjectCounts(Ongoing: 1, NotStarted: 2, OverBudget: 0), await new ProjectService(_s.VesselFactory).CountsAsync(_s.CompanyId));
    }
}
