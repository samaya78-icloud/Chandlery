using Chandlery.Models;
using Chandlery.Data;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>One person's day that a drill disturbed: what is recorded now and what it would be with the drill added as work.</summary>
public sealed record DrillRestChange(Guid CrewId, string Name, DateTime Day, string Before, string After);

public sealed record DrillRestOffer(Guid DrillId, string Code, string Reason, IReadOnlyList<DrillRestChange> Changes);

public sealed record DrillRestResult(int Applied, IReadOnlyList<string> Problems);

/// <summary>How a drill's duration lands on hours of work (spec 13, "Rest hours link"). Pure, so the rule is tested without a database.</summary>
public static class DrillRestRules
{
    private static int Floor(int minutes) => minutes / RestSpans.Resolution * RestSpans.Resolution;
    private static int Ceil(int minutes) => (minutes + RestSpans.Resolution - 1) / RestSpans.Resolution * RestSpans.Resolution;

    /// <summary>The drill as work periods on the day or days it ran, widened to the quarter hour the hours screen keeps. A drill across midnight is entered on each of the two days.</summary>
    public static List<(DateTime Day, WorkSpan Span)> Pieces(DateTime start, DateTime end)
    {
        var pieces = new List<(DateTime, WorkSpan)>();
        if (end <= start) return pieces;
        var first = start.Date;
        var from = Floor((int)start.TimeOfDay.TotalMinutes);
        if (end.Date == first) pieces.Add((first, new WorkSpan(from, Ceil((int)end.TimeOfDay.TotalMinutes))));
        else
        {
            pieces.Add((first, new WorkSpan(from, RestSpans.DayMinutes)));
            var tail = Ceil((int)end.TimeOfDay.TotalMinutes);
            if (tail > 0) pieces.Add((end.Date, new WorkSpan(0, tail)));
        }
        return pieces.Where(p => p.Item2.End > p.Item2.Start).ToList();
    }

    /// <summary>
    /// The day's work periods with the drill added, or null when there is nothing to offer: the day is not recorded (a drill does not invent a record), or the drill already
    /// falls inside work that is recorded.
    /// </summary>
    public static string? Merge(string? recorded, WorkSpan piece)
    {
        if (recorded is null) return null;
        var spans = RestSpans.Normalize(RestSpans.Parse(recorded));
        if (spans.Any(s => s.Start <= piece.Start && s.End >= piece.End)) return null;
        return RestSpans.Format(RestSpans.Normalize([.. spans, piece]));
    }
}

/// <summary>
/// Offers to add a drill's duration to the hours of work of those who took part, when their recorded hours show rest while it ran. Never silent: it is a button the Master (or a crew
/// admin) presses, through the same recording the hours screen uses, so the reason, the Master's lock and the seafarer's agreement when signing all apply.
/// </summary>
public class DrillRestService(IDbContextFactory<ChandleryDbContext> factory, DrillService drills, RestHoursService rest)
{
    public async Task<DrillRestOffer?> OfferAsync(Guid companyId, Guid drillId, Actor actor)
    {
        var detail = await drills.GetAsync(companyId, drillId, actor);
        if (detail is null || detail.Drill.EndedAt is not { } end) return null;
        var drill = detail.Drill;
        await using var db = factory.CreateDbContext();
        db.CurrentCompanyId = companyId;
        if (!await RestHoursService.IsEnabledForVesselAsync(db, companyId, drill.VesselId)) return null;

        var pieces = DrillRestRules.Pieces(drill.StartedAt, end);
        if (pieces.Count == 0) return null;
        var changes = new List<DrillRestChange>();
        foreach (var p in drill.Participants.Where(p => p.Attendance == DrillAttendance.Present && p.CrewMemberId is not null))
        {
            var crewId = p.CrewMemberId!.Value;
            var history = await rest.HistoryAsync(companyId, crewId, pieces.Min(x => x.Day), pieces.Max(x => x.Day));
            foreach (var (day, span) in pieces)
            {
                var latest = history.Where(e => e.Date.Date == day.Date).OrderBy(e => e.EnteredAt).ThenBy(e => e.Id).LastOrDefault();
                var after = DrillRestRules.Merge(latest?.WorkSpans, span);
                if (after is not null) changes.Add(new DrillRestChange(crewId, p.Name, day, latest!.WorkSpans, after));
            }
        }
        var reason = $"Drill {drill.Code} ({drill.TypeName}) {drill.StartedAt:HH:mm} to {end:HH:mm}";
        return new DrillRestOffer(drillId, drill.Code, reason, changes);
    }

    /// <summary>Adds the drill to each person's day. Whatever cannot be done (a period the Master has signed, when the actor is not the Master) is reported, and the rest still goes through.</summary>
    public async Task<DrillRestResult> ApplyAsync(Guid companyId, Guid drillId, Actor actor)
    {
        var offer = await OfferAsync(companyId, drillId, actor);
        if (offer is null || offer.Changes.Count == 0) throw new InvalidOperationException("There is nothing to add to anyone's hours for this drill.");
        var detail = (await drills.GetAsync(companyId, drillId, actor))!;
        var applied = 0;
        var problems = new List<string>();
        foreach (var c in offer.Changes)
        {
            try
            {
                await rest.RecordAsync(companyId, detail.Drill.VesselId, actor, c.CrewId, c.Day, c.After, offer.Reason);
                applied++;
            }
            catch (InvalidOperationException ex) { problems.Add($"{c.Name}, {c.Day:d MMM}: {ex.Message}"); }
        }
        return new DrillRestResult(applied, problems);
    }
}
