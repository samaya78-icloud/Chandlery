using System.Globalization;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

// The office's view of the hours of rest across the fleet (spec 11, S5). Read-only: the office never edits a vessel's entries.
//
// Like RestReview this has a pure layer (RestFleet: one seafarer's month, summed up) and a service that loads the books of each vessel with the same
// code the vessel runs, so the office and the vessel always show the same breaches and the same signatures.

/// <summary>One seafarer's month as the office sees it: what was recorded, which breaches stand and where they are, and which signing periods are not complete.</summary>
public sealed record RestCrewMonth(Guid VesselId, string Vessel, Guid CrewId, string Name, string Rank, DateTime First, int Elapsed, int Recorded, int Missing,
    IReadOnlyList<BreachView> Breaches, IReadOnlyList<RestPeriod> Periods, int? Worst24, int? Worst7, bool NeverRecorded = false)
{
    /// <summary>Breaches the Master has not yet dealt with (including those whose days were changed after he did).</summary>
    public int Unresolved => Breaches.Count(b => !b.Resolved);

    public int Stale => Breaches.Count(b => b.Status == BreachStatus.Stale);

    /// <summary>Exceptions declared whose compensatory rest the Master has not yet confirmed.</summary>
    public int CompensatoryOutstanding => Breaches.Count(b => b.CompensatoryOutstanding);

    public int OwedMinutes => Breaches.Where(b => b.CompensatoryOutstanding).Sum(b => b.OwedMinutes);

    /// <summary>Periods that are over and hold records but lack a current signature from the seafarer or the Master.</summary>
    public IEnumerable<RestPeriod> Unsigned => Periods.Where(p => p.Ended && p.Recorded > 0 && !p.Complete);

    public int ToSign => Unsigned.Count();
    public int OutOfDate => Periods.Count(p => p.OutOfDate);

    /// <summary>Nothing needs anyone's attention.</summary>
    public bool Clean => Unresolved == 0 && Missing == 0 && ToSign == 0 && CompensatoryOutstanding == 0;
}

/// <summary>A vessel's month: its seafarers, and the totals the fleet table shows.</summary>
public sealed record RestVesselMonth(Guid VesselId, string Name, DateTime First, List<RestCrewMonth> Crew)
{
    public int Breaches => Crew.Sum(c => c.Breaches.Count);
    public int Unresolved => Crew.Sum(c => c.Unresolved);
    public int Stale => Crew.Sum(c => c.Stale);
    public int CompensatoryOutstanding => Crew.Sum(c => c.CompensatoryOutstanding);
    public int Missing => Crew.Sum(c => c.Missing);
    public int ToSign => Crew.Sum(c => c.ToSign);
    public int OutOfDate => Crew.Sum(c => c.OutOfDate);

    /// <summary>The shares of days that were recorded, of those that had passed.</summary>
    public int RecordedPercent => Crew.Sum(c => c.Elapsed) is var elapsed and > 0 ? (int)Math.Round(100.0 * (elapsed - Crew.Sum(c => c.Missing)) / elapsed) : 100;

    /// <summary>The tone of the vessel's row: red when something is unresolved or signatures no longer match, amber when records or signatures are missing.</summary>
    public string Tone => Unresolved > 0 || OutOfDate > 0 ? "danger" : Missing > 0 || ToSign > 0 || CompensatoryOutstanding > 0 ? "warning" : "success";
}

/// <summary>One date of one seafarer, for the daily export.</summary>
public sealed record RestDailyRow(string Vessel, string Name, string Rank, DateTime Date, string Spans, int? WorkMinutes, int? RestMinutes, int? Worst24, int? Worst7, RestDayState State, string Findings);

/// <summary>What the office home page shows: the last two months across the fleet.</summary>
public sealed record RestFleetCounts(int Vessels, int Unresolved, int CompensatoryOutstanding, int ToSign, int OutOfDate, int Missing)
{
    public bool Any => Unresolved + CompensatoryOutstanding + ToSign + OutOfDate + Missing > 0;
}

