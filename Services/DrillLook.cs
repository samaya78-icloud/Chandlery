using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>The words and chip tones for drills, in one place so every screen says the same thing.</summary>
public static class DrillLook
{
    public static string Category(DrillCategory category) => category switch
    {
        DrillCategory.Emergency => "Emergency",
        DrillCategory.Security => "Security",
        DrillCategory.Pollution => "Pollution",
        DrillCategory.Medical => "Medical",
        _ => "Training",
    };

    /// <summary>"Every month", "Every 3 months", "Every 45 days".</summary>
    public static string Interval(DrillIntervalKind kind, int count)
        => kind == DrillIntervalKind.Days ? (count == 1 ? "Every day" : $"Every {count} days") : (count == 1 ? "Every month" : $"Every {count} months");

    public static string Interval(DrillRule rule) => Interval(rule.Kind, rule.Count);

    public static (string Label, string Tone) Status(DrillStatus status) => status switch
    {
        DrillStatus.NotDue => ("Not due", "success"),
        DrillStatus.DueSoon => ("Due soon", "info"),
        DrillStatus.Due => ("Due today", "warning"),
        DrillStatus.Overdue => ("Overdue", "danger"),
        _ => ("Never held", "warning"),
    };

    public static string Attendance(DrillAttendance attendance) => attendance switch
    {
        DrillAttendance.Present => "Took part",
        DrillAttendance.Absent => "Absent",
        _ => "Observer",
    };

    public static string Absence(DrillAbsence absence) => absence switch
    {
        DrillAbsence.OnWatch => "On watch",
        DrillAbsence.Ashore => "Ashore",
        DrillAbsence.OnLeave => "On leave",
        DrillAbsence.Sick => "Sick",
        DrillAbsence.JoinedAfter => "Joined after",
        DrillAbsence.Other => "Other reason",
        _ => "",
    };

    public static string When(DateTime at) => at.ToString("d MMM yyyy HH:mm");

    public static string Day(DateTime? date) => date is { } d ? d.ToString("d MMM yyyy") : "–";

    public static string Duration(TimeSpan? span) => span is not { } s ? "" : s.TotalMinutes < 60 ? $"{(int)s.TotalMinutes} min" : $"{(int)s.TotalHours} h {s.Minutes:00} min";
}
