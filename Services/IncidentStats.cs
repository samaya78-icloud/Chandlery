using System.Globalization;
using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>A submitted, unwithdrawn report with what the statistics need to know about where it stands.</summary>
public sealed record IncidentCase(Incident Incident, IncidentStatus Status, DateTime? ClosedAt, bool InvestigationOverdue)
{
    public bool IsOpen => Status is not (IncidentStatus.Closed or IncidentStatus.Withdrawn or IncidentStatus.Draft);
}

/// <summary>Hours worked by the people of a vessel in a month, and where the number came from ("Hours of rest" or "Typed").</summary>
public sealed record MonthHours(DateTime Month, decimal Hours, string Source);

public sealed record IncidentStatsRow(
    Guid VesselId, string Vessel, int Reports, int NearMisses, int InjuryReports, int LostTimeInjuries, int LostDays, int Recordable,
    decimal Hours, int MonthsWithHours, int Months, int LostTimeForRate, int RecordableForRate, double? Ltif, double? Trcf, double? NearMissesPerInjury,
    int Open, int Urgent, int Overdue, double? MeanDaysToClose, DateTime? LastReported,
    IReadOnlyDictionary<IncidentKind, int> ByKind, IReadOnlyDictionary<string, int> ByBand)
{
    /// <summary>Hours are known for every month of the period, so the rates read the whole of it.</summary>
    public bool HoursComplete => Months > 0 && MonthsWithHours == Months;
}

public sealed record IncidentMonthPoint(DateTime Month, int Reports, int NearMisses, int Injuries, int Other);

/// <summary>
/// The figures a safety officer reports on, as pure functions of the reports and the hours worked, so a vessel and the office reach the same numbers from the same
/// records. The definitions are stated on the screen; a company that reports under another standard changes them here, in one place, and the tests say what moved.
/// </summary>
public static class IncidentStats
{
    /// <summary>Per what the frequency rates are quoted: a million hours worked.</summary>
    public const double RateBase = 1_000_000;

    /// <summary>A person injured or ill, with days lost from work.</summary>
    public static bool IsLostTime(IncidentPerson p) => p.Role == IncidentPersonRole.Injured && p.DaysLost > 0;

    /// <summary>
    /// A recordable case: an injured person who lost days, was put on light duty, or needed more than first aid (medical treatment, hospital, evacuation).
    /// First aid alone is not recordable. (The company's reporting standard decides; this is the common reading.)
    /// </summary>
    public static bool IsRecordable(IncidentPerson p)
        => p.Role == IncidentPersonRole.Injured && (p.DaysLost > 0 || p.RestrictedDays > 0 || p.Treatment is IncidentTreatment.MedicalTreatment or IncidentTreatment.Hospital or IncidentTreatment.Evacuation);

    /// <summary>Cases per million hours worked, or null when no hours are known.</summary>
    public static double? Rate(int cases, decimal hours) => hours > 0 ? cases * RateBase / (double)hours : null;

