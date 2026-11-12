namespace Chandlery.Models;

// Safety meetings (spec 13, plan 13a, stage C3): the safety committee, toolbox talks and briefings, minuted onboard with their actions.

public enum MeetingItemKind { Agenda, Suggestion }

/// <summary>
/// A kind of meeting, and how often it is due where it is scheduled at all. The office's master data, copied to every vessel. The standard agenda is offered when a meeting
/// of this type is recorded.
/// </summary>
public class MeetingType
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>False for meetings held when needed (a toolbox talk, a briefing): no due date, never overdue.</summary>
    public bool Scheduled { get; set; }
    public DrillIntervalKind IntervalKind { get; set; } = DrillIntervalKind.CalendarMonths;
    public int IntervalCount { get; set; } = 1;
    public int WarningDays { get; set; } = 7;

    /// <summary>The standard agenda, one item to a line.</summary>
    public string StandardAgenda { get; set; } = "";

    /// <summary>What sets the interval, for the record (for example "Company SMS (to verify)").</summary>
    public string Reference { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One meeting held onboard: who chaired it and who was there, the agenda items with what was said, the suggestions crew raised, signed by the Master. Written by the Master or
/// crew the office gave the authority to conduct drills and record meetings; changed until the Master signs, and after that only dated notes are added.
/// </summary>
public class Meeting
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }

    /// <summary>MT-{vessel}-... (spec 01).</summary>
    public string Code { get; set; } = "";
    public Guid MeetingTypeId { get; set; }
    public string TypeName { get; set; } = "";

    /// <summary>When it was held, as the ship's date and time kept as written (not converted).</summary>
    public DateTime HeldAt { get; set; }
    public string Place { get; set; } = "";
    public Guid? ChairCrewId { get; set; }
    public string ChairName { get; set; } = "";
    public string SecretaryName { get; set; } = "";

    /// <summary>A line or two on the outcome, for the list.</summary>
    public string Summary { get; set; } = "";

    public string SignedByName { get; set; } = "";
    public Guid? SignedByCrewId { get; set; }
    public DateTime? SignedAt { get; set; }
    public bool IsSigned => SignedAt is not null;

    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;

    public List<MeetingAttendee> Attendees { get; set; } = [];
    public List<MeetingItem> Items { get; set; } = [];
    public List<MeetingNote> Notes { get; set; } = [];
}

/// <summary>Who was at a meeting. The name is kept as text beside the optional crew link.</summary>
public class MeetingAttendee
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid? CrewMemberId { get; set; }
    public string Name { get; set; } = "";
    public string Rank { get; set; } = "";
    public DrillAttendance Attendance { get; set; }
    public DrillAbsence Absence { get; set; }
}

/// <summary>An item on the agenda, or a suggestion a crew member raised, with what was said and decided.</summary>
public class MeetingItem
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public int Position { get; set; }
    public MeetingItemKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>Who raised it, for a suggestion.</summary>
    public string RaisedBy { get; set; } = "";
}

/// <summary>A dated addition to a signed meeting. Never edited.</summary>
public class MeetingNote
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>The office's comment on a meeting. Written once, by the office.</summary>
public class MeetingComment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid MeetingId { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}
