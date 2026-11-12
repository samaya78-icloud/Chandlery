using System.Globalization;
using Chandlery.Models;

namespace Chandlery.Services;

public sealed record DrillParticipationRow(Guid CrewId, string Name, string Rank, IReadOnlyList<DrillPersonalRow> Standing);

/// <summary>Every crew member against each drill type everyone must attend.</summary>
public sealed record DrillParticipation(string VesselName, DateTime Today, IReadOnlyList<DrillType> Types, IReadOnlyList<DrillParticipationRow> Crew);

/// <summary>One type on one vessel in the fleet table. <c>PeopleLate</c> counts the crew whose own date has come or passed.</summary>
public sealed record FleetDrillCell(DrillStatus Status, DateTime? LastHeld, DateTime? Due, int PeopleLate);

public sealed record FleetDrillVessel(Guid VesselId, string Name, DrillStatus Worst, int Overdue, int NeverHeld, bool QuarterTriggered, IReadOnlyDictionary<Guid, FleetDrillCell> Cells);

public sealed record FleetMeetingCell(DateTime? LastHeld, DateTime? Due, DrillStatus Status);

public sealed record FleetMeetingVessel(Guid VesselId, string Name, IReadOnlyDictionary<Guid, FleetMeetingCell> Cells);

public sealed record DrillFleetView(DateTime Today, IReadOnlyList<DrillType> Types, IReadOnlyList<FleetDrillVessel> Vessels, IReadOnlyList<MeetingType> MeetingTypes, IReadOnlyList<FleetMeetingVessel> Meetings)
{
    /// <summary>Drill types overdue across the fleet (a type on a vessel counts once). What the menu shows.</summary>
    public int Overdue => Vessels.Sum(v => v.Overdue);
}

/// <summary>The fleet table, worked out from each vessel's own schedule so it can never disagree with it.</summary>
public static class DrillFleet
{
    private static int Rank(DrillStatus s) => s switch { DrillStatus.Overdue => 0, DrillStatus.NeverHeld => 1, DrillStatus.Due => 2, DrillStatus.DueSoon => 3, _ => 4 };

    /// <summary>Vessels with the most pressing status first, then by name. The types shown are the ones that apply on at least one vessel.</summary>
    public static DrillFleetView Build(DateTime today, IEnumerable<DrillSchedule> schedules, IEnumerable<MeetingType> meetingTypes, IEnumerable<(Guid VesselId, string Name, IReadOnlyList<MeetingScheduleRow> Rows)> meetings)
    {
        var list = schedules.ToList();
        var types = list.SelectMany(s => s.Rows.Select(r => r.Type)).GroupBy(t => t.Id).Select(g => g.First()).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToList();
        var vessels = list.Select(s => new FleetDrillVessel(s.VesselId, s.VesselName, s.Worst,
                s.Rows.Count(r => r.Status == DrillStatus.Overdue), s.Rows.Count(r => r.Status == DrillStatus.NeverHeld), s.Quarter.Triggered,
                s.Rows.ToDictionary(r => r.Type.Id, r => new FleetDrillCell(r.Status, r.LastHeld, r.Due, r.People.Count(p => p.Status is DrillStatus.Overdue or DrillStatus.NeverHeld)))))
            .OrderBy(v => Rank(v.Worst)).ThenByDescending(v => v.Overdue).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var mtypes = meetingTypes.Where(t => t.Scheduled).ToList();
        var mvessels = meetings.Select(m => new FleetMeetingVessel(m.VesselId, m.Name, m.Rows.Where(r => r.Type.Scheduled).ToDictionary(r => r.Type.Id, r => new FleetMeetingCell(r.LastHeld, r.Due, r.Status))))
            .OrderBy(v => v.Cells.Values.Select(c => Rank(c.Status)).DefaultIfEmpty(4).Min()).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new DrillFleetView(today, types, vessels, mtypes, mvessels);
    }
}

