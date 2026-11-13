using Chandlery.Models;
using Chandlery.Services;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Conversations between a vessel and the office: who writes what, what travels, what never does, and that both sides read the same thread.</summary>
public class ChatTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ChatService _shore = null!, _vessel = null!;
    private CrewMember _chief = null!, _cook = null!;
    private readonly Guid _po = Guid.NewGuid();

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
        _chief = await SignOnAsync("C3001", "Chief Officer");
        _cook = await SignOnAsync("C3002", "Cook");
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, false), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private ChatPost Post(string body, ChatChannel channel = ChatChannel.Internal, Guid? vessel = null, string topic = "", Guid? reply = null)
        => new(ChatModules.PurchaseOrder, _po, vessel ?? _s.VesselId, channel, body, topic, reply);

    private async Task<ChatThreadView> ThreadAsync(ChatService side, Actor who) => await side.ThreadAsync(_s.CompanyId, who, ChatModules.PurchaseOrder, _po);

    // ---- the exchange ----

    [Fact]
    public async Task AVesselAndTheOfficeTalkOnARecord_AndBothSeeTheSameOrderedThread()
    {
        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("The supplier delivered 3 of 5 drums."));
        await _s.SettleUntilQuietAsync();
        var first = await ThreadAsync(_shore, _s.User);
        Assert.Equal(["The supplier delivered 3 of 5 drums."], first.Items.Select(i => i.Body));

        var question = (await _shore.PostAsync(_s.CompanyId, _s.User, Post("Can you send a photo of the delivery note?"))).Id;
        await _s.SettleUntilQuietAsync();
        await _vessel.PostAsync(_s.CompanyId, As(_cook), Post("Sent in the morning.", reply: question));
        await _s.SettleUntilQuietAsync();

        var onboard = await ThreadAsync(_vessel, As(_chief));
        var ashore = await ThreadAsync(_shore, _s.User);
        Assert.Equal(3, ashore.Items.Count);
        Assert.Equal(ashore.Items.Select(i => i.Message.Id), onboard.Items.Select(i => i.Message.Id));      // the same messages, in the same order
        Assert.Equal(ashore.Items.Select(i => i.Body), onboard.Items.Select(i => i.Body));
        Assert.Equal([SyncStages.Vessel, SyncStages.Office, SyncStages.Vessel], ashore.Items.Select(i => i.Message.OwnerStage));   // each side wrote its own
        Assert.All(ashore.Items, i => Assert.Equal(_s.VesselId, i.Message.VesselId));
    }

    [Fact]
    public async Task EditsRemovalsAndResolvingTravelAsNewMessages_AndBothSidesAgree()
    {
        var sent = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("Delivery on Monday"));
        await _vessel.EditAsync(_s.CompanyId, As(_chief), sent.Id, "Delivery on Tuesday");
        var gone = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("wrong thread, sorry"));
        await _vessel.DeleteAsync(_s.CompanyId, As(_chief), gone.Id);
        await _s.SettleUntilQuietAsync();
        await _shore.SetResolvedAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po, "", true);
        await _s.SettleUntilQuietAsync();

        foreach (var (side, who) in new[] { (_shore, _s.User), (_vessel, As(_chief)) })
        {
            var view = await ThreadAsync(side, who);
            Assert.True(view.Resolved);
            Assert.Equal("Delivery on Tuesday", view.Items[0].Body);
            Assert.True(view.Items[0].Edited);
            Assert.True(view.Items[1].Removed);
        }

        await _vessel.SetResolvedAsync(_s.CompanyId, As(_chief), ChatModules.PurchaseOrder, _po, "", false);
        await _s.SettleUntilQuietAsync();
        Assert.False((await ThreadAsync(_shore, _s.User)).Resolved);
    }

    // ---- what never travels ----

    [Fact]
    public async Task OfficeOnlyMessagesAndRecordsWithoutAVessel_NeverReachTheVessel_NotEvenAfterAFullBaseline()
    {
        await _shore.PostAsync(_s.CompanyId, _s.User, Post("Quote looks high; do not tell the vessel.", ChatChannel.OfficeOnly));
        var internalOne = await _shore.PostAsync(_s.CompanyId, _s.User, Post("Please confirm the quantity."));
        var shared = await _shore.PostAsync(_s.CompanyId, _s.User, Post("Supplier is told Friday.", ChatChannel.Shared));
        await _shore.PostAsync(_s.CompanyId, _s.User, new ChatPost(ChatModules.Rfq, Guid.NewGuid(), null, ChatChannel.Internal, "An RFQ with no vessel."));
        await _s.SettleUntilQuietAsync();
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        await using var vessel = await _s.VesselFactory.CreateDbContextAsync();
        var held = await vessel.ChatMessages.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(new[] { internalOne.Id, shared.Id }.Order(), held.Select(m => m.Id).Order());
        Assert.DoesNotContain(held, m => m.Channel == ChatChannel.OfficeOnly);
        Assert.DoesNotContain(held, m => m.VesselId is null);

        // The office keeps all four, apart.
        await using var shore = await _s.ShoreFactory.CreateDbContextAsync();
        Assert.Equal(4, await shore.ChatMessages.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task TheVesselCannotWriteOnTheOfficesChannels_OrOnAnotherVessel_OrAfterTheSwitchIsOff()
    {
        var shared = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.PostAsync(_s.CompanyId, As(_chief), Post("x", ChatChannel.Shared)));
        Assert.Contains("internal", shared.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.PostAsync(_s.CompanyId, As(_chief), Post("x", ChatChannel.OfficeOnly)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.PostAsync(_s.CompanyId, As(_chief), Post("x", vessel: _s.OtherVesselId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.PostAsync(_s.CompanyId, As(_chief), new ChatPost(ChatModules.Rfq, Guid.NewGuid(), null, ChatChannel.Internal, "no vessel")));

        // The office cannot start a conversation for a vessel that does not have the module, and a module's system line is quietly dropped.
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.PostAsync(_s.CompanyId, _s.User, Post("x", vessel: _s.OtherVesselId)));
        Assert.Contains("not switched on", off.Message);
        Assert.Null(await _shore.PostSystemAsync(_s.CompanyId, ChatModules.PurchaseOrder, _po, _s.OtherVesselId, "Approved"));
    }

    [Fact]
    public async Task OnlyTheAuthorChangesAMessage_AndOnlyAnAdministratorRedactsSomeoneElses()
    {
        var chief = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("The cook mentioned Mr Lim's injury."));
        await _s.SettleUntilQuietAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.EditAsync(_s.CompanyId, As(_cook), chief.Id, "tampered"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DeleteAsync(_s.CompanyId, As(_cook), chief.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RedactAsync(_s.CompanyId, As(_chief), chief.Id, "personal"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.EditAsync(_s.CompanyId, _s.User, chief.Id, "tampered by the office"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RedactAsync(_s.CompanyId, _s.User, chief.Id, " "));      // a reason is required

        await _shore.RedactAsync(_s.CompanyId, _s.User, chief.Id, "Personal data about a crew member.");
        await _s.SettleUntilQuietAsync();
        foreach (var (side, who) in new[] { (_shore, _s.User), (_vessel, As(_chief)) })
        {
            var item = Assert.Single((await ThreadAsync(side, who)).Items);
            Assert.True(item.Removed && item.RemovedByAdmin);
            Assert.Equal("", item.Body);
            Assert.Equal("Personal data about a crew member.", item.RedactReason);
        }
    }

    [Fact]
    public async Task HistoryIsNeverRewritten_AMessageCannotBeChangedOrRemovedInTheDatabase()
    {
        var sent = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("original"));
        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        var row = await db.ChatMessages.IgnoreQueryFilters().SingleAsync(m => m.Id == sent.Id);
        row.Body = "rewritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        await using var db2 = await _s.VesselFactory.CreateDbContextAsync();
        db2.ChatMessages.Remove(await db2.ChatMessages.IgnoreQueryFilters().SingleAsync(m => m.Id == sent.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db2.SaveChangesAsync());
    }

    // ---- the rest ----

    [Fact]
    public async Task AMentionOfTheOfficeTravelsOnTheUrgentLane_AndAnOrdinaryMessageDoesNot()
    {
        var urgent = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("@office the pump is leaking again, please call."));
        var ordinary = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("Thanks, noted."));
        Assert.Equal("r:office", urgent.Mentions);
        Assert.Equal(Lane.Urgent, SyncPublisher.LaneFor(urgent));
        Assert.Equal(Lane.Transaction, SyncPublisher.LaneFor(ordinary));
    }

    [Fact]
    public async Task EachSideCountsItsOwnUnread_AndReadMarksNeverTravel()
    {
        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("one"));
        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("two"));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, await _shore.UnreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po));
        Assert.Equal(0, await _vessel.UnreadAsync(_s.CompanyId, As(_chief), ChatModules.PurchaseOrder, _po));      // the Chief wrote them
        Assert.Equal(2, await _vessel.UnreadAsync(_s.CompanyId, As(_cook), ChatModules.PurchaseOrder, _po));

        await _shore.MarkReadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po);
        await _vessel.MarkReadAsync(_s.CompanyId, As(_cook), ChatModules.PurchaseOrder, _po);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(0, await _shore.UnreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po));
        Assert.Equal(0, await _vessel.UnreadAsync(_s.CompanyId, As(_cook), ChatModules.PurchaseOrder, _po));

        await using var shore = await _s.ShoreFactory.CreateDbContextAsync();
        Assert.DoesNotContain(await shore.ChatReads.IgnoreQueryFilters().ToListAsync(), r => r.UserId == _cook.UserId);      // the vessel's read mark stayed onboard

        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("three"));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(1, await _shore.UnreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po));
    }

    [Fact]
    public async Task ASystemLineWrittenByAModuleReachesTheOtherSide_AndCannotBeEdited()
    {
        var line = await _shore.PostSystemAsync(_s.CompanyId, ChatModules.PurchaseOrder, _po, _s.VesselId, "Order approved by the office.");
        await _s.SettleUntilQuietAsync();
        var item = Assert.Single((await ThreadAsync(_vessel, As(_chief))).Items);
        Assert.Equal(ChatKind.System, item.Message.Kind);
        Assert.Equal("System", item.Message.AuthorName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.EditAsync(_s.CompanyId, _s.User, line!.Id, "tampered"));
    }

    [Fact]
    public async Task AMessageShowsAsNotYetSynced_UntilItHasLeft()
    {
        var sent = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("waiting for the link"));
        Assert.Contains(sent.Id, await _vessel.UnsyncedAsync(_s.CompanyId, [sent.Id]));
        await _s.SettleUntilQuietAsync();
        Assert.Empty(await _vessel.UnsyncedAsync(_s.CompanyId, [sent.Id]));
        Assert.Empty(await _vessel.UnsyncedAsync(_s.CompanyId, []));
    }

    [Fact]
    public async Task ThePanelKnowsWhoIsLooking_AndWhetherTheModuleIsOn()
    {
        var office = await _shore.ViewerAsync(_s.CompanyId, _s.User);
        Assert.Equal(ChatSide.Office, office!.Side);
        var crew = await _vessel.ViewerAsync(_s.CompanyId, As(_chief));
        Assert.Equal((ChatSide.Vessel, _s.VesselId), (crew!.Side, crew.VesselId));
        Assert.Null(await _vessel.ViewerAsync(_s.CompanyId, new Actor(Guid.NewGuid(), "Nobody")));
        Assert.Null(await _vessel.ViewerAsync(_s.CompanyId, Actor.System));

        Assert.True(await _shore.IsEnabledAsync(_s.CompanyId, _s.VesselId));
        Assert.False(await _shore.IsEnabledAsync(_s.CompanyId, _s.OtherVesselId));
        Assert.True(await _shore.IsEnabledAsync(_s.CompanyId, null));      // a record with no vessel always may
    }

    // ---- the inbox ----

    private async Task<WorklistTask> TaskAsync(string title)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Worklist, Enabled = true });
            await db.SaveChangesAsync();
        }
        return await new WorklistService(_s.ShoreFactory).CreateTaskAsync(_s.CompanyId, new NewWorklistTask(_s.VesselId, title), _s.User);
    }

    [Fact]
    public async Task TheInboxShowsEachThreadWithWhatIsNewWhoIsNamedAndWhereToGo()
    {
        var task = await TaskAsync("Replace the galley extractor fan belt");
        ChatPost OnTask(string body) => new(ChatModules.Worklist, task.Id, _s.VesselId, ChatChannel.Internal, body);
        await _vessel.PostAsync(_s.CompanyId, As(_chief), OnTask("@office the belt is the wrong size, please advise."));
        await _s.SettleUntilQuietAsync();
        await _shore.PostAsync(_s.CompanyId, _s.User, OnTask("Ordering the right one today."));
        await _s.SettleUntilQuietAsync();

        var office = Assert.Single(await _shore.InboxAsync(_s.CompanyId, _s.User));
        Assert.StartsWith(task.Code, office.Label);
        Assert.Equal($"/worklist/{task.Id}#conversation", office.Href);
        Assert.Equal((1, 1), (office.Unread, office.UnreadMentions));                    // the Chief's message is new and names the office; the office's own is not new to it
        Assert.Equal("MV Sea Falcon", office.VesselName);
        Assert.Equal("Ordering the right one today.", office.LastPreview);

        var onboard = Assert.Single(await _vessel.InboxAsync(_s.CompanyId, As(_cook)));
        Assert.Equal((2, 0), (onboard.Unread, onboard.UnreadMentions));                  // the cook has read neither, and "@office" does not name the crew
        var chief = Assert.Single(await _vessel.InboxAsync(_s.CompanyId, As(_chief)));
        Assert.Equal((1, 0), (chief.Unread, chief.UnreadMentions));                      // only the office's reply is new to the one who wrote the first

        await _shore.MarkReadAsync(_s.CompanyId, _s.User, ChatModules.Worklist, task.Id);
        Assert.Equal((0, 0), ((await _shore.InboxAsync(_s.CompanyId, _s.User)).Single().Unread, (await _shore.InboxAsync(_s.CompanyId, _s.User)).Single().UnreadMentions));
    }

    [Fact]
    public async Task TheInboxNeverQuotesOrCountsWhatThePersonMayNotSee()
    {
        await _shore.PostAsync(_s.CompanyId, _s.User, Post("Quote looks high, keep this between us.", ChatChannel.OfficeOnly));
        await _s.SettleUntilQuietAsync();
        Assert.Single(await _shore.InboxAsync(_s.CompanyId, _s.User));
        Assert.Empty(await _vessel.InboxAsync(_s.CompanyId, As(_chief)));
        var counts = await _vessel.InboxCountsAsync(_s.CompanyId, As(_chief));
        Assert.Equal((0, 0, 0), (counts!.UnreadThreads, counts.MentionThreads, counts.WaitingThreads));
    }

    [Fact]
    public async Task AnotherPersonsMarkAsNeedingAReplyWaitsForEveryoneElse_UntilRepliedToOrResolved()
    {
        var ask = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("Can the office send the spare?"));
        await _s.SettleUntilQuietAsync();
        await _shore.NeedsReplyAsync(_s.CompanyId, _s.User, ask.Id);
        await _s.SettleUntilQuietAsync();

        Assert.Equal(1, Assert.Single(await _vessel.InboxAsync(_s.CompanyId, As(_cook))).WaitingForMe);
        Assert.Equal(0, Assert.Single(await _vessel.InboxAsync(_s.CompanyId, As(_chief))).WaitingForMe);       // the Chief wrote it
        Assert.Equal(1, (await _vessel.InboxCountsAsync(_s.CompanyId, As(_cook)))!.WaitingThreads);

        await _vessel.SetResolvedAsync(_s.CompanyId, As(_chief), ChatModules.PurchaseOrder, _po, "", true);
        Assert.Equal(0, Assert.Single(await _vessel.InboxAsync(_s.CompanyId, As(_cook))).WaitingForMe);
    }

    [Fact]
    public async Task TheInboxCountsThreadsNotMessages_AndLeavesOutThoseGoneQuiet()
    {
        var other = Guid.NewGuid();
        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("one"));
        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("two"));
        await _vessel.PostAsync(_s.CompanyId, As(_chief), new ChatPost(ChatModules.Incident, other, _s.VesselId, ChatChannel.Internal, "three"));
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, ModuleType = ChatModules.Rfq, RecordId = Guid.NewGuid(), Kind = ChatKind.Message, Channel = ChatChannel.Internal,
                Body = "an old thread", AuthorName = "Old hand", At = DateTime.UtcNow.AddDays(-(ChatService.InboxDays + 20)), OwnerStage = SyncStages.Office,
            });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();

        var counts = await _shore.InboxCountsAsync(_s.CompanyId, _s.User);
        Assert.Equal(2, counts!.UnreadThreads);                                          // three messages in two threads; the old thread is not in the inbox
        Assert.DoesNotContain(await _shore.InboxAsync(_s.CompanyId, _s.User), r => r.LastPreview == "an old thread");
    }

    [Fact]
    public async Task ASupplierHasNoInbox_AndTheCompanyKnowsWhetherConversationsAreOn()
    {
        Assert.True(await _shore.IsEnabledForCompanyAsync(_s.CompanyId));
        Assert.Null(await _vessel.InboxCountsAsync(_s.CompanyId, Actor.System));
    }

    [Fact]
    public async Task TopicsAreSeparateThreadsOfTheSameRecord()
    {
        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("main thread"));
        await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("two drums are dented", topic: "Delivery damage"));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(["", "Delivery damage"], await _shore.TopicsAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po));
        Assert.Equal(["main thread"], (await ThreadAsync(_shore, _s.User)).Items.Select(i => i.Body));
        Assert.Equal(["two drums are dented"], (await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po, "Delivery damage")).Items.Select(i => i.Body));
    }
}
