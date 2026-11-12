namespace Chandlery.Models;

// Seafarers' hours of work and rest (ILO Convention 180, STCW A-VIII/1). See docs/specs/11-safety-rest-hours.md.
//
// Everything a seafarer does is written down as history that is never edited: a correction is a new revision, a signature is a
// new record that remembers what it covered. What a day looks like now, and whether it complied, is worked out from that history
// (RestRules, RestHoursService) the same way on the vessel and ashore.

public enum VesselDayKind { AtSea, InPort, Anchored }

public enum RestRole { Seafarer, Master }

/// <summary>What a note on a breach says: an explanation, a declared exception (emergency, drill...), the Master's acknowledgement, or compensatory rest given.</summary>
public enum RestNoteKind { Explanation, Exception, MasterAcknowledged, CompensatoryRestGiven }

public enum RestExceptionKind { None, Emergency, Drill, OperationalNeed }

/// <summary>
/// The limits that apply to a vessel's seafarers. All the numbers are data, in minutes, so a flag state or collective agreement can differ.
/// The defaults are the ILO 180 / STCW values. A rule set with no vessel is the company default; one naming a vessel overrides it for that vessel.
/// </summary>
public class RestRuleSet
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? VesselId { get; set; }
    public string Name { get; set; } = "";
    public string FlagState { get; set; } = "";

    /// <summary>Minimum rest in any 24-hour period (10 h).</summary>
    public int MinRest24Minutes { get; set; } = 600;

    /// <summary>Minimum rest in any 7-day period (77 h).</summary>
    public int MinRest7Minutes { get; set; } = 4620;

    /// <summary>Rest may be divided into no more than this many periods (2).</summary>
    public int MaxRestPeriods { get; set; } = 2;

    /// <summary>One rest period at least this long (6 h).</summary>
    public int MinLongestPeriodMinutes { get; set; } = 360;

    /// <summary>The time between consecutive rest periods may not exceed this (14 h).</summary>
    public int MaxIntervalMinutes { get; set; } = 840;

    /// <summary>STCW A-VIII/1.4: the 10 hours may fall to a shorter consecutive rest for a limited number of days. Off unless the flag state allows it.</summary>
    public bool ReductionAllowed { get; set; }
    public int ReductionMinConsecutiveMinutes { get; set; } = 360;
    public int ReductionMaxDays { get; set; } = 2;

    /// <summary>The 7-day rest that still has to be given when a reduction was used (70 h).</summary>
    public int ReductionMin7Minutes { get; set; } = 4200;

    /// <summary>Convention 180 art. 6: no night work under 18. Night starts at this minute of the day and lasts the length below (9 h, must include midnight to 05:00).</summary>
    public bool MinorNightProtection { get; set; } = true;
    public int MinorNightStartMinute { get; set; } = 1260;
    public int MinorNightLengthMinutes { get; set; } = 540;

    /// <summary>How often the seafarer and the Master sign (days). The guidelines ask for at least weekly.</summary>
    public int SignIntervalDays { get; set; } = 7;

    /// <summary>How long records are kept. The figure is the company's to confirm against its flag state.</summary>
    public int RetentionMonths { get; set; } = 36;
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One revision of what a seafarer worked on one date. Anything not listed as work is rest. A correction is a new revision with a
/// reason; the latest revision (by time, then id) is the day as it stands. The seafarer's name and rank are kept as they were, so the
/// record reads the same after they sign off the vessel.
/// </summary>
public class RestEntry
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid CrewMemberId { get; set; }
    public string CrewName { get; set; } = "";
    public string Rank { get; set; } = "";

    /// <summary>The date, at midnight UTC (ship's local date; this is a calendar date, not a moment).</summary>
    public DateTime Date { get; set; }

    /// <summary>The work periods of the day as text, for example "00:00-04:00,16:00-20:00" (to the quarter hour; 24:00 ends the day).</summary>
    public string WorkSpans { get; set; } = "";
    public string Reason { get; set; } = "";
    public string EnteredByName { get; set; } = "";
    public Guid? EnteredByCrewId { get; set; }

    /// <summary>Entered by an officer for the seafarer; the seafarer still has to agree when signing.</summary>
    public bool OnBehalf { get; set; }
    public DateTime EnteredAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>
/// The seafarer's, then the Master's, agreement that the record is accurate for a period. It remembers a fingerprint of the days it covered;
/// if any of them is revised afterwards the fingerprint no longer matches and the signature shows as out of date.
/// </summary>
public class RestSignOff
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid CrewMemberId { get; set; }
    public string CrewName { get; set; } = "";
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public RestRole Role { get; set; }
    public string Fingerprint { get; set; } = "";
    public string SignedByName { get; set; } = "";
    public Guid? SignedByCrewId { get; set; }
    public DateTime SignedAt { get; set; }

    /// <summary>The Master's signature was given by a deputy the office had named, not by the Master. Always false for the seafarer's own signature.</summary>
    public bool AsDeputy { get; set; }

    /// <summary>The name as printed: a deputy's signature says so.</summary>
    public string SignerLabel => AsDeputy ? $"{SignedByName} (deputy for the Master)" : SignedByName;
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>A breach is worked out, never stored; what is stored is what people said about it: the explanation, a declared exception, the Master's acknowledgement.</summary>
public class RestBreachNote
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid CrewMemberId { get; set; }
    public string CrewName { get; set; } = "";

    /// <summary>The date of the finding, and the rule it broke (a <c>RestRule</c> name).</summary>
    public DateTime Date { get; set; }
    public string Rule { get; set; } = "";
    public RestNoteKind Kind { get; set; }
    public RestExceptionKind ExceptionKind { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
    public DateTime At { get; set; }

    /// <summary>The follow-up task the Master's acknowledgement raised, so it is never raised twice.</summary>
    public Guid? WorklistTaskId { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>Whether the vessel was at sea, in port or at anchor on a date; it decides which row of the working-arrangements table pre-fills a day. The latest one for a date counts.</summary>
public class VesselDayStatus
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public DateTime Date { get; set; }
    public VesselDayKind Kind { get; set; }
    public string PortName { get; set; } = "";
    public string SetByName { get; set; } = "";
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>
/// The table of shipboard working arrangements (appendix 3 of the guidelines) for one vessel, as one version. The office writes it; a change
/// is a new version, so the old ones remain. The rows (one per position) are kept inside the version as JSON.
/// </summary>
public class WorkArrangementTable
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public int VersionNo { get; set; }
    public DateTime EffectiveFrom { get; set; }

    /// <summary>The national law or agreement the limits come from (shown in the table's header).</summary>
    public string LawReference { get; set; } = "";
    public string LimitsText { get; set; } = "";
    public string OtherRequirements { get; set; } = "";
    public string RowsJson { get; set; } = "[]";
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>
/// The Master's signature on one version of the working-arrangements table, given onboard. The table itself is the office's and cannot be
/// changed there; a new version needs a new signature. A record of its own, written once, so it travels up to the office like the other onboard records.
/// </summary>
public class WorkArrangementSignOff
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid TableId { get; set; }
    public int VersionNo { get; set; }
    public string SignedByName { get; set; } = "";
    public Guid? SignedByCrewId { get; set; }
    public DateTime SignedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}