/// <summary>The fleet's drills and meetings for the office: one table across vessels.</summary>
public class DrillFleetService(DrillService drills, MeetingService meetings)
{
    public async Task<DrillFleetView> FleetAsync(Guid companyId, Actor actor, DateTime? today = null)
    {
        if (NodeContext.Current.IsVessel) throw new InvalidOperationException("The fleet view is the office's.");
        var day = (today ?? DateTime.UtcNow).Date;
        var vessels = await drills.EnabledVesselsAsync(companyId);
        var schedules = new List<DrillSchedule>();
        foreach (var v in vessels) schedules.Add(await drills.ScheduleAsync(companyId, v.Id, actor, day));
        var mtypes = await meetings.TypesAsync(companyId);
        var rows = new List<(Guid, string, IReadOnlyList<MeetingScheduleRow>)>();
        foreach (var v in await meetings.EnabledVesselsAsync(companyId)) rows.Add((v.Id, v.Name, await meetings.ScheduleAsync(companyId, v.Id, actor, day)));
        return DrillFleet.Build(day, schedules, mtypes, rows);
    }

    public async Task<int> OverdueAsync(Guid companyId, Actor actor)
    {
        var vessels = await drills.EnabledVesselsAsync(companyId);
        var total = 0;
        foreach (var v in vessels) total += (await drills.ScheduleAsync(companyId, v.Id, actor)).Rows.Count(r => r.Status == DrillStatus.Overdue);
        return total;
    }
}

public static class DrillReports
{
    private static string D(DateTime? date) => date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    public static string RegisterCsv(IEnumerable<DrillRow> rows) => WorklistReports.Csv(
        ["Drill", "Vessel", "Type", "Category", "Started", "Ended", "Place", "Conducted by", "Took part", "Absent", "Findings", "Deficiencies", "Signed by", "Signed"],
        rows.Select(r => new[]
        {
            r.Drill.Code, r.VesselName, r.Drill.TypeName, DrillLook.Category(r.Drill.Category), r.Drill.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.Drill.EndedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
            r.Drill.Place, r.Drill.ConductedByName, r.Present.ToString(), r.Absent.ToString(), r.Drill.Findings, r.Drill.Deficiencies, r.Drill.SignedByName, D(r.Drill.SignedAt),
        }));

    public static string FleetCsv(DrillFleetView view) => WorklistReports.Csv(
        ["Vessel", "Drill", "Status", "Last held", "Next due", "People late"],
        view.Vessels.SelectMany(v => view.Types.Where(t => v.Cells.ContainsKey(t.Id)).Select(t =>
        {
            var c = v.Cells[t.Id];
            return new[] { v.Name, t.Name, DrillLook.Status(c.Status).Label, D(c.LastHeld), D(c.Due), c.PeopleLate.ToString() };
        })));

    public static string ParticipationCsv(DrillParticipation p) => WorklistReports.Csv(
        ["Vessel", "Name", "Rank", "Drill", "Last took part", "Next due", "Status"],
        p.Crew.SelectMany(c => c.Standing.Select(s => new[] { p.VesselName, c.Name, c.Rank, s.Type.Name, D(s.LastTookPart), D(s.Due), DrillLook.Status(s.Status).Label })));

    public static string MeetingsCsv(IEnumerable<MeetingRow> rows) => WorklistReports.Csv(
        ["Meeting", "Vessel", "Kind", "Held", "Place", "Chair", "Present", "Items", "Outcome", "Signed by", "Signed"],
        rows.Select(r => new[]
        {
            r.Meeting.Code, r.VesselName, r.Meeting.TypeName, r.Meeting.HeldAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.Meeting.Place, r.Meeting.ChairName,
            r.Present.ToString(), r.Items.ToString(), r.Meeting.Summary, r.Meeting.SignedByName, D(r.Meeting.SignedAt),
        }));
}
