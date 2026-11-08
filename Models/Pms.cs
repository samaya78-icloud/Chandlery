namespace Chandlery.Models;

public enum PmsJobType { Check, Overhaul, Test, Service, Inspection }

/// <summary>Floating: the next due date counts from when the job was last done. Fixed calendar: due dates stay on a fixed grid (an annual item stays annual).</summary>
public enum PmsAnchor { FromCompletion, FixedCalendar }

public enum PmsIntervalUnit { Days, Weeks, Months, Years }

public enum PmsStatus { NoSchedule, NotDue, Due, Overdue }

// ---- the library: kept by the office, copied to every vessel (master data) ----

/// <summary>A kind of equipment (main engine, cylinder unit...). A two-level or deeper tree through <see cref="ParentId"/>.</summary>
public class MachineryClass
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A standard maintenance job. Planned jobs made from it keep the <see cref="Version"/> they were made from until a change is accepted.</summary>
public class JobTemplate
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? MachineryClassId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>The checklist and instructions, one step per line.</summary>
    public string Instructions { get; set; } = "";
    public PmsJobType Type { get; set; }
    public int? CalendarInterval { get; set; }
    public PmsIntervalUnit CalendarUnit { get; set; } = PmsIntervalUnit.Months;
    public int? HoursInterval { get; set; }
    public PmsAnchor Anchor { get; set; }

    /// <summary>Days after the due date before the job counts as overdue. Empty means the default: a tenth of the calendar interval, at least one day.</summary>
    public int? GraceDays { get; set; }
    public bool IsCritical { get; set; }
    public Guid? DepartmentId { get; set; }
    public int? EstimatedMinutes { get; set; }

    /// <summary>A completion of this job waits for a verification record (second sign-off).</summary>
    public bool RequiresVerification { get; set; }
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

// ---- a vessel's plan: documents with one owner (the side that made them) ----

