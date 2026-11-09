namespace Chandlery.Models;

/// <summary>Why this version exists: the first assessment, a repeat of the job, the scheduled re-review, or a correction of a signed one.</summary>
public enum RaKind { Initial, Interim, Annual, Revision }

/// <summary>Worked out from the submissions and sign-offs of an assessment, never stored.</summary>
public enum RaStatus { Draft, Submitted, InReview, Returned, Signed }

public enum RaDecision { Started, Signed, Returned }

public enum RaAxis { Consequence, Probability }

// ---- the library: kept by the office, copied to every vessel (master data) ----

/// <summary>A kind of work (navigational, hot work, ...). The short code goes into references and lists.</summary>
public class RaCategory
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>The word for one level of the matrix (consequence 1 "Insignificant", probability 5 "Almost certain").</summary>
public class RaMatrixLabel
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public RaAxis Axis { get; set; }
    public int Level { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>A risk band: every factor from <see cref="MinFactor"/> up to the next band's minimum belongs to it.</summary>
public class RaMatrixBand
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public int MinFactor { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A reusable assessment written by the office. Assessments made from it keep their own copy, so changing it never changes them.</summary>
public class RaTemplate
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public string JobDescription { get; set; } = "";
    public string Contingency { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public class RaTemplateActivity
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TemplateId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
}

public class RaTemplateHazard
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TemplateId { get; set; }
    public Guid ActivityId { get; set; }
    public string Hazard { get; set; } = "";
    public string Effect { get; set; } = "";
    public string ExistingControls { get; set; } = "";
    public string AdditionalControls { get; set; } = "";
    public int ConsequenceBefore { get; set; }
    public int ProbabilityBefore { get; set; }
    public int ConsequenceAfter { get; set; }
    public int ProbabilityAfter { get; set; }
    public string Contingency { get; set; } = "";
    public int SortOrder { get; set; }
}

// ---- an assessment: one document, written by one side, signed once, then frozen ----

/// <summary>
/// A risk assessment, one version of it. Versions of the same assessment share the <see cref="Code"/> and differ in <see cref="Version"/>. It is written by the side that
/// started it, submitted, and signed off through <see cref="RaSignOff"/> records; once signed it is locked, and a change is a new version that links back to this one.
/// </summary>
public class RiskAssessment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>The vessel it is for (or was made on); this decides who receives it.</summary>
    public Guid VesselId { get; set; }

    /// <summary>Reference, like SF-RA-2026-0001 (an H after the year for one started ashore). Every version of an assessment shares it.</summary>
    public string Code { get; set; } = "";
    public int Version { get; set; } = 1;
    public Guid? PreviousVersionId { get; set; }
    public RaKind Kind { get; set; }
    public Guid? CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public string CategoryCode { get; set; } = "";
    public Guid? TemplateId { get; set; }
    public int TemplateVersion { get; set; }
    public string JobDescription { get; set; } = "";
    public DateTime AssessedOn { get; set; }
    public string Contingency { get; set; } = "";

    /// <summary>The months between signing and the next review, as it was when this version was made.</summary>
    public int ReviewPeriodMonths { get; set; } = 11;

    /// <summary>How many times it has been submitted. Zero means a draft.</summary>
    public int SubmissionCount { get; set; }
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Worked out when it is submitted, so lists and alerts need not read every hazard.</summary>
    public int HighestResidualFactor { get; set; }
    public string HighestResidualBand { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Set on a copy made by sharing: the assessment it was copied from, so the vessel can see where it came from.</summary>
    public Guid? SharedFromId { get; set; }
    public string SharedFromCode { get; set; } = "";
    public int SharedFromVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;

    public List<RaTeamMember> Team { get; set; } = new();
    public List<RaActivity> Activities { get; set; } = new();
    public List<RaHazard> Hazards { get; set; } = new();
}

public class RaTeamMember
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public string Name { get; set; } = "";
    public string Rank { get; set; } = "";

    /// <summary>Set when the person is a user of the system; otherwise the name was typed.</summary>
    public Guid? UserId { get; set; }
}

public class RaActivity
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>One hazard of one activity, scored before and after controls. The scores, factors and bands are kept as they were when saved.</summary>
public class RaHazard
{
    public Guid Id { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid ActivityId { get; set; }
    public string Hazard { get; set; } = "";
    public string Effect { get; set; } = "";
    public int ConsequenceBefore { get; set; }
    public int ProbabilityBefore { get; set; }
    public string ExistingControls { get; set; } = "";
    public string AdditionalControls { get; set; } = "";
    public int ConsequenceAfter { get; set; }
    public int ProbabilityAfter { get; set; }

    /// <summary>Needed only when the risk after controls is not lower than before.</summary>
    public string ResidualReason { get; set; } = "";
    public string ResponsibleName { get; set; } = "";
    public DateTime? DueDate { get; set; }
    public string Contingency { get; set; } = "";
    public int SortOrder { get; set; }

    public int FactorBefore { get; set; }
    public string BandBefore { get; set; } = "";
    public int FactorAfter { get; set; }
    public string BandAfter { get; set; } = "";

    /// <summary>The worklist task raised for the additional control, so resubmitting or a new version never raises it twice.</summary>
    public Guid? WorklistTaskId { get; set; }
}

/// <summary>A reviewer's response to one submission of an assessment. Written once.</summary>
public class RaSignOff
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid AssessmentId { get; set; }

    /// <summary>Which submission this answers; a decision on an earlier submission no longer counts once the assessment is submitted again.</summary>
    public int ForSubmission { get; set; }
    public RaDecision Decision { get; set; }
    public string Comment { get; set; } = "";
    public string ReviewerName { get; set; } = "";
    public string ReviewerRank { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}
