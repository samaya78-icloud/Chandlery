namespace Chandlery.Models;

/// <summary>Who may see a message (spec 10 section 2).</summary>
public enum ChatChannel
{
    /// <summary>Company users only: the office and the vessel's crew.</summary>
    Internal,

    /// <summary>Also visible to an outside party the record is shared with (a supplier on an RFQ or a PO).</summary>
    Shared,

    /// <summary>The office only. Never sent to a vessel.</summary>
    OfficeOnly,
}

/// <summary>What a chat record is. Only <see cref="Message"/> and <see cref="System"/> are read as conversation; the others act on a message or on the thread.</summary>
public enum ChatKind
{
    Message,

    /// <summary>A line the platform writes about the record (a status change, an approval, a delivery). Cannot be edited by anyone.</summary>
    System,

    /// <summary>A new text for the message in <see cref="ChatMessage.TargetId"/>, by its author, within the edit window.</summary>
    Edit,

    /// <summary>The author removes their message. It stays as "message removed".</summary>
    Delete,

    /// <summary>An administrator removes a message, with the reason in <see cref="ChatMessage.Body"/>.</summary>
    Redact,

    /// <summary>The thread (record and topic) is marked resolved.</summary>
    Resolve,
    Reopen,

    /// <summary>The message in <see cref="ChatMessage.TargetId"/> is marked as waiting for an answer.</summary>
    NeedsReply,
}

/// <summary>
/// One line of a conversation, written once and never changed. A thread is all the messages that share a record (<see cref="ModuleType"/>, <see cref="RecordId"/>)
/// and a <see cref="Topic"/>; edits, removals, resolving and reopening are further messages that point at the one they act on, and
/// <c>ChatRules.Replay</c> works out how the thread reads. The side of the author owns the message, so chat never conflicts.
/// There is deliberately no foreign key from <see cref="TargetId"/> or <see cref="ReplyToId"/>: an edit may reach a node before the message it edits.
/// </summary>
public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>The vessel the record belongs to, copied when the message is written. Null for a record with no vessel; such messages never leave the office.</summary>
    public Guid? VesselId { get; set; }

    /// <summary>The kind of record (see <c>ChatModules</c>) and the record itself.</summary>
    public string ModuleType { get; set; } = "";
    public Guid RecordId { get; set; }

    /// <summary>Empty for the record's main thread.</summary>
    public string Topic { get; set; } = "";
    public ChatKind Kind { get; set; }

    /// <summary>The message an edit, removal or marker acts on.</summary>
    public Guid? TargetId { get; set; }
    public ChatChannel Channel { get; set; }
    public string Body { get; set; } = "";
    public Guid? ReplyToId { get; set; }

    /// <summary>Who is mentioned, as tokens separated by semicolons: <c>u:{userId}</c> for a person, <c>r:office</c>, <c>r:master</c> or <c>r:crew</c> for a group.</summary>
    public string Mentions { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = "";
    public DateTime At { get; set; }

    /// <summary>The side the author wrote from (see <see cref="SyncStages"/>).</summary>
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>
/// How far one person has read one thread. Personal and local to each installation: it is never sent, so each side counts its own unread.
/// </summary>
public class ChatRead
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public string ModuleType { get; set; } = "";
    public Guid RecordId { get; set; }
    public string Topic { get; set; } = "";
    public DateTime LastReadAt { get; set; }
}

/// <summary>The kinds of record a conversation can belong to. Modules add theirs here when they get a panel.</summary>
public static class ChatModules
{
    public const string Requisition = "Requisition";
    public const string Rfq = "Rfq";
    public const string PurchaseOrder = "PurchaseOrder";
    public const string Delivery = "Delivery";
    public const string Worklist = "Worklist";
    public const string PmsJob = "PmsJob";
    public const string TankInspection = "TankInspection";
    public const string RiskAssessment = "RiskAssessment";
    public const string Project = "Project";
    public const string Incident = "Incident";

    public static readonly string[] All = [Requisition, Rfq, PurchaseOrder, Delivery, Worklist, PmsJob, TankInspection, RiskAssessment, Project, Incident];
}
