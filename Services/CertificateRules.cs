using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>Where a certificate or survey stands against the calendar. The order is the order of urgency.</summary>
public enum CertificateState
{
    /// <summary>No date has been given yet.</summary>
    NoDate,

    /// <summary>More than 90 days to go.</summary>
    Valid,

    /// <summary>A survey whose window is open, with more than 90 days to its last day.</summary>
    Window,

    /// <summary>61 to 90 days to go.</summary>
    Due90,

    /// <summary>31 to 60 days to go.</summary>
    Due60,

    /// <summary>0 to 30 days to go.</summary>
    Due30,

    /// <summary>Past its last day: expired, or a survey not done within its window.</summary>
    Overdue,
}

/// <summary>What the rules say about one certificate on one day.</summary>
public record CertificateStatus(DateTime? DueDate, DateTime? Deadline, CertificateState State, int? DaysLeft)
{
    /// <summary>A date is due within 30 days or already past: what the menu badge and the attention strip count.</summary>
    public bool NeedsAttention => State is CertificateState.Due30 or CertificateState.Overdue;
}

/// <summary>The rules for dates on certificates and surveys. Pure: no database, so they can be tested with a date.</summary>
public static class CertificateRules
{
    public const int FirstWarningDays = 90, SecondWarningDays = 60, FinalWarningDays = 30;

    /// <summary>The date that counts: the one carried by the latest event that carries one (the latest day, then the latest recording), otherwise the certificate's own.</summary>
    public static DateTime? EffectiveDue(ShipCertificate cert, IEnumerable<CertificateEvent> events)
        => events.Where(e => e.NewDueDate is not null).OrderBy(e => e.OccurredOn.Date).ThenBy(e => e.At).LastOrDefault()?.NewDueDate?.Date ?? cert.DueDate?.Date;

    /// <summary>The last day it may be done: the expiry of a certificate, or the end of a survey's window.</summary>
    public static DateTime? DeadlineOf(ShipCertificate cert, DateTime? due)
        => due is { } d ? d.Date.AddDays(cert.Kind == CertificateKind.Survey ? Math.Max(cert.WindowAfterDays, 0) : 0) : null;

    public static CertificateStatus StatusOf(ShipCertificate cert, IEnumerable<CertificateEvent> events, DateTime today)
    {
        var due = EffectiveDue(cert, events);
        if (DeadlineOf(cert, due) is not { } deadline) return new CertificateStatus(due, null, CertificateState.NoDate, null);
        var days = (deadline - today.Date).Days;
        if (days < 0) return new CertificateStatus(due, deadline, CertificateState.Overdue, days);
        var state = days <= FinalWarningDays ? CertificateState.Due30
            : days <= SecondWarningDays ? CertificateState.Due60
            : days <= FirstWarningDays ? CertificateState.Due90
            : cert.Kind == CertificateKind.Survey && today.Date >= due!.Value.AddDays(-Math.Max(cert.WindowBeforeDays, 0)) ? CertificateState.Window
            : CertificateState.Valid;
        return new CertificateStatus(due, deadline, state, days);
    }

    /// <summary>What an event of this type must carry, or null if it is complete.</summary>
    public static string? Problem(CertificateEventType type, DateTime? newDue, string reference, string note) => type switch
    {
        CertificateEventType.Issued when newDue is null => "Enter the new expiry date.",
        CertificateEventType.Extended when newDue is null => "Enter the date it is extended to.",
        CertificateEventType.Extended when string.IsNullOrWhiteSpace(reference) => "Enter the reference of the authority's letter or the surveyor.",
        CertificateEventType.SurveyDone when newDue is null => "Enter the date the next survey is due.",
        CertificateEventType.Note when string.IsNullOrWhiteSpace(note) => "Write the note.",
        _ => null,
    };
}

/// <summary>Words and colours for the screens, kept out of the markup so they read the same everywhere.</summary>
public static class CertificateLook
{
    public static string Label(CertificateState s) => s switch
    {
        CertificateState.NoDate => "No date",
        CertificateState.Valid => "Valid",
        CertificateState.Window => "Window open",
        CertificateState.Due90 => "Due in 90 days",
        CertificateState.Due60 => "Due in 60 days",
        CertificateState.Due30 => "Due in 30 days",
        _ => "Overdue",
    };

    public static string Tone(CertificateState s) => s switch
    {
        CertificateState.Valid => "success",
        CertificateState.Window or CertificateState.Due90 => "info",
        CertificateState.Due60 or CertificateState.Due30 => "warning",
        CertificateState.Overdue => "danger",
        _ => "neutral",
    };

    public static string Label(CertificateKind k) => k switch { CertificateKind.Statutory => "Statutory certificate", CertificateKind.Class => "Class certificate", _ => "Survey" };

    public static string Label(CertificateEventType t) => t switch
    {
        CertificateEventType.Issued => "Issued or renewed",
        CertificateEventType.Endorsed => "Endorsed",
        CertificateEventType.Extended => "Extended",
        CertificateEventType.SurveyDone => "Survey done",
        CertificateEventType.Document => "Document added",
        _ => "Note",
    };

    /// <summary>"in 12 days", "today", "5 days ago".</summary>
    public static string When(int? daysLeft) => daysLeft switch
    {
        null => "",
        0 => "today",
        1 => "tomorrow",
        > 1 => $"in {daysLeft} days",
        -1 => "yesterday",
        _ => $"{-daysLeft} days ago",
    };
}

/// <summary>A certificate or survey the office can add to a vessel in one click, with the usual cycle and window.</summary>
public record CertificateTemplate(CertificateKind Kind, string Title, string IssuedBy, int? IntervalMonths, int WindowBefore, int WindowAfter);

public static class CertificateStarter
{
    /// <summary>The usual set for a SOLAS cargo ship. The office edits dates and details after adding them.</summary>
    public static readonly IReadOnlyList<CertificateTemplate> Standard =
    [
        new(CertificateKind.Statutory, "Safety Management Certificate (SMC)", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "International Ship Security Certificate (ISSC)", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "International Oil Pollution Prevention Certificate (IOPP)", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "International Air Pollution Prevention Certificate (IAPP)", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "International Sewage Pollution Prevention Certificate (ISPP)", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "International Load Line Certificate", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "Cargo Ship Safety Construction Certificate", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "Cargo Ship Safety Equipment Certificate", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "Cargo Ship Safety Radio Certificate", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "Maritime Labour Certificate (MLC)", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Statutory, "Ballast Water Management Certificate", "Flag administration or RO", 60, 0, 0),
        new(CertificateKind.Class, "Certificate of Class", "Classification society", 60, 0, 0),
        new(CertificateKind.Survey, "Class annual survey", "Classification society", 12, 90, 90),
        new(CertificateKind.Survey, "Class intermediate survey", "Classification society", 30, 180, 180),
        new(CertificateKind.Survey, "Class special survey (renewal)", "Classification society", 60, 180, 0),
        new(CertificateKind.Survey, "Dry-docking survey", "Classification society", 30, 180, 180),
        new(CertificateKind.Survey, "Tailshaft survey", "Classification society", 60, 180, 0),
        new(CertificateKind.Survey, "Boiler survey", "Classification society", 24, 90, 90),
    ];
}
