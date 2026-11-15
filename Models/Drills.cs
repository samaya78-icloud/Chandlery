namespace Chandlery.Models;

// Drills (spec 13, plan 13a). A drill is a document the vessel writes; the schedule is worked out from the drills held, never typed.

public enum DrillCategory { Emergency, Security, Pollution, Medical, Training }

/// <summary>How a drill interval is counted: a number of days after the last drill, or a number of calendar months (a monthly drill is due by the end of the next month).</summary>
public enum DrillIntervalKind { Days, CalendarMonths }

public enum DrillAttendance { Present, Absent, Observer }

/// <summary>Why a crew member was not at a drill. An excused absence does not reset the person's clock; it only explains the gap.</summary>
public enum DrillAbsence { None, OnWatch, Ashore, OnLeave, Sick, JoinedAfter, Other }

/// <summary>A kind of drill the company runs, and how often. The office's master data, copied to every vessel. The defaults are a starting list to check against the flag state, class and the safety management system.</summary>
public class DrillType
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public DrillCategory Category { get; set; }
    public DrillIntervalKind IntervalKind { get; set; } = DrillIntervalKind.CalendarMonths;
    public int IntervalCount { get; set; } = 1;

    /// <summary>Days before the due date when the schedule starts to say "due soon".</summary>
    public int WarningDays { get; set; } = 7;

    /// <summary>Every crew member must take part within the interval (fire and abandon ship), so each person has a due date of their own.</summary>
    public bool EveryCrewMember { get; set; }

    /// <summary>Standard scenario text, offered when a drill of this type is recorded.</summary>
    public string Scenario { get; set; } = "";

    /// <summary>What to test, one item to a line (alarms, muster, lifeboat engine start...).</summary>
    public string Checklist { get; set; } = "";

    /// <summary>What the drill satisfies, for the record (for example "SOLAS III/19 (to verify)").</summary>
    public string Reference { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A change to a drill type for one vessel: it does not apply there, or it has another interval, with the reason. Master data, copied to that vessel only.</summary>
public class DrillOverride
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid DrillTypeId { get; set; }
    public bool Applies { get; set; } = true;
    public DrillIntervalKind? IntervalKind { get; set; }
    public int? IntervalCount { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// One drill held. Written by the vessel (the Master, or crew the office gave the authority to conduct drills), changed until the Master signs it, and after that only
/// added to with dated notes. The type's name and category are kept on the drill so a later change to the type never rewrites it.
/// </summary>
public class Drill
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }

    /// <summary>DR-{vessel}-... (spec 01).</summary>
    public string Code { get; set; } = "";
    public Guid DrillTypeId { get; set; }
    public string TypeName { get; set; } = "";
    public DrillCategory Category { get; set; }

    /// <summary>When it began and ended, as the ship's date and time kept as written (not converted).</summary>
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public IncidentShipStatus ShipStatus { get; set; }
    public string Place { get; set; } = "";
    public string Scenario { get; set; } = "";
    public Guid? ConductedByCrewId { get; set; }
    public string ConductedByName { get; set; } = "";

    /// <summary>The equipment used or tested, in words.</summary>
    public string EquipmentTested { get; set; } = "";

    /// <summary>What went wrong or could be better.</summary>
    public string Findings { get; set; } = "";

    /// <summary>Equipment or procedure that failed. Each is worth an action.</summary>
    public string Deficiencies { get; set; } = "";
    public string Remarks { get; set; } = "";

    public string SignedByName { get; set; } = "";
    public Guid? SignedByCrewId { get; set; }
    public DateTime? SignedAt { get; set; }
    public bool IsSigned => SignedAt is not null;

    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;

    public List<DrillParticipant> Participants { get; set; } = [];
    public List<DrillNote> Notes { get; set; } = [];
}

/// <summary>Who was at a drill. The name is kept as text beside the optional crew link, so a crew record that is removed never damages the history.</summary>
public class DrillParticipant
{
    public Guid Id { get; set; }
    public Guid DrillId { get; set; }
    public Guid? CrewMemberId { get; set; }
    public string Name { get; set; } = "";
    public string Rank { get; set; } = "";
    public DrillAttendance Attendance { get; set; }
    public DrillAbsence Absence { get; set; }
}

/// <summary>A dated addition to a drill that has been signed. Never edited.</summary>
public class DrillNote
{
    public Guid Id { get; set; }
    public Guid DrillId { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>The office's comment on a drill. Written once, by the office.</summary>
public class DrillComment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid DrillId { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}
