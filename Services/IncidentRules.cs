using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>The numbers the incident rules use, read from the company's settings (or the defaults when it has none).</summary>
public sealed record IncidentLimits(int LateReportHours, int RepeatWindowMonths, IReadOnlyList<int> InvestigationDays, bool UrgentForLostTime, bool UrgentForPollution, bool AllowAnonymousToOffice)
{
    public static readonly IncidentLimits Default = Of(null);

    public static IncidentLimits Of(IncidentSettings? s)
    {
        s ??= new IncidentSettings();
        return new(s.LateReportHours, s.RepeatWindowMonths, ParseDays(s.InvestigationDays), s.UrgentForLostTime, s.UrgentForPollution, s.AllowAnonymousToOffice);
    }

    /// <summary>"7,14,30,60" gives 7, 14, 30 and 60; anything that is not a whole number of days is left out.</summary>
    public static IReadOnlyList<int> ParseDays(string? text)
        => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => int.TryParse(p, out var n) && n > 0 ? n : 0).Where(n => n > 0).ToList();
}

/// <summary>Everything the status of a report is worked out from.</summary>
public sealed record IncidentFacts(Incident Incident, IReadOnlyList<IncidentEvent> Events, IncidentInvestigation? Investigation, int OpenActions, bool InvestigationRequired);

/// <summary>A shared lesson: what happened and what was learned, with no person's name and no injury detail.</summary>
public sealed record IncidentLesson(Guid IncidentId, string Code, IncidentKind Kind, DateTime Date, IncidentShipStatus ShipStatus, string Place, string Description, string ImmediateAction,
    string EquipmentName, string Band, string RootCauses, string Lessons);

/// <summary>
/// The rules of incident reporting, as pure functions of the report, its events and the company's settings, so a vessel and the office reach the same answer
/// from the same records. Thresholds are data (the settings), never constants here.
/// </summary>
public static class IncidentRules
{
    public static bool IsLevel(int level) => RiskMatrix.IsLevel(level);

    /// <summary>The checks on the severity entered with a report: 0 to 5 for what happened, 1 to 5 for what it could have been and for how likely it is again.</summary>
    public static string? SeverityProblem(int actual, int potential, int likelihood)
    {
        if (actual is < 0 or > RiskMatrix.Levels) return $"What actually happened is scored 0 (nothing) to {RiskMatrix.Levels}.";
        if (!IsLevel(potential)) return $"What it could have been is scored 1 to {RiskMatrix.Levels}.";
        if (!IsLevel(likelihood)) return $"How likely it is to happen again is scored 1 to {RiskMatrix.Levels}.";
        return null;
    }

    /// <summary>The matrix band of the higher of the two consequences and the likelihood.</summary>
    public static string BandOf(RiskMatrix matrix, int actual, int potential, int likelihood)
        => SeverityProblem(actual, potential, likelihood) is not null ? "" : matrix.BandOf(RiskMatrix.Factor(Math.Max(actual, potential), likelihood));

    /// <summary>
    /// Goes to the office at once: an injury with days lost or an evacuation, any pollution report (where the settings say so), or a band in the top two.
    /// </summary>
    public static bool IsUrgent(Incident incident, RiskMatrix matrix, IncidentLimits limits)
    {
        if (limits.UrgentForPollution && incident.Kind == IncidentKind.Pollution) return true;
        if (limits.UrgentForLostTime && incident.Persons.Any(p => p.Role == IncidentPersonRole.Injured && (p.DaysLost > 0 || p.Treatment == IncidentTreatment.Evacuation))) return true;
        return incident.Band != "" && matrix.RankOf(incident.Band) >= 0 && matrix.IsSerious(incident.Band);
    }

    /// <summary>Entered more than the allowed hours after it happened. A fact for the record, not a penalty.</summary>
    public static bool ReportedLate(Incident incident, IncidentLimits limits)
        => incident.SubmittedAt is { } at && at - incident.OccurredAt > TimeSpan.FromHours(limits.LateReportHours);

