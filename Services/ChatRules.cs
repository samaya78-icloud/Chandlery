using System.Text.Json.Nodes;
using Chandlery.Models;

namespace Chandlery.Services;

public enum ChatSide { Office, Vessel, Supplier }

/// <summary>Who is looking at (or writing in) a conversation. <see cref="VesselId"/> is the vessel of a vessel viewer; <see cref="IsAdmin"/> is a company administrator.</summary>
public sealed record ChatViewer(ChatSide Side, Guid? UserId, Guid? VesselId = null, bool IsAdmin = false);

/// <summary>A person who can be mentioned.</summary>
public sealed record ChatPerson(Guid UserId, string Name);

/// <summary>One line of a thread as it reads now, after edits and removals.</summary>
public sealed record ChatItem(ChatMessage Message, string Body, bool Edited, bool Removed, bool RemovedByAdmin, string RedactReason, bool NeedsReply, bool Mine, IReadOnlyList<string> History);

public sealed record ChatThreadView(IReadOnlyList<ChatItem> Items, bool Resolved, DateTime? ResolvedAt, int Unread, int NeedingReply);

/// <summary>
/// The rules of a conversation (spec 10), pure: who may see and write a message, which edits and removals count, how a thread reads, what is unread
/// and who is mentioned. The screens, the sync filter and the tests all use these, so they cannot disagree.
/// </summary>
public static class ChatRules
{
    /// <summary>An author may change their own message for this long after writing it.</summary>
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(10);

    public const int MaxBodyLength = 4000;

    // ---- who may see and write ----

    /// <summary>
    /// The office sees everything. A vessel sees its own vessel's messages except those marked office only. An outside party sees only shared messages.
    /// A message with no vessel is never seen onboard.
    /// </summary>
    public static bool Visible(ChatMessage message, ChatViewer viewer) => viewer.Side switch
    {
        ChatSide.Office => true,
        ChatSide.Vessel => message.Channel != ChatChannel.OfficeOnly && message.VesselId is not null && message.VesselId == viewer.VesselId,
        ChatSide.Supplier => message.Channel == ChatChannel.Shared,
        _ => false,
    };

    /// <summary>The office may write on any channel; the vessel's crew only internally; an outside party only on the shared channel.</summary>
    public static bool CanPost(ChatSide side, ChatChannel channel) => side switch
    {
        ChatSide.Office => true,
        ChatSide.Vessel => channel == ChatChannel.Internal,
        ChatSide.Supplier => channel == ChatChannel.Shared,
        _ => false,
    };

    /// <summary>The channels a side may write on, in the order a composer offers them.</summary>
    public static IReadOnlyList<ChatChannel> ChannelsFor(ChatSide side)
        => Enum.GetValues<ChatChannel>().Where(c => CanPost(side, c)).ToList();

    public static string ChannelLabel(ChatChannel channel) => channel switch
    {
        ChatChannel.Internal => "Internal (company only)",
        ChatChannel.Shared => "Shared with the supplier",
        ChatChannel.OfficeOnly => "Office only (hidden from the vessel)",
        _ => channel.ToString(),
    };

    public static string StageOf(ChatSide side) => side == ChatSide.Vessel ? SyncStages.Vessel : SyncStages.Office;

    /// <summary>The vessel a message is sent to: none for a message that must stay ashore (office only, or a record without a vessel).</summary>
    public static Guid? SyncVessel(Guid? vesselId, ChatChannel channel) => channel == ChatChannel.OfficeOnly ? null : vesselId;

    /// <summary>Reads the channel from a synced document (enums travel as text).</summary>
    public static ChatChannel ChannelOf(JsonObject payload)
    {
        var node = payload[nameof(ChatMessage.Channel)];
        if (node is null) return ChatChannel.Internal;
        var text = node.ToString();
        return Enum.TryParse<ChatChannel>(text, ignoreCase: true, out var channel) ? channel : ChatChannel.OfficeOnly;   // anything unreadable is kept at home
    }

    public static bool CanEdit(ChatMessage message, Guid? userId, DateTime now)
        => message.Kind == ChatKind.Message && userId is not null && message.AuthorUserId == userId && now - message.At <= EditWindow;

    public static bool CanDelete(ChatMessage message, Guid? userId)
        => message.Kind == ChatKind.Message && userId is not null && message.AuthorUserId == userId;

    public static bool CanRedact(ChatMessage message, ChatViewer viewer) => message.Kind is ChatKind.Message && viewer.Side == ChatSide.Office && viewer.IsAdmin;

    // ---- mentions ----

    private static readonly string[] Roles = ["office", "master", "crew"];

