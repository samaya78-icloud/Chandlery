using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>A signed period of hours old enough to be past the rule set's retention time. Listed for someone to decide about; nothing here deletes anything.</summary>
public sealed record RestRetentionRow(string Vessel, Guid VesselId, Guid CrewId, string CrewName, DateTime From, DateTime To, int Revisions, string SignedBy, DateTime SignedAt, int AgeMonths, int RetentionMonths);

/// <summary>
/// Which records have been kept for longer than the rule set says they must be. A period is listed only when it is old enough (its last day is more than the
/// retention time ago) and was finished: the Master signed it and nothing in it changed after. An unsigned or changed-since period is a different problem, shown
/// on the unsigned report, and is never offered for clearing.
/// </summary>
public static class RestRetention
{
    public static List<RestRetentionRow> PastRetention(string vessel, Guid vesselId, int retentionMonths, DateTime today, IEnumerable<RestSignOff> signs, IEnumerable<RestEntry> entries)
    {
        today = today.Date;
        var all = entries.ToList();
        var rows = new List<RestRetentionRow>();
        foreach (var period in signs.Where(s => s.Role == RestRole.Master).GroupBy(s => (s.CrewMemberId, s.FromDate.Date, s.ToDate.Date)))
        {
            var (crewId, from, to) = period.Key;
            if (to.AddMonths(retentionMonths) >= today) continue;                       // not yet old enough: kept
            var signed = period.OrderBy(s => s.SignedAt).ThenBy(s => s.Id).Last();
            var inPeriod = all.Where(e => e.CrewMemberId == crewId && e.Date.Date >= from && e.Date.Date <= to).ToList();
            if (inPeriod.Any(e => e.EnteredAt > signed.SignedAt)) continue;             // a day was changed after the signature: not final
            rows.Add(new RestRetentionRow(vessel, vesselId, crewId, signed.CrewName, from, to, inPeriod.Count, signed.SignedByName, signed.SignedAt, AgeInMonths(to, today), retentionMonths));
        }
        return rows.OrderBy(r => r.Vessel).ThenBy(r => r.CrewName).ThenBy(r => r.From).ToList();
    }

    /// <summary>Whole months from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static int AgeInMonths(DateTime from, DateTime to)
    {
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        return to.Day < from.Day ? months - 1 : months;
    }
}