    public static DateTime MonthOf(DateTime date) => new(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    public static List<DateTime> MonthsFrom(DateTime from, int count) => Enumerable.Range(0, Math.Max(0, count)).Select(n => MonthOf(from).AddMonths(n)).ToList();

    /// <summary>One vessel over a period of whole months. <paramref name="cases"/> are all the vessel's reports; those outside the period are used only for the last report date.</summary>
    public static IncidentStatsRow ForVessel(Guid vesselId, string vessel, IEnumerable<IncidentCase> cases, IEnumerable<MonthHours> hours, DateTime from, int months)
    {
        var all = cases.Where(c => c.Incident.SubmittedAt is not null && c.Status != IncidentStatus.Withdrawn).ToList();
        var period = MonthsFrom(from, months);
        var inPeriod = all.Where(c => period.Contains(MonthOf(c.Incident.OccurredAt))).ToList();
        var known = hours.Where(h => h.Hours > 0 && period.Contains(MonthOf(h.Month))).GroupBy(h => MonthOf(h.Month)).ToDictionary(g => g.Key, g => g.Sum(h => h.Hours));
        var forRate = inPeriod.Where(c => known.ContainsKey(MonthOf(c.Incident.OccurredAt))).ToList();

        int Injured(IEnumerable<IncidentCase> source, Func<IncidentPerson, bool> test) => source.Sum(c => c.Incident.Persons.Count(test));
        var lostTime = Injured(inPeriod, IsLostTime);
        var recordable = Injured(inPeriod, IsRecordable);
        var lostForRate = Injured(forRate, IsLostTime);
        var recordableForRate = Injured(forRate, IsRecordable);
        var totalHours = known.Values.Sum();
        var nearMisses = inPeriod.Count(c => c.Incident.Kind == IncidentKind.NearMiss);
        var injuryReports = inPeriod.Count(c => c.Incident.Kind == IncidentKind.InjuryIllness);
        var closed = inPeriod.Where(c => c.Status == IncidentStatus.Closed && c.ClosedAt is not null).ToList();

        return new IncidentStatsRow(vesselId, vessel, inPeriod.Count, nearMisses, injuryReports, lostTime, inPeriod.Sum(c => c.Incident.Persons.Where(IsLostTime).Sum(p => p.DaysLost)), recordable,
            totalHours, known.Count, period.Count, lostForRate, recordableForRate, Rate(lostForRate, totalHours), Rate(recordableForRate, totalHours),
            injuryReports == 0 ? null : (double)nearMisses / injuryReports,
            inPeriod.Count(c => c.IsOpen), inPeriod.Count(c => c.Incident.IsUrgent), inPeriod.Count(c => c.InvestigationOverdue),
            closed.Count == 0 ? null : closed.Average(c => (c.ClosedAt!.Value - c.Incident.SubmittedAt!.Value).TotalDays),
            all.Count == 0 ? null : all.Max(c => c.Incident.SubmittedAt),
            Enum.GetValues<IncidentKind>().ToDictionary(k => k, k => inPeriod.Count(c => c.Incident.Kind == k)),
            inPeriod.Where(c => c.Incident.Band != "").GroupBy(c => c.Incident.Band).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>
    /// The fleet from its vessels: counts and hours are added, and the rates are worked out again from the added cases and hours (never an average of rates).
    /// </summary>
    public static IncidentStatsRow Combine(string name, IReadOnlyList<IncidentStatsRow> rows, int months)
    {
        var hours = rows.Sum(r => r.Hours);
        var lostForRate = rows.Sum(r => r.LostTimeForRate);
        var recordableForRate = rows.Sum(r => r.RecordableForRate);
        var nearMisses = rows.Sum(r => r.NearMisses);
        var injuryReports = rows.Sum(r => r.InjuryReports);
        var closedRows = rows.Where(r => r.MeanDaysToClose is not null).ToList();
        return new IncidentStatsRow(Guid.Empty, name, rows.Sum(r => r.Reports), nearMisses, injuryReports, rows.Sum(r => r.LostTimeInjuries), rows.Sum(r => r.LostDays), rows.Sum(r => r.Recordable),
            hours, rows.Count == 0 ? 0 : rows.Min(r => r.MonthsWithHours), months, lostForRate, recordableForRate, Rate(lostForRate, hours), Rate(recordableForRate, hours),
            injuryReports == 0 ? null : (double)nearMisses / injuryReports, rows.Sum(r => r.Open), rows.Sum(r => r.Urgent), rows.Sum(r => r.Overdue),
            closedRows.Count == 0 ? null : closedRows.Average(r => r.MeanDaysToClose!.Value),
            rows.Where(r => r.LastReported is not null).Select(r => r.LastReported).DefaultIfEmpty(null).Max(),
            Enum.GetValues<IncidentKind>().ToDictionary(k => k, k => rows.Sum(r => r.ByKind.GetValueOrDefault(k))),
            rows.SelectMany(r => r.ByBand).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value)));
    }

    /// <summary>Reports by month of occurrence over the period: all, near-misses, injuries and the rest.</summary>
    public static List<IncidentMonthPoint> Trend(IEnumerable<IncidentCase> cases, DateTime from, int months)
    {
        var all = cases.Where(c => c.Incident.SubmittedAt is not null && c.Status != IncidentStatus.Withdrawn).ToList();
        return MonthsFrom(from, months).Select(m =>
        {
            var inMonth = all.Where(c => MonthOf(c.Incident.OccurredAt) == m).ToList();
            var near = inMonth.Count(c => c.Incident.Kind == IncidentKind.NearMiss);
            var injuries = inMonth.Count(c => c.Incident.Kind == IncidentKind.InjuryIllness);
            return new IncidentMonthPoint(m, inMonth.Count, near, injuries, inMonth.Count - near - injuries);
        }).ToList();
    }

    /// <summary>
    /// Vessels that have made no report for a while: a quiet vessel is a warning (nobody is looking, or nobody is saying), not a good sign. A vessel with no report at all counts.
    /// </summary>
    public static List<(Guid VesselId, string Vessel, DateTime? LastReported)> Quiet(IEnumerable<(Guid Id, string Name)> vessels, IEnumerable<IncidentCase> cases, DateTime today, int months)
    {
        var since = today.Date.AddMonths(-Math.Max(1, months));
        var last = cases.Where(c => c.Incident.SubmittedAt is not null && c.Status != IncidentStatus.Withdrawn).GroupBy(c => c.Incident.VesselId).ToDictionary(g => g.Key, g => g.Max(c => c.Incident.SubmittedAt));
        return vessels.Select(v => (v.Id, v.Name, last.GetValueOrDefault(v.Id)))
            .Where(v => v.Item3 is null || v.Item3.Value < since)
            .OrderBy(v => v.Item3 ?? DateTime.MinValue).ThenBy(v => v.Name).ToList();
    }

    /// <summary>The hours worked in a month from recorded work periods, in minutes: the sum over the crew and the days.</summary>
    public static decimal HoursFromMinutes(long minutes) => Math.Round(minutes / 60m, 2);
}

/// <summary>The incident figures as CSV.</summary>
public static class IncidentReports
{
    private static string D(DateTime? date) => date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
    private static string N(double? value, string format = "0.00") => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "";

