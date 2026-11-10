using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The vessel's side of a project: asking for another job, the office's answer, and what the vessel sees of the numbers.</summary>
public class ProjectVesselTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!, _vessel = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _vessel = new ProjectService(_s.VesselFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Project> RunningProjectAsync(string title = "Drydock 2027")
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, title, PlannedStart: Today.AddDays(-3)), _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast and repaint the underwater hull")], _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today.AddDays(-2), "", _s.User);
        await _s.SettleUntilQuietAsync();
        return project;
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
    public async Task TheVesselAsksForAnotherJob_TheOfficeAddsItOrDeclinesIt_AndBothSidesAgree()
    {
        var project = await RunningProjectAsync();

        var blank = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddItemRequestAsync(_s.CompanyId, project.Id, " ", "reason", _s.User));
        Assert.Contains("Describe the job", blank.Message);
        var grating = await _vessel.AddItemRequestAsync(_s.CompanyId, project.Id, "Renew the sea chest grating", "Found wasted on the port side once the hull was blasted", _s.User);
        await _s.SettleUntilQuietAsync();

        // The office sees it waiting, and so do both dashboards' counts.
        var waiting = Assert.Single(await _shore.RequestsAsync(_s.CompanyId, project.Id));
        Assert.Equal((grating.Id, null), (waiting.Request.Id, waiting.Decision));
        Assert.Equal(1, (await _shore.CountsAsync(_s.CompanyId))!.RequestsWaiting);
        Assert.Equal(1, (await _vessel.CountsAsync(_s.CompanyId))!.RequestsWaiting);

        // Only a project manager answers, and only the office.
        var clerk = await ClerkAsync();
        Assert.Contains("project manager", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideRequestAsync(_s.CompanyId, grating.Id, true, "", "", clerk))).Message);
        Assert.Contains("office", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DecideRequestAsync(_s.CompanyId, grating.Id, true, "", "", _s.User))).Message);
        Assert.Contains("Say why", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideRequestAsync(_s.CompanyId, grating.Id, false, "", " ", _s.User))).Message);

        // Accepting adds it to the specification, and says which item it became.
        var accepted = await _shore.DecideRequestAsync(_s.CompanyId, grating.Id, true, "Hull", "Agreed, quote to follow", _s.User);
        var items = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        var added = items.Single(i => i.SourceId == grating.Id);
        Assert.Equal((2, accepted.ItemId, ProjectItemSource.VesselRequest, "Hull", "Renew the sea chest grating", ProjectItemStatus.Open), (items.Count, added.Id, added.Source, added.Group, added.Description, added.Status));
        Assert.Contains("already been answered", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideRequestAsync(_s.CompanyId, grating.Id, false, "", "No", _s.User))).Message);

        // A second request is declined, with a reason, and adds nothing.
        var paint = await _vessel.AddItemRequestAsync(_s.CompanyId, project.Id, "Repaint the funnel", "", _s.User);
        await _s.SettleUntilQuietAsync();
        var declined = await _shore.DecideRequestAsync(_s.CompanyId, paint.Id, false, "", "Not in this docking; next year", _s.User);
        Assert.Null(declined.ItemId);
        await _s.SettleUntilQuietAsync();

        // The vessel ends with the same specification and the same answers, and the counts have moved.
        foreach (var side in new[] { _shore, _vessel })
        {
            var detail = (await side.GetAsync(_s.CompanyId, project.Id))!;
            Assert.Equal(["Blast and repaint the underwater hull", "Renew the sea chest grating"], detail.Project.Items.Select(i => i.Description));
            var views = await side.RequestsAsync(_s.CompanyId, project.Id);
            Assert.Equal([grating.Id, paint.Id], views.Select(v => v.Request.Id));
            Assert.Equal([true, false], views.Select(v => v.Decision!.Accepted));
            Assert.Equal(["Agreed, quote to follow", "Not in this docking; next year"], views.Select(v => v.Decision!.Note));
            Assert.Equal(0, (await side.CountsAsync(_s.CompanyId))!.RequestsWaiting);
        }

        // Requests are history: nobody can rewrite one. A closed project takes no more.
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            var request = await db.ProjectItemRequests.FirstAsync(r => r.Id == grating.Id);
            request.Description = "Rewritten";
            Assert.Contains("history", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        var everything = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, everything.Select(i => new ProjectItemInput(i.Id, i.Group, i.Description, i.Source, i.SourceId, i.SourceRef, ProjectItemStatus.Done)).ToList(), _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Closed, Today, "Reconciled", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Contains("closed and locked", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddItemRequestAsync(_s.CompanyId, project.Id, "Too late", "", _s.User))).Message);

        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AVesselReadsItsProjectsAndNotes_ButNeverBudgetOrCost_AndAWaitingRequestShowsOnTheOfficeAttention()
    {
        var project = await RunningProjectAsync();
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 50_000m)], _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, project.Id, Today, "Surveyor", "", 9_000m, _s.User);
        await _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "Hull blasting started on the port side", 15), _s.User);
        await _vessel.AddItemRequestAsync(_s.CompanyId, project.Id, "Renew the sea chest grating", "", _s.User);
        await _s.SettleUntilQuietAsync();

        var row = Assert.Single(await _vessel.ListWithMoneyAsync(_s.CompanyId));
        Assert.Equal((ProjectStatus.Ongoing, 0m, 0m, 0m, false), (row.Row.Status, row.Budget, row.Contingency, row.Cost.ToDate, row.OverBudget));
        Assert.Empty(await _vessel.BudgetAsync(_s.CompanyId, project.Id));
        Assert.Empty(await _vessel.CostEntriesAsync(_s.CompanyId, project.Id));
        Assert.Equal("Hull blasting started on the port side", Assert.Single((await _vessel.GetAsync(_s.CompanyId, project.Id))!.Notes).Text);       // what the vessel wrote reaches both sides

        // The office card counts the request, which also feeds the attention strip.
        Assert.Equal(1, (await _shore.CountsAsync(_s.CompanyId))!.RequestsWaiting);
        var strip = DashboardModuleService.Attention([new ModuleSummary("projects", "Projects", "/projects", [new("Vessel requests to answer", "1", "/projects", "warning", null, 1, "vessel requests to answer")])]);
        Assert.Equal(["vessel requests to answer"], strip.Select(i => i.Text));
    }
}
