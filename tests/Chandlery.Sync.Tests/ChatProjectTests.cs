using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The lines a project writes into its conversation (budget submitted, approved or rejected, work started, completed, closed), read the same by the office and the vessel.</summary>
public class ChatProjectTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!;
    private ApprovalService _matrix = null!;
    private ChatService _shoreChat = null!, _vesselChat = null!;
    private Actor _crew = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _matrix = new ApprovalService(_s.ShoreFactory);
        _shoreChat = new ChatService(_s.ShoreFactory);
        _vesselChat = new ChatService(_s.VesselFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApprovalRules.RemoveRange(db.ApprovalRules);
            foreach (var module in new[] { PolicyModules.Projects, PolicyModules.Conversations })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "PJ1", "Chief Projects", "Chief Officer", "", null, "", null, null, "", "", _s.VesselId, null, true, false), _s.User);
        _crew = new Actor(crew.UserId, crew.FullName);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Project> PlannedAsync()
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027", PlannedStart: Today.AddDays(-3)), _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast and repaint the underwater hull")], _s.User);
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 20_000m)], _s.User);
        return project;
    }

    private async Task<Actor> ApproverAsync(string name)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name}{Guid.NewGuid():N}@test.example.com", PasswordHash = "x", Role = UserRole.CompanyUser, DisplayName = name, CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, name);
    }

    [Fact]
    public async Task TheProjectsStepsAreOneConversationOnBothSides()
    {
        var project = await PlannedAsync();
        await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);                                                       // no rules: approved on the spot
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today.AddDays(-1), "Into the dock", _s.User);
        await _s.SettleUntilQuietAsync();

        var ashore = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.Project, project.Id);
        var onboard = await _vesselChat.ThreadAsync(_s.CompanyId, _crew, ChatModules.Project, project.Id);

        Assert.Equal(2, ashore.Items.Count);
        Assert.StartsWith("Project approved by", ashore.Items[0].Body);
        Assert.StartsWith("Project started by", ashore.Items[1].Body);
        Assert.Contains("Into the dock", ashore.Items[1].Body);
        Assert.Equal(ashore.Items.Select(i => i.Body), onboard.Items.Select(i => i.Body));
    }

    [Fact]
    public async Task ABudgetThroughTheMatrix_SaysWhoSubmittedAndWhoDecided()
    {
        var ann = await ApproverAsync("Ann");
        await _matrix.AddRuleAsync(_s.CompanyId, null, null, 1, ann.UserId!.Value);
        var project = await PlannedAsync();
        var request = await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        await _shore.DecideApprovalAsync(_s.CompanyId, request.Id, ann, false, "Too high for this year");

        var thread = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.Project, project.Id);
        Assert.Equal(2, thread.Items.Count);
        Assert.Contains("submitted for approval", thread.Items[0].Body);
        Assert.StartsWith("Budget request rejected by Ann", thread.Items[1].Body);
        Assert.Contains("Too high for this year", thread.Items[1].Body);
    }

    [Fact]
    public async Task TheInboxNamesTheProject()
    {
        var project = await PlannedAsync();
        await _shore.SubmitAsync(_s.CompanyId, project.Id, _s.User);
        var row = (await _shoreChat.InboxAsync(_s.CompanyId, _s.User)).Single(r => r.RecordId == project.Id);
        Assert.StartsWith("Project ", row.Label);
        Assert.Contains("Drydock 2027", row.Label);
        Assert.Equal($"/projects/{project.Id}#conversation", row.Href);
    }
}
