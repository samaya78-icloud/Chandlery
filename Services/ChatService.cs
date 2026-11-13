using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>What a person writes: the record the conversation belongs to, which vessel that record belongs to (null when it has none), and the text.</summary>
public sealed record ChatPost(string ModuleType, Guid RecordId, Guid? VesselId, ChatChannel Channel, string Body, string Topic = "", Guid? ReplyToId = null);

/// <summary>One thread in a person's inbox: which record, how new it is, and what was said last.</summary>
public sealed record ChatInboxRow(string ModuleType, Guid RecordId, string Topic, Guid? VesselId, string VesselName, string Label, string? Href, DateTime LastAt, string LastAuthor,
    string LastPreview, int Unread, int UnreadMentions, int WaitingForMe, bool Resolved);

/// <summary>The numbers behind the menu badge and the home page card: threads, not messages.</summary>
public sealed record ChatInboxCounts(int UnreadThreads, int MentionThreads, int WaitingThreads);

/// <summary>
/// Conversations on records (spec 10). Everything is written as a new message by the side of its author, so the vessel and the office never write the same
/// row; the rules in <see cref="ChatRules"/> say who may see and do what, and every one of them is checked here whatever the page lets through.
/// </summary>
public class ChatService(IDbContextFactory<ChandleryDbContext> factory)
{
    private sealed record Who(ChatViewer Viewer, string Name, Guid? UserId)
    {
        public ChatSide Side => Viewer.Side;
    }

