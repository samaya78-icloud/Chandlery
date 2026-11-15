namespace Chandlery.Models;

/// <summary>How often a form is due. Voyage, PMS, Periodical, ThirdParty and None are not worked out from the calendar: they are started by hand or by another module.</summary>
public enum FormPeriodicity { None, Daily, Weekly, Fortnight, Monthly, Quarterly, HalfYearly, Yearly, Voyage, Pms, Periodical, ThirdParty }

/// <summary>Whether the next entry is due on the calendar (every period from a start date) or a period after the previous entry.</summary>
public enum FormScheduleBasis { Calendar, FromPrevious }

public enum FormVersionStatus { Draft, Published, Retired }

/// <summary>Worked out from the submissions and reviews of an entry, never stored (the same shape as a tank inspection's).</summary>
public enum FormStatus { Draft, Submitted, InReview, Returned, Reviewed }

// ---- the catalogue: kept by the office, copied to the vessels a form applies to (master data) ----

/// <summary>
/// A kind of form (for example <c>PM05 - Crankpin Bearings</c>). What it asks lives in its versions. It applies to the vessel groups and the named vessels listed
/// (a group applies to every vessel beneath it); with both lists empty it applies to every vessel.
/// </summary>
public class FormType
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Department { get; set; } = "";
    public FormPeriodicity Periodicity { get; set; }
    public FormScheduleBasis Basis { get; set; }

    /// <summary>The date the first period starts, for a form due on the calendar.</summary>
    public DateTime? StartsOn { get; set; }

    /// <summary>Vessel group ids separated by commas; see <see cref="FormApplicability"/>.</summary>
    public string AppliesToGroups { get; set; } = "";

    /// <summary>Vessel ids separated by commas.</summary>
    public string AppliesToVessels { get; set; } = "";

    /// <summary>A form the office fills itself (a third-party or shore form): never sent to a vessel.</summary>
    public bool OfficeOnly { get; set; }
    public bool IsActive { get; set; } = true;
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One version of what a form asks: a JSON definition (see <c>FormDefinitionRules</c>). A draft is the office's own; once published it never changes, so an entry
/// filled on it can always be checked against it. The applicability is copied from the type when the version is published, so a vessel can tell, from the version
/// alone, whether it should hold it.
/// </summary>
public class FormVersion
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid FormTypeId { get; set; }
    public int Number { get; set; }
    public FormVersionStatus Status { get; set; }
    public string Definition { get; set; } = "";
    public string Note { get; set; } = "";
    public string AppliesToGroups { get; set; } = "";
    public string AppliesToVessels { get; set; } = "";
    public bool OfficeOnly { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

// ---- an entry: a document owned by the side that fills it ----

/// <summary>
/// One filled form, for one vessel and one date, on the version named. Filled by one side (normally the vessel); once submitted it is looked at by the office through
/// <see cref="FormReview"/> records. A draft or a returned entry can be changed by the side that made it; a reviewed one is locked and a correction is a new entry
/// that names this one.
/// </summary>
public class FormEntry
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid FormTypeId { get; set; }
    public Guid VersionId { get; set; }
    public int VersionNumber { get; set; }

    /// <summary>Copied from the type when the entry is started, so lists and exports read well even before the catalogue has arrived.</summary>
    public string FormCode { get; set; } = "";
    public string FormTitle { get; set; } = "";

    /// <summary>Reference, like SF-FRM-2026-0001 (an H after the year for one started ashore).</summary>
    public string Code { get; set; } = "";

    /// <summary>The day the form is about (the period or the voyage it covers).</summary>
    public DateTime FormDate { get; set; }

    /// <summary>The answers, as JSON keyed by field and cell ids (see <c>FormValueRules</c>).</summary>
    public string Values { get; set; } = "{}";
    public Guid? CorrectsEntryId { get; set; }
    public bool FilledAshore { get; set; }

    /// <summary>The form is the office's own (a third-party or shore form): the entry never leaves the office.</summary>
    public bool OfficeOnly { get; set; }

    /// <summary>How many times it has been submitted. Zero means a draft.</summary>
    public int SubmissionCount { get; set; }
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Worked out when it is submitted, so lists and alerts need not read every value.</summary>
    public int ExceptionCount { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;
}

/// <summary>The office's response to one submission of an entry. Written once.</summary>
public class FormReview
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid EntryId { get; set; }

    /// <summary>Which submission this answers; a review of an earlier submission no longer counts once the entry is submitted again.</summary>
    public int ForSubmission { get; set; }
    public ReviewDecision Decision { get; set; }
    public string Comment { get; set; } = "";
    public string ReviewerName { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Office;
}
