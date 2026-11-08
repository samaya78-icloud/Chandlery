namespace Chandlery.Models;

public enum WorklistListKind { Source, Nature, Category }

/// <summary>
/// One entry of a configurable list: where a task came from (audit, class, port state control...), what kind of task it is
/// (planned, emergency, deferred...), or its category (a tree). Kept by the office and sent to every vessel.
/// </summary>
public class WorklistListItem
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public WorklistListKind Kind { get; set; }
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Sources only: tasks from this source are not closed until the office has verified them (class and port state control items, for example).</summary>
    public bool RequiresVerification { get; set; }
}

public enum WorklistPriority { Normal, Urgent }

public enum WorklistStatus { Open, InProgress, Waiting, Completed, Cancelled }

public enum WorklistEntryType
{
    /// <summary>The first entry of every task: carries its starting title, description, priority, dates and assignee.</summary>
    Created,
    Comment,
    Status,
    Percent,
    Priority,

    /// <summary>The target (end) date moved, with a reason.</summary>
    Target,
    Assignee,

    /// <summary>The title or description was reworded.</summary>
    Details,

    /// <summary>Deferred: a reason and a new target date.</summary>
    Defer,

    /// <summary>Marked completed, with a note about what was done.</summary>
    Completed,

    /// <summary>The office confirms the completion.</summary>
    Verified,

    /// <summary>Linked to something else (a requisition, an order...).</summary>
    Link,
}

/// <summary>
/// A task a vessel owes. Created once and never changed: it carries only its identity. Everything else about it (title, priority, dates,
/// assignee, status, progress) is the result of its timeline entries, replayed in a fixed order, so the vessel and the office always
/// arrive at the same answer however they worked while apart. The projected columns are kept here for fast lists and never travel.
/// </summary>
public class WorklistTask
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }

    /// <summary>Reference code, for example MVSF-WL-2026-0007 (H after the year when raised ashore).</summary>
    public string Code { get; set; } = "";
    public Guid? SourceId { get; set; }
    public Guid? NatureId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>Copied from the source when the task was raised, so a later change to the list does not change an old task.</summary>
    public bool RequiresVerification { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>Which side raised it (see <see cref="SyncStages"/>).</summary>
    public string OwnerStage { get; set; } = SyncStages.Office;

    // ---- projected from the timeline; each side works these out for itself and they are never sent ----
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public WorklistPriority Priority { get; set; }
    public WorklistStatus Status { get; set; }
    public DateTime? PlannedDate { get; set; }
    public DateTime? TargetDate { get; set; }
    public string AssigneeName { get; set; } = "";
    public int PercentComplete { get; set; }
    public int DeferralCount { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? LastEntryAt { get; set; }

    /// <summary>Completed onboard but the source asks the office to verify it first.</summary>
    public bool AwaitingVerification => RequiresVerification && Status == WorklistStatus.Completed && VerifiedAt is null;

    public bool IsClosed => Status == WorklistStatus.Cancelled || (Status == WorklistStatus.Completed && !AwaitingVerification);

    public bool IsOverdue(DateTime today) => !IsClosed && Status != WorklistStatus.Completed && TargetDate is { } target && target.Date < today.Date;
}

/// <summary>One line of a task's history. Written once by one side and never edited or deleted by anyone.</summary>
public class WorklistEntry
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaskId { get; set; }
    public WorklistEntryType Type { get; set; }

    /// <summary>What the author wrote: the comment, the reason, the note.</summary>
    public string Text { get; set; } = "";

    /// <summary>The values the entry sets, as JSON (for example {"target":"2026-12-01"}). See <c>WorklistProjection</c>.</summary>
    public string Payload { get; set; } = "{}";
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = "";
    public DateTime At { get; set; }

    /// <summary>The side that wrote it. Entries from both sides appear on the same task.</summary>
    public string OwnerStage { get; set; } = SyncStages.Office;
}