    /// <summary>The days allowed to investigate a report of this band: the list is read from the highest band down, and the bands below it need none.</summary>
    public static int? InvestigationDays(string band, RiskMatrix matrix, IncidentLimits limits)
    {
        var rank = matrix.RankOf(band);
        if (rank < 0) return null;
        var fromTop = matrix.Bands.Count - 1 - rank;
        return fromTop < limits.InvestigationDays.Count ? limits.InvestigationDays[fromTop] : null;
    }

    public static bool InvestigationRequired(string band, RiskMatrix matrix, IncidentLimits limits) => InvestigationDays(band, matrix, limits) is not null;

    /// <summary>The date by which the investigation is due: the day the report was submitted plus the days for its band. Null when none is needed.</summary>
    public static DateTime? InvestigationDue(Incident incident, RiskMatrix matrix, IncidentLimits limits)
        => incident.SubmittedAt is { } at && InvestigationDays(incident.Band, matrix, limits) is { } days ? at.Date.AddDays(days) : null;

    /// <summary>An observation, or a report of a band that needs no investigation, can be closed without one, with a note saying why.</summary>
    public static bool CanSkipInvestigation(Incident incident, RiskMatrix matrix, IncidentLimits limits)
        => incident.Kind == IncidentKind.Observation || !InvestigationRequired(incident.Band, matrix, limits);

    /// <summary>Past the due date with the investigation not finished, for a report still open.</summary>
    public static bool InvestigationOverdue(IncidentFacts facts, IncidentStatus status, RiskMatrix matrix, IncidentLimits limits, DateTime today)
        => status is not (IncidentStatus.Closed or IncidentStatus.Withdrawn or IncidentStatus.Draft)
           && !Finished(facts)
           && InvestigationDue(facts.Incident, matrix, limits) is { } due && due < today.Date;

    private static bool Finished(IncidentFacts f) => f.Investigation?.Finished == true || f.Events.Any(e => e.Kind == IncidentEventKind.NoInvestigationNeeded);

    private static bool Opened(IncidentFacts f) => f.Events.Any(e => e.Kind == IncidentEventKind.InvestigationOpened) || f.Investigation is not null;

    /// <summary>What stops a report being closed now, in words. Empty means it can be closed.</summary>
    public static List<string> CloseBlockers(IncidentFacts f)
    {
        var problems = new List<string>();
        var status = IncidentStatusRules.Of(f);
        if (status == IncidentStatus.Withdrawn) return ["The report was withdrawn."];
        if (status == IncidentStatus.Closed) return ["The report is already closed."];
        if (f.Incident.SubmittedAt is null) return ["The report has not been submitted."];
        if (!f.Events.Any(e => e.Kind == IncidentEventKind.Reviewed)) problems.Add("The Master has not reviewed it yet.");
        if (!Finished(f))
        {
            if (Opened(f)) problems.Add("The investigation is not finished.");
            else if (f.InvestigationRequired) problems.Add($"An investigation is required for a report of band {f.Incident.Band}: open one, or if it is not needed say why.");
        }
        if (f.OpenActions > 0) problems.Add($"{f.OpenActions} action{(f.OpenActions == 1 ? " is" : "s are")} still open.");
        return problems;
    }

    /// <summary>The name the viewer is shown for the reporter: the office sees "A crew member" where the reporter asked for it and the company allows it.</summary>
    public static string ReporterNameFor(Incident incident, bool viewerIsOffice, IncidentLimits limits)
        => viewerIsOffice && incident.AnonymousToOffice && limits.AllowAnonymousToOffice ? "A crew member" : incident.ReporterName;

