using System.Text.Json.Nodes;
using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>The rules of a conversation without a database: who sees what, which edits count, how a thread reads, who is mentioned.</summary>
public class ChatRulesTests
{
    private static readonly Guid Vessel = Guid.NewGuid(), OtherVessel = Guid.NewGuid();
    private static readonly Guid Ana = Guid.NewGuid(), Ben = Guid.NewGuid(), Chief = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);
    private static readonly ChatViewer Office = new(ChatSide.Office, Ana, IsAdmin: true);
    private static readonly ChatViewer OfficeUser = new(ChatSide.Office, Ben);
    private static readonly ChatViewer Crew = new(ChatSide.Vessel, Chief, Vessel);
    private static readonly ChatViewer Supplier = new(ChatSide.Supplier, Guid.NewGuid());

    private static int _tick;

    private static ChatMessage Msg(string body, Guid author, int minutes = 0, ChatChannel channel = ChatChannel.Internal, ChatKind kind = ChatKind.Message, Guid? target = null,
        Guid? vessel = null, string stage = SyncStages.Vessel, Guid? replyTo = null, string mentions = "")
        => new()
        {
            Id = Guid.CreateVersion7(), CompanyId = Guid.Empty, VesselId = vessel ?? Vessel, ModuleType = ChatModules.PurchaseOrder, RecordId = Guid.Empty,
            Kind = kind, TargetId = target, Channel = channel, Body = body, AuthorUserId = author, AuthorName = "x", At = T0.AddMinutes(minutes).AddMilliseconds(_tick++),
            OwnerStage = stage, ReplyToId = replyTo, Mentions = mentions,
        };

    // ---- who sees what ----

    [Fact]
    public void TheOfficeSeesEverything_TheVesselNotOfficeOnly_ASupplierOnlyShared()
    {
        var internalMessage = Msg("a", Chief);
        var shared = Msg("b", Ana, channel: ChatChannel.Shared, stage: SyncStages.Office);
        var officeOnly = Msg("c", Ana, channel: ChatChannel.OfficeOnly, stage: SyncStages.Office);
        Assert.All(new[] { internalMessage, shared, officeOnly }, m => Assert.True(ChatRules.Visible(m, Office)));
        Assert.Equal([true, true, false], new[] { internalMessage, shared, officeOnly }.Select(m => ChatRules.Visible(m, Crew)));
        Assert.Equal([false, true, false], new[] { internalMessage, shared, officeOnly }.Select(m => ChatRules.Visible(m, Supplier)));
    }

    [Fact]
    public void AVesselNeverSeesAnotherVesselsMessages_OrOneWithNoVessel()
    {
        Assert.False(ChatRules.Visible(Msg("x", Ana, vessel: OtherVessel), Crew));
        var none = Msg("y", Ana);
        none.VesselId = null;
        Assert.False(ChatRules.Visible(none, Crew));
        Assert.True(ChatRules.Visible(none, Office));
    }

    [Theory]
    [InlineData(ChatSide.Office, ChatChannel.Internal, true)]
    [InlineData(ChatSide.Office, ChatChannel.Shared, true)]
    [InlineData(ChatSide.Office, ChatChannel.OfficeOnly, true)]
    [InlineData(ChatSide.Vessel, ChatChannel.Internal, true)]
    [InlineData(ChatSide.Vessel, ChatChannel.Shared, false)]
    [InlineData(ChatSide.Vessel, ChatChannel.OfficeOnly, false)]
    [InlineData(ChatSide.Supplier, ChatChannel.Shared, true)]
    [InlineData(ChatSide.Supplier, ChatChannel.Internal, false)]
    [InlineData(ChatSide.Supplier, ChatChannel.OfficeOnly, false)]
    public void WhoMayWriteOnWhichChannel(ChatSide side, ChatChannel channel, bool allowed) => Assert.Equal(allowed, ChatRules.CanPost(side, channel));

    [Fact]
    public void OfficeOnlyMessagesAndRecordsWithoutAVesselNeverGoToAVessel()
    {
        Assert.Null(ChatRules.SyncVessel(Vessel, ChatChannel.OfficeOnly));
        Assert.Null(ChatRules.SyncVessel(null, ChatChannel.Internal));
        Assert.Equal(Vessel, ChatRules.SyncVessel(Vessel, ChatChannel.Internal));
        Assert.Equal(Vessel, ChatRules.SyncVessel(Vessel, ChatChannel.Shared));
    }

    [Fact]
    public void TheChannelIsReadFromADocument_AndAnythingUnreadableStaysAtHome()
    {
        Assert.Equal(ChatChannel.OfficeOnly, ChatRules.ChannelOf(new JsonObject { ["Channel"] = "OfficeOnly" }));
        Assert.Equal(ChatChannel.Shared, ChatRules.ChannelOf(new JsonObject { ["Channel"] = "shared" }));
        Assert.Equal(ChatChannel.OfficeOnly, ChatRules.ChannelOf(new JsonObject { ["Channel"] = "Nonsense" }));
        Assert.Equal(ChatChannel.Internal, ChatRules.ChannelOf(new JsonObject()));
    }

    // ---- editing and removing ----

    [Fact]
    public void AnAuthorMayEditForTenMinutes_AndOnlyTheirOwnMessage()
    {
        var m = Msg("hello", Chief);
        Assert.True(ChatRules.CanEdit(m, Chief, m.At.AddMinutes(10)));
        Assert.False(ChatRules.CanEdit(m, Chief, m.At.AddMinutes(10).AddSeconds(1)));
        Assert.False(ChatRules.CanEdit(m, Ana, m.At.AddMinutes(1)));
        Assert.False(ChatRules.CanEdit(m, null, m.At.AddMinutes(1)));
        Assert.False(ChatRules.CanEdit(Msg("sys", Chief, kind: ChatKind.System), Chief, T0));
    }

    [Fact]
    public void OnlyTheAuthorRemovesTheirMessage_AndOnlyAnOfficeAdministratorRedacts()
    {
        var m = Msg("hello", Chief);
        Assert.True(ChatRules.CanDelete(m, Chief));
        Assert.False(ChatRules.CanDelete(m, Ana));
        Assert.True(ChatRules.CanRedact(m, Office));
        Assert.False(ChatRules.CanRedact(m, OfficeUser));
        Assert.False(ChatRules.CanRedact(m, Crew));
    }

    // ---- mentions ----

    [Fact]
    public void MentionsFindPeopleAndGroups_LongestNameFirst_AtAWordBoundary()
    {
        var people = new List<ChatPerson> { new(Ana, "Anna"), new(Ben, "Annabel Lee"), new(Chief, "Chief Engineer") };
        Assert.Equal($"u:{Ben}", ChatRules.ParseMentions("Please check, @Annabel Lee.", people));
        Assert.Equal($"u:{Ana}", ChatRules.ParseMentions("@anna can you look", people));
        Assert.Equal($"u:{Chief};r:office", ChatRules.ParseMentions("@Chief Engineer and @office, thanks", people));
        Assert.Equal("", ChatRules.ParseMentions("@Annas is nobody, and mail@anna.example is not a mention", people));
        Assert.Equal("r:master;r:crew", ChatRules.ParseMentions("@master @crew @crew", people));
        Assert.Equal("", ChatRules.ParseMentions("", people));
    }

    [Fact]
    public void OnlyAMentionOfTheOfficeIsUrgent()
    {
        Assert.True(ChatRules.MentionsOffice("u:abc;r:office"));
        Assert.False(ChatRules.MentionsOffice("r:master;r:crew"));
        Assert.False(ChatRules.MentionsOffice(""));
        Assert.True(ChatRules.MentionsUser($"r:crew;u:{Ben}", Ben));
        Assert.False(ChatRules.MentionsUser($"u:{Ben}", Ana));
    }

    // ---- how a thread reads ----

    [Fact]
    public void MessagesReadInSendOrder_WhateverOrderTheyArrivedIn()
    {
        var first = Msg("one", Chief, 0);
        var second = Msg("two", Ana, 1, stage: SyncStages.Office);
        var third = Msg("three", Chief, 2);
        var view = ChatRules.Replay([third, first, second], Office);
        Assert.Equal(["one", "two", "three"], view.Items.Select(i => i.Body));
    }

    [Fact]
    public void AnEditByTheAuthorInTime_ReplacesTheText_AndKeepsTheOriginal()
    {
        var m = Msg("shipment on Monday", Chief, 0);
        var edit = Msg("shipment on Tuesday", Chief, 5, kind: ChatKind.Edit, target: m.Id);
        var item = Assert.Single(ChatRules.Replay([m, edit], Office).Items);
        Assert.Equal("shipment on Tuesday", item.Body);
        Assert.True(item.Edited);
        Assert.Equal(["shipment on Monday"], item.History);
    }

    [Fact]
    public void AnEditIsIgnored_ByAnotherPerson_OrAfterTheWindow()
    {
        var m = Msg("original", Chief, 0);
        var byOther = Msg("hijacked", Ana, 2, kind: ChatKind.Edit, target: m.Id, stage: SyncStages.Office);
        var late = Msg("too late", Chief, 11, kind: ChatKind.Edit, target: m.Id);
        var item = Assert.Single(ChatRules.Replay([m, byOther, late], Office).Items);
        Assert.Equal("original", item.Body);
        Assert.False(item.Edited);
    }

    [Fact]
    public void AnEditWhoseMessageHasNotArrivedYet_WaitsAndThenApplies()
    {
        var m = Msg("original", Chief, 0);
        var edit = Msg("corrected", Chief, 1, kind: ChatKind.Edit, target: m.Id);
        Assert.Empty(ChatRules.Replay([edit], Office).Items);
        Assert.Equal("corrected", Assert.Single(ChatRules.Replay([edit, m], Office).Items).Body);
    }

    [Fact]
    public void ARemovedMessageStaysAsRemoved_AndAnEditCannotBringItBack()
    {
        var m = Msg("oops", Chief, 0);
        var delete = Msg("", Chief, 1, kind: ChatKind.Delete, target: m.Id);
        var edit = Msg("back again", Chief, 2, kind: ChatKind.Edit, target: m.Id);
        var item = Assert.Single(ChatRules.Replay([m, delete, edit], Office).Items);
        Assert.True(item.Removed);
        Assert.False(item.RemovedByAdmin);
        Assert.Equal("", item.Body);
    }

    [Fact]
    public void ARedactionKeepsTheReason_ButOnlyFromTheOffice()
    {
        var m = Msg("a name that should not be here", Chief, 0);
        var fromVessel = Msg("deleted by crew", Chief, 1, kind: ChatKind.Redact, target: m.Id);
        Assert.False(Assert.Single(ChatRules.Replay([m, fromVessel], Office).Items).Removed);
        var fromOffice = Msg("personal data", Ana, 2, kind: ChatKind.Redact, target: m.Id, stage: SyncStages.Office);
        var item = Assert.Single(ChatRules.Replay([m, fromVessel, fromOffice], Office).Items);
        Assert.True(item.Removed && item.RemovedByAdmin);
        Assert.Equal("personal data", item.RedactReason);
        Assert.Equal("", item.Body);
    }

    [Fact]
    public void AThreadIsResolvedUntilReopened_AndTheLastWordCounts()
    {
        var m = Msg("question", Chief, 0);
        Assert.False(ChatRules.Replay([m], Office).Resolved);
        var resolve = Msg("", Ana, 1, kind: ChatKind.Resolve, stage: SyncStages.Office);
        Assert.True(ChatRules.Replay([m, resolve], Office).Resolved);
        var reopen = Msg("", Chief, 2, kind: ChatKind.Reopen);
        Assert.False(ChatRules.Replay([m, resolve, reopen], Office).Resolved);
        Assert.True(ChatRules.Replay([m, resolve, reopen, Msg("", Ana, 3, kind: ChatKind.Resolve, stage: SyncStages.Office)], Office).Resolved);
    }

    [Fact]
    public void AMessageWaitingForAReplyStopsWaitingWhenSomeoneRepliesToIt()
    {
        var ask = Msg("when can you deliver?", Ana, 0, stage: SyncStages.Office);
        var mark = Msg("", Ana, 1, kind: ChatKind.NeedsReply, target: ask.Id, stage: SyncStages.Office);
        var waiting = ChatRules.Replay([ask, mark], Office);
        Assert.True(Assert.Single(waiting.Items).NeedsReply);
        Assert.Equal(1, waiting.NeedingReply);
        var answered = ChatRules.Replay([ask, mark, Msg("Friday", Chief, 5, replyTo: ask.Id)], Office);
        Assert.Equal(0, answered.NeedingReply);
        Assert.False(answered.Items[0].NeedsReply);
    }

    [Fact]
    public void ASystemLineCannotBeEditedOrRemovedByAnyone()
    {
        var line = Msg("Status changed to Approved", Ana, 0, kind: ChatKind.System, stage: SyncStages.Office);
        var edit = Msg("tampered", Ana, 1, kind: ChatKind.Edit, target: line.Id, stage: SyncStages.Office);
        var delete = Msg("", Ana, 1, kind: ChatKind.Delete, target: line.Id, stage: SyncStages.Office);
        var item = Assert.Single(ChatRules.Replay([line, edit, delete], Office).Items);
        Assert.Equal("Status changed to Approved", item.Body);
        Assert.False(item.Removed);
    }

    [Fact]
    public void AViewerReadsOnlyWhatTheyMaySee_EvenIfTheOtherRecordsAreHeld()
    {
        var shared = Msg("to the supplier", Ana, 0, ChatChannel.Shared, stage: SyncStages.Office);
        var inside = Msg("internal talk", Ana, 1, ChatChannel.Internal, stage: SyncStages.Office);
        var edit = Msg("edited internal", Ana, 2, kind: ChatKind.Edit, target: inside.Id, stage: SyncStages.Office);
        Assert.Equal(["to the supplier"], ChatRules.Replay([shared, inside, edit], Supplier).Items.Select(i => i.Body));
        Assert.Equal(["to the supplier", "edited internal"], ChatRules.Replay([shared, inside, edit], Office).Items.Select(i => i.Body));
    }

    // ---- unread ----

    [Fact]
    public void UnreadCountsOtherPeoplesMessagesAfterTheLastRead()
    {
        var mine = Msg("mine", Chief, 0);
        var one = Msg("one", Ana, 1, stage: SyncStages.Office);
        var two = Msg("two", Ana, 5, stage: SyncStages.Office);
        var system = Msg("sys", Ana, 6, kind: ChatKind.System, stage: SyncStages.Office);
        Assert.Equal(2, ChatRules.Replay([mine, one, two, system], Crew).Unread);
        Assert.Equal(1, ChatRules.Replay([mine, one, two, system], Crew, one.At).Unread);
        Assert.Equal(0, ChatRules.Replay([mine, one, two, system], Crew, two.At).Unread);
        Assert.Equal(1, ChatRules.Replay([mine, one, two, system], Office).Unread);          // for Ana only the Chief's message is someone else's
    }

    [Fact]
    public void ARemovedMessageIsNotUnread()
    {
        var one = Msg("one", Ana, 1, stage: SyncStages.Office);
        var gone = Msg("", Ana, 2, kind: ChatKind.Delete, target: one.Id, stage: SyncStages.Office);
        Assert.Equal(0, ChatRules.Replay([one, gone], Crew).Unread);
    }
}