    /// <summary>
    /// The tokens for the people and groups a text mentions: <c>@Name</c> (the longest matching name wins), <c>@office</c>, <c>@master</c>, <c>@crew</c>.
    /// Matching ignores case and needs the name to end at a word boundary, so "@Anna" does not mention "Annabel".
    /// </summary>
    public static string ParseMentions(string? body, IReadOnlyList<ChatPerson> people)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var found = new List<string>();
        var byLength = people.Where(p => !string.IsNullOrWhiteSpace(p.Name)).OrderByDescending(p => p.Name.Length).ToList();
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '@' || (i > 0 && char.IsLetterOrDigit(body[i - 1]))) continue;
            var rest = body[(i + 1)..];
            var token = byLength.FirstOrDefault(p => StartsWithWord(rest, p.Name)) is { } person ? $"u:{person.UserId}"
                : Roles.FirstOrDefault(r => StartsWithWord(rest, r)) is { } role ? $"r:{role}" : null;
            if (token is not null && !found.Contains(token)) found.Add(token);
        }
        return string.Join(';', found);
    }

    private static bool StartsWithWord(string text, string word)
        => text.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (text.Length == word.Length || !char.IsLetterOrDigit(text[word.Length]));

    private static IEnumerable<string> Tokens(string? mentions) => (mentions ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool MentionsOffice(string? mentions) => Tokens(mentions).Contains("r:office");
    public static bool MentionsUser(string? mentions, Guid userId) => Tokens(mentions).Contains($"u:{userId}");
    public static bool MentionsRole(string? mentions, string role) => Tokens(mentions).Contains($"r:{role}");

    // ---- how a thread reads ----

    /// <summary>
    /// The thread as a viewer reads it. <paramref name="thread"/> holds every record of one thread that the viewer holds; what the viewer may not see is dropped.
    /// Messages are in send order (time, then id). An edit counts only when its author wrote the message and did so inside the edit window; a removal only
    /// from the author or from the office; an action whose message has not arrived yet is ignored until it does. Unread are other people's messages after
    /// <paramref name="lastReadAt"/>.
    /// </summary>
    public static ChatThreadView Replay(IEnumerable<ChatMessage> thread, ChatViewer viewer, DateTime? lastReadAt = null)
    {
        var ordered = thread.Where(m => Visible(m, viewer)).OrderBy(m => m.At).ThenBy(m => m.Id).ToList();
        var content = ordered.Where(m => m.Kind is ChatKind.Message or ChatKind.System).ToList();
        var byId = content.ToDictionary(m => m.Id);
        var body = content.ToDictionary(m => m.Id, m => m.Body);
        var history = content.ToDictionary(m => m.Id, _ => new List<string>());
        var removed = new HashSet<Guid>();
        var redacted = new Dictionary<Guid, string>();
        var waiting = new HashSet<Guid>();
        bool resolved = false;
        DateTime? resolvedAt = null;

        foreach (var action in ordered.Where(m => m.Kind is not (ChatKind.Message or ChatKind.System)))
        {
            if (action.Kind is ChatKind.Resolve) { resolved = true; resolvedAt = action.At; continue; }
            if (action.Kind is ChatKind.Reopen) { resolved = false; resolvedAt = null; continue; }
            if (action.TargetId is not { } id || !byId.TryGetValue(id, out var target) || target.Kind != ChatKind.Message) continue;
            switch (action.Kind)
            {
                case ChatKind.Edit when !removed.Contains(id) && action.AuthorUserId is not null && action.AuthorUserId == target.AuthorUserId && action.At >= target.At && action.At - target.At <= EditWindow:
                    history[id].Add(body[id]);
                    body[id] = action.Body;
                    break;
                case ChatKind.Delete when action.AuthorUserId is not null && action.AuthorUserId == target.AuthorUserId:
                    removed.Add(id);
                    break;
                case ChatKind.Redact when action.OwnerStage == SyncStages.Office:
                    removed.Add(id);
                    redacted[id] = action.Body;
                    break;
                case ChatKind.NeedsReply:
                    waiting.Add(id);
                    break;
            }
        }

        // A marked message is answered by a later reply to it, and is no longer waiting once it is removed or the thread is resolved.
        foreach (var id in waiting.ToList())
            if (resolved || removed.Contains(id) || content.Any(m => m.Kind == ChatKind.Message && m.ReplyToId == id && !removed.Contains(m.Id))) waiting.Remove(id);

        var items = content.Select(m => new ChatItem(m, removed.Contains(m.Id) ? "" : body[m.Id], history[m.Id].Count > 0, removed.Contains(m.Id), redacted.ContainsKey(m.Id),
            redacted.GetValueOrDefault(m.Id, ""), waiting.Contains(m.Id), viewer.UserId is not null && m.AuthorUserId == viewer.UserId, history[m.Id])).ToList();

        var unread = content.Count(m => m.Kind == ChatKind.Message && !removed.Contains(m.Id) && (viewer.UserId is null || m.AuthorUserId != viewer.UserId) && (lastReadAt is null || m.At > lastReadAt));
        return new ChatThreadView(items, resolved, resolvedAt, unread, waiting.Count);
    }
}
