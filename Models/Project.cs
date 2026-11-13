namespace Chandlery.Models;

public enum ProjectType { Drydock, NonDrydock }

/// <summary>Worked out from the status events (see <c>ProjectStatusRules</c>); never stored.</summary>
public enum ProjectStatus { Planning, Approved, Ongoing, Complete, Closed }

/// <summary>What the office recorded about a project's progress. Withdrawn takes an approved project back to Planning.</summary>
public enum ProjectEventKind { Approved, Started, Completed, Closed, Withdrawn }

public enum ProjectItemSource { Manual, WorklistTask, PmsJob, ClassRequirement, OwnerRequest, Other, VesselRequest, Variation }

public enum ProjectItemStatus { Open, Done, Dropped }

/// <summary>The kinds of procurement document a project can collect costs from.</summary>
public enum ProjectLinkKind { Requisition, PurchaseOrder, Invoice }

// ---- what the vessel receives: the project, its specification (no costs), and the history written around it ----

/// <summary>
/// A drydock or technical project. The office owns it and plans it; the vessel receives it with its specification items, and adds notes and links. Its status is
/// worked out from <see cref="ProjectStatusEvent"/> records, and a closed project is locked. Money (budget, estimates, cost entries) is never part of this document:
/// it lives in tables that stay at the office.
/// </summary>
public class Project
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>The vessel it is for; this decides who receives it.</summary>
    public Guid VesselId { get; set; }

    /// <summary>Reference, like MVSF-PR-2026-H0001 (H after the year, as projects are planned ashore).</summary>
    public string Code { get; set; } = "";
    public ProjectType Type { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public Guid? SuperintendentUserId { get; set; }
    public string SuperintendentName { get; set; } = "";
    public DateTime? PlannedStart { get; set; }
    public DateTime? PlannedEnd { get; set; }

    /// <summary>The yard: awarding a tender sets it, and it can still be typed.</summary>
    public string Yard { get; set; } = "";

    /// <summary>Underwater inspection in lieu of drydock.</summary>
    public bool IsUwild { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;

    public List<ProjectItem> Items { get; set; } = new();
}

/// <summary>One job in the project's specification. No amounts here: estimates are kept at the office (<see cref="ProjectItemCost"/>).</summary>
public class ProjectItem
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Group { get; set; } = "";
    public string Description { get; set; } = "";
    public ProjectItemSource Source { get; set; }

    /// <summary>The Worklist task or PMS job it came from; with <see cref="Source"/> it makes sure the same one is never added twice.</summary>
    public Guid? SourceId { get; set; }

    /// <summary>What to show for the source (the task's code, the job's code), so a vessel need not look it up.</summary>
    public string SourceRef { get; set; } = "";
    public ProjectItemStatus Status { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A step in the project's life. Written once, by the office.</summary>
public class ProjectStatusEvent
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid ProjectId { get; set; }
    public ProjectEventKind Kind { get; set; }

    /// <summary>The day it took effect (the day the ship arrived in the yard, the day work finished), which may differ from the day it was recorded.</summary>
    public DateTime Date { get; set; }
    public string Note { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public string ByName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>A progress note or daily yard report. Written once, by either side.</summary>
public class ProjectNote
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>The specification item it is about, if any.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>The day the note is about.</summary>
    public DateTime Date { get; set; }
    public string Text { get; set; } = "";

    /// <summary>How far along the item or the stay is, when the writer says so (0 to 100).</summary>
    public int? ProgressPct { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>
/// "This document belongs to that project", or the opposite. Written once, by either side, and never changed: the project a document belongs to is the one named by the
/// latest link written for it. No amount is kept here; cost is worked out from the document itself.
/// </summary>
public class ProjectLink
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid ProjectId { get; set; }
    public ProjectLinkKind Kind { get; set; }
    public Guid DocId { get; set; }

    /// <summary>True for "belongs to this project"; false for "no longer does".</summary>
    public bool Added { get; set; } = true;
    public Guid? ByUserId { get; set; }
    public string ByName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>A job the vessel asks to have added to the project (something found once the work began). Written once, onboard; the office answers it with a <see cref="ProjectItemDecision"/>.</summary>
public class ProjectItemRequest
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid ProjectId { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Why it is needed: what was found, and where.</summary>
    public string Reason { get; set; } = "";
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>The office's answer to a request: added to the specification (the item it became is named) or declined with a reason. Written once.</summary>
public class ProjectItemDecision
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid RequestId { get; set; }
    public bool Accepted { get; set; }
    public string Note { get; set; } = "";
    /// <summary>The specification item the request became, when it was accepted.</summary>
    public Guid? ItemId { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public string DecidedByName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

// ---- what stays at the office: budget and money. Not synced; a vessel installation cannot even write them. ----

public class ProjectBudgetLine
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public string Category { get; set; } = "";

    /// <summary>In US dollars.</summary>
    public decimal PlannedUsd { get; set; }

    /// <summary>A reserve held against this line, as a percentage of it; shown beside the total, not inside it.</summary>
    public decimal ContingencyPct { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>The estimate and the yard's quote for one specification item, in US dollars.</summary>
public class ProjectItemCost
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ItemId { get; set; }
    public decimal? EstimatedUsd { get; set; }
    public decimal? QuotedUsd { get; set; }
}

/// <summary>A cost that did not come through procurement (a yard bill paid directly, a surveyor's fee).</summary>
public class ProjectCostEntry
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public DateTime Date { get; set; }
    public string Description { get; set; } = "";
    public string Reference { get; set; } = "";

    /// <summary>In US dollars.</summary>
    public decimal AmountUsd { get; set; }
    public string ByName { get; set; } = "";
    public DateTime At { get; set; }
}

public enum ProjectRequestKind { Budget, Variation }

public enum ProjectRequestStatus { Pending, Approved, Rejected, Withdrawn }

/// <summary>
/// A request for the approval matrix to approve a project's budget (before work) or a variation (extra work and money, after approval). The amount is held in US dollars,
/// frozen when the request is made. Stays at the office like every other figure.
/// </summary>
public class ProjectApprovalRequest
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public ProjectRequestKind Kind { get; set; }
    public ProjectRequestStatus Status { get; set; }

    /// <summary>What the matrix is asked to approve: the budget with its contingency, or the variation's amount.</summary>
    public decimal AmountUsd { get; set; }
    public decimal ContingencyUsd { get; set; }

    /// <summary>For a variation: the specification group and the job it adds, and why.</summary>
    public string Group { get; set; } = "";
    public string Description { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid? SubmittedByUserId { get; set; }
    public string SubmittedByName { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    /// <summary>For an approved variation: the specification item it became.</summary>
    public Guid? ItemId { get; set; }
}

/// <summary>One approver's slot on a project request, the same shape as on a purchase order.</summary>
public class ProjectApprovalSlot
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid RequestId { get; set; }
    public int Level { get; set; }
    public Guid ApproverUserId { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string Comment { get; set; } = "";
}

/// <summary>What the budget came to when the project was approved, kept with the approval so a later change to the budget never rewrites what was approved.</summary>
public class ProjectBudgetApproval
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EventId { get; set; }
    public decimal TotalUsd { get; set; }
    public decimal ContingencyUsd { get; set; }
    public string ApprovedByName { get; set; } = "";
    public DateTime At { get; set; }
}
