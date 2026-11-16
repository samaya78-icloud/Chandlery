namespace Chandlery.Models;

/// <summary>A statutory certificate (SMC, Load Line...), a class certificate, or a class or statutory survey that comes round on a cycle.</summary>
public enum CertificateKind { Statutory, Class, Survey }

public enum CertificateEventType
{
    /// <summary>The certificate was issued or renewed: carries the new expiry date.</summary>
    Issued,

    /// <summary>The surveyor endorsed it (an annual or intermediate endorsement). May carry a new date.</summary>
    Endorsed,

    /// <summary>The authority extended it: carries the new date and the reference of the letter.</summary>
    Extended,

    /// <summary>A survey was carried out: carries the date the next one is due.</summary>
    SurveyDone,

    /// <summary>Anything worth recording that changes no date.</summary>
    Note,

    /// <summary>A scan or a photo was added.</summary>
    Document,
}

/// <summary>
/// A certificate or survey of one vessel. Set up and changed by the office; the vessel adds to its history through <see cref="CertificateEvent"/>.
/// The date that counts is worked out from the events (<c>CertificateRules.EffectiveDue</c>), so a renewal or an extension never edits this record.
/// </summary>
public class ShipCertificate
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }

    /// <summary>The reference people quote, e.g. MVSF-CRT-2026-H0004.</summary>
    public string Code { get; set; } = "";
    public CertificateKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string CertificateNumber { get; set; } = "";

    /// <summary>The flag administration, recognised organisation or class society that issued it.</summary>
    public string IssuedBy { get; set; } = "";
    public DateTime? IssuedOn { get; set; }

    /// <summary>The expiry of a certificate, or the date a survey falls due (its anniversary). The events may move it.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>How many days before the due date a survey may be done (its window opens).</summary>
    public int WindowBeforeDays { get; set; }

    /// <summary>How many days after the due date a survey may still be done. A certificate has none: it expires on the date.</summary>
    public int WindowAfterDays { get; set; }

    /// <summary>The cycle, in months (12 for an annual survey, 60 for a renewal). Informational: the next date is entered when a survey is recorded.</summary>
    public int? IntervalMonths { get; set; }

    /// <summary>Equipment surveyed by class under this item, if it is about one machinery item.</summary>
    public Guid? MachineryItemId { get; set; }
    public string Notes { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string CreatedByName { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>The office sets certificates up; the vessel receives them.</summary>
    public string OwnerStage { get; set; } = SyncStages.Office;
}

/// <summary>Something that happened to a certificate. Written once, by either side, never changed.</summary>
public class CertificateEvent
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid VesselId { get; set; }
    public Guid CertificateId { get; set; }
    public CertificateEventType Type { get; set; }

    /// <summary>The day it happened (the day of the survey or the endorsement), not the day it was recorded.</summary>
    public DateTime OccurredOn { get; set; }

    /// <summary>The date the certificate or survey is due from now on. Empty when the event changes no date.</summary>
    public DateTime? NewDueDate { get; set; }

    /// <summary>The surveyor, the authority or the reference of the letter.</summary>
    public string Reference { get; set; } = "";
    public string Note { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? AuthorUserId { get; set; }
    public DateTime At { get; set; }
    public string OwnerStage { get; set; } = SyncStages.Vessel;

    /// <summary>Scans and photos added with this event. They are part of it, so they travel with it.</summary>
    public List<CertificateFile> Files { get; set; } = [];
}

public class CertificateFile : RecordFile
{
    public Guid CertificateEventId { get; set; }
}
