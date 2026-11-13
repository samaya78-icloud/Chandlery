namespace Chandlery.Models;

// ---- Projects J9: yards and tenders, the documents checklist, phases, and links to risk assessments.
// ---- All of it is the office's own working: none is registered for sync, and a vessel installation cannot write it.

/// <summary>A shipyard the company deals with. A yard that is no longer used is switched off, never deleted, so old tenders keep their name.</summary>
public class ProjectYard
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public enum TenderStatus { Invited, Quoted, Declined, Awarded, NotAwarded }

/// <summary>An invitation to one yard to quote for a project's specification, and what the yard answered. Amounts are in US dollars.</summary>
public class ProjectTender
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid YardId { get; set; }
    public TenderStatus Status { get; set; }
    public DateTime InvitedAt { get; set; }
    public string InvitedByName { get; set; } = "";

    /// <summary>The yard's own quotation number.</summary>
    public string YardReference { get; set; } = "";
    public DateTime? QuotedAt { get; set; }
    public DateTime? ValidUntil { get; set; }

    /// <summary>Charges that are not tied to a specification item (docking and undocking, shore power, wharfage).</summary>
    public decimal OtherChargesUsd { get; set; }
    public string Remarks { get; set; } = "";
    public DateTime? AwardedAt { get; set; }
    public string AwardedByName { get; set; } = "";

    /// <summary>True when the yard entered this quote itself through its own login, rather than the office typing it in.</summary>
    public bool QuotedByYard { get; set; }
}

/// <summary>What a yard quoted for one specification item.</summary>
public class ProjectTenderLine
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TenderId { get; set; }
    public Guid ItemId { get; set; }
    public decimal AmountUsd { get; set; }
}

/// <summary>A stage of the yard stay (arrival, docking, work in dock, undocking, sea trial) with its planned and actual dates.</summary>
public class ProjectPhase
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public DateTime? PlannedStart { get; set; }
    public DateTime? PlannedEnd { get; set; }
    public DateTime? ActualStart { get; set; }
    public DateTime? ActualEnd { get; set; }
    public int SortOrder { get; set; }
}

public enum DocumentCheckStatus { Required, Received, NotNeeded }

/// <summary>One document the project needs, and whether it has arrived.</summary>
public class ProjectDocumentCheck
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public DocumentCheckStatus Status { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? ReceivedOn { get; set; }

    /// <summary>Where to find it (a file number, a link, a name).</summary>
    public string Reference { get; set; } = "";
    public string Note { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>"This project is covered by that risk assessment." The assessment is held by its reference, so the link follows it to its latest version.</summary>
public class ProjectRiskLink
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public string AssessmentCode { get; set; } = "";
    public string LinkedByName { get; set; } = "";
    public DateTime At { get; set; }
}

// ---- Projects J10: files kept with a project, and groups of projects (a drydock for several vessels) ----

/// <summary>
/// A file kept with a project at the office: a yard's quotation, a survey report. It may belong to one tender (the yard's quotation) or to the project as a whole.
/// The bytes live in the file store; this row is the record. Never synced, so the vessel never receives a yard's prices.
/// </summary>
public class ProjectFile
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>The tender it is a quotation for; null for a file on the project as a whole.</summary>
    public Guid? TenderId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StoredKey { get; set; } = "";
    public string UploadedByName { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

/// <summary>
/// A set of projects run together, such as one drydock for sister ships. Each vessel still has its own project (so each ship receives only its own), and the group
/// adds up what they come to across the fleet.
/// </summary>
public class ProjectGroup
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string CreatedByName { get; set; } = "";
}

/// <summary>A project's membership of a group. A project is in at most one group.</summary>
public class ProjectGroupMember
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid GroupId { get; set; }
    public Guid ProjectId { get; set; }
}
