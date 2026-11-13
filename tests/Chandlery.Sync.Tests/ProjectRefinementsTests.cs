using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Approval refinements (category sign-offs, extensions of a drydock, variations that change a job), files kept with a project, and groups of projects.</summary>
public class ProjectRefinementsTests : IAsyncLifetime
{
    private sealed class MemoryFiles : IFileStore
    {
        public readonly Dictionary<string, byte[]> Stored = new();
        public Task<string> SaveAsync(Guid companyId, Stream content, CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var key = $"{companyId}/{Guid.NewGuid():N}";
            Stored[key] = copy.ToArray();
            return Task.FromResult(key);
        }
        public Stream? OpenRead(string key) => Stored.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null;
        public void Delete(string key) => Stored.Remove(key);
    }

    private readonly TwoSites _s = new();
    private readonly MemoryFiles _files = new();
    private ProjectService _shore = null!, _vessel = null!;
    private ProjectPlanningService _plan = null!, _vesselPlan = null!;
    private ApprovalService _matrix = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _vessel = new ProjectService(_s.VesselFactory);
        _plan = new ProjectPlanningService(_s.ShoreFactory, _files);
        _vesselPlan = new ProjectPlanningService(_s.VesselFactory, _files);
        _matrix = new ApprovalService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApprovalRules.RemoveRange(db.ApprovalRules);
        foreach (var vessel in new[] { _s.VesselId, _s.OtherVesselId })
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.Projects, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Actor> PersonAsync(string name)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name}{Guid.NewGuid():N}@test.example.com", PasswordHash = "x", Role = UserRole.CompanyUser, DisplayName = name, CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, name);
    }

    private async Task<Project> PlannedAsync(Guid? vesselId = null, ProjectType type = ProjectType.Drydock, params (string Group, string Description)[] items)
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(vesselId ?? _s.VesselId, type, "Drydock 2027", PlannedStart: Today.AddDays(-3), PlannedEnd: Today.AddDays(10)), _s.User);
        var list = items.Length == 0 ? [("Hull", "Blast and repaint the underwater hull")] : items;
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, list.Select(i => new ProjectItemInput(null, i.Group, i.Description)).ToList(), _s.User);
        return project;
    }

    /// <summary>Takes a planned project to Ongoing while there are no rules, so the matrix is only involved in what the test does next.</summary>
    private async Task StartedAsync(Project project)
    {
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today, "", _s.User);
    }

    private async Task<int> OnVessel(Func<ChandleryDbContext, Task<int>> count)
    {
        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        return await count(db);
    }

    // ---- category sign-offs ----

    [Fact]
    public async Task ARuleAboutABudgetCategoryIsAnExtraSignOff_MatchedWithoutRegardToCase_AndNeverUsedForPurchaseOrders()
    {
        var (ann, ben) = (await PersonAsync("Ann"), await PersonAsync("Ben"));
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ann.UserId!.Value);
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ben.UserId!.Value, "  Owner's costs ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _matrix.AddRuleAsync(_s.CompanyId, _s.DepartmentId, null, 1, ben.UserId!.Value, "Owner's costs"));   // a category covers every department

        // What the engine hands a purchase order is untouched by the category rule.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
            Assert.Equal([ann.UserId!.Value], ApprovalEngine.ResolveChain(await db.ApprovalRules.ToListAsync(), null, 100m).Select(r => r.ApproverUserId));
        Assert.Contains(await _matrix.GetRulesAsync(_s.CompanyId), r => r.Department == "Projects: Owner's costs");

        var withOwner = await PlannedAsync();
        await _shore.SaveBudgetAsync(_s.CompanyId, withOwner.Id, [new("Yard", 20_000m), new("owner's COSTS", 3_000m)], _s.User);
        var plain = await PlannedAsync();
        await _shore.SaveBudgetAsync(_s.CompanyId, plain.Id, [new("Yard", 20_000m)], _s.User);

        var asked = await _shore.SubmitAsync(_s.CompanyId, withOwner.Id, _s.User);
        var view = Assert.Single(await _shore.RequestsOfAsync(_s.CompanyId, withOwner.Id, ben.UserId));
        Assert.Equal([("", "Ann"), ("Owner's costs", "Ben")], view.Slots.OrderBy(s => s.Scope).Select(s => (s.Scope, s.ApproverName)));
        Assert.True(view.YourTurn);                                                                                                                         // Ben's sign-off is open at the same time as Ann's
        var inbox = Assert.Single(await _shore.PendingForUserAsync(_s.CompanyId, ben.UserId!.Value));
        Assert.StartsWith("Owner's costs: ", inbox.LevelName);

        await _shore.DecideApprovalAsync(_s.CompanyId, asked.Id, ann, true, "");
        Assert.Equal(ProjectStatus.Planning, (await _shore.GetAsync(_s.CompanyId, withOwner.Id))!.Status);                                                   // Ann alone is not enough
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideApprovalAsync(_s.CompanyId, asked.Id, ann, true, ""));                      // and she has nothing more to answer
        await _shore.DecideApprovalAsync(_s.CompanyId, asked.Id, ben, true, "");
        Assert.Equal(ProjectStatus.Approved, (await _shore.GetAsync(_s.CompanyId, withOwner.Id))!.Status);

        // A budget with no such category asks only the general chain.
        var other = await _shore.SubmitAsync(_s.CompanyId, plain.Id, _s.User);
        Assert.Equal(["Ann"], (await _shore.RequestsOfAsync(_s.CompanyId, plain.Id)).Single().Slots.Select(s => s.ApproverName));
        Assert.Equal(ProjectRequestStatus.Pending, other.Status);

        // Copying one department's rules never drags the category rules along.
        await _matrix.AddRuleAsync(_s.CompanyId, _s.DepartmentId, null, 1, ann.UserId!.Value);
        Assert.Equal(1, await _matrix.CopyRulesAsync(_s.CompanyId, _s.DepartmentId, null));
        Assert.Equal(0, await OnVessel(db => db.ProjectApprovalSlots.CountAsync()));
    }

    // ---- extensions ----

    [Fact]
    public async Task AnExtensionMovesThePlannedEnd_AndOnlyALongOneNeedsTheSecondStep()
    {
        var project = await PlannedAsync();
        await StartedAsync(project);
        var (ann, ben) = (await PersonAsync("Ann"), await PersonAsync("Ben"));
        await _matrix.AddRuleAsync(_s.CompanyId, null, 10_000m, 1, ann.UserId!.Value);
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ann.UserId!.Value);
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 2, ben.UserId!.Value);
        var end = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.PlannedEnd!.Value;

        var shortOne = await _shore.RequestExtensionAsync(_s.CompanyId, project.Id, 3, 0m, "Weather", _s.User);                                             // 3 days, no cost: the first step only
        Assert.Equal(["Ann"], (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).Single().Slots.Select(s => s.ApproverName));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestExtensionAsync(_s.CompanyId, project.Id, 2, 0m, "Again", _s.User));          // one at a time
        await _shore.DecideApprovalAsync(_s.CompanyId, shortOne.Id, ann, true, "");
        Assert.Equal(end.AddDays(3), (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.PlannedEnd);

        var longOne = await _shore.RequestExtensionAsync(_s.CompanyId, project.Id, 10, 4_000m, "Extra steel work", _s.User);                                 // more than 7 days: the second step as well
        Assert.Equal(["Ann", "Ben"], (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).First().Slots.OrderBy(s => s.Level).Select(s => s.ApproverName));
        await _shore.DecideApprovalAsync(_s.CompanyId, longOne.Id, ann, true, "");
        Assert.Equal(end.AddDays(3), (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.PlannedEnd);                                                 // not yet
        await _shore.DecideApprovalAsync(_s.CompanyId, longOne.Id, ben, true, "");
        Assert.Equal(end.AddDays(13), (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.PlannedEnd);
        Assert.Contains(await _shore.BudgetAsync(_s.CompanyId, project.Id), l => l.PlannedUsd == 4_000m && l.Category.StartsWith("Extend the stay by 10 days"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestExtensionAsync(_s.CompanyId, project.Id, 0, 0m, "", _s.User));
        var technical = await PlannedAsync(type: ProjectType.NonDrydock);
        await StartedAsync(technical);
        Assert.Contains("Only a drydock", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestExtensionAsync(_s.CompanyId, technical.Id, 3, 0m, "", _s.User))).Message);

        // With no rules at all it is applied on the spot, as every approval is.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync()) { db.ApprovalRules.RemoveRange(db.ApprovalRules); await db.SaveChangesAsync(); }
        await _shore.RequestExtensionAsync(_s.CompanyId, project.Id, 2, 0m, "", _s.User);
        Assert.Equal(end.AddDays(15), (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.PlannedEnd);

        // The vessel hears of the new planned end through the project and holds no request.
        await _s.SettleUntilQuietAsync();
        Assert.Equal(end.AddDays(15), (await _vessel.GetAsync(_s.CompanyId, project.Id))!.Project.PlannedEnd);
        Assert.Equal(0, await OnVessel(db => db.ProjectApprovalRequests.CountAsync()));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    // ---- variations that change a job ----

    [Fact]
    public async Task AVariationCanRewriteAnOpenJobInsteadOfAddingOne_AndNeverTouchesOneThatIsDoneOrAnother()
    {
        var project = await PlannedAsync(items: [("Hull", "Blast the hull"), ("Machinery", "Overhaul the pumps")]);
        await StartedAsync(project);
        var items = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items.OrderBy(i => i.SortOrder).ToList();
        var ann = await PersonAsync("Ann");

        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ann.UserId!.Value);
        var asked = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "ignored", "Blast the hull and renew 200 m2 of plating", 5_000m, "Plating found wasted", _s.User, items[0].Id);
        Assert.Equal(items[0].Id, asked.TargetItemId);
        Assert.Equal("Blast the hull", (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items.First(i => i.Id == items[0].Id).Description);          // nothing until approved
        await _shore.DecideApprovalAsync(_s.CompanyId, asked.Id, ann, true, "");

        var after = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        Assert.Equal(2, after.Count);                                                                                                                       // rewritten, not added
        Assert.Equal("Blast the hull and renew 200 m2 of plating", after.First(i => i.Id == items[0].Id).Description);
        Assert.Contains(await _shore.BudgetAsync(_s.CompanyId, project.Id), l => l.PlannedUsd == 5_000m);

        // One job is marked done: it can no longer be rewritten. An unknown job is refused too.
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, after.Select(i => new ProjectItemInput(i.Id, i.Group, i.Description, i.Source, i.SourceId, i.SourceRef, i.Id == items[1].Id ? ProjectItemStatus.Done : ProjectItemStatus.Open)).ToList(), _s.User);
        Assert.Contains("still open", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", "Changed", 100m, "", _s.User, items[1].Id))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestVariationAsync(_s.CompanyId, project.Id, "", "Changed", 100m, "", _s.User, Guid.NewGuid()));

        // The vessel reads the rewritten job; it holds no request and no money.
        await _s.SettleUntilQuietAsync();
        Assert.Equal("Blast the hull and renew 200 m2 of plating", (await _vessel.GetAsync(_s.CompanyId, project.Id))!.Project.Items.First(i => i.Id == items[0].Id).Description);
        Assert.Equal((0, 0), (await OnVessel(db => db.ProjectApprovalRequests.CountAsync()), await OnVessel(db => db.ProjectBudgetLines.CountAsync())));
    }

    // ---- files ----

    private static PendingAttachment Pdf(string name = "quotation.pdf") => new() { FileName = name, Data = [0x25, 0x50, 0x44, 0x46, 1, 2, 3] };

    [Fact]
    public async Task AYardsQuotationIsKeptWithItsTender_OnlyOfficeCanOpenIt_AndAClosedProjectLocksIt()
    {
        var project = await PlannedAsync();
        var yard = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Alpha Yard"), _s.User);
        var tender = await _plan.InviteAsync(_s.CompanyId, project.Id, yard.Id, _s.User);

        var kept = await _plan.AddFileAsync(_s.CompanyId, project.Id, tender.Id, Pdf(), _s.User);
        Assert.Equal(("quotation.pdf", "application/pdf", tender.Id), (kept.FileName, kept.ContentType, kept.TenderId));
        Assert.Single(_files.Stored);
        var listed = Assert.Single(await _plan.FilesAsync(_s.CompanyId, project.Id));
        Assert.Equal((tender.Id, 7L), (listed.TenderId, listed.SizeBytes));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddFileAsync(_s.CompanyId, project.Id, tender.Id, Pdf("virus.exe"), _s.User));                // images and PDFs only
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddFileAsync(_s.CompanyId, project.Id, tender.Id, new PendingAttachment { FileName = "empty.pdf", Data = [] }, _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddFileAsync(_s.CompanyId, project.Id, Guid.NewGuid(), Pdf(), _s.User));                    // not one of this project's tenders
        for (var i = 1; i < AttachmentRules.MaxPerTarget; i++) await _plan.AddFileAsync(_s.CompanyId, project.Id, tender.Id, Pdf($"q{i}.pdf"), _s.User);
        Assert.Contains("up to", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddFileAsync(_s.CompanyId, project.Id, tender.Id, Pdf("one-too-many.pdf"), _s.User))).Message);

        var opened = await _plan.OpenFileAsync(_s.CompanyId, kept.Id);
        Assert.NotNull(opened);
        await using (var content = opened!.Value.Content) Assert.Equal(7, content.Length);
        Assert.Null(await _plan.OpenFileAsync(Guid.NewGuid(), kept.Id));                                                                                            // another company never sees it

        await _plan.DeleteFileAsync(_s.CompanyId, kept.Id, _s.User);
        Assert.Equal(AttachmentRules.MaxPerTarget - 1, _files.Stored.Count);

        // Onboard: no rows, nothing to open, nothing to add.
        await _s.SettleUntilQuietAsync();
        Assert.Equal(0, await OnVessel(db => db.ProjectFiles.CountAsync()));
        Assert.Null(await _vesselPlan.OpenFileAsync(_s.CompanyId, listed.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselPlan.AddFileAsync(_s.CompanyId, project.Id, null, Pdf(), _s.User));

        // Once the project is closed its files are locked.
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today, "", _s.User);
        var all = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, all.Select(i => new ProjectItemInput(i.Id, i.Group, i.Description, i.Source, i.SourceId, i.SourceRef, ProjectItemStatus.Dropped)).ToList(), _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Closed, Today, "Reconciled", _s.User);
        Assert.Contains("closed and locked", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddFileAsync(_s.CompanyId, project.Id, null, Pdf("late.pdf"), _s.User))).Message);
    }

    // ---- groups ----

    [Fact]
    public async Task SisterShipProjectsAreGrouped_OneProjectPerVessel_AndTheGroupAddsThemUp_WithoutTouchingWhatEachShipSees()
    {
        var falcon = await PlannedAsync(_s.VesselId);
        var osprey = await PlannedAsync(_s.OtherVesselId);
        var second = await PlannedAsync(_s.VesselId);
        await _shore.SaveBudgetAsync(_s.CompanyId, falcon.Id, [new("Yard", 40_000m)], _s.User);
        await _shore.SaveBudgetAsync(_s.CompanyId, osprey.Id, [new("Yard", 30_000m, 10m)], _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, osprey.Id, Today, "Surveyor", "", 35_000m, _s.User);

        var group = await _plan.CreateGroupAsync(_s.CompanyId, "Sister ships drydock 2027", "Same yard, same slot", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.CreateGroupAsync(_s.CompanyId, "sister SHIPS drydock 2027", "", _s.User));
        await _plan.AddToGroupAsync(_s.CompanyId, group.Id, falcon.Id, _s.User);
        await _plan.AddToGroupAsync(_s.CompanyId, group.Id, osprey.Id, _s.User);
        Assert.Contains("already has a project for that vessel", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddToGroupAsync(_s.CompanyId, group.Id, second.Id, _s.User))).Message);
        var other = await _plan.CreateGroupAsync(_s.CompanyId, "Another group", "", _s.User);
        Assert.Contains("already in another group", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddToGroupAsync(_s.CompanyId, other.Id, falcon.Id, _s.User))).Message);
        Assert.Equal(group.Name, (await _plan.GroupOfAsync(_s.CompanyId, falcon.Id))!.Value.Name);

        var rows = (await _shore.ListWithMoneyAsync(_s.CompanyId)).Where(r => r.Row.Project.Id == falcon.Id || r.Row.Project.Id == osprey.Id).ToList();
        var sum = ProjectGroupRules.Summarise(rows, Today);
        Assert.Equal((2, 2, 70_000m, 3_000m, 35_000m), (sum.Projects, sum.Vessels, sum.Budget, sum.Contingency, sum.Cost));
        Assert.Equal((ProjectStatus.Planning, 2), (sum.Overall, sum.ItemsTotal));
        Assert.Equal((Today.AddDays(-3), Today.AddDays(10)), (sum.Start, sum.End));

        // Each ship still receives only its own project; no group, member or money reaches either vessel.
        await _s.SettleUntilQuietAsync();
        Assert.Single(await _vessel.ListAsync(_s.CompanyId, _s.VesselId), p => p.Project.Id == falcon.Id);
        Assert.Equal((0, 0), (await OnVessel(db => db.ProjectGroups.CountAsync()), await OnVessel(db => db.ProjectGroupMembers.CountAsync())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselPlan.CreateGroupAsync(_s.CompanyId, "Onboard group", "", _s.User));

        // Taking a project out, and deleting a group, leave the projects alone.
        await _plan.RemoveFromGroupAsync(_s.CompanyId, osprey.Id, _s.User);
        await _plan.DeleteGroupAsync(_s.CompanyId, group.Id, _s.User);
        Assert.Null(await _plan.GroupOfAsync(_s.CompanyId, falcon.Id));
        Assert.NotNull(await _shore.GetAsync(_s.CompanyId, falcon.Id));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public void AGroupsOverallStatusIsItsMostActiveMembers()
    {
        static ProjectListRow Row(ProjectStatus status)
        {
            var p = new Project { Id = Guid.NewGuid(), VesselId = Guid.NewGuid(), Code = "P", Title = "T", CreatedAt = DateTime.UtcNow };
            return new ProjectListRow(new ProjectRow(p, status, null, null, 0, 0, 0), "V", 0, 0, new ProjectCost(0, 0, 0), null, null);
        }
        Assert.Null(ProjectGroupRules.Summarise([], Today).Overall);
        Assert.Equal(ProjectStatus.Ongoing, ProjectGroupRules.Summarise([Row(ProjectStatus.Planning), Row(ProjectStatus.Ongoing)], Today).Overall);
        Assert.Equal(ProjectStatus.Complete, ProjectGroupRules.Summarise([Row(ProjectStatus.Complete), Row(ProjectStatus.Closed)], Today).Overall);
        Assert.Equal(ProjectStatus.Closed, ProjectGroupRules.Summarise([Row(ProjectStatus.Closed), Row(ProjectStatus.Closed)], Today).Overall);
        Assert.Equal(ProjectStatus.Planning, ProjectGroupRules.Summarise([Row(ProjectStatus.Planning), Row(ProjectStatus.Approved)], Today).Overall);
        Assert.Equal(ProjectStatus.Approved, ProjectGroupRules.Summarise([Row(ProjectStatus.Approved), Row(ProjectStatus.Complete)], Today).Overall);
    }
}
