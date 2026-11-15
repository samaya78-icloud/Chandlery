using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Searching the words of messages: what is found, what is never found by someone who may not read it, and how edits and removals count.</summary>
public class ChatSearchTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ChatService _shore = null!, _vessel = null!;
    private CrewMember _chief = null!;
    private readonly Guid _po = Guid.NewGuid(), _task = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ChatService(_s.ShoreFactory);
        _vessel = new ChatService(_s.VesselFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Conversations, Enabled = true });
            await db.SaveChangesAsync();
        }
        _chief = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "SR1", "Chief Search", "Chief Officer", "", null, "", null, null, "", "", _s.VesselId, null, true, false), _s.User);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private Actor Chief => new(_chief.UserId, _chief.FullName);

    private ChatPost Post(string body, string module = ChatModules.PurchaseOrder, ChatChannel channel = ChatChannel.Internal)
        => new(module, module == ChatModules.PurchaseOrder ? _po : _task, _s.VesselId, channel, body);

    private Task<List<ChatSearchHit>> SearchAsync(ChatService side, Actor who, string text, string? module = null, DateTime? from = null, DateTime? to = null)
        => side.SearchAsync(_s.CompanyId, who, new ChatSearch(text, module, From: from, To: to));

    [Fact]
    public async Task AWordIsFoundOnBothSides_WithItsRecordAndASnippet_NewestFirst_AndNarrowedByKindAndDate()
    {
        await _vessel.PostAsync(_s.CompanyId, Chief, Post("The hydraulic hose on the crane is leaking"));
        await _vessel.PostAsync(_s.CompanyId, Chief, Post("Hydraulic oil topped up", ChatModules.Worklist));
        await _s.SettleUntilQuietAsync();
        await Task.Delay(20);
        await _shore.PostAsync(_s.CompanyId, _s.User, Post("Spare hydraulic hose ordered, arrives Friday"));
        await _s.SettleUntilQuietAsync();

        var ashore = await SearchAsync(_shore, _s.User, "HYDRAULIC");
        Assert.Equal(3, ashore.Count);
        Assert.Equal(ashore.OrderByDescending(h => h.At).Select(h => h.MessageId), ashore.Select(h => h.MessageId));         // newest first
        Assert.Contains("hydraulic", ashore[0].Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await SearchAsync(_shore, _s.User, "hydraulic", ChatModules.PurchaseOrder)).Count);                // by kind of record
        Assert.Equal(3, (await SearchAsync(_vessel, Chief, "hydraulic")).Count);                                              // the vessel finds the same
        Assert.Empty(await SearchAsync(_shore, _s.User, "hydraulic", from: DateTime.UtcNow.Date.AddDays(1)));                // by date
        Assert.Equal(3, (await SearchAsync(_shore, _s.User, "hydraulic", from: DateTime.UtcNow.Date.AddDays(-1), to: DateTime.UtcNow.Date)).Count);
        Assert.Empty(await SearchAsync(_shore, _s.User, "h"));                                                                // too short to search
        Assert.Empty(await SearchAsync(_shore, _s.User, "100%"));                                                             // a wildcard is a letter, not a pattern
    }

    [Fact]
    public async Task NothingIsFoundThatThePersonMayNotRead_AndEditsAndRemovalsCountAsTheThreadNowReads()
    {
        await _shore.PostAsync(_s.CompanyId, _s.User, Post("Confidential price for the new owner", channel: ChatChannel.OfficeOnly));
        var edited = await _vessel.PostAsync(_s.CompanyId, Chief, Post("Drum count is twelve"));
        var removed = await _vessel.PostAsync(_s.CompanyId, Chief, Post("Wrong note about pallets"));
        await _vessel.EditAsync(_s.CompanyId, Chief, edited.Id, "Drum count is fourteen");
        await _vessel.DeleteAsync(_s.CompanyId, Chief, removed.Id);
        await _s.SettleUntilQuietAsync();

        Assert.Single(await SearchAsync(_shore, _s.User, "Confidential"));
        Assert.Empty(await SearchAsync(_vessel, Chief, "Confidential"));                                                      // office only never reaches the vessel
        Assert.Single(await SearchAsync(_shore, _s.User, "fourteen"));
        Assert.Empty(await SearchAsync(_shore, _s.User, "twelve"));                                                           // the old words are gone
        Assert.Empty(await SearchAsync(_shore, _s.User, "pallets"));                                                          // a removed message is not found
    }
}
