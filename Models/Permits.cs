namespace Chandlery.Models;

// Permit to work (spec 14, plan 14a). A permit is a document the vessel writes; its state is worked out from its events and the clock, never typed.
// Every limit, interval and sign-off level here is configuration marked "to verify" against the company's SMS.

/// <summary>The lowest rank that may do a step: any crew member, an officer or engineer, or the Master.</summary>
public enum PermitRole { AnyCrew, Officer, Master }

public enum PermitAnswerKind { Unanswered, Yes, No, NotApplicable }

public enum PermitEventKind
{
    Requested, ChecklistCompleted, GasTested, Issued, MasterApproved, Started, PersonEntered, PersonLeft, Suspended, Resumed,
    Extended, HandedOver, ConflictAcknowledged, Completed, Cancelled, Expired, Withdrawn
}

/// <summary>
/// A kind of permit the company issues, and the rules it carries. The office's master data, copied to every vessel. The checklist is kept as items of a version, so a change
/// to the checklist makes a new version and never touches a permit issued on an older one.
/// </summary>
public class PermitType
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The letters in the reference number, PTW-{vessel}-{CODE}-... .</summary>
    public string Code { get; set; } = "";
    public int MaxValidityHours { get; set; } = 8;
    public PermitRole RequesterRole { get; set; } = PermitRole.Officer;

    /// <summary>The person who requests may also issue (a small vessel). Off unless the SMS allows it (to verify).</summary>
    public bool SameRequesterAndIssuer { get; set; }
    public bool GasTestRequired { get; set; }
    public int GasRepeatMinutes { get; set; } = 30;
    public decimal OxygenMin { get; set; } = 20.9m;
    public decimal OxygenMax { get; set; } = 22.0m;
    public decimal FlammableMaxPercentLel { get; set; } = 1m;
    public decimal H2sMaxPpm { get; set; } = 1m;
    public decimal CoMaxPpm { get; set; } = 25m;
    public bool AttendantRequired { get; set; }
    public bool EntryLogRequired { get; set; }
    public bool RiskAssessmentRequired { get; set; }
    public bool MasterApprovalRequired { get; set; }

    /// <summary>The current checklist version. Items of older versions stay, so old permits can still be read.</summary>
    public int ChecklistVersion { get; set; } = 1;

    /// <summary>What sets the rules, for the record (for example "A.1050(27) and the company SMS (to verify)").</summary>
    public string Reference { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One line of a permit type's checklist, in a version of it.</summary>
public class PermitChecklistItem
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PermitTypeId { get; set; }
    public int Version { get; set; } = 1;
    public int Position { get; set; }
    public string Section { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Must be answered yes or not applicable before the permit can be issued; a no on a mandatory item blocks it.</summary>
    public bool Mandatory { get; set; } = true;
    public bool NoteRequired { get; set; }
}

/// <summary>Two kinds of permit that should not be active together at one place, or on one vessel at all. A warning the issuer acknowledges.</summary>
public class PermitConflictPair
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PermitTypeAId { get; set; }
    public Guid PermitTypeBId { get; set; }

    /// <summary>Only when both are at the same location (as written); otherwise anywhere on the vessel.</summary>
    public bool SameLocationOnly { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>The type's rules as they were when the permit was requested. Kept on the permit as JSON, so the rules read the permit alone.</summary>
public sealed record PermitTerms(
    int MaxValidityHours, bool GasTestRequired, int GasRepeatMinutes, decimal OxygenMin, decimal OxygenMax, decimal FlammableMaxPercentLel, decimal H2sMaxPpm, decimal CoMaxPpm,
    bool AttendantRequired, bool EntryLogRequired, bool RiskAssessmentRequired, bool MasterApprovalRequired, bool SameRequesterAndIssuer);

/// <summary>
/// One permit. Written by the vessel; checklist answers, persons, readings, entry log and events are parts of it. Events are only ever added. The window is the period
/// the permit was issued for; an extension adds a period in an event and does not rewrite it.
/// </summary>
public class Permit
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }

    /// <summary>PTW-{vessel}-... (spec 01).</summary>
    public string Code { get; set; } = "";
    public Guid PermitTypeId { get; set; }
    public string TypeName { get; set; } = "";
    public int ChecklistVersion { get; set; }

    /// <summary>The terms of the type when requested, as JSON of <see cref="PermitTerms"/>.</summary>
    public string Terms { get; set; } = "";

    /// <summary>Where, as written, with an optional pick from the vessel's tanks or machinery.</summary>
    public string Location { get; set; } = "";
    public Guid? TankId { get; set; }
    public Guid? MachineryItemId { get; set; }
    public string Description { get; set; } = "";
    public string Isolation { get; set; } = "";
    public string Conditions { get; set; } = "";

    public Guid? RequesterCrewId { get; set; }
    public string RequesterName { get; set; } = "";
    public Guid? IssuerCrewId { get; set; }
    public string IssuerName { get; set; } = "";
    public Guid? AttendantCrewId { get; set; }
    public string AttendantName { get; set; } = "";

    /// <summary>The period requested, as the ship's date and time kept as written. Extensions are events.</summary>
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }

    public Guid? RiskAssessmentId { get; set; }
    public Guid? WorklistTaskId { get; set; }
    public Guid? PlannedJobId { get; set; }

    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;

    public List<PermitAnswer> Answers { get; set; } = [];
    public List<PermitPerson> Persons { get; set; } = [];
    public List<PermitGasReading> Readings { get; set; } = [];
    public List<PermitEntry> Entries { get; set; } = [];
    public List<PermitEvent> Events { get; set; } = [];
}