    public static string RegisterCsv(IEnumerable<IncidentRow> rows) => WorklistReports.Csv(
        ["Report", "Vessel", "Kind", "Happened", "Place", "Band", "Urgent", "Status", "Reported", "Reported late", "Reported by", "People injured", "Days lost", "Investigation due", "Investigation overdue", "Open actions"],
        rows.Select(r => new[]
        {
            r.Incident.Code, r.VesselName, IncidentLook.Kind(r.Incident.Kind), r.Incident.OccurredAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.Incident.Place, r.Incident.Band,
            r.Incident.IsUrgent ? "yes" : "", IncidentStatusRules.Label(r.Status), D(r.Incident.SubmittedAt), r.ReportedLate ? "yes" : "", r.ReporterName,
            r.Incident.Persons.Count(p => p.Role == IncidentPersonRole.Injured).ToString(), r.Incident.Persons.Where(IncidentStats.IsLostTime).Sum(p => p.DaysLost).ToString(), D(r.Due),
            r.InvestigationOverdue ? "yes" : "", r.OpenActions.ToString(),
        }));

    public static string StatisticsCsv(IEnumerable<IncidentStatsRow> rows) => WorklistReports.Csv(
        ["Vessel", "Reports", "Near misses", "Injury reports", "Lost-time injuries", "Days lost", "Recordable cases", "Hours worked", "Months with hours", "Months", "Lost-time injury frequency", "Recordable case frequency",
         "Near misses per injury report", "Open", "Urgent", "Investigations overdue", "Mean days to close", "Last report"],
        rows.Select(r => new[]
        {
            r.Vessel, r.Reports.ToString(), r.NearMisses.ToString(), r.InjuryReports.ToString(), r.LostTimeInjuries.ToString(), r.LostDays.ToString(), r.Recordable.ToString(),
            r.Hours.ToString("0.##", CultureInfo.InvariantCulture), r.MonthsWithHours.ToString(), r.Months.ToString(), N(r.Ltif), N(r.Trcf), N(r.NearMissesPerInjury), r.Open.ToString(), r.Urgent.ToString(),
            r.Overdue.ToString(), N(r.MeanDaysToClose, "0.0"), D(r.LastReported),
        }));
}
