using System.Globalization;
using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>One vessel's permits: what is open now, and what went wrong in the period.</summary>
public sealed record PermitVesselActivity(Guid VesselId, string Name, int Active, int Suspended, int Awaiting, int Expired, int GasExceedances, int InPeriod);

/// <summary>Permits raised in a month, by kind.</summary>
public sealed record PermitTypeMonth(string TypeName, string Month, int Count);

/// <summary>A permit the office should look at, and why.</summary>
public sealed record PermitAttention(PermitRow Row, string Why);

public sealed record PermitFleetView(DateTime From, DateTime To, IReadOnlyList<PermitVesselActivity> Vessels, IReadOnlyList<PermitTypeMonth> ByTypeMonth, IReadOnlyList<PermitAttention> Attention)
{
    /// <summary>Permits past their window and not closed, across the fleet. What the menu shows.</summary>
    public int Expired => Vessels.Sum(v => v.Expired);
}

/// <summary>
/// The fleet's permit activity for the office, worked out from the permits each vessel has sent ashore. Pure: the rows go in, the table comes out. "Open now" counts are as of the
/// moment the rows were read; the period only limits the counts by month, the permits in the period and the gas exceedances.
/// </summary>
public static class PermitFleet
{
    public static PermitFleetView Build(DateTime from, DateTime to, IEnumerable<PermitRow> rows)
    {
        var all = rows.GroupBy(r => r.Permit.Id).Select(g => g.First()).ToList();
        bool InPeriod(DateTime at) => at >= from && at <= to;

        var vessels = all.GroupBy(r => (r.Permit.VesselId, r.VesselName)).Select(g => new PermitVesselActivity(g.Key.VesselId, g.Key.VesselName,
            g.Count(r => r.State == PermitState.Active), g.Count(r => r.State == PermitState.Suspended), g.Count(r => r.State is PermitState.Requested or PermitState.Issued),
            g.Count(r => r.State == PermitState.Expired), g.Sum(r => r.Permit.Readings.Count(x => !x.WithinLimits && InPeriod(x.At))), g.Count(r => InPeriod(r.Permit.ValidFrom))))
            .OrderByDescending(v => v.Expired).ThenByDescending(v => v.GasExceedances).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var byMonth = all.Where(r => InPeriod(r.Permit.ValidFrom))
            .GroupBy(r => (r.Permit.TypeName, Month: r.Permit.ValidFrom.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
            .Select(g => new PermitTypeMonth(g.Key.TypeName, g.Key.Month, g.Count())).OrderBy(x => x.Month).ThenBy(x => x.TypeName).ToList();

        var attention = new List<PermitAttention>();
        foreach (var r in all.OrderBy(r => r.Until))
        {
            if (r.State == PermitState.Expired) attention.Add(new(r, $"Expired {PermitLook.When(r.Until)} and not closed"));
            else if (r.State == PermitState.Active && !r.Valid) attention.Add(new(r, $"Active but not valid: {r.Reason.ToLowerInvariant()}"));
            else if (r.State == PermitState.Suspended) attention.Add(new(r, "Suspended"));
            else if (r.State == PermitState.Completed && r.Permit.Events.Any(e => e.Kind == PermitEventKind.Completed && e.Note.StartsWith("LeftUnsafe") && InPeriod(e.At)))
                attention.Add(new(r, "Closed with the work left unsafe"));
        }
        return new(from, to, vessels, byMonth, attention);
    }
}

/// <summary>Permit lists as CSV, for the audit and for the company's own records.</summary>
public static class PermitReports
{
    private static string D(DateTime? at) => at is { } d ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    public static string RegisterCsv(IEnumerable<PermitRow> rows) => WorklistReports.Csv(
        ["Permit", "Vessel", "Kind", "Where", "Person in charge", "Issued by", "Valid from", "Valid until", "State", "Gas readings", "Readings outside limits", "People entered", "Checklist version"],
        rows.Select(r => new[]
        {
            r.Permit.Code, r.VesselName, r.Permit.TypeName, r.Permit.Location, r.Permit.RequesterName, r.Permit.IssuerName, D(r.Permit.ValidFrom), D(r.Until), PermitLook.State(r.State).Label,
            r.Permit.Readings.Count.ToString(), r.Permit.Readings.Count(x => !x.WithinLimits).ToString(), r.Permit.Entries.Count.ToString(), r.Permit.ChecklistVersion.ToString(),
        }));

    public static string FleetCsv(PermitFleetView view) => WorklistReports.Csv(
        ["Vessel", "Active", "Suspended", "Awaiting issue", "Expired not closed", "Gas readings outside limits", "Permits in the period"],
        view.Vessels.Select(v => new[] { v.Name, v.Active.ToString(), v.Suspended.ToString(), v.Awaiting.ToString(), v.Expired.ToString(), v.GasExceedances.ToString(), v.InPeriod.ToString() }));

    public static string MonthsCsv(PermitFleetView view) => WorklistReports.Csv(["Month", "Kind", "Permits"], view.ByTypeMonth.Select(m => new[] { m.Month, m.TypeName, m.Count.ToString() }));

    /// <summary>Every event of every permit, for the audit: who did what, and when.</summary>
    public static string EventsCsv(IEnumerable<PermitRow> rows) => WorklistReports.Csv(
        ["Permit", "Vessel", "Kind", "Time", "Event", "By", "Note"],
        rows.SelectMany(r => r.Permit.Events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => new[] { r.Permit.Code, r.VesselName, r.Permit.TypeName, D(e.At), PermitLook.Event(e.Kind), e.ByName, e.Note })));
}
