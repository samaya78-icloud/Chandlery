using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The lines planned maintenance writes into a job's conversation: a completion, a verification, a deferral asked and decided.</summary>
public class ChatMaintenanceTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _vessel = null!, _shore = null!;
    private ChatService _shoreChat = null!, _vesselChat = null!;
    private CrewMember _master = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private Actor Master => new(_master.UserId, _master.FullName);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new PmsService(_s.VesselFactory);
        _shore = new PmsService(_s.ShoreFactory);
        _shoreChat = new ChatService(_s.ShoreFactory);
        _vesselChat = new ChatService(_s.VesselFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Pms, PolicyModules.Conversations })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "CAPT8", "Capt. Jobs", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, false, IsMaster: true), _s.User);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, _master.Id, "Temp-pass-1", false, _s.User);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<PlannedJob> JobAsync()
    {
        var item = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true), _s.User);
        var job = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "ME-1", Title: "Oil sample", CalendarInterval: 1, StartDate: Today.AddDays(-40)), _s.User);
        await _s.SettleUntilQuietAsync();
        return job;
    }

    [Fact]
    public async Task ADeferralAskedOnboardAndDecidedAshore_ReadsAsOneConversationOnBothSides()
    {
        var job = await JobAsync();
        var asked = await _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, Today.AddDays(20), "Spare part arrives at the next port", Master);
        await _s.SettleUntilQuietAsync();
        await _shore.DecideDeferralAsync(_s.CompanyId, asked.Id, true, Today.AddDays(15), "Fifteen days, then it must be done", _s.User);
        await _s.SettleUntilQuietAsync();

        var ashore = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PmsJob, job.Id);
        var onboard = await _vesselChat.ThreadAsync(_s.CompanyId, Master, ChatModules.PmsJob, job.Id);

        Assert.Equal(2, ashore.Items.Count);
        Assert.Contains("asked to put the job off", ashore.Items[0].Body);
        Assert.Contains("Spare part arrives at the next port", ashore.Items[0].Body);
        Assert.StartsWith("Deferral approved", ashore.Items[1].Body);
        Assert.Equal(ashore.Items.Select(i => i.Body), onboard.Items.Select(i => i.Body));
    }

    [Fact]
    public async Task ACompletion_IsNotedOnTheJob_ButAnImportedHistoryIsNot()
    {
        var job = await JobAsync();
        await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Today.AddDays(-1), Comments: "Sample sent ashore"), Master);
        await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Today.AddDays(-30), IsImported: true), Master);
        await _s.SettleUntilQuietAsync();

        var thread = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PmsJob, job.Id);
        var line = Assert.Single(thread.Items);
        Assert.Contains("Sample sent ashore", line.Body);
        Assert.StartsWith("Done on", line.Body);
    }

    [Fact]
    public async Task TheInboxNamesTheJob()
    {
        var job = await JobAsync();
        await _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, Today.AddDays(20), "Short of crew", Master);
        await _s.SettleUntilQuietAsync();
        var row = (await _shoreChat.InboxAsync(_s.CompanyId, _s.User)).Single(r => r.RecordId == job.Id);
        Assert.Equal("ME-1 · Oil sample", row.Label);
        Assert.Equal($"/pms/jobs/{job.Id}#conversation", row.Href);
    }
}
