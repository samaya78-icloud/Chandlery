using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The lines a tank inspection writes into its conversation when it is submitted and reviewed, read the same on both sides.</summary>
public class ChatTankInspectionTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private TankService _shoreTanks = null!;
    private TankInspectionService _shore = null!, _vessel = null!;
    private ChatService _shoreChat = null!, _vesselChat = null!;
    private Actor _crew = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreTanks = new TankService(_s.ShoreFactory);
        _shore = new TankInspectionService(_s.ShoreFactory);
        _vessel = new TankInspectionService(_s.VesselFactory);
        _shoreChat = new ChatService(_s.ShoreFactory);
        _vesselChat = new ChatService(_s.VesselFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.TankInspection, PolicyModules.Conversations })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "TK1", "Chief Tanks", "Chief Officer", "", null, "", null, null, "", "", _s.VesselId, null, true, false), _s.User);
        _crew = new Actor(crew.UserId, crew.FullName);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Guid> SubmitAsync(string criterion, string value)
    {
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput("1P", "Tank 1P", TankType.Ballast, 10, 5, 12, 8), _s.User);
        await _s.SettleUntilQuietAsync();
        var template = (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).Single(t => t.Template.TankType == TankType.Ballast).Template.Id;
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, template, Today, "At sea"), _s.User);
        var detail = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        var answers = detail.Inspection.Results.Select(r => new InspectionAnswer(r.Id, r.CriterionName == criterion ? value : r.Scale == ScaleKind.PercentBucket ? "76-100" : "Good", "")).ToList();
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today, "At sea", "", "", "Chief Officer", answers), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        return started.Id;
    }

    [Fact]
    public async Task ASubmissionAndTheOfficesReply_AreOneConversationOnBothSides()
    {
        var id = await SubmitAsync("Cracks", "Bad");
        await _shore.ReviewAsync(_s.CompanyId, id, ReviewDecision.Returned, "Describe the crack and its length", _s.User);
        await _s.SettleUntilQuietAsync();

        var ashore = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.TankInspection, id);
        var onboard = await _vesselChat.ThreadAsync(_s.CompanyId, _crew, ChatModules.TankInspection, id);

        Assert.Equal(2, ashore.Items.Count);
        Assert.StartsWith("Submitted by", ashore.Items[0].Body);
        Assert.Contains("including a Bad rating", ashore.Items[0].Body);
        Assert.StartsWith("Returned to the vessel", ashore.Items[1].Body);
        Assert.Contains("Describe the crack", ashore.Items[1].Body);
        Assert.Equal(ashore.Items.Select(i => i.Body), onboard.Items.Select(i => i.Body));
    }

    [Fact]
    public async Task TheInboxNamesTheInspection()
    {
        var id = await SubmitAsync("Cracks", "Good");
        await _shore.ReviewAsync(_s.CompanyId, id, ReviewDecision.Accepted, "", _s.User);
        var row = (await _shoreChat.InboxAsync(_s.CompanyId, _s.User)).Single(r => r.RecordId == id);
        Assert.StartsWith("Tank inspection ", row.Label);
        Assert.Contains("Tank 1P", row.Label);
        Assert.Equal($"/tanks/inspections/{id}#conversation", row.Href);
    }
}

/// <summary>The lines a risk assessment writes into its conversation when it is submitted and signed.</summary>
public class ChatRiskAssessmentTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RiskAssessmentService _shore = null!, _vessel = null!;
    private RiskLibraryService _library = null!;
    private ChatService _shoreChat = null!, _vesselChat = null!;
    private Actor _crewReviewer = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new RiskAssessmentService(_s.ShoreFactory);
        _vessel = new RiskAssessmentService(_s.VesselFactory);
        _library = new RiskLibraryService(_s.ShoreFactory);
        _shoreChat = new ChatService(_s.ShoreFactory);
        _vesselChat = new ChatService(_s.VesselFactory);
        _crewReviewer = await RiskTestSupport.MakeReviewersAsync(_s);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        foreach (var module in new[] { PolicyModules.RiskAssessment, PolicyModules.Conversations })
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Guid> SubmittedAsync()
    {
        var category = (await _library.ListCategoriesAsync(_s.CompanyId)).First();
        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Crane lift", Today, category.Id), _crewReviewer);
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Crane lift", category.Id, Today, "", [new RaTeamInput("Bosun")],
            [new RaActivityInput(null, "Lifting", [new RaHazardInput(null, "Dropped load", "", 5, 5, "Tag lines", "", 2, 2, "", "Chief Officer", Today.AddDays(30))])]), _crewReviewer);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _crewReviewer);
        await _s.SettleUntilQuietAsync();
        return started.Id;
    }

    [Fact]
    public async Task ASubmissionAndTheOfficesSignature_AreOneConversationOnBothSides()
    {
        var id = await SubmittedAsync();
        await _shore.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "Good work", _s.User);
        await _s.SettleUntilQuietAsync();

        var ashore = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.RiskAssessment, id);
        var onboard = await _vesselChat.ThreadAsync(_s.CompanyId, _crewReviewer, ChatModules.RiskAssessment, id);

        Assert.Equal(2, ashore.Items.Count);
        Assert.Contains("for sign-off", ashore.Items[0].Body);
        Assert.StartsWith("Signed by", ashore.Items[1].Body);
        Assert.Contains("Good work", ashore.Items[1].Body);
        Assert.Equal(ashore.Items.Select(i => i.Body), onboard.Items.Select(i => i.Body));
    }

    [Fact]
    public async Task TheInboxNamesTheAssessmentWithItsVersion()
    {
        var id = await SubmittedAsync();
        var row = (await _shoreChat.InboxAsync(_s.CompanyId, _s.User)).Single(r => r.RecordId == id);
        Assert.StartsWith("Risk assessment ", row.Label);
        Assert.Contains("v1", row.Label);
        Assert.Equal($"/risk/{id}#conversation", row.Href);
    }
}
