using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>What a person writes: the record the conversation belongs to, which vessel that record belongs to (null when it has none), and the text.</summary>
/// <summary>A message to write. <paramref name="Files"/> are photos or PDFs kept with it (a message may be only files).</summary>
public sealed record ChatPost(string ModuleType, Guid RecordId, Guid? VesselId, ChatChannel Channel, string Body, string Topic = "", Guid? ReplyToId = null, IReadOnlyList<PendingAttachment>? Files = null);

/// <summary>One thread in a person's inbox: which record, how new it is, and what was said last.</summary>
public sealed record ChatInboxRow(string ModuleType, Guid RecordId, string Topic, Guid? VesselId, string VesselName, string Label, string? Href, DateTime LastAt, string LastAuthor,
    string LastPreview, int Unread, int UnreadMentions, int WaitingForMe, bool Resolved);

/// <summary>The numbers behind the menu badge and the home page card: threads, not messages.</summary>
public sealed record ChatSearch(string Text, string? ModuleType = null, Guid? VesselId = null, DateTime? From = null, DateTime? To = null);

public sealed record ChatSearchHit(string ModuleType, Guid RecordId, string Topic, Guid MessageId, string Label, string? Href, string VesselName, string Author, DateTime At, string Snippet);

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

    /// <summary>Whether the shared channel (conversations with suppliers) is switched on for a vessel; a record with no vessel always may.</summary>
    public static async Task<bool> IsSharedEnabledAsync(ChandleryDbContext db, Guid companyId, Guid? vesselId)
        => vesselId is not { } vessel || (await IsEnabledForVesselAsync(db, companyId, vessel) && await ModuleGate.IsEnabledAsync(db, companyId, vessel, PolicyModules.ConversationsWithSuppliers));

    /// <summary>The same question for a screen: should it offer the shared channel for this vessel?</summary>
    public async Task<bool> IsSharedEnabledAsync(Guid companyId, Guid? vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await IsSharedEnabledAsync(db, companyId, vesselId);
    }

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
        if (user.IsSupplierRole)
        {
            var supplier = user.SupplierId is { } sid ? await db.Suppliers.AsNoTracking().IgnoreQueryFilters().Where(s => s.Id == sid).Select(s => s.Name).FirstOrDefaultAsync() : null;
            return new(new ChatViewer(ChatSide.Supplier, user.Id, SupplierId: user.SupplierId), supplier is null ? actor.Name : $"{actor.Name} ({supplier})", user.Id);
        }
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
        // A supplier is never taken at its word about which vessel a record belongs to.
        if (who.Side == ChatSide.Supplier)
            post = post with { VesselId = post.ModuleType == ChatModules.Rfq ? await db.Rfqs.AsNoTracking().Where(r => r.Id == post.RecordId).Select(r => r.VesselId).FirstOrDefaultAsync()
                : await db.PurchaseOrders.AsNoTracking().Where(p => p.Id == post.RecordId).Select(p => p.VesselId).FirstOrDefaultAsync() };
        await RequireEnabledAsync(db, companyId, post.VesselId);
        if ((await HiddenRecordsAsync(db, companyId, who.Viewer, post.ModuleType, [post.RecordId])).Count > 0) throw new InvalidOperationException("This record is not open to you.");
        if (post.Channel == ChatChannel.Shared && !await IsSharedEnabledAsync(db, companyId, post.VesselId)) throw new InvalidOperationException("Conversations with suppliers are not switched on for this vessel.");
        if (!ChatRules.CanPost(who.Side, post.Channel)) throw new InvalidOperationException(who.Side == ChatSide.Vessel ? "Onboard, messages are internal." : "This channel is not open to you.");
        if (who.Side == ChatSide.Vessel && post.VesselId != who.Viewer.VesselId) throw new InvalidOperationException("You are not signed on to this vessel.");

        if (post.ReplyToId is { } reply)
        {
            var original = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == reply && m.CompanyId == companyId);
            if (original is null || !ChatRules.Visible(original, who.Viewer) || original.ModuleType != post.ModuleType || original.RecordId != post.RecordId)
                throw new InvalidOperationException("The message you are replying to is not in this conversation.");
        }

        var body = post.Body.Trim();
        var message = New(db, companyId, who, post.ModuleType, post.RecordId, post.VesselId, TopicFor(who.Viewer, post.Topic), ChatKind.Message, post.Channel, body, null, post.ReplyToId,
            ChatRules.ParseMentions(body, who.Side == ChatSide.Supplier ? [] : await PeopleAsync(db, companyId)));
        db.ChatMessages.Add(message);
        if (post.Files is { Count: > 0 } files)
            FileAttachmentService.Add(db, companyId, post.VesselId, FileAttachmentService.ChatOwner, message.Id, post.Channel == ChatChannel.OfficeOnly, files, who.UserId, who.Name);
        await db.SaveChangesAsync();
        return message;
    }

    private static void Validate(ChatPost post, Who who)
    {
        if (!ChatModules.All.Contains(post.ModuleType)) throw new InvalidOperationException("Unknown kind of record.");
        if (post.RecordId == Guid.Empty) throw new InvalidOperationException("Name the record the conversation is about.");
        if (string.IsNullOrWhiteSpace(post.Body) && post.Files is not { Count: > 0 }) throw new InvalidOperationException("Write something first.");
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

    /// <summary>
    /// Adds a system line ("approved", "delivery recorded") to a conversation inside the caller's own save, so the line and the event it records stand or fall together.
    /// Does nothing where conversations are off for the vessel. The caller saves.
    /// </summary>
    public static async Task NoteAsync(ChandleryDbContext db, Guid companyId, string moduleType, Guid recordId, Guid? vesselId, string text, ChatChannel channel = ChatChannel.Internal)
    {
        if (!ChatModules.All.Contains(moduleType) || string.IsNullOrWhiteSpace(text)) return;
        if (vesselId is { } vessel) { if (!await IsEnabledForVesselAsync(db, companyId, vessel)) return; }
        else if (db.Node.IsVessel) return;
        db.ChatMessages.Add(New(db, companyId, null, moduleType, recordId, vesselId, "", ChatKind.System, channel, text.Trim(), null, null, ""));
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
        if (who.Side == ChatSide.Supplier) throw new InvalidOperationException("Suppliers have no inbox.");
        var cutoff = DateTime.UtcNow.AddDays(-InboxDays);
        var recent = db.ChatMessages.Where(m => m.CompanyId == companyId && m.At >= cutoff).Select(m => m.RecordId).Distinct();
        var visible = (await db.ChatMessages.AsNoTracking().Where(m => m.CompanyId == companyId && recent.Contains(m.RecordId)).ToListAsync()).Where(m => ChatRules.Visible(m, who.Viewer)).ToList();
        var hiddenIncidents = await HiddenRecordsAsync(db, companyId, who.Viewer, ChatModules.Incident, visible.Where(m => m.ModuleType == ChatModules.Incident).Select(m => m.RecordId));
        visible = visible.Where(m => !(m.ModuleType == ChatModules.Incident && hiddenIncidents.Contains(m.RecordId))).ToList();
        var reads = await db.ChatReads.AsNoTracking().Where(r => r.UserId == who.UserId).ToListAsync();
        var threads = visible.GroupBy(m => (m.ModuleType, m.RecordId, m.Topic)).ToList();

        var vesselIds = threads.Select(g => g.First().VesselId).Where(v => v is not null).Select(v => v!.Value).Distinct().ToList();
        var vessels = await db.Vessels.AsNoTracking().Where(v => vesselIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name);
        var records = await DescribeAsync(db, who.Side, threads.Select(g => (g.Key.ModuleType, g.Key.RecordId)));
        var supplierIds = threads.Select(g => ChatRules.SupplierOfTopic(g.Key.Topic)).OfType<Guid>().Distinct().ToList();
        var supplierNames = supplierIds.Count == 0 ? [] : await db.Suppliers.AsNoTracking().IgnoreQueryFilters().Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);

        var rows = new List<ChatInboxRow>();
        foreach (var g in threads)
        {
            var read = reads.FirstOrDefault(r => r.ModuleType == g.Key.ModuleType && r.RecordId == g.Key.RecordId && r.Topic == g.Key.Topic)?.LastReadAt;
            var s = ChatRules.Summarize(g, who.Viewer, read);
            if (s.Last is null) continue;
            var vessel = g.First().VesselId;
            var info = records.GetValueOrDefault((g.Key.ModuleType, g.Key.RecordId));
            var label = (info?.Label ?? g.Key.ModuleType) + (g.Key.Topic.Length == 0 ? "" : ChatRules.SupplierOfTopic(g.Key.Topic) is { } supplierId ? $" · with {supplierNames.GetValueOrDefault(supplierId, "the supplier")}" : $" · {g.Key.Topic}");
            rows.Add(new ChatInboxRow(g.Key.ModuleType, g.Key.RecordId, g.Key.Topic, vessel, vessel is { } v ? vessels.GetValueOrDefault(v, "") : "", label, info?.Href, s.Last.Message.At,
                s.Last.Message.Kind == ChatKind.System ? "" : s.Last.Message.AuthorName, s.Last.Body.Length == 0 ? "Sent an attachment" : s.Last.Body, s.Unread, s.UnreadMentions, s.WaitingForMe, s.Resolved));
        }
        return rows.OrderByDescending(r => r.LastAt).ToList();
    }

    /// <summary>
    /// Searches the words of the messages the person may read, across records: edits and removals count as the thread now reads, and nothing a person may not see is searched.
    /// The newest matches come first. A record kind, a vessel and a date range narrow it.
    /// </summary>
    public async Task<List<ChatSearchHit>> SearchAsync(Guid companyId, Actor actor, ChatSearch filter, int max = 100)
    {
        var text = (filter.Text ?? "").Trim();
        if (text.Length < 2) return [];
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (who.Side == ChatSide.Supplier) throw new InvalidOperationException("Suppliers have no inbox.");

        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var matched = db.ChatMessages.Where(m => m.CompanyId == companyId && (filter.ModuleType == null || m.ModuleType == filter.ModuleType) && EF.Functions.ILike(m.Body, pattern)).Select(m => m.RecordId).Distinct();
        var all = (await db.ChatMessages.AsNoTracking().Where(m => m.CompanyId == companyId && matched.Contains(m.RecordId)).ToListAsync()).Where(m => ChatRules.Visible(m, who.Viewer)).ToList();
        var hidden = await HiddenRecordsAsync(db, companyId, who.Viewer, ChatModules.Incident, all.Where(m => m.ModuleType == ChatModules.Incident).Select(m => m.RecordId));
        var threads = all.Where(m => !(m.ModuleType == ChatModules.Incident && hidden.Contains(m.RecordId))).GroupBy(m => (m.ModuleType, m.RecordId, m.Topic)).ToList();

        var hits = new List<(ChatItem Item, string ModuleType, Guid RecordId, string Topic)>();
        foreach (var g in threads)
            foreach (var item in ChatRules.Replay(g, who.Viewer).Items)
            {
                if (item.Removed || item.Message.Kind != ChatKind.Message) continue;
                if (item.Body.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (filter.VesselId is { } vessel && item.Message.VesselId != vessel) continue;
                if (filter.From is { } from && item.Message.At < from) continue;
                if (filter.To is { } to && item.Message.At >= to.AddDays(1)) continue;
                hits.Add((item, g.Key.ModuleType, g.Key.RecordId, g.Key.Topic));
            }
        var top = hits.OrderByDescending(h => h.Item.Message.At).Take(max).ToList();

        var records = await DescribeAsync(db, who.Side, top.Select(h => (h.ModuleType, h.RecordId)));
        var vesselIds = top.Select(h => h.Item.Message.VesselId).OfType<Guid>().Distinct().ToList();
        var vessels = await db.Vessels.AsNoTracking().Where(v => vesselIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name);
        return top.Select(h =>
        {
            var info = records.GetValueOrDefault((h.ModuleType, h.RecordId));
            return new ChatSearchHit(h.ModuleType, h.RecordId, h.Topic, h.Item.Message.Id, info?.Label ?? ChatModules.Label(h.ModuleType), info?.Href,
                h.Item.Message.VesselId is { } v ? vessels.GetValueOrDefault(v, "") : "", h.Item.Message.AuthorName, h.Item.Message.At, Snippet(h.Item.Body, text));
        }).ToList();
    }

    /// <summary>
    /// For a supplier's lists: how many messages from the buyer are new to it on each of its own RFQs or orders. It is how a supplier learns a reply has come,
    /// since it has no inbox. Only the buyer's shared messages in the supplier's own thread count, never its own.
    /// </summary>
    public async Task<Dictionary<Guid, int>> SupplierUnreadAsync(Actor actor, Guid supplierId, string moduleType, IEnumerable<Guid> recordIds)
    {
        var ids = recordIds.Distinct().ToList();
        if (actor.UserId is not { } userId || ids.Count == 0 || (moduleType != ChatModules.Rfq && moduleType != ChatModules.PurchaseOrder)) return [];
        await using var db = await factory.CreateDbContextAsync();
        var topic = ChatRules.SupplierTopic(supplierId);
        var messages = await db.ChatMessages.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.ModuleType == moduleType && ids.Contains(m.RecordId) && m.Topic == topic && m.Channel == ChatChannel.Shared && m.Kind == ChatKind.Message && m.AuthorUserId != userId)
            .Select(m => new { m.RecordId, m.At }).ToListAsync();
        var reads = (await db.ChatReads.AsNoTracking().IgnoreQueryFilters().Where(r => r.UserId == userId && r.ModuleType == moduleType && r.Topic == topic && ids.Contains(r.RecordId))
            .Select(r => new { r.RecordId, r.LastReadAt }).ToListAsync()).ToDictionary(r => r.RecordId, r => r.LastReadAt);
        return messages.GroupBy(m => m.RecordId).Select(g => (g.Key, Count: g.Count(m => !reads.TryGetValue(g.Key, out var seen) || m.At > seen))).Where(x => x.Count > 0).ToDictionary(x => x.Key, x => x.Count);
    }

    /// <summary>
    /// A file attached to a message, if this person may read that message and it has not been removed: the same rules as the words, so a photo on an office-only message,
    /// on another supplier's thread or on an incident one may not read is never handed over. Null when there is nothing this person may open.
    /// </summary>
    public async Task<FileAttachment?> OpenFileAsync(Actor actor, Guid fileId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var file = await db.FileAttachments.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == fileId && f.OwnerType == FileAttachmentService.ChatOwner);
        if (file is null) return null;
        db.CurrentCompanyId = file.CompanyId;
        ChatViewer viewer;
        try { viewer = (await WhoAsync(db, file.CompanyId, actor)).Viewer; }
        catch (InvalidOperationException) { return null; }
        var message = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == file.OwnerId);
        if (message is null || !ChatRules.Visible(message, viewer)) return null;
        var thread = (await LoadAsync(db, file.CompanyId, message.ModuleType, message.RecordId, viewer)).Where(m => m.Topic == message.Topic).ToList();
        var item = ChatRules.Replay(thread, viewer).Items.FirstOrDefault(i => i.Message.Id == message.Id);
        return item is null || item.Removed ? null : file;
    }

    /// <summary>What a record is called in a conversation (its number and title), or the kind of record when it is not named.</summary>
    public async Task<string> LabelAsync(Guid companyId, Actor actor, string moduleType, Guid recordId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        return (await DescribeAsync(db, who.Side, [(moduleType, recordId)])).GetValueOrDefault((moduleType, recordId))?.Label ?? ChatModules.Label(moduleType);
    }

    /// <summary>What a thread is called: nothing for the main thread, "With {supplier}" for a supplier thread, otherwise its topic.</summary>
    public async Task<string> TopicTitleAsync(string topic)
    {
        if (topic.Length == 0) return "";
        if (ChatRules.SupplierOfTopic(topic) is not { } supplierId) return topic;
        await using var db = await factory.CreateDbContextAsync();
        return "With " + (await db.Suppliers.AsNoTracking().IgnoreQueryFilters().Where(s => s.Id == supplierId).Select(s => s.Name).FirstOrDefaultAsync() ?? "the supplier");
    }

    /// <summary>The words around the first match, so a result shows why it matched.</summary>
    public static string Snippet(string body, string text)
    {
        var at = Math.Max(0, body.IndexOf(text, StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, at - 50);
        var end = Math.Min(body.Length, at + text.Length + 80);
        return (start > 0 ? "…" : "") + body[start..end].Replace("\n", " ") + (end < body.Length ? "…" : "");
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

    /// <summary>The name and address of each record a conversation belongs to, as the viewer's side opens it. A module appears here when its pages carry the conversation panel.</summary>
    private static async Task<Dictionary<(string ModuleType, Guid RecordId), RecordInfo>> DescribeAsync(ChandleryDbContext db, ChatSide side, IEnumerable<(string ModuleType, Guid RecordId)> keys)
    {
        var result = new Dictionary<(string, Guid), RecordInfo>();
        var wanted = keys.ToList();
        List<Guid> Of(string module) => wanted.Where(k => k.ModuleType == module).Select(k => k.RecordId).Distinct().ToList();
        string? Href(string? office, string? vessel) => side switch { ChatSide.Office => office, ChatSide.Vessel => vessel, _ => null };

        var tasks = Of(ChatModules.Worklist);
        foreach (var t in await db.WorklistTasks.AsNoTracking().Where(t => tasks.Contains(t.Id)).Select(t => new { t.Id, t.Code, t.Title }).ToListAsync())
            result[(ChatModules.Worklist, t.Id)] = new RecordInfo($"{t.Code} · {t.Title}", $"/worklist/{t.Id}#conversation");

        var orders = Of(ChatModules.PurchaseOrder);
        foreach (var o in await db.PurchaseOrders.AsNoTracking().Where(o => orders.Contains(o.Id)).Select(o => new { o.Id, o.PoNumber }).ToListAsync())
            result[(ChatModules.PurchaseOrder, o.Id)] = new RecordInfo($"Order {o.PoNumber}", Href($"/company/purchase-orders/{o.Id}#conversation", $"/vessel/orders/{o.Id}#conversation"));

        var requisitions = Of(ChatModules.Requisition);
        foreach (var r in await db.Requisitions.AsNoTracking().Where(r => requisitions.Contains(r.Id)).Select(r => new { r.Id, r.ReqnNumber }).ToListAsync())
            result[(ChatModules.Requisition, r.Id)] = new RecordInfo($"Requisition {r.ReqnNumber}", Href($"/company/requisitions/{r.Id}#conversation", null));

        var rfqs = Of(ChatModules.Rfq);
        foreach (var r in await db.Rfqs.AsNoTracking().Where(r => rfqs.Contains(r.Id)).Select(r => new { r.Id, r.RfqNumber }).ToListAsync())
            result[(ChatModules.Rfq, r.Id)] = new RecordInfo($"RFQ {r.RfqNumber}", Href($"/company/rfqs/{r.Id}#conversation", $"/vessel/rfqs/{r.Id}#conversation"));
        var jobs = Of(ChatModules.PmsJob);
        foreach (var j in await db.PlannedJobs.AsNoTracking().Where(j => jobs.Contains(j.Id)).Select(j => new { j.Id, j.Code, j.Title }).ToListAsync())
            result[(ChatModules.PmsJob, j.Id)] = new RecordInfo($"{j.Code} · {j.Title}", Href($"/pms/jobs/{j.Id}#conversation", $"/pms/jobs/{j.Id}#conversation"));
        var inspections = Of(ChatModules.TankInspection);
        foreach (var x in await db.TankInspections.AsNoTracking().Where(x => inspections.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.TankId }).ToListAsync())
        {
            var tank = await db.Tanks.AsNoTracking().Where(t => t.Id == x.TankId).Select(t => t.Name).FirstOrDefaultAsync();
            result[(ChatModules.TankInspection, x.Id)] = new RecordInfo($"Tank inspection {x.Code}" + (tank is null ? "" : $" · {tank}"), Href($"/tanks/inspections/{x.Id}#conversation", $"/tanks/inspections/{x.Id}#conversation"));
        }

        var risks = Of(ChatModules.RiskAssessment);
        foreach (var x in await db.RiskAssessments.AsNoTracking().Where(x => risks.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.Version, x.JobDescription }).ToListAsync())
            result[(ChatModules.RiskAssessment, x.Id)] = new RecordInfo($"Risk assessment {x.Code} v{x.Version} · {(x.JobDescription.Length <= 50 ? x.JobDescription : x.JobDescription[..50] + "…")}", Href($"/risk/{x.Id}#conversation", $"/risk/{x.Id}#conversation"));

        var projects = Of(ChatModules.Project);
        foreach (var x in await db.Projects.AsNoTracking().Where(x => projects.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.Title }).ToListAsync())
            result[(ChatModules.Project, x.Id)] = new RecordInfo($"Project {x.Code} · {x.Title}", Href($"/projects/{x.Id}#conversation", $"/projects/{x.Id}#conversation"));

        var incidents = Of(ChatModules.Incident);
        foreach (var x in await db.Incidents.AsNoTracking().Where(x => incidents.Contains(x.Id)).Select(x => new { x.Id, x.Code }).ToListAsync())
            result[(ChatModules.Incident, x.Id)] = new RecordInfo($"Incident {x.Code}", Href($"/incidents/{x.Id}#conversation", $"/incidents/{x.Id}#conversation"));
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
    {
        if ((await HiddenRecordsAsync(db, companyId, viewer, moduleType, [recordId])).Count > 0) return [];
        return (await db.ChatMessages.AsNoTracking().Where(m => m.CompanyId == companyId && m.ModuleType == moduleType && m.RecordId == recordId).ToListAsync())
            .Where(m => ChatRules.Visible(m, viewer)).ToList();
    }

    /// <summary>
    /// The records of one kind whose conversation this person may not read. An incident is confidential: the office sees every one; onboard only the Master and the reporter do.
    /// A supplier sees only the conversations on an RFQ it was invited to and on an approved order that is its own, and nothing else.
    /// </summary>
    private static async Task<HashSet<Guid>> HiddenRecordsAsync(ChandleryDbContext db, Guid companyId, ChatViewer viewer, string moduleType, IEnumerable<Guid> recordIds)
    {
        var ids = recordIds.Distinct().ToList();
        if (ids.Count == 0 || viewer.Side == ChatSide.Office) return [];

        if (viewer.Side == ChatSide.Supplier)
        {
            if (viewer.SupplierId is not { } supplier) return ids.ToHashSet();
            var own = moduleType == ChatModules.Rfq
                ? await db.Rfqs.AsNoTracking().Where(r => r.CompanyId == companyId && ids.Contains(r.Id) && r.InvitedSupplierIds.Contains(supplier)).Select(r => new { r.Id, r.VesselId }).ToListAsync()
                : moduleType == ChatModules.PurchaseOrder
                    ? await db.PurchaseOrders.AsNoTracking().Where(p => p.CompanyId == companyId && ids.Contains(p.Id) && p.SupplierId == supplier && p.Status == PoStatus.Approved).Select(p => new { p.Id, p.VesselId }).ToListAsync()
                    : [];
            var open = new HashSet<Guid>();
            foreach (var record in own) if (await IsSharedEnabledAsync(db, companyId, record.VesselId)) open.Add(record.Id);
            return ids.Where(i => !open.Contains(i)).ToHashSet();
        }

        if (moduleType != ChatModules.Incident) return [];
        var allowed = await db.Incidents.AsNoTracking().Where(i => i.CompanyId == companyId && ids.Contains(i.Id) && i.VesselId == viewer.VesselId).Select(i => new { i.Id, i.ReporterCrewId }).ToListAsync();
        if (viewer.IsMaster) return ids.Except(allowed.Select(a => a.Id)).ToHashSet();
        var reporters = await db.CrewMembers.AsNoTracking().Where(c => c.UserId == viewer.UserId).Select(c => c.Id).ToListAsync();
        return ids.Except(allowed.Where(a => a.ReporterCrewId is { } r && reporters.Contains(r)).Select(a => a.Id)).ToHashSet();
    }

    /// <summary>A supplier always reads and writes its own thread; everyone else names the topic they mean.</summary>
    private static string TopicFor(ChatViewer viewer, string? topic)
        => viewer.Side == ChatSide.Supplier ? ChatRules.SupplierTopic(viewer.SupplierId) : (topic ?? "").Trim();

    /// <summary>One thread of a record (the main thread when <paramref name="topic"/> is empty), as the person reads it.</summary>
    public async Task<ChatThreadView> ThreadAsync(Guid companyId, Actor actor, string moduleType, Guid recordId, string topic = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        topic = TopicFor(who.Viewer, topic);
        var thread = (await LoadAsync(db, companyId, moduleType, recordId, who.Viewer)).Where(m => m.Topic == topic).ToList();
        var read = await db.ChatReads.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == who.UserId && r.ModuleType == moduleType && r.RecordId == recordId && r.Topic == topic);
        var view = ChatRules.Replay(thread, who.Viewer, read?.LastReadAt);
        var authors = view.Items.Select(i => i.Message.AuthorUserId).OfType<Guid>().Distinct().ToList();
        var fromSuppliers = authors.Count == 0 ? [] : (await db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => authors.Contains(u.Id) && u.SupplierId != null).Select(u => u.Id).ToListAsync()).ToHashSet();
        var files = await FileAttachmentService.ListAsync(db, FileAttachmentService.ChatOwner, view.Items.Where(i => !i.Removed).Select(i => i.Message.Id));
        return view with { Items = view.Items.Select(i => i with { FromSupplier = i.Message.AuthorUserId is { } a && fromSuppliers.Contains(a), Files = files.GetValueOrDefault(i.Message.Id) }).ToList() };
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
        topic = TopicFor(who.Viewer, topic);
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