/// <summary>A checklist item as it stood on this permit, with the answer. The wording is copied, so the permit never changes with the template.</summary>
public class PermitAnswer
{
    public Guid Id { get; set; }
    public Guid PermitId { get; set; }
    public Guid? ChecklistItemId { get; set; }
    public int Position { get; set; }
    public string Section { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Mandatory { get; set; }
    public bool NoteRequired { get; set; }
    public PermitAnswerKind Answer { get; set; }
    public string Note { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
    public DateTime? At { get; set; }
}

/// <summary>A person who does the work, or may enter the space.</summary>
public class PermitPerson
{
    public Guid Id { get; set; }
    public Guid PermitId { get; set; }
    public Guid? CrewMemberId { get; set; }
    public string Name { get; set; } = "";
}

public class PermitGasReading
{
    public Guid Id { get; set; }
    public Guid PermitId { get; set; }
    public DateTime At { get; set; }
    public string Instrument { get; set; } = "";
    public DateTime? CalibrationDate { get; set; }
    public string Location { get; set; } = "";
    public decimal? Oxygen { get; set; }
    public decimal? FlammablePercentLel { get; set; }
    public decimal? H2sPpm { get; set; }
    public decimal? CoPpm { get; set; }
    public bool WithinLimits { get; set; }

    /// <summary>What was outside the limits, when anything was.</summary>
    public string Failures { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
}

/// <summary>One person's time in and out of an enclosed space.</summary>
public class PermitEntry
{
    public Guid Id { get; set; }
    public Guid PermitId { get; set; }
    public Guid? CrewMemberId { get; set; }
    public string PersonName { get; set; } = "";
    public DateTime In { get; set; }
    public DateTime? Out { get; set; }
    public string AttendantName { get; set; } = "";
}

/// <summary>An event in a permit's life. Appended, never edited.</summary>
public class PermitEvent
{
    public Guid Id { get; set; }
    public Guid PermitId { get; set; }
    public PermitEventKind Kind { get; set; }
    public DateTime At { get; set; }
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
    public string Note { get; set; } = "";

    /// <summary>The new end of the window, for an extension.</summary>
    public DateTime? Until { get; set; }
}

/// <summary>The office's comment on a permit (the audit view). Written once, by the office.</summary>
public class PermitComment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid PermitId { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}