    // ---- the module ----

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Conversations);

    private static async Task RequireEnabledAsync(ChandleryDbContext db, Guid companyId, Guid? vesselId)
    {
        if (vesselId is { } vessel && !await IsEnabledForVesselAsync(db, companyId, vessel))
            throw new InvalidOperationException("Conversations are not switched on for this vessel.");
    }

    // ---- who is asking ----

    private static async Task<Who> WhoAsync(ChandleryDbContext db, Guid companyId, Actor actor)
    {
        if (actor.UserId is null) throw new InvalidOperationException("Sign in to take part in a conversation.");
        if (db.Node.IsVessel)
        {
            var crew = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.UserId == actor.UserId && c.VesselId != null && c.IsActive)
                ?? throw new InvalidOperationException("Conversations onboard are for the crew of the vessel.");
            return new(new ChatViewer(ChatSide.Vessel, actor.UserId, crew.VesselId, IsMaster: crew.IsMaster), crew.FullName, actor.UserId);
        }

        var user = await db.Users.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == actor.UserId && u.IsEnabled)
            ?? throw new InvalidOperationException("This user cannot take part in conversations.");
        if (user.IsSupplierRole) return new(new ChatViewer(ChatSide.Supplier, user.Id), actor.Name, user.Id);
        if (user.CompanyId != companyId) throw new InvalidOperationException("This user does not belong to this company.");
        return new(new ChatViewer(ChatSide.Office, user.Id, IsAdmin: user.Role == UserRole.CompanyAdmin), actor.Name, user.Id);
    }

    private static string Stage(ChandleryDbContext db) => db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office;

    private static async Task<List<ChatPerson>> PeopleAsync(ChandleryDbContext db, Guid companyId)
        => await db.Users.AsNoTracking().Where(u => u.CompanyId == companyId && u.IsEnabled && (u.Role == UserRole.CompanyAdmin || u.Role == UserRole.CompanyUser))
            .Select(u => new ChatPerson(u.Id, u.DisplayName)).ToListAsync();

    // ---- writing ----

    public async Task<ChatMessage> PostAsync(Guid companyId, Actor actor, ChatPost post)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        Validate(post, who);
        await RequireEnabledAsync(db, companyId, post.VesselId);
        if (!ChatRules.CanPost(who.Side, post.Channel)) throw new InvalidOperationException(who.Side == ChatSide.Vessel ? "Onboard, messages are internal." : "This channel is not open to you.");
        if (who.Side == ChatSide.Vessel && post.VesselId != who.Viewer.VesselId) throw new InvalidOperationException("You are not signed on to this vessel.");

        if (post.ReplyToId is { } reply)
        {
            var original = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == reply && m.CompanyId == companyId);
            if (original is null || !ChatRules.Visible(original, who.Viewer) || original.ModuleType != post.ModuleType || original.RecordId != post.RecordId)
                throw new InvalidOperationException("The message you are replying to is not in this conversation.");
        }

        var body = post.Body.Trim();
        var message = New(db, companyId, who, post.ModuleType, post.RecordId, post.VesselId, post.Topic.Trim(), ChatKind.Message, post.Channel, body, null, post.ReplyToId,
            ChatRules.ParseMentions(body, await PeopleAsync(db, companyId)));
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    private static void Validate(ChatPost post, Who who)
    {
        if (!ChatModules.All.Contains(post.ModuleType)) throw new InvalidOperationException("Unknown kind of record.");
        if (post.RecordId == Guid.Empty) throw new InvalidOperationException("Name the record the conversation is about.");
        if (string.IsNullOrWhiteSpace(post.Body)) throw new InvalidOperationException("Write something first.");
        if (post.Body.Trim().Length > ChatRules.MaxBodyLength) throw new InvalidOperationException($"A message is at most {ChatRules.MaxBodyLength} characters.");
        if (post.Topic.Trim().Length > 80) throw new InvalidOperationException("A topic is at most 80 characters.");
        if (who.Side == ChatSide.Vessel && post.VesselId is null) throw new InvalidOperationException("This record is not a vessel's.");
    }

    private static ChatMessage New(ChandleryDbContext db, Guid companyId, Who? who, string moduleType, Guid recordId, Guid? vesselId, string topic, ChatKind kind, ChatChannel channel,
        string body, Guid? targetId, Guid? replyTo, string mentions) => new()
    {
        Id = Guid.CreateVersion7(),
        CompanyId = companyId,
        VesselId = vesselId,
        ModuleType = moduleType,
        RecordId = recordId,
        Topic = topic,
        Kind = kind,
        TargetId = targetId,
        Channel = channel,
        Body = body,
        ReplyToId = replyTo,
        Mentions = mentions,
        AuthorUserId = who?.UserId,
        AuthorName = who?.Name ?? "System",
        At = DateTime.UtcNow,
        OwnerStage = who is null ? Stage(db) : ChatRules.StageOf(who.Side),
    };

    /// <summary>Loads a message the person may act on, or says it is not theirs to see.</summary>
    private static async Task<ChatMessage> TargetAsync(ChandleryDbContext db, Guid companyId, Who who, Guid messageId)
    {
        var target = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId && m.CompanyId == companyId);
        if (target is null || !ChatRules.Visible(target, who.Viewer)) throw new InvalidOperationException("That message is not in a conversation you can see.");
        return target;
    }

    private async Task<ChatMessage> ActOnAsync(Guid companyId, Actor actor, Guid messageId, ChatKind kind, Func<ChatMessage, Who, string> body, Action<ChatMessage, Who> check)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var target = await TargetAsync(db, companyId, who, messageId);
        await RequireEnabledAsync(db, companyId, target.VesselId);
        check(target, who);
        var text = body(target, who);
        var action = New(db, companyId, who, target.ModuleType, target.RecordId, target.VesselId, target.Topic, kind, target.Channel, text, target.Id, null,
            kind == ChatKind.Edit ? ChatRules.ParseMentions(text, await PeopleAsync(db, companyId)) : "");
        db.ChatMessages.Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    /// <summary>Changes the author's own message, within ten minutes of writing it. The earlier text is kept and shown as "edited".</summary>
    public Task<ChatMessage> EditAsync(Guid companyId, Actor actor, Guid messageId, string newBody)
    {
        var text = (newBody ?? "").Trim();
        if (text.Length == 0) throw new InvalidOperationException("Write something first.");
        if (text.Length > ChatRules.MaxBodyLength) throw new InvalidOperationException($"A message is at most {ChatRules.MaxBodyLength} characters.");
        return ActOnAsync(companyId, actor, messageId, ChatKind.Edit, (_, _) => text, (target, who) =>
        {
            if (!ChatRules.CanEdit(target, who.UserId, DateTime.UtcNow)) throw new InvalidOperationException("You can change your own message for 10 minutes after writing it.");
        });
    }

    /// <summary>The author removes their message. It stays in the thread as "message removed".</summary>
    public Task<ChatMessage> DeleteAsync(Guid companyId, Actor actor, Guid messageId)
        => ActOnAsync(companyId, actor, messageId, ChatKind.Delete, (_, _) => "", (target, who) =>
        {
            if (!ChatRules.CanDelete(target, who.UserId)) throw new InvalidOperationException("You can remove only your own messages.");
        });

    /// <summary>A company administrator removes someone's message, with the reason. The reason and the original are kept for the audit.</summary>
    public Task<ChatMessage> RedactAsync(Guid companyId, Actor actor, Guid messageId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Give a reason for removing the message.");
        return ActOnAsync(companyId, actor, messageId, ChatKind.Redact, (_, _) => reason.Trim(), (target, who) =>
        {
            if (!ChatRules.CanRedact(target, who.Viewer)) throw new InvalidOperationException("Only a company administrator can remove someone else's message.");
        });
    }

    /// <summary>Marks a message as waiting for an answer. It stops waiting when someone replies to it.</summary>
    public Task<ChatMessage> NeedsReplyAsync(Guid companyId, Actor actor, Guid messageId)
        => ActOnAsync(companyId, actor, messageId, ChatKind.NeedsReply, (_, _) => "", (target, who) =>
        {
            if (target.Kind != ChatKind.Message) throw new InvalidOperationException("Only a message can wait for a reply.");
            if (who.Side == ChatSide.Supplier) throw new InvalidOperationException("This is not open to you.");
        });

    /// <summary>Marks a thread resolved, or reopens it. Done internally: an outside party does not see it.</summary>
    public async Task<ChatMessage> SetResolvedAsync(Guid companyId, Actor actor, string moduleType, Guid recordId, string topic, bool resolved)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (who.Side == ChatSide.Supplier) throw new InvalidOperationException("This is not open to you.");
        topic = (topic ?? "").Trim();
        var first = (await db.ChatMessages.AsNoTracking().Where(m => m.CompanyId == companyId && m.ModuleType == moduleType && m.RecordId == recordId && m.Topic == topic).ToListAsync())
            .Where(m => ChatRules.Visible(m, who.Viewer)).OrderBy(m => m.At).FirstOrDefault()
            ?? throw new InvalidOperationException("There is no conversation here yet.");
        await RequireEnabledAsync(db, companyId, first.VesselId);
        var marker = New(db, companyId, who, moduleType, recordId, first.VesselId, topic, resolved ? ChatKind.Resolve : ChatKind.Reopen, ChatChannel.Internal, "", null, null, "");
        db.ChatMessages.Add(marker);
        await db.SaveChangesAsync();
        return marker;
    }

    /// <summary>
    /// A line the platform writes about a record (a status change, an approval): not edited by anyone, written by the side where it happened. Nothing is written
    /// when conversations are not switched on for the vessel, so a module can call it without checking.
    /// </summary>
    public async Task<ChatMessage?> PostSystemAsync(Guid companyId, string moduleType, Guid recordId, Guid? vesselId, string text, ChatChannel channel = ChatChannel.Internal, string topic = "")
    {
        if (!ChatModules.All.Contains(moduleType) || string.IsNullOrWhiteSpace(text)) return null;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (vesselId is { } vessel && !await IsEnabledForVesselAsync(db, companyId, vessel)) return null;
        if (db.Node.IsVessel && vesselId is null) return null;
        var message = New(db, companyId, null, moduleType, recordId, vesselId, topic.Trim(), ChatKind.System, channel, text.Trim(), null, null, "");
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    // ---- what a screen needs to know ----

    /// <summary>True when a vessel's conversations are switched on (a record with no vessel always may).</summary>
    public async Task<bool> IsEnabledAsync(Guid companyId, Guid? vesselId)
    {
        if (vesselId is null) return true;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await IsEnabledForVesselAsync(db, companyId, vesselId.Value);
    }

    /// <summary>True when conversations are switched on for at least one vessel: what decides whether the menu offers the inbox.</summary>
    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Conversations)).Count > 0;
    }

    // ---- the inbox ----

    /// <summary>How far back an inbox looks: a thread with no message in this time is left out.</summary>
    public const int InboxDays = 180;

    /// <summary>
    /// The person's conversations, newest first. A thread is in the list when it has had a message in the last <see cref="InboxDays"/> days; what the
    /// person may not see is never counted or quoted.
    /// </summary>
    public async Task<List<ChatInboxRow>> InboxAsync(Guid companyId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var cutoff = DateTime.UtcNow.AddDays(-InboxDays);
        var recent = db.ChatMessages.Where(m => m.CompanyId == companyId && m.At >= cutoff).Select(m => m.RecordId).Distinct();
        var visible = (await db.ChatMessages.AsNoTracking().Where(m => m.CompanyId == companyId && recent.Contains(m.RecordId)).ToListAsync()).Where(m => ChatRules.Visible(m, who.Viewer)).ToList();
        var reads = await db.ChatReads.AsNoTracking().Where(r => r.UserId == who.UserId).ToListAsync();
        var threads = visible.GroupBy(m => (m.ModuleType, m.RecordId, m.Topic)).ToList();

        var vesselIds = threads.Select(g => g.First().VesselId).Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();
        var vessels = await db.Vessels.AsNoTracking().Where(v => vesselIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name);
        var records = await DescribeAsync(db, threads.Select(g => (g.Key.ModuleType, g.Key.RecordId)));

        var rows = new List<ChatInboxRow>();
        foreach (var g in threads)
        {
            var read = reads.FirstOrDefault(r => r.ModuleType == g.Key.ModuleType && r.RecordId == g.Key.RecordId && r.Topic == g.Key.Topic)?.LastReadAt;
            var s = ChatRules.Summarize(g, who.Viewer, read);
            if (s.Last is null) continue;
            var vessel = g.First().VesselId;
            var info = records.GetValueOrDefault((g.Key.ModuleType, g.Key.RecordId));
            var label = (info?.Label ?? g.Key.ModuleType) + (g.Key.Topic.Length > 0 ? $" · {g.Key.Topic}" : "");
            rows.Add(new ChatInboxRow(g.Key.ModuleType, g.Key.RecordId, g.Key.Topic, vessel, vessel is { } v ? vessels.GetValueOrDefault(v, "") : "", label, info?.Href, s.Last.Message.At,
                s.Last.Message.Kind == ChatKind.System ? "" : s.Last.Message.AuthorName, s.Last.Body, s.Unread, s.UnreadMentions, s.WaitingForMe, s.Resolved));
        }
        return rows.OrderByDescending(r => r.LastAt).ToList();
    }

    /// <summary>What the menu badge and the home page card show. Returns null when the person cannot take part in conversations.</summary>
    public async Task<ChatInboxCounts?> InboxCountsAsync(Guid companyId, Actor actor)
    {
        List<ChatInboxRow> rows;
        try { rows = await InboxAsync(companyId, actor); }
        catch (InvalidOperationException) { return null; }
        return new ChatInboxCounts(rows.Count(r => r.Unread > 0), rows.Count(r => r.UnreadMentions > 0), rows.Count(r => r.WaitingForMe > 0));
    }

    private sealed record RecordInfo(string Label, string? Href);

    /// <summary>The name and address of each record a conversation belongs to. A module appears here when its pages carry the conversation panel.</summary>
    private static async Task<Dictionary<(string ModuleType, Guid RecordId), RecordInfo>> DescribeAsync(ChandleryDbContext db, IEnumerable<(string ModuleType, Guid RecordId)> keys)
    {
        var result = new Dictionary<(string, Guid), RecordInfo>();
        var tasks = keys.Where(k => k.ModuleType == ChatModules.Worklist).Select(k => k.RecordId).Distinct().ToList();
        foreach (var t in await db.WorklistTasks.AsNoTracking().Where(t => tasks.Contains(t.Id)).Select(t => new { t.Id, t.Code, t.Title }).ToListAsync())
            result[(ChatModules.Worklist, t.Id)] = new RecordInfo($"{t.Code} · {t.Title}", $"/worklist/{t.Id}#conversation");
        return result;
    }

    /// <summary>Who this person is in a conversation (side, vessel, administrator), or null when they cannot take part.</summary>
    public async Task<ChatViewer?> ViewerAsync(Guid companyId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        try { return (await WhoAsync(db, companyId, actor)).Viewer; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Which of these messages are still waiting to be sent to the other side (shown as "not yet synced").</summary>
    public async Task<HashSet<Guid>> UnsyncedAsync(Guid companyId, IEnumerable<Guid> messageIds)
    {
        var ids = messageIds.ToList();
        if (ids.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await db.SyncDirties.AsNoTracking().Where(d => d.DocType == nameof(ChatMessage) && ids.Contains(d.DocId)).Select(d => d.DocId).ToListAsync()).ToHashSet();
    }

    // ---- reading ----

    private static async Task<List<ChatMessage>> LoadAsync(ChandleryDbContext db, Guid companyId, string moduleType, Guid recordId, ChatViewer viewer)
        => (await db.ChatMessages.AsNoTracking().Where(m => m.CompanyId == companyId && m.ModuleType == moduleType && m.RecordId == recordId).ToListAsync())
            .Where(m => ChatRules.Visible(m, viewer)).ToList();

    /// <summary>One thread of a record (the main thread when <paramref name="topic"/> is empty), as the person reads it.</summary>
    public async Task<ChatThreadView> ThreadAsync(Guid companyId, Actor actor, string moduleType, Guid recordId, string topic = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        topic = (topic ?? "").Trim();
        var thread = (await LoadAsync(db, companyId, moduleType, recordId, who.Viewer)).Where(m => m.Topic == topic).ToList();
        var read = await db.ChatReads.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == who.UserId && r.ModuleType == moduleType && r.RecordId == recordId && r.Topic == topic);
        return ChatRules.Replay(thread, who.Viewer, read?.LastReadAt);
    }

    /// <summary>The topics of a record that have messages the person can see, with the main thread (empty) first.</summary>
    public async Task<List<string>> TopicsAsync(Guid companyId, Actor actor, string moduleType, Guid recordId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        return (await LoadAsync(db, companyId, moduleType, recordId, who.Viewer)).Select(m => m.Topic).Distinct().OrderBy(t => t.Length == 0 ? 0 : 1).ThenBy(t => t).ToList();
    }

    /// <summary>The unread messages of a record, over all its topics: what a badge on the record shows.</summary>
    public async Task<int> UnreadAsync(Guid companyId, Actor actor, string moduleType, Guid recordId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var visible = await LoadAsync(db, companyId, moduleType, recordId, who.Viewer);
        var reads = await db.ChatReads.AsNoTracking().Where(r => r.UserId == who.UserId && r.ModuleType == moduleType && r.RecordId == recordId).ToListAsync();
        return visible.GroupBy(m => m.Topic).Sum(g => ChatRules.Replay(g, who.Viewer, reads.FirstOrDefault(r => r.Topic == g.Key)?.LastReadAt).Unread);
    }

    /// <summary>Notes that the person has read the thread up to its newest message, so what comes after counts as unread.</summary>
    public async Task MarkReadAsync(Guid companyId, Actor actor, string moduleType, Guid recordId, string topic = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        topic = (topic ?? "").Trim();
        var newest = (await LoadAsync(db, companyId, moduleType, recordId, who.Viewer)).Where(m => m.Topic == topic).Select(m => (DateTime?)m.At).Max();
        if (newest is null) return;
        var row = await db.ChatReads.FirstOrDefaultAsync(r => r.UserId == who.UserId && r.ModuleType == moduleType && r.RecordId == recordId && r.Topic == topic);
        if (row is null)
            db.ChatReads.Add(new ChatRead { Id = Guid.CreateVersion7(), CompanyId = companyId, UserId = who.UserId!.Value, ModuleType = moduleType, RecordId = recordId, Topic = topic, LastReadAt = newest.Value });
        else if (newest > row.LastReadAt) row.LastReadAt = newest.Value;
        else return;
        await db.SaveChangesAsync();
    }
}