public static class RestFleet
{
    /// <summary>One seafarer's month, worked out from their book. <paramref name="today"/> is passed in so the result never depends on the clock.</summary>
    public static RestCrewMonth CrewMonth(Guid vesselId, string vessel, CrewBook book, RestLimits limits, int signIntervalDays, int year, int month, DateTime today)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = first.AddMonths(1).AddDays(-1);
        var days = RestReviewService.DaysOf(book, limits, first, last);
        var breaches = RestReview.Breaches(book, days);
        var inputs = book.Inputs;
        var periods = RestReview.Periods(year, month, signIntervalDays).Select(p => RestReview.PeriodOf(book, p.From, p.To, today, breaches)).ToList();
        var recorded = inputs.Count(d => d.Date.Date >= first && d.Date.Date <= last);
        // A seafarer who has recorded nothing yet has no days to chase: they are shown as "no records yet", the same as on the vessel's own home page.
        var never = book.FirstRecorded is null;
        var missing = never ? 0 : RestReview.Gaps(first, last, book.FirstRecorded, inputs, today).Count;
        var start = book.FirstRecorded is { } joined && joined.Date > first ? joined.Date : first;
        var end = last > today.Date ? today.Date : last;
        var elapsed = never || end < start ? 0 : (end - start).Days + 1;
        return new RestCrewMonth(vesselId, vessel, book.CrewId, book.Name, book.Rank, first, elapsed, recorded, missing, breaches, periods,
            days.Where(d => d.Worst24 is not null).Select(d => d.Worst24).Min(), days.Where(d => d.Worst7 is not null).Select(d => d.Worst7).Min(), never);
    }
}

public class RestFleetService(IDbContextFactory<ChandleryDbContext> factory)
{
    /// <summary>The month for every vessel that has the module on (or one vessel), with each seafarer's standing.</summary>
    public async Task<List<RestVesselMonth>> FleetAsync(Guid companyId, int year, int month, Guid? onlyVessel = null, DateTime? today = null)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = first.AddMonths(1).AddDays(-1);
        var now = (today ?? DateTime.UtcNow).Date;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var result = new List<RestVesselMonth>();
        foreach (var (id, name) in (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.RestHours)).Where(v => onlyVessel is null || v.Id == onlyVessel))
        {
            var (set, limits) = await RestHoursService.RulesForAsync(db, companyId, id);
            var books = await RestReviewService.BooksAsync(db, companyId, id, first, last);
            result.Add(new RestVesselMonth(id, name, first, books.Select(b => RestFleet.CrewMonth(id, name, b, limits, set?.SignIntervalDays ?? 7, year, month, now)).ToList()));
        }
        return result.OrderBy(v => v.Name).ToList();
    }

    /// <summary>One seafarer over the last months, oldest first, to see whether things are getting better or worse.</summary>
    public async Task<List<RestCrewMonth>> TrendAsync(Guid companyId, Guid vesselId, Guid crewId, int months = 6, DateTime? today = null)
    {
        var now = (today ?? DateTime.UtcNow).Date;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == vesselId && v.CompanyId == companyId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";
        var (set, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        var list = new List<RestCrewMonth>();
        for (var i = months - 1; i >= 0; i--)
        {
            var m = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-i);
            var book = (await RestReviewService.BooksAsync(db, companyId, vesselId, m, m.AddMonths(1).AddDays(-1), crewId)).FirstOrDefault();
            if (book is not null) list.Add(RestFleet.CrewMonth(vesselId, vessel, book, limits, set?.SignIntervalDays ?? 7, m.Year, m.Month, now));
        }
        return list;
    }

    /// <summary>Every date of the month for every seafarer, for the daily export.</summary>
    public async Task<List<RestDailyRow>> DailyAsync(Guid companyId, int year, int month, DateTime? today = null)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = first.AddMonths(1).AddDays(-1);
        var now = (today ?? DateTime.UtcNow).Date;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rows = new List<RestDailyRow>();
        foreach (var (id, name) in await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.RestHours))
        {
            var (_, limits) = await RestHoursService.RulesForAsync(db, companyId, id);
            foreach (var book in await RestReviewService.BooksAsync(db, companyId, id, first, last))
            {
                var inputs = book.Inputs.ToDictionary(d => d.Date.Date);
                foreach (var day in RestReviewService.DaysOf(book, limits, first, last.Date > now ? now : last))
                {
                    var recorded = inputs.TryGetValue(day.Date.Date, out var input);
                    rows.Add(new RestDailyRow(name, book.Name, book.Rank, day.Date, recorded ? RestSpans.Format(input!.Work) : "", recorded ? day.WorkMinutes : null, recorded ? day.RestMinutes : null,
                        day.Worst24, day.Worst7, day.State, string.Join(" ", day.Findings.Select(f => f.Message))));
                }
            }
        }
        return rows.OrderBy(r => r.Vessel).ThenBy(r => r.Name).ThenBy(r => r.Date).ToList();
    }

    /// <summary>The last two months across the fleet, for the office home page. Null when no vessel has the module on.</summary>
    public async Task<RestFleetCounts?> CountsAsync(Guid companyId, DateTime? today = null)
    {
        var now = (today ?? DateTime.UtcNow).Date;
        var previous = now.AddMonths(-1);
        var fleet = (await FleetAsync(companyId, previous.Year, previous.Month, null, now)).Concat(await FleetAsync(companyId, now.Year, now.Month, null, now)).ToList();
        if (fleet.Count == 0) return null;
        return new RestFleetCounts(fleet.Select(v => v.VesselId).Distinct().Count(), fleet.Sum(v => v.Unresolved), fleet.Sum(v => v.CompensatoryOutstanding),
            fleet.Sum(v => v.ToSign), fleet.Sum(v => v.OutOfDate), fleet.Sum(v => v.Missing));
    }
}

