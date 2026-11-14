using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>J11: category rules on variations and extensions, one tender for a whole group of projects, and a yard entering its own quote through its own login.</summary>
public class ProjectYardPortalTests : IAsyncLifetime
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
    private ProjectService _shore = null!;
    private ProjectPlanningService _plan = null!;
    private YardPortalService _portal = null!, _vesselPortal = null!;
    private ApprovalService _matrix = null!;
    private ProcurementService _procurement = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _plan = new ProjectPlanningService(_s.ShoreFactory, _files);
        _portal = new YardPortalService(_s.ShoreFactory, _files);
        _vesselPortal = new YardPortalService(_s.VesselFactory, _files);
        _matrix = new ApprovalService(_s.ShoreFactory);
        _procurement = new ProcurementService(_s.ShoreFactory);
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

    private async Task<Project> PlannedAsync(Guid? vesselId = null, string title = "Drydock 2027")
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(vesselId ?? _s.VesselId, ProjectType.Drydock, title, "Dock, blast and repaint", PlannedStart: Today.AddDays(-3), PlannedEnd: Today.AddDays(10)), _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast and repaint the underwater hull"), new(null, "Machinery", "Overhaul the pumps")], _s.User);
        return project;
    }

    private async Task StartedAsync(Project project)
    {
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today, "", _s.User);
    }

    private async Task<List<ProjectItem>> ItemsAsync(Guid projectId) => (await _shore.GetAsync(_s.CompanyId, projectId))!.Project.Items.OrderBy(i => i.SortOrder).ToList();

    private static TenderQuoteInput Quote(Guid first, decimal a, Guid? second = null, decimal b = 0, decimal other = 0)
        => new("", null, other, "", new Dictionary<Guid, decimal?> { [first] = a, [second ?? Guid.NewGuid()] = second is null ? null : b });

    // ---- J11c: category rules on variations and extensions ----

    [Fact]
    public async Task ACategoryRuleAlsoCoversAVariationOrExtensionInThatCategory_AndTheBudgetLineLandsInIt()
    {
        var project = await PlannedAsync();
        await StartedAsync(project);
        var (ann, ben) = (await PersonAsync("Ann"), await PersonAsync("Ben"));
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ann.UserId!.Value);
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ben.UserId!.Value, "Owner's costs");

        var plain = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "Hull", "Extra grit blasting", 1_000m, "", _s.User);
        Assert.Equal(["Ann"], (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).First(r => r.Request.Id == plain.Id).Slots.Select(s => s.ApproverName));
        await _shore.DecideApprovalAsync(_s.CompanyId, plain.Id, ann, true, "");

        var owned = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "Hull", "Owner's inspection of the new coating", 2_000m, "", _s.User, null, "owner's COSTS");
        var view = (await _shore.RequestsOfAsync(_s.CompanyId, project.Id, ben.UserId)).First(r => r.Request.Id == owned.Id);
        Assert.Equal([("", "Ann"), ("Owner's costs", "Ben")], view.Slots.OrderBy(s => s.Scope).Select(s => (s.Scope, s.ApproverName)));
        await _shore.DecideApprovalAsync(_s.CompanyId, owned.Id, ann, true, "");
        Assert.DoesNotContain(await _shore.BudgetAsync(_s.CompanyId, project.Id), l => l.Category == "owner's COSTS");                                         // Ann alone is not enough
        await _shore.DecideApprovalAsync(_s.CompanyId, owned.Id, ben, true, "");
        Assert.Contains(await _shore.BudgetAsync(_s.CompanyId, project.Id), l => l.Category == "owner's COSTS" && l.PlannedUsd == 2_000m);                  // the money is in that category

        // An extension is covered by the category only when it costs something; days alone are not money.
        var free = await _shore.RequestExtensionAsync(_s.CompanyId, project.Id, 3, 0m, "Weather", _s.User, "Owner's costs");
        Assert.Equal(["Ann"], (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).First(r => r.Request.Id == free.Id).Slots.Select(s => s.ApproverName));
        await _shore.DecideApprovalAsync(_s.CompanyId, free.Id, ann, true, "");
        var paid = await _shore.RequestExtensionAsync(_s.CompanyId, project.Id, 3, 500m, "Standing charges", _s.User, "Owner's costs");
        Assert.Equal(["Ann", "Ben"], (await _shore.RequestsOfAsync(_s.CompanyId, project.Id)).First(r => r.Request.Id == paid.Id).Slots.Select(s => s.ApproverName).Order());
    }

    // ---- J11b: one tender for a group ----

    [Fact]
    public async Task AYardIsInvitedToEveryShipOnce_ComparedAcrossTheGroup_AndAwardedToAllOrNone()
    {
        var falcon = await PlannedAsync(_s.VesselId, "Falcon drydock");
        var osprey = await PlannedAsync(_s.OtherVesselId, "Osprey drydock");
        var group = await _plan.CreateGroupAsync(_s.CompanyId, "Sister ships", "", _s.User);
        await _plan.AddToGroupAsync(_s.CompanyId, group.Id, falcon.Id, _s.User);
        await _plan.AddToGroupAsync(_s.CompanyId, group.Id, osprey.Id, _s.User);
        var alpha = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Alpha Yard"), _s.User);
        var beta = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Beta Yard"), _s.User);

        var (invited, skipped) = await _plan.InviteGroupAsync(_s.CompanyId, group.Id, alpha.Id, _s.User);
        Assert.Equal((2, 0), (invited, skipped.Count));
        await _plan.InviteGroupAsync(_s.CompanyId, group.Id, beta.Id, _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.InviteGroupAsync(_s.CompanyId, group.Id, alpha.Id, _s.User));                       // already invited everywhere

        var (fItems, oItems) = (await ItemsAsync(falcon.Id), await ItemsAsync(osprey.Id));
        async Task QuoteAsync(Guid projectId, Guid yardId, List<ProjectItem> items, decimal first, decimal? second)
        {
            var tender = (await _plan.TendersAsync(_s.CompanyId, projectId)).Single(t => t.Tender.YardId == yardId).Tender;
            await _plan.RecordQuoteAsync(_s.CompanyId, tender.Id, new TenderQuoteInput("", null, 0m, "", new Dictionary<Guid, decimal?> { [items[0].Id] = first, [items[1].Id] = second }), _s.User);
        }
        await QuoteAsync(falcon.Id, alpha.Id, fItems, 10_000m, 5_000m);
        await QuoteAsync(osprey.Id, alpha.Id, oItems, 9_000m, 4_000m);
        await QuoteAsync(falcon.Id, beta.Id, fItems, 8_000m, 3_000m);                                                                                      // Beta quotes one ship only

        var yards = await _plan.GroupYardsAsync(_s.CompanyId, group.Id);
        var (a, b) = (yards.Single(y => y.YardName == "Alpha Yard"), yards.Single(y => y.YardName == "Beta Yard"));
        Assert.Equal((2, 28_000m, true, true), (a.Quoted, a.Total, a.Complete, a.IsLowest));
        Assert.Equal((1, 11_000m, false, false), (b.Quoted, b.Total, b.Complete, b.IsLowest));                                                             // cheaper so far, but not comparable
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AwardGroupAsync(_s.CompanyId, group.Id, beta.Id, _s.User));
        Assert.Contains("has no quote from this yard", refused.Message);
        Assert.DoesNotContain(await _plan.TendersAsync(_s.CompanyId, falcon.Id), t => t.Tender.Status == TenderStatus.Awarded);                            // nothing awarded: all or none

        Assert.Equal(2, await _plan.AwardGroupAsync(_s.CompanyId, group.Id, alpha.Id, _s.User));
        foreach (var id in new[] { falcon.Id, osprey.Id }) Assert.Equal("Alpha Yard", (await _shore.GetAsync(_s.CompanyId, id))!.Project.Yard);
        Assert.True((await _plan.GroupYardsAsync(_s.CompanyId, group.Id)).Single(y => y.YardName == "Alpha Yard").Awarded);
    }

    [Fact]
    public void TheGroupComparisonCountsAYardAsCompleteOnlyWhenItPricedEveryJobOnEveryShip()
    {
        TenderColumn Col(Guid yard, string name, decimal total, int unpriced, TenderStatus status = TenderStatus.Quoted) => new(Guid.NewGuid(), yard, name, status, new(), 0m, total, unpriced, false);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var ships = new[]
        {
            new TenderComparison([], [Col(a, "Alpha", 10m, 0), Col(b, "Beta", 7m, 1)]),
            new TenderComparison([], [Col(a, "Alpha", 20m, 0), Col(b, "Beta", 8m, 0)]),
        };
        var yards = ProjectGroupTenderRules.CompareYards(ships);
        Assert.Equal((30m, true, true), (yards[0].Total, yards[0].Complete, yards[0].IsLowest));
        Assert.Equal((15m, false, false), (yards[1].Total, yards[1].Complete, yards[1].IsLowest));                                                         // a job left unpriced on one ship
    }

    // ---- J11d: the yard's own login ----

    [Fact]
    public async Task AYardSeesOnlyItsOwnTenders_EntersItsOwnQuote_AttachesItsFile_AndCannotTouchAnotherYardsTender()
    {
        var project = await PlannedAsync();
        var items = await ItemsAsync(project.Id);
        var alpha = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Alpha Yard"), _s.User);
        var beta = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Beta Yard"), _s.User);
        var (tenderA, tenderB) = (await _plan.InviteAsync(_s.CompanyId, project.Id, alpha.Id, _s.User), await _plan.InviteAsync(_s.CompanyId, project.Id, beta.Id, _s.User));

        // The login: the Yard role, bound to its yard, with a temporary password it must change.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.CreateYardLoginAsync(_s.CompanyId, alpha.Id, "not-an-email", "Rao", "longenough1", _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.CreateYardLoginAsync(_s.CompanyId, alpha.Id, "rao@alpha.example.com", "Rao", "short", _s.User));
        var login = await _plan.CreateYardLoginAsync(_s.CompanyId, alpha.Id, "rao@alpha.example.com", "Rao of Alpha", "longenough1", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.CreateYardLoginAsync(_s.CompanyId, beta.Id, "RAO@alpha.example.com", "Someone", "longenough1", _s.User));   // one login per email
        var user = (await _procurement.VerifyLoginAsync("rao@alpha.example.com", "longenough1"))!;
        Assert.Equal((UserRole.YardUser, alpha.Id, true, true), (user.Role, user.YardId, user.MustChangePassword, user.IsYardRole));
        Assert.Null(await _procurement.VerifyLoginAsync("rao@alpha.example.com", "wrong-password"));
        await _plan.SetYardLoginEnabledAsync(_s.CompanyId, login.UserId, false, _s.User);
        Assert.Null(await _procurement.VerifyLoginAsync("rao@alpha.example.com", "longenough1"));                                                          // a disabled login cannot sign in
        await _plan.SetYardLoginEnabledAsync(_s.CompanyId, login.UserId, true, _s.User);

        // The person replaces the temporary password with their own: a wrong current one is refused, a short one is refused, then it works and the mark is cleared.
        Assert.False(await _procurement.ChangeOwnPasswordAsync(user.Id, "not-the-temporary-one", "a-new-password-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _procurement.ChangeOwnPasswordAsync(user.Id, "longenough1", "short"));
        Assert.True(await _procurement.ChangeOwnPasswordAsync(user.Id, "longenough1", "a-new-password-1"));
        Assert.Null(await _procurement.VerifyLoginAsync("rao@alpha.example.com", "longenough1"));
        var own = (await _procurement.VerifyLoginAsync("rao@alpha.example.com", "a-new-password-1"))!;
        Assert.False(own.MustChangePassword);

        // It lists only its own invitations and reads only the specification.
        var mine = Assert.Single(await _portal.InvitationsAsync(_s.CompanyId, alpha.Id));
        Assert.Equal((tenderA.Id, "Drydock 2027", TenderStatus.Invited, true), (mine.TenderId, mine.ProjectTitle, mine.Status, mine.CanQuote));
        var view = await _portal.TenderAsync(_s.CompanyId, alpha.Id, tenderA.Id);
        Assert.Equal(["Blast and repaint the underwater hull", "Overhaul the pumps"], view.Items.Select(i => i.Description));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.TenderAsync(_s.CompanyId, alpha.Id, tenderB.Id));                                // another yard's tender: not found
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tenderB.Id, Quote(items[0].Id, 1m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.TenderAsync(Guid.NewGuid(), alpha.Id, tenderA.Id));                              // nor under another company

        // It enters its own quote; the office sees it, marked as the yard's own.
        await _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tenderA.Id, new TenderQuoteInput("ALPHA-7", Today.AddDays(30), 1_000m, "Valid for a May dock", new Dictionary<Guid, decimal?> { [items[0].Id] = 10_000m, [items[1].Id] = 5_000m }));
        var officeView = (await _plan.TendersAsync(_s.CompanyId, project.Id)).Single(t => t.Tender.Id == tenderA.Id);
        Assert.Equal((TenderStatus.Quoted, true, "ALPHA-7", 16_000m), (officeView.Tender.Status, officeView.Tender.QuotedByYard, officeView.Tender.YardReference, officeView.Lines.Sum(l => l.AmountUsd) + officeView.Tender.OtherChargesUsd));
        Assert.Equal(16_000m, (await _portal.InvitationsAsync(_s.CompanyId, alpha.Id)).Single().QuoteTotal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tenderA.Id, Quote(Guid.NewGuid(), 5m)));      // a job that is not on the project
        await _plan.RecordQuoteAsync(_s.CompanyId, tenderA.Id, new TenderQuoteInput("", null, 0m, "", new Dictionary<Guid, decimal?> { [items[0].Id] = 11_000m }), _s.User);
        Assert.False((await _plan.TendersAsync(_s.CompanyId, project.Id)).Single(t => t.Tender.Id == tenderA.Id).Tender.QuotedByYard);                      // typed in by the office instead

        // Its quotation file: its own tender only, opened only by it.
        await _portal.AddFileAsync(_s.CompanyId, alpha.Id, tenderA.Id, new PendingAttachment { FileName = "alpha-quotation.pdf", Data = [0x25, 0x50, 0x44, 0x46, 1] }, "Rao of Alpha");
        var file = Assert.Single((await _portal.TenderAsync(_s.CompanyId, alpha.Id, tenderA.Id)).Files);
        Assert.Equal("Rao of Alpha (yard)", file.UploadedByName);
        Assert.NotNull(await _portal.OpenFileAsync(_s.CompanyId, alpha.Id, file.Id));
        Assert.Null(await _portal.OpenFileAsync(_s.CompanyId, beta.Id, file.Id));                                                                          // Beta cannot open Alpha's quotation
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.AddFileAsync(_s.CompanyId, alpha.Id, tenderA.Id, new PendingAttachment { FileName = "x.exe", Data = [1] }, "Rao"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.AddFileAsync(_s.CompanyId, beta.Id, tenderA.Id, new PendingAttachment { FileName = "x.pdf", Data = [1] }, "Eve"));

        // Once awarded, the yard can no longer change its quote or attach more.
        await _plan.AwardAsync(_s.CompanyId, tenderA.Id, _s.User);
        Assert.False((await _portal.InvitationsAsync(_s.CompanyId, alpha.Id)).Single().CanQuote);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tenderA.Id, Quote(items[0].Id, 1m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.AddFileAsync(_s.CompanyId, alpha.Id, tenderA.Id, new PendingAttachment { FileName = "late.pdf", Data = [1] }, "Rao"));
        Assert.Equal(TenderStatus.NotAwarded, (await _portal.InvitationsAsync(_s.CompanyId, beta.Id)).Single().Status);
    }

    [Fact]
    public async Task AYardLoginNeverReachesAVessel_AndAFinishedProjectTakesNoMoreQuotes()
    {
        var project = await PlannedAsync();
        var items = await ItemsAsync(project.Id);
        var alpha = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Alpha Yard"), _s.User);
        var tender = await _plan.InviteAsync(_s.CompanyId, project.Id, alpha.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        // Onboard: no yard, no tender and no login exist, and the portal refuses outright.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselPortal.InvitationsAsync(_s.CompanyId, alpha.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselPortal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tender.Id, Quote(items[0].Id, 1m)));
        Assert.Null(await _vesselPortal.OpenFileAsync(_s.CompanyId, alpha.Id, Guid.NewGuid()));
        await using (var db = await _s.VesselFactory.CreateDbContextAsync()) Assert.Equal((0, 0), (await db.ProjectYards.CountAsync(), await db.Users.CountAsync(u => u.Role == UserRole.YardUser)));

        // Work complete: the list says so and a new quote is refused.
        await StartedAsync(project);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today, "", _s.User);
        Assert.False(Assert.Single(await _portal.InvitationsAsync(_s.CompanyId, alpha.Id)).CanQuote);
        Assert.Contains("closed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tender.Id, Quote(items[0].Id, 1m)))).Message);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }
}