/// <summary>One piece of equipment on a vessel. The tree is location, system, sub-system, item.</summary>
public class MachineryItem
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid? ParentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public Guid? MachineryClassId { get; set; }
    public bool IsCritical { get; set; }

    /// <summary>Running hours are read from this item, so jobs with an hours interval can be planned on it.</summary>
    public bool HasHourMeter { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>The side that made the item; only that side changes it (the other proposes a change).</summary>
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>
/// A maintenance job on one machinery item: an instance of a template (with its version) or a job the vessel made itself. The definition is
/// changed only by its owner. The columns under "worked out" are the schedule, worked out on each side from the completions and readings; they
/// are never sent.
/// </summary>
public class PlannedJob
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid MachineryItemId { get; set; }
    public Guid? TemplateId { get; set; }
    public int? TemplateVersion { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public PmsJobType Type { get; set; }
    public int? CalendarInterval { get; set; }
    public PmsIntervalUnit CalendarUnit { get; set; } = PmsIntervalUnit.Months;
    public int? HoursInterval { get; set; }
    public PmsAnchor Anchor { get; set; }

    /// <summary>Fixed calendar jobs: one date on the grid the due dates follow. Empty means the start date.</summary>
    public DateTime? FixedAnchorDate { get; set; }
    public int? GraceDays { get; set; }
    public bool IsCritical { get; set; }
    public Guid? DepartmentId { get; set; }
    public int? EstimatedMinutes { get; set; }
    public bool RequiresVerification { get; set; }

    /// <summary>When the job was put on the plan; the first due date counts from here when nothing has been done yet.</summary>
    public DateTime StartDate { get; set; }

    /// <summary>When it was last done before it came onto this plan (an imported plan, or a job that already had history).</summary>
    public DateTime? BaselineDoneDate { get; set; }
    public decimal? BaselineDoneHours { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;

    // ---- worked out on each side, never sent ----
    public DateTime? LastDoneDate { get; set; }
    public decimal? LastDoneHours { get; set; }
    public DateTime? NextDueDate { get; set; }

    /// <summary>"calendar" or "hours": which interval gives the next due date.</summary>
    public string DueBasis { get; set; } = "";

    /// <summary>The hours date is a projection from recent running hours (not a reading already reached).</summary>
    public bool HoursEstimated { get; set; }

    /// <summary>An hours interval exists but cannot be projected yet (no hours at the last completion, or too few readings).</summary>
    public bool HoursUnknown { get; set; }
    public DateTime? LastReadingDate { get; set; }
    public DateTime? GraceUntil { get; set; }

    /// <summary>Set when an approved deferral is what gives the next due date.</summary>
    public DateTime? DeferredUntil { get; set; }
}

// ---- things that happened: written once, never changed ----

/// <summary>A reading of an hour meter. A wrong one is corrected by a new reading marked as a correction; for one date, the latest correction wins.</summary>
public class RunningHoursReading
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid MachineryItemId { get; set; }
    public DateTime ReadingDate { get; set; }
    public decimal Hours { get; set; }

    /// <summary>"manual" or "import".</summary>
    public string Source { get; set; } = "manual";
    public bool IsCorrection { get; set; }
    public string Note { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>A job done. The job's schedule restarts from the latest done date.</summary>
public class JobCompletion
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid PlannedJobId { get; set; }
    public DateTime DoneDate { get; set; }

    /// <summary>The meter reading when it was done, for jobs with an hours interval. Empty means "take the reading on or before the done date".</summary>
    public decimal? HoursAtDone { get; set; }
    public string PerformedBy { get; set; } = "";
    public int? TimeSpentMinutes { get; set; }
    public string Comments { get; set; } = "";

    /// <summary>History brought in from an earlier plan: shown, never counted as work done on this system.</summary>
    public bool IsImported { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>A second sign-off on a completion (for jobs that require it).</summary>
public class JobVerification
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid CompletionId { get; set; }
    public string VerifiedBy { get; set; } = "";
    public string Note { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

// ---- deferrals and plan changes: requests and decisions, written once ----

/// <summary>A request to put a job off. It counts only for the cycle it was asked in: once the job is done again, it no longer applies.</summary>
public class JobDeferralRequest
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid PlannedJobId { get; set; }

    /// <summary>The due date when the request was made.</summary>
    public DateTime CurrentDueDate { get; set; }
    public DateTime RequestedDueDate { get; set; }

    /// <summary>When the job was last done at the time (empty if never): the cycle this request belongs to.</summary>
    public DateTime? LastDoneAtRequest { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>The job was critical when asked, so the request travels on the urgent lane.</summary>
    public bool IsCritical { get; set; }
    public string RequestedByName { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>The answer to a deferral request. The latest decision on a request is the one that counts; earlier ones stay in the history.</summary>
public class JobDeferralDecision
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid RequestId { get; set; }
    public bool Approved { get; set; }

    /// <summary>The new due date that was granted (it can differ from the one asked for).</summary>
    public DateTime? ApprovedDueDate { get; set; }
    public string Comment { get; set; } = "";
    public string DecidedByName { get; set; } = "";

    /// <summary>"office", or "master" for a Master approving onboard within the limit.</summary>
    public string DecidedByRole { get; set; } = "office";
    public bool IsCritical { get; set; }
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

public enum PlanChangeKind { JobInterval, JobDeactivate, MachineryChange }

/// <summary>A vessel asking the office to change what the office owns (a job's interval, taking a job off the plan, a machinery item's details).</summary>
public class PlanChangeRequest
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public PlanChangeKind Kind { get; set; }
    public Guid? PlannedJobId { get; set; }
    public Guid? MachineryItemId { get; set; }

    /// <summary>The proposed values, as JSON (which values depend on <see cref="Kind"/>).</summary>
    public string Proposed { get; set; } = "{}";
    public string Reason { get; set; } = "";
    public string RequestedByName { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

public class PlanChangeDecision
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid RequestId { get; set; }
    public bool Approved { get; set; }
    public string Comment { get; set; } = "";
    public string DecidedByName { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>A spare part kept for a machinery item: what a job on it needs when it is ordered. Made and changed by the side that added it.</summary>
public class MachinerySpare
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid MachineryItemId { get; set; }

    /// <summary>The catalog item to order.</summary>
    public Guid ItemId { get; set; }

    /// <summary>How many to order when the job is done.</summary>
    public decimal Quantity { get; set; } = 1;
    public string Note { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}