    /// <summary>
    /// Earlier reports of the same kind that look like this one: at the same place, or on the same equipment, within the window before it. Withdrawn and unsubmitted
    /// reports are not offered. Newest first.
    /// </summary>
    public static List<Incident> Repeats(Incident subject, IEnumerable<Incident> others, int windowMonths)
    {
        var from = subject.OccurredAt.AddMonths(-windowMonths);
        return others
            .Where(o => o.Id != subject.Id && o.SubmittedAt is not null && o.Kind == subject.Kind && o.OccurredAt <= subject.OccurredAt && o.OccurredAt >= from)
            .Where(o => (subject.Place.Trim() != "" && string.Equals(o.Place.Trim(), subject.Place.Trim(), StringComparison.OrdinalIgnoreCase))
                        || (subject.EquipmentId is { } e && o.EquipmentId == e)
                        || (subject.EquipmentId is null && subject.EquipmentName.Trim() != "" && string.Equals(o.EquipmentName.Trim(), subject.EquipmentName.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(o => o.OccurredAt).ThenByDescending(o => o.Id).ToList();
    }

    /// <summary>A third report like the earlier two is the point at which the risk assessment for the job should be looked at again.</summary>
    public static bool SuggestsAssessmentReview(int repeats) => repeats >= 2;

    /// <summary>
    /// The shared form of a report: nothing about who was involved or injured, no reporter and no vessel. Only a report marked to be shared, and reviewed, is a lesson.
    /// The free text is the reporter's own, so the form tells them not to put names in it.
    /// </summary>
    public static IncidentLesson? LessonOf(Incident incident, IncidentInvestigation? investigation, bool reviewed)
        => incident.ShareLesson && reviewed && incident.SubmittedAt is not null
            ? new IncidentLesson(incident.Id, incident.Code, incident.Kind, incident.OccurredAt.Date, incident.ShipStatus, incident.Place, incident.Description, incident.ImmediateAction,
                incident.EquipmentName, incident.Band, investigation?.RootCauses ?? "", investigation?.Lessons ?? "")
            : null;
}

/// <summary>Where a report stands, worked out from the report, its events, the newest investigation and the open actions: the same on both sides.</summary>
public static class IncidentStatusRules
{
    public static IncidentStatus Of(IncidentFacts f)
    {
        var events = f.Events.OrderBy(e => e.At).ThenBy(e => e.Id).ToList();
        if (events.Any(e => e.Kind == IncidentEventKind.Withdrawn)) return IncidentStatus.Withdrawn;
        if (f.Incident.SubmittedAt is null) return IncidentStatus.Draft;

        var lastClosing = events.LastOrDefault(e => e.Kind is IncidentEventKind.Closed or IncidentEventKind.Reopened);
        if (lastClosing?.Kind == IncidentEventKind.Closed) return IncidentStatus.Closed;
        if (!events.Any(e => e.Kind == IncidentEventKind.Reviewed)) return IncidentStatus.Reported;

        var finished = f.Investigation?.Finished == true || events.Any(e => e.Kind == IncidentEventKind.NoInvestigationNeeded);
        var opened = events.Any(e => e.Kind == IncidentEventKind.InvestigationOpened) || f.Investigation is not null;
        if (!finished)
        {
            if (opened) return IncidentStatus.Investigating;
            if (f.InvestigationRequired) return IncidentStatus.Reviewed;
        }
        return f.OpenActions > 0 ? IncidentStatus.ActionsOpen : IncidentStatus.ReadyToClose;
    }

    /// <summary>Until the Master has reviewed it the reporter's text can still be changed; after that only supplements are added.</summary>
    public static bool TextIsLocked(IncidentStatus status) => status is not (IncidentStatus.Draft or IncidentStatus.Reported);

    /// <summary>The words for a status, as screens show it.</summary>
    public static string Label(IncidentStatus status) => status switch
    {
        IncidentStatus.Draft => "Draft",
        IncidentStatus.Reported => "Reported",
        IncidentStatus.Reviewed => "Reviewed",
        IncidentStatus.Investigating => "Investigating",
        IncidentStatus.ActionsOpen => "Actions open",
        IncidentStatus.ReadyToClose => "Ready to close",
        IncidentStatus.Closed => "Closed",
        _ => "Withdrawn",
    };
}
