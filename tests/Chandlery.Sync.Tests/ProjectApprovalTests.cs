using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>A project's budget and its variations approved through the company's approval matrix, on a real office and a real vessel.</summary>
public class ProjectApprovalTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!, _vessel = null!;
    private ApprovalService _matrix = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _vessel = new ProjectService(_s.VesselFactory);
        _matrix = new ApprovalService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Actor> PersonAsync(string name, bool manager = false)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name}{Guid.NewGuid():N}@test.example.com", PasswordHash = "x", Role = UserRole.CompanyUser, DisplayName = name, CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow, IsProjectManager = manager };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, name);
    }

    private async Task<Project> PlannedAsync(decimal yard)
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027", PlannedStart: Today.AddDays(-3)), _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast and repaint the underwater hull")], _s.User);
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", yard)], _s.User);
        return project;
    }

    /// <summary>Up to 10,000 one approver (level 1: Ann); above it Ann then Ben.</summary>
    private async Task<(Actor Ann, Actor Ben)> RulesAsync()
    {
        var (ann, ben) = (await PersonAsync("Ann"), await PersonAsync("Ben"));
        await _matrix.AddRuleAsync(_s.CompanyId, null, 10_000m, 1, ann.UserId!.Value);
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ann.UserId!.Value);
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 2, ben.UserId!.Value);
        return (ann, ben);
    }

    private static async Task<int> CountAsync(IDbContextFactory<ChandleryDbContext> factory, Func<ChandleryDbContext, Task<int>> count)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await count(db);
    }

    [Fact]
    public async Task WithNoRulesTheBudgetIsApprovedOnTheSpot_AsItAlwaysWas()
    {
        var project = await PlannedAsync(20_000m);
        Assert.False(await _shore.NeedsMatrixAsync(_s.CompanyId, project.Id));

        var request = await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        Assert.Equal(ProjectRequestStatus.Approved, request.Status);
        Assert.Equal(ProjectStatus.Approved, (await _shore.GetAsync(_s.CompanyId, project.Id))!.Status);
        Assert.Equal(20_000m, Assert.Single(await _shore.ApprovalsAsync(_s.CompanyId, project.Id)).TotalUsd);
        Assert.Empty(Assert.Single(await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).Slots);
    }

    [Fact]
    public async Task TheMatrixApprovesTheBudgetLevelByLevel_ARejectionSendsItBack_AndNothingGetsAroundIt()
    {
        var (ann, ben) = await RulesAsync();
        var project = await PlannedAsync(20_000m);
        Assert.True(await _shore.NeedsMatrixAsync(_s.CompanyId, project.Id));

        // The approval cannot be skipped, and only a manager asks.
        Assert.Contains("approval matrix", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User))).Message);
        var clerk = await PersonAsync("Clerk");
        Assert.Contains("project manager", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SubmitAsync(_s.CompanyId, project.Id, clerk))).Message);

        var request = await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        Assert.Equal((ProjectRequestStatus.Pending, 20_000m), (request.Status, request.AmountUsd));
        Assert.Equal(ProjectStatus.Planning, (await _shore.GetAsync(_s.CompanyId, project.Id))!.Status);
        Assert.Contains("already with the approvers", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User))).Message);
        Assert.Contains("with the approvers", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 1m)], _s.User))).Message);   // held still

        // Ann first; Ben has to wait, and a stranger has no turn at all.
        Assert.Empty(await _shore.PendingForUserAsync(_s.CompanyId, ben.UserId!.Value));
        Assert.Equal(request.Id, Assert.Single(await _shore.PendingForUserAsync(_s.CompanyId, ann.UserId!.Value)).Request.Id);
        Assert.Contains("not your turn", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideApprovalAsync(_s.CompanyId, request.Id, ben, true, ""))).Message);
        Assert.Contains("not your turn", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideApprovalAsync(_s.CompanyId, request.Id, _s.User, true, ""))).Message);
        await _shore.DecideApprovalAsync(_s.CompanyId, request.Id, ann, true, "Looks right");
        Assert.Equal(ProjectStatus.Planning, (await _shore.GetAsync(_s.CompanyId, project.Id))!.Status);
        Assert.Empty(await _shore.PendingForUserAsync(_s.CompanyId, ann.UserId!.Value));
        Assert.Equal(1, (await _shore.PendingForUserAsync(_s.CompanyId, ben.UserId!.Value)).Count);
        Assert.True(Assert.Single(await _shore.RequestsOfAsync(_s.CompanyId, project.Id, ben.UserId)).YourTurn);

        // Ben rejects, with a reason; the budget is free to change and go round again.
        Assert.Contains("reason", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideApprovalAsync(_s.CompanyId, request.Id, ben, false, " "))).Message);
        await _shore.DecideApprovalAsync(_s.CompanyId, request.Id, ben, false, "Yard price too high");
        Assert.Equal(ProjectRequestStatus.Rejected, Assert.Single(await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).Request.Status);
        Assert.Contains("no longer waiting", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideApprovalAsync(_s.CompanyId, request.Id, ann, true, ""))).Message);
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 18_000m)], _s.User);

        var second = await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        await _shore.WithdrawRequestAsync(_s.CompanyId, second.Id, _s.User);                                                      // taken back, then asked again
        var third = await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        await _shore.DecideApprovalAsync(_s.CompanyId, third.Id, ann, true, "");
        await _shore.DecideApprovalAsync(_s.CompanyId, third.Id, ben, true, "Agreed");

        // Approved: the status moved, what was approved is kept, and the whole trail is there.
        var detail = (await _shore.GetAsync(_s.CompanyId, project.Id))!;
        Assert.Equal((ProjectStatus.Approved, "Ben"), (detail.Status, detail.Events.Last().ByName));
        Assert.Equal(18_000m, Assert.Single(await _shore.ApprovalsAsync(_s.CompanyId, project.Id)).TotalUsd);
        Assert.Equal([ProjectRequestStatus.Approved, ProjectRequestStatus.Withdrawn, ProjectRequestStatus.Rejected], (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).Select(v => v.Request.Status));
        Assert.Equal(["Ann", "Ben"], (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).First().Slots.Select(sl => sl.ApproverName));

        // The vessel hears that it is approved and holds nothing of the asking.
        await _s.SettleUntilQuietAsync();
        Assert.Equal(ProjectStatus.Approved, (await _vessel.GetAsync(_s.CompanyId, project.Id))!.Status);
        Assert.Equal(0, await CountAsync(_s.VesselFactory, db => db.ProjectApprovalRequests.CountAsync()));
        Assert.Equal(0, await CountAsync(_s.VesselFactory, db => db.ProjectApprovalSlots.CountAsync()));
        Assert.Contains("office", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, project.Id, _s.User))).Message);
        Assert.Empty(await _vessel.RequestsOfAsync(_s.CompanyId, project.Id));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AVariationAddsTheJobAndTheMoneyOnceTheMatrixApproves_ByItsOwnAmount_AndOnlyOnAnApprovedProject()
    {
        var (ann, _) = await RulesAsync();
        var project = await PlannedAsync(8_000m);                                                                                     // within the first tier: Ann alone
        Assert.Contains("approved project", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", "Early", 100m, "", _s.User))).Message);
        var budget = await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        await _shore.DecideApprovalAsync(_s.CompanyId, budget.Id, ann, true, "");
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today.AddDays(-1), "", _s.User);

        Assert.Contains("Describe", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", " ", 100m, "", _s.User))).Message);
        Assert.Contains("above zero", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", "Free work", 0m, "", _s.User))).Message);

        var variation = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", "Renew the sea chest grating", 4_000m, "Wasted once blasted", _s.User);
        Assert.Equal(ProjectRequestStatus.Pending, variation.Status);
        Assert.Single((await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items);                                              // nothing yet
        Assert.Equal(8_000m, (await _shore.BudgetAsync(_s.CompanyId, project.Id)).Sum(l => l.PlannedUsd));
        var row = Assert.Single(await _shore.PendingForUserAsync(_s.CompanyId, ann.UserId!.Value));
        Assert.Equal((ProjectRequestKind.Variation, 4_000m), (row.Request.Kind, row.Request.AmountUsd));

        await _shore.DecideApprovalAsync(_s.CompanyId, variation.Id, ann, true, "");

        var items = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        var added = items.Single(i => i.Source == ProjectItemSource.Variation);
        Assert.Equal(("Variations", "Renew the sea chest grating", ProjectItemStatus.Open, variation.Id), (added.Group, added.Description, added.Status, added.SourceId));
        Assert.Equal(12_000m, (await _shore.BudgetAsync(_s.CompanyId, project.Id)).Sum(l => l.PlannedUsd));                       // the budget now includes it
        Assert.Equal(added.Id, Assert.Single(await _shore.RequestsOfAsync(_s.CompanyId, project.Id), v => v.Request.Kind == ProjectRequestKind.Variation).Request.ItemId);
        Assert.Equal(8_000m, Assert.Single(await _shore.ApprovalsAsync(_s.CompanyId, project.Id)).TotalUsd);                           // what the project was approved at is unchanged

        // A big variation goes up to both levels; the job reaches the vessel, the money does not.
        var big = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "Hull", "Replate the bilge keel", 30_000m, "", _s.User);
        Assert.Equal(2, (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).First().Slots.Count);
        await _shore.WithdrawRequestAsync(_s.CompanyId, big.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, (await _vessel.GetAsync(_s.CompanyId, project.Id))!.Project.Items.Count);
        Assert.Empty(await _vessel.BudgetAsync(_s.CompanyId, project.Id));
        Assert.Equal(0, await CountAsync(_s.VesselFactory, db => db.ProjectApprovalRequests.CountAsync()));

        // Without rules it is added at once; a closed project takes none.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApprovalRules.RemoveRange(db.ApprovalRules);
            await db.SaveChangesAsync();
        }
        var instant = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", "Extra anodes", 500m, "", _s.User);
        Assert.Equal(ProjectRequestStatus.Approved, instant.Status);
        Assert.Equal(3, (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items.Count);

        var all = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, all.Select(i => new ProjectItemInput(i.Id, i.Group, i.Description, i.Source, i.SourceId, i.SourceRef, ProjectItemStatus.Done)).ToList(), _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today, "", _s.User);
        Assert.Contains("approved project", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", "Too late", 1m, "", _s.User))).Message);
    }
}
