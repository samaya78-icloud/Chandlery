using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The conversation on an incident: the steps it writes, and that it keeps the incident's own confidentiality. Onboard only the Master and the reporter may read it,
/// so crew who cannot open the report never see its conversation, its count or its words in the inbox.
/// </summary>
public class ChatIncidentTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private IncidentService _shore = null!, _vessel = null!;
    private ChatService _shoreChat = null!, _vesselChat = null!;
    private CrewMember _reporter = null!, _master = null!, _cook = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new IncidentService(_s.ShoreFactory, new WorklistService(_s.ShoreFactory));
        _vessel = new IncidentService(_s.VesselFactory, new WorklistService(_s.VesselFactory));
        _shoreChat = new ChatService(_s.ShoreFactory);
        _vesselChat = new ChatService(_s.VesselFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Incidents, PolicyModules.Worklist, PolicyModules.Conversations })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        _reporter = await SignOnAsync("CI1", "Third Officer");
        _master = await SignOnAsync("CI2", "Master", master: true);
        _cook = await SignOnAsync("CI3", "Cook");
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private async Task<Incident> SubmittedAsync()
    {
        var input = new IncidentInput(IncidentKind.NearMiss, DateTime.UtcNow.AddHours(-3), "Engine room", IncidentShipStatus.AtSea, "Calm", "A wrench fell from the grating.", "Area cleared.",
            null, "Purifier", "", false, false, 0, 4, 3, []);
        var draft = await _vessel.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null, input);
        var submitted = await _vessel.SubmitAsync(_s.CompanyId, draft.Id, As(_reporter));
        await _s.SettleUntilQuietAsync();
        return submitted;
    }

    private ChatPost Post(Guid incident, string body) => new(ChatModules.Incident, incident, _s.VesselId, ChatChannel.Internal, body);

    [Fact]
    public async Task TheStepsOfAReportAreWrittenIntoItsConversation_AndTheMasterAndTheOfficeReadThemAlike()
    {
        var report = await SubmittedAsync();
        await _vessel.ReviewAsync(_s.CompanyId, report.Id, As(_master), "Facts confirmed.");
        await _s.SettleUntilQuietAsync();
        await _shore.OpenInvestigationAsync(_s.CompanyId, report.Id, _s.User, "Superintendent to visit");
        await _s.SettleUntilQuietAsync();

        var ashore = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.Incident, report.Id);
        var master = await _vesselChat.ThreadAsync(_s.CompanyId, As(_master), ChatModules.Incident, report.Id);

        Assert.Equal(3, ashore.Items.Count);
        Assert.StartsWith("Report submitted by", ashore.Items[0].Body);
        Assert.StartsWith("Reviewed by", ashore.Items[1].Body);
        Assert.StartsWith("Investigation opened by", ashore.Items[2].Body);
        Assert.DoesNotContain("Superintendent to visit", ashore.Items[2].Body);                    // a line names the step, never the confidential text
        Assert.Equal(ashore.Items.Select(i => i.Body), master.Items.Select(i => i.Body));
    }

    [Fact]
    public async Task CrewWhoCannotOpenTheReport_NeverSeeItsConversation_OrReadItInTheInbox_OrWriteInIt()
    {
        var report = await SubmittedAsync();
        await _shoreChat.PostAsync(_s.CompanyId, _s.User, Post(report.Id, "Please keep this between us until the investigation is done."));
        await _s.SettleUntilQuietAsync();

        // The reporter and the Master may read it; the cook may not, by any road.
        Assert.NotEmpty((await _vesselChat.ThreadAsync(_s.CompanyId, As(_reporter), ChatModules.Incident, report.Id)).Items);
        Assert.NotEmpty((await _vesselChat.ThreadAsync(_s.CompanyId, As(_master), ChatModules.Incident, report.Id)).Items);
        Assert.Empty((await _vesselChat.ThreadAsync(_s.CompanyId, As(_cook), ChatModules.Incident, report.Id)).Items);
        Assert.DoesNotContain(await _vesselChat.InboxAsync(_s.CompanyId, As(_cook)), r => r.RecordId == report.Id);
        Assert.Contains(await _vesselChat.InboxAsync(_s.CompanyId, As(_master)), r => r.RecordId == report.Id);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselChat.PostAsync(_s.CompanyId, As(_cook), Post(report.Id, "What happened?")));
        Assert.Contains("not open to you", refused.Message);
    }

    [Fact]
    public async Task TheInboxNamesTheIncident()
    {
        var report = await SubmittedAsync();
        var row = (await _shoreChat.InboxAsync(_s.CompanyId, _s.User)).Single(r => r.RecordId == report.Id);
        Assert.Equal($"Incident {report.Code}", row.Label);
        Assert.Equal($"/incidents/{report.Id}#conversation", row.Href);
    }
}
