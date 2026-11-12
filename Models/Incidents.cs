namespace Chandlery.Models;

// Incidents, near-misses and observations (spec 12, plan 12a). The report is a document the vessel writes; what happens to it afterwards is appended
// (events, investigation versions, Worklist tasks); its status is worked out from all of that, the same on both sides.

public enum IncidentKind { NearMiss, UnsafeAct, UnsafeCondition, Observation, InjuryIllness, HazardousOccurrence, Pollution, EquipmentDamage, Security, NonConformity }

public enum IncidentShipStatus { AtSea, InPort, Anchored, Shipyard }

public enum IncidentTreatment { None, FirstAid, MedicalTreatment, Hospital, Evacuation }

public enum IncidentPersonRole { Involved, Injured, Witness }

/// <summary>What the investigation found about the risk assessment that covered the job.</summary>
public enum IncidentAssessmentFinding { NotLooked, Adequate, Inadequate, NoneExisted }

public enum IncidentEventKind
{
    /// <summary>The Master has reviewed the report and sent it to the office (vessel).</summary>
    Reviewed,
    /// <summary>The office has opened an investigation (office).</summary>
    InvestigationOpened,
    /// <summary>The office decided none is needed, with the reason (office).</summary>
    NoInvestigationNeeded,
    /// <summary>The vessel's own findings, added alongside the office's investigation (vessel).</summary>
    VesselFindings,
    /// <summary>Who was told (flag state, class, P&amp;I...), when, and the reference (office).</summary>
    NotificationRecorded,
    Closed,
    Reopened,
    /// <summary>Entered by mistake, with the reason (vessel, or the office for its own).</summary>
    Withdrawn,
}

public enum IncidentStatus { Draft, Reported, Reviewed, Investigating, ActionsOpen, ReadyToClose, Closed, Withdrawn }

/// <summary>
/// One report. A draft until <see cref="SubmittedAt"/> is set; after the Master's review the reporter's text is locked and later additions are
/// <see cref="Supplements"/>. Owned by the side that made it (the vessel normally; the office when it enters one for a vessel).
/// </summary>
public class Incident
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }

    /// <summary>INC-{vessel}-... (spec 01), so a report named on the phone onboard is found ashore.</summary>
    public string Code { get; set; } = "";
    public IncidentKind Kind { get; set; }

    /// <summary>When it happened: the ship's date and time, kept as written (like the hours of rest dates, not converted).</summary>
    public DateTime OccurredAt { get; set; }
    public string Place { get; set; } = "";
    public IncidentShipStatus ShipStatus { get; set; }
    public string Weather { get; set; } = "";
    public string Description { get; set; } = "";
    public string ImmediateAction { get; set; } = "";

    /// <summary>The machinery involved, from the vessel's machinery list when it is one, with its name kept beside the link.</summary>
    public Guid? EquipmentId { get; set; }
    public string EquipmentName { get; set; } = "";

    /// <summary>The risk assessment that covered the job, as the reporter names it (a reference such as RA-..., or text); empty when there was none.</summary>
    public string RiskAssessmentRef { get; set; } = "";

    public Guid? ReporterCrewId { get; set; }
    public string ReporterName { get; set; } = "";

    /// <summary>The reporter asks that the office sees "a crew member" (honoured only where the company's setting allows it).</summary>
    public bool AnonymousToOffice { get; set; }

    /// <summary>Shared as a lesson learned, without the persons involved.</summary>
    public bool ShareLesson { get; set; }

    /// <summary>What actually happened to people or things (0 none, then 1 to 5 on the company's risk matrix).</summary>
    public int ActualConsequence { get; set; }

    /// <summary>What it could have been (1 to 5).</summary>
    public int PotentialConsequence { get; set; }

    /// <summary>How likely it is to happen again (1 to 5).</summary>
    public int Likelihood { get; set; }

    /// <summary>The matrix band of the higher consequence and the likelihood, kept as it was when the report was submitted so a later change to the matrix never rewrites it.</summary>
    public string Band { get; set; } = "";

    /// <summary>Set when it is submitted: it goes to the office on the urgent lane.</summary>
    public bool IsUrgent { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;

    public List<IncidentPerson> Persons { get; set; } = [];
    public List<IncidentSupplement> Supplements { get; set; } = [];
}

/// <summary>A person in a report. The name is kept as text beside the optional crew link, so removing a crew record never damages the history.</summary>
public class IncidentPerson
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public Guid? CrewMemberId { get; set; }
    public string Name { get; set; } = "";
    public string Rank { get; set; } = "";
    public string Company { get; set; } = "";
    public IncidentPersonRole Role { get; set; }
    public string InjuryType { get; set; } = "";
    public string BodyPart { get; set; } = "";
    public IncidentTreatment Treatment { get; set; }
    public int DaysLost { get; set; }
    public int RestrictedDays { get; set; }
}

/// <summary>A dated addition to a report that has been reviewed. Never edited.</summary>
public class IncidentSupplement
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Something that happened to a report. Written once, by the side named in <see cref="IncidentEventKind"/>.</summary>
public class IncidentEvent
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid IncidentId { get; set; }
    public IncidentEventKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>The office's investigation. Each save is a new version and the newest counts; <see cref="Finished"/> says it is complete.</summary>
public class IncidentInvestigation
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid IncidentId { get; set; }
    public int Version { get; set; }
    public string Facts { get; set; } = "";
    public string ImmediateCause { get; set; } = "";

    /// <summary>The root causes picked from the company's list, as their names, separated by semicolons.</summary>
    public string RootCauses { get; set; } = "";
    public string WhyChain { get; set; } = "";
    public string ContributingFactors { get; set; } = "";
    public IncidentAssessmentFinding AssessmentFinding { get; set; }
    public string Lessons { get; set; } = "";
    public string InvestigatedBy { get; set; } = "";
    public bool Finished { get; set; }
    public DateTime? FinishedOn { get; set; }
    public string ByName { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>A root-cause category the office offers when investigating. Master data.</summary>
public class IncidentCause
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>The company's incident settings: one row per company. Master data.</summary>
public class IncidentSettings
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>A report entered more than this many hours after it happened is flagged as reported late (a fact, not a penalty).</summary>
    public int LateReportHours { get; set; } = 24;

    /// <summary>Reports of the same kind at the same place or on the same equipment within this many months are shown together.</summary>
    public int RepeatWindowMonths { get; set; } = 12;

    /// <summary>Days allowed for an investigation by band, from the highest band down, separated by commas ("7,14,30,60"); the bands below the list need none.</summary>
    public string InvestigationDays { get; set; } = "7,14,30,60";

    /// <summary>An injury with days lost, or one that needed an evacuation, goes to the office on the urgent lane.</summary>
    public bool UrgentForLostTime { get; set; } = true;

    /// <summary>Any pollution report goes to the office on the urgent lane.</summary>
    public bool UrgentForPollution { get; set; } = true;

    /// <summary>A reporter may ask that the office sees "a crew member" rather than their name.</summary>
    public bool AllowAnonymousToOffice { get; set; }
}