/// <summary>The fleet reports as CSV (spreadsheet-ready: the caller adds the byte order mark).</summary>
public static class RestReports
{
    private static string D(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string H(int? minutes) => minutes is { } m ? (m / 60.0).ToString("0.00", CultureInfo.InvariantCulture) : "";

    public static string FleetCsv(IEnumerable<RestVesselMonth> vessels) => WorklistReports.Csv(
        ["Vessel", "Month", "Seafarers", "Days recorded %", "Days not recorded", "Breaches", "Not dealt with", "Changed since the Master dealt with them", "Compensatory rest outstanding", "Periods to sign", "Signatures out of date"],
        vessels.Select(v => new[]
        {
            v.Name, v.First.ToString("yyyy-MM"), v.Crew.Count.ToString(), v.RecordedPercent.ToString(), v.Missing.ToString(), v.Breaches.ToString(), v.Unresolved.ToString(), v.Stale.ToString(),
            v.CompensatoryOutstanding.ToString(), v.ToSign.ToString(), v.OutOfDate.ToString(),
        }));

    public static string BreachesCsv(IEnumerable<RestVesselMonth> vessels) => WorklistReports.Csv(
        ["Vessel", "Seafarer", "Rank", "Date", "Rule", "Actual (h)", "Limit (h)", "Status", "Explanation", "Master's decision", "Exception", "Master's reason", "Compensatory rest owed (h)", "Compensatory rest confirmed"],
        vessels.SelectMany(v => v.Crew).SelectMany(c => c.Breaches.Select(b => new[]
        {
            c.Vessel, c.Name, c.Rank, D(b.Finding.Date), b.Finding.Rule.ToString(), H(b.Finding.Actual), H(b.Finding.Limit), b.Status.ToString(), b.Explanation?.Text ?? "",
            b.Decision is null ? "" : b.Decision.Kind == RestNoteKind.Exception ? "Exception" : "Acknowledged", b.Decision?.ExceptionKind is { } k && k != RestExceptionKind.None ? k.ToString() : "",
            b.Decision?.Text ?? "", H(b.OwedMinutes), b.Compensatory is null ? "" : D(b.Compensatory.At),
        })));

    public static string UnsignedCsv(IEnumerable<RestVesselMonth> vessels) => WorklistReports.Csv(
        ["Vessel", "Seafarer", "Rank", "From", "To", "Days", "Days recorded", "Seafarer's signature", "Master's signature", "Breaches not dealt with"],
        vessels.SelectMany(v => v.Crew).SelectMany(c => c.Unsigned.Select(p => new[]
        {
            c.Vessel, c.Name, c.Rank, D(p.From), D(p.To), p.Days.ToString(), p.Recorded.ToString(), Sign(p.Seafarer), Sign(p.Master), p.BreachesUnresolved.ToString(),
        })));

    private static string Sign(SignView? s) => s is null ? "Not signed" : s.Current ? "Signed " + D(s.Sign.SignedAt) : "Out of date (signed " + D(s.Sign.SignedAt) + ")";

    public static string DailyCsv(IEnumerable<RestDailyRow> rows) => WorklistReports.Csv(
        ["Vessel", "Seafarer", "Rank", "Date", "Work periods", "Work (h)", "Rest (h)", "Least rest in 24 h", "Least rest in 7 days", "Result", "Findings"],
        rows.Select(r => new[]
        {
            r.Vessel, r.Name, r.Rank, D(r.Date), r.Spans, H(r.WorkMinutes), H(r.RestMinutes), H(r.Worst24), H(r.Worst7), RestLook.Describe(r.State).Label, r.Findings,
        }));
}
