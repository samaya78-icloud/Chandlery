using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>J12: what a yard tells the office (notices, questions, revisions, declining), the yard keeping its own contact details, and the superintendent's own sign-off.</summary>
public class ProjectYardDialogueTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!;
    private ProjectPlanningService _plan = null!;
    private YardPortalService _portal = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _plan = new ProjectPlanningService(_s.ShoreFactory, new NullFiles());
        _portal = new YardPortalService(_s.ShoreFactory, new NullFiles());
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApprovalRules.RemoveRange(db.ApprovalRules);
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private sealed class NullFiles : IFileStore
    {
        public Task<string> SaveAsync(Guid companyId, Stream content, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid().ToString("N"));
        public Stream? OpenRead(string key) => null;
        public void Delete(string key) { }
    }

    private async Task<Actor> PersonAsync(string name)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name}{Guid.NewGuid():N}@test.example.com", PasswordHash = "x", Role = UserRole.CompanyUser, DisplayName = name, CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow, IsProjectManager = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, name);
    }

    private async Task<Project> PlannedAsync(Guid? superintendent = null, string superName = "")
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027", "Dock and repaint", SuperintendentUserId: superintendent, SuperintendentName: superName,
            PlannedStart: Today.AddDays(-3), PlannedEnd: Today.AddDays(10)), _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast and repaint the underwater hull"), new(null, "Machinery", "Overhaul the pumps")], _s.User);
        return project;
    }

    private async Task<List<ProjectItem>> ItemsAsync(Guid projectId) => (await _shore.GetAsync(_s.CompanyId, projectId))!.Project.Items.OrderBy(i => i.SortOrder).ToList();

    private static TenderQuoteInput Quote(Guid item, decimal amount, string reason = "") => new("", null, 0m, "", new Dictionary<Guid, decimal?> { [item] = amount }, reason);

    [Fact]
    public async Task AYardQuotingRevisingDecliningAndAsking_TellsTheOffice_AndKeepsItsHistory()
    {
        var project = await PlannedAsync();
        var items = await ItemsAsync(project.Id);
        var alpha = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Alpha Yard"), _s.User);
        var beta = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Beta Yard"), _s.User);
        var tenderA = await _plan.InviteAsync(_s.CompanyId, project.Id, alpha.Id, _s.User);
        var tenderB = await _plan.InviteAsync(_s.CompanyId, project.Id, beta.Id, _s.User);

        await _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tenderA.Id, Quote(items[0].Id, 10_000m));
        var notice = Assert.Single(await _plan.NoticesAsync(_s.CompanyId, project.Id));
        Assert.Contains("Alpha Yard quoted USD 10,000.00", notice.Text);

        // A second version has to say why, and both versions are kept.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tenderA.Id, Quote(items[0].Id, 9_000m)));
        await _portal.SubmitQuoteAsync(_s.CompanyId, alpha.Id, tenderA.Id, Quote(items[0].Id, 9_000m, "Steel price fell"));
        var history = await _portal.RevisionsAsync(_s.CompanyId, alpha.Id, tenderA.Id);
        Assert.Equal([10_000m, 9_000m], history.Select(r => r.TotalUsd));
        Assert.Equal(["", "Steel price fell"], history.Select(r => r.Reason));
        Assert.Contains("revised its quote", (await _plan.NoticesAsync(_s.CompanyId, project.Id)).First().Text);
        Assert.Empty(await _portal.RevisionsAsync(_s.CompanyId, beta.Id, tenderB.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.RevisionsAsync(_s.CompanyId, beta.Id, tenderA.Id));                          // another yard's history

        // Declining needs a reason and only works before quoting.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.DeclineAsync(_s.CompanyId, beta.Id, tenderB.Id, " "));
        await _portal.DeclineAsync(_s.CompanyId, beta.Id, tenderB.Id, "No dock free that month");
        Assert.Equal(TenderStatus.Declined, (await _plan.TendersAsync(_s.CompanyId, project.Id)).Single(t => t.YardName == "Beta Yard").Tender.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.DeclineAsync(_s.CompanyId, alpha.Id, tenderA.Id, "Changed my mind"));            // already quoted

        // Questions: asked by one yard, answered by the office, seen only by the yard that asked.
        await _portal.AskAsync(_s.CompanyId, alpha.Id, tenderA.Id, "Is the tank coating included?", "Rao");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.AskAsync(_s.CompanyId, alpha.Id, tenderA.Id, " ", "Rao"));
        var question = Assert.Single(await _plan.QuestionsOfAsync(_s.CompanyId, project.Id));
        Assert.Equal("Alpha Yard", question.YardName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AnswerQuestionAsync(_s.CompanyId, question.Question.Id, "", _s.User));
        await _plan.AnswerQuestionAsync(_s.CompanyId, question.Question.Id, "Yes, both tanks.", _s.User);
        Assert.Equal("Yes, both tanks.", Assert.Single(await _portal.QuestionsAsync(_s.CompanyId, alpha.Id, tenderA.Id)).Answer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.QuestionsAsync(_s.CompanyId, beta.Id, tenderA.Id));

        // Notices can be marked read.
        Assert.True((await _plan.NoticesAsync(_s.CompanyId)).Count >= 4);
        await _plan.MarkNoticesReadAsync(_s.CompanyId);
        Assert.Empty(await _plan.NoticesAsync(_s.CompanyId));
    }

    [Fact]
    public async Task AYardKeepsItsOwnContactDetails_ButNotItsNameOrOtherYards()
    {
        var alpha = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Alpha Yard", Notes: "Preferred"), _s.User);
        await _portal.SaveProfileAsync(_s.CompanyId, alpha.Id, "Piraeus", "Rao", "rao@alpha-yard.example.com");
        var profile = await _portal.ProfileAsync(_s.CompanyId, alpha.Id);
        Assert.Equal(("Alpha Yard", "Piraeus", "Rao", "rao@alpha-yard.example.com"), (profile.Name, profile.Location, profile.ContactName, profile.ContactEmail));
        var kept = (await _plan.YardsAsync(_s.CompanyId)).Single(y => y.Id == alpha.Id);
        Assert.Equal("Preferred", kept.Notes);
        Assert.True(kept.IsActive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.SaveProfileAsync(_s.CompanyId, alpha.Id, "Piraeus", "Rao", "not an email"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.SaveProfileAsync(_s.CompanyId, Guid.NewGuid(), "x", "y", ""));                 // no such yard
        await Assert.ThrowsAsync<InvalidOperationException>(() => _portal.ProfileAsync(Guid.NewGuid(), alpha.Id));                                        // nor under another company
    }

    [Fact]
    public async Task TheSuperintendentSignsOffTheirOwnProject_WhenTheCompanyAsksForIt()
    {
        var sam = await PersonAsync("Sam");
        var project = await PlannedAsync(sam.UserId, "Sam");

        // Off by default: with no matrix rules the budget is approved on the spot.
        Assert.False(await _shore.GetSuperintendentSignOffAsync(_s.CompanyId));
        Assert.False(await _shore.NeedsMatrixAsync(_s.CompanyId, project.Id, _s.User.UserId));

        await _shore.SetSuperintendentSignOffAsync(_s.CompanyId, true);
        Assert.True(await _shore.NeedsMatrixAsync(_s.CompanyId, project.Id, _s.User.UserId));
        Assert.False(await _shore.NeedsMatrixAsync(_s.CompanyId, project.Id, sam.UserId));                                                                  // Sam asking is Sam's own sign-off

        var request = await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        Assert.Equal(ProjectRequestStatus.Pending, request.Status);
        var view = (await _shore.RequestsOfAsync(_s.CompanyId, project.Id, sam.UserId)).Single();
        Assert.True(view.YourTurn);
        var slot = Assert.Single(view.Slots);
        Assert.Equal(("Project superintendent", "Sam"), (slot.Scope, slot.ApproverName));

        var other = await PersonAsync("Other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideApprovalAsync(_s.CompanyId, request.Id, other, true, ""));                   // only Sam can
        await _shore.DecideApprovalAsync(_s.CompanyId, request.Id, sam, true, "");
        Assert.Equal(ProjectStatus.Approved, (await _shore.GetAsync(_s.CompanyId, project.Id))!.Status);

        // A project with no superintendent has no such step; the same goes for a variation raised by the superintendent.
        var bare = await PlannedAsync();
        Assert.False(await _shore.NeedsMatrixAsync(_s.CompanyId, bare.Id, _s.User.UserId));
        var variation = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "Hull", "Extra blasting", 500m, "", _s.User);
        Assert.Equal(ProjectRequestStatus.Pending, variation.Status);
        var bySam = await _shore.RequestVariationAsync(_s.CompanyId, project.Id, "Hull", "More blasting", 500m, "", sam);
        Assert.Equal(ProjectRequestStatus.Approved, bySam.Status);
    }
}
