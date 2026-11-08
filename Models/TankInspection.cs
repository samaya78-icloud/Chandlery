namespace Chandlery.Models;

public enum TankType { Cargo, Ballast, Slops, FreshWater, VoidSpace, Other }

public enum CriterionGroup { VariableData, InternalFittings, Structure, Reports, TypeSpecific }

/// <summary>How a criterion is answered. The values of each scale are fixed lists in <c>TankScales</c>.</summary>
public enum ScaleKind { Rating, YesNoNa, PercentBucket }

/// <summary>Worked out from the submissions and reviews of an inspection, never stored.</summary>
public enum InspectionStatus { Draft, Submitted, InReview, Returned, Reviewed }

public enum ReviewDecision { Started, Accepted, Returned }

// ---- checklists: kept by the office, copied to every vessel (master data) ----

/// <summary>A checklist for one kind of tank. Inspections made from it keep a copy of what it asked, so changing it later never changes them.</summary>
public class TankChecklistTemplate
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public TankType TankType { get; set; }

    /// <summary>Empty means every ship type; a type-specific set (gas carriers...) names it.</summary>
    public string ShipType { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

/// <summary>One thing to look at, with the scale it is answered on and which answers count as an exception.</summary>
public class TankCriterion
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TemplateId { get; set; }
    public CriterionGroup Group { get; set; }
    public string Name { get; set; } = "";
    public ScaleKind Scale { get; set; }

    /// <summary>Answers that make an exception, separated by commas (for a rating: "Fair,Bad").</summary>
    public string ExceptionValues { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

// ---- a vessel's tanks: documents with one owner (the side that made them) ----

/// <summary>A tank on a vessel, with its place on the tank plan and its structural data.</summary>
public class Tank
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public TankType Type { get; set; }

    // Place on the plan: a rectangle on a grid of 100 columns by 40 rows (profile view, bow to the right).
    public int PlanX { get; set; }
    public int PlanY { get; set; }
    public int PlanWidth { get; set; } = 10;
    public int PlanHeight { get; set; } = 8;

    // Structural data.
    public string SteelGradeDeck { get; set; } = "";
    public string SteelGradeBottom { get; set; } = "";
    public string SteelGradeSide { get; set; } = "";
    public string SteelGradeBulkhead { get; set; } = "";
    public string OriginalCoating { get; set; } = "";
    public decimal? CoatedAreaM2 { get; set; }
    public string CathodicProtection { get; set; } = "";
    public decimal? ProtectedAreaM2 { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>
/// An inspection of one tank, made and filled in by the side that started it (normally the vessel). Once submitted it is looked at by the office through
/// <see cref="TankInspectionReview"/> records; its status is worked out from those. A returned inspection is corrected and submitted again (the submission count goes up);
/// a reviewed one is locked.
/// </summary>
public class TankInspection
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid TankId { get; set; }

    /// <summary>Reference, like SF-TI-2026-0001 (an H after the year for one started ashore).</summary>
    public string Code { get; set; } = "";
    public Guid? TemplateId { get; set; }
    public string TemplateName { get; set; } = "";
    public int TemplateVersion { get; set; }
    public DateTime InspectedOn { get; set; }

    /// <summary>"At sea" or the port.</summary>
    public string Place { get; set; } = "";
    public string WorkDone { get; set; } = "";
    public Guid? PlannedJobId { get; set; }
    public string GeneralComments { get; set; } = "";
    public string InspectorName { get; set; } = "";

    /// <summary>How many times it has been submitted. Zero means a draft.</summary>
    public int SubmissionCount { get; set; }
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Worked out when it is submitted, so lists and alerts need not read every result.</summary>
    public int ExceptionCount { get; set; }
    public bool HasBadRating { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;

    public List<TankInspectionResult> Results { get; set; } = new();
}

/// <summary>One answer. It keeps its own copy of the criterion, so the history reads the same whatever happens to the template.</summary>
public class TankInspectionResult
{
    public Guid Id { get; set; }
    public Guid InspectionId { get; set; }
    public Guid? CriterionId { get; set; }
    public CriterionGroup Group { get; set; }
    public string CriterionName { get; set; } = "";
    public ScaleKind Scale { get; set; }
    public string ExceptionValues { get; set; } = "";
    public int SortOrder { get; set; }

    /// <summary>The answer (a value of the scale); empty until answered.</summary>
    public string Value { get; set; } = "";
    public string Comment { get; set; } = "";

    /// <summary>The worklist task raised for this exception, so resubmitting never raises it twice.</summary>
    public Guid? WorklistTaskId { get; set; }
}

/// <summary>The office's response to one submission of an inspection. Written once.</summary>
public class TankInspectionReview
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid InspectionId { get; set; }

    /// <summary>Which submission this answers; a review of an earlier submission no longer counts once the inspection is submitted again.</summary>
    public int ForSubmission { get; set; }
    public ReviewDecision Decision { get; set; }
    public string Comment { get; set; } = "";
    public string ReviewerName { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}
