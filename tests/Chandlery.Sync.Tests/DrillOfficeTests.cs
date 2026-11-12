using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The office's drill and meeting views (C4): the fleet table worked out from each vessel's schedule, crew participation, the reports.</summary>
public class DrillFleetTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<Guid, DrillOverride> NoOverrides = new Dictionary<Guid, DrillOverride>();

    private static DrillType Type(string name, int months = 1, bool everyone = false, int order = 0)
        => new() { Id = Guid.NewGuid(), Name = name, IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = months, WarningDays = 7, EveryCrewMember = everyone, IsActive = true, SortOrder = order };

    private static CrewMember Crew(string name, DateTime signedOn) => new() { Id = Guid.NewGuid(), FullName = name, Rank = "AB", SignOnDate = signedOn, VesselId = Guid.NewGuid(), IsActive = true };

    private static Drill Held(DrillType type, DateTime on, params CrewMember[] present)
        => new() { Id = Guid.NewGuid(), DrillTypeId = type.Id, StartedAt = on, Participants = [.. present.Select(c => new DrillParticipant { CrewMemberId = c.Id, Name = c.FullName, Attendance = DrillAttendance.Present })] };

    private static DrillSchedule Vessel(string name, DateTime today, IReadOnlyList<DrillType> types, IReadOnlyList<Drill> drills, IReadOnlyList<CrewMember>? crew = null, IReadOnlyDictionary<Guid, DrillOverride>? overrides = null)
        => DrillSchedules.Build(Guid.NewGuid(), name, today, new DrillSnapshot(types, overrides ?? NoOverrides, drills, crew ?? []));

    [Fact]
    public void TheVesselWithTheMostPressingStatusIsFirst_ThenByName()
    {
        var today = D(2026, 11, 12);
        var fire = Type("Fire");
        var fine = Vessel("Alpha", today, [fire], [Held(fire, D(2026, 11, 2))]);                 // due 31 Dec
        var late = Vessel("Zulu", today, [fire], [Held(fire, D(2026, 9, 1))]);                   // due 31 Oct: overdue
        var never = Vessel("Bravo", today, [fire], []);                                          // never held
        var soon = Vessel("Charlie", today, [fire], [Held(fire, D(2026, 10, 20))]);              // due 30 Nov: not yet due
        var view = DrillFleet.Build(today, [fine, never, soon, late], [], []);

        Assert.Equal(["Zulu", "Bravo", "Alpha", "Charlie"], view.Vessels.Select(v => v.Name));
        Assert.Equal(1, view.Overdue);                                                           // what the menu shows: overdue only, never-held is not counted as overdue
        Assert.Equal((1, 0), (view.Vessels[0].Overdue, view.Vessels[0].NeverHeld));
        Assert.Equal((0, 1), (view.Vessels[1].Overdue, view.Vessels[1].NeverHeld));
    }

    [Fact]
    public void TheColumnsAreTheTypesThatApplyOnAnyVessel_AndATypeThatDoesNotApplyHasNoCellThere()
    {
        var today = D(2026, 11, 12);
        var fire = Type("Fire", order: 1);
        var abandon = Type("Abandon ship", order: 2);
        var spare = Type("Spare", order: 3);
        var off = new Dictionary<Guid, DrillOverride> { [abandon.Id] = new() { DrillTypeId = abandon.Id, Applies = false, Reason = "No lifeboat" } };
        var a = Vessel("Alpha", today, [fire, abandon, spare], [], overrides: off);
        var b = Vessel("Bravo", today, [fire, abandon], []);
        var view = DrillFleet.Build(today, [a, b], [], []);

        Assert.Equal(["Fire", "Abandon ship", "Spare"], view.Types.Select(t => t.Name));
        var alpha = view.Vessels.Single(v => v.Name == "Alpha");
        Assert.False(alpha.Cells.ContainsKey(abandon.Id));
        Assert.True(alpha.Cells.ContainsKey(spare.Id));
        Assert.True(view.Vessels.Single(v => v.Name == "Bravo").Cells.ContainsKey(abandon.Id));
        Assert.Empty(DrillFleet.Build(today, [], [], []).Vessels);
    }

    [Fact]
    public void ACellCountsTheCrewWhoseOwnDateHasPassed_AndTheVesselIsLateBecauseOfThem()
    {
        var today = D(2026, 11, 12);
        var fire = Type("Fire", everyone: true);
        var a = Crew("A", D(2026, 1, 1));
        var b = Crew("B", D(2026, 1, 1));
        var c = Crew("C", today.AddDays(-3));                                                    // joined days ago: grace
        var schedule = Vessel("Alpha", today, [fire], [Held(fire, D(2026, 11, 2), a)], [a, b, c]);
        var cell = DrillFleet.Build(today, [schedule], [], []).Vessels.Single().Cells[fire.Id];

        Assert.Equal((DrillStatus.NotDue, 1), (schedule.Rows.Single().ShipStatus, cell.PeopleLate));     // the ship is fine, one person has never taken part
        Assert.Equal(DrillStatus.Overdue, cell.Status);                                                   // the row shows what most needs doing: a person past the joiner grace who never took part is overdue
    }

    [Fact]
    public void OnlyScheduledMeetingsAreInTheFleetTable()
    {
        var today = D(2026, 11, 12);
        var committee = new MeetingType { Id = Guid.NewGuid(), Name = "Safety committee", Scheduled = true, IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 1, WarningDays = 7, IsActive = true };
        var toolbox = new MeetingType { Id = Guid.NewGuid(), Name = "Toolbox talk", Scheduled = false, IsActive = true };
        var held = new Dictionary<Guid, DateTime> { [committee.Id] = D(2026, 9, 1) };
        var rows = MeetingSchedules.Build([committee, toolbox], held, today);
        var view = DrillFleet.Build(today, [], [committee, toolbox], [(Guid.NewGuid(), "Alpha", rows)]);

        Assert.Equal(["Safety committee"], view.MeetingTypes.Select(t => t.Name));
        var cell = Assert.Single(view.Meetings.Single().Cells);
        Assert.Equal((committee.Id, DrillStatus.Overdue, D(2026, 10, 31)), (cell.Key, cell.Value.Status, cell.Value.Due));
    }

    // ---- crew participation ----

    [Fact]
    public void ParticipationListsEachCrewMemberAgainstTheTypesEveryoneAttends_AndNothingElse()
    {
        var today = D(2026, 11, 12);
        var fire = Type("Fire", everyone: true, order: 1);
        var abandon = Type("Abandon ship", everyone: true, order: 2);
        var mob = Type("Man overboard", order: 3);
        var a = Crew("A", D(2026, 1, 1));
        var b = Crew("B", D(2026, 1, 1));
        var off = new Dictionary<Guid, DrillOverride> { [abandon.Id] = new() { DrillTypeId = abandon.Id, Applies = false, Reason = "No lifeboat" } };
        var p = DrillSchedules.Participation("MV One", new DrillSnapshot([fire, abandon, mob], off, [Held(fire, D(2026, 11, 2), a), Held(mob, D(2026, 11, 3), a, b)], [a, b]), today);

        Assert.Equal(["Fire"], p.Types.Select(t => t.Name));                                      // not the ship drill, not the type that does not apply here
        Assert.Equal(["A", "B"], p.Crew.Select(c => c.Name));
        var rowA = Assert.Single(p.Crew[0].Standing);
        var rowB = Assert.Single(p.Crew[1].Standing);
        Assert.Equal((D(2026, 11, 2), D(2026, 12, 31), DrillStatus.NotDue), (rowA.LastTookPart, rowA.Due, rowA.Status));
        Assert.Equal((DrillStatus.Overdue, null), (rowB.Status, rowB.LastTookPart));            // took part in the man overboard drill, which does not count for this type
    }

    [Fact]
    public void AnExcusedAbsenceDoesNotMoveAPersonsLastParticipation()
    {
        var today = D(2026, 11, 12);
        var fire = Type("Fire", everyone: true);
        var a = Crew("A", D(2026, 1, 1));
        var held = new Drill { Id = Guid.NewGuid(), DrillTypeId = fire.Id, StartedAt = D(2026, 11, 2), Participants = [new DrillParticipant { CrewMemberId = a.Id, Name = "A", Attendance = DrillAttendance.Absent, Absence = DrillAbsence.OnWatch }] };
        var row = Assert.Single(Assert.Single(DrillSchedules.Participation("MV One", new DrillSnapshot([fire], NoOverrides, [held], [a]), today).Crew).Standing);
        Assert.Equal((null, DrillStatus.Overdue), (row.LastTookPart, row.Status));
    }

    // ---- the reports ----

    [Fact]
    public void TheFleetCsvHasOneLinePerVesselAndTypeThatApplies_WithTextQuotedForExcel()
    {
        var today = D(2026, 11, 12);
        var fire = Type("Fire, \"muster\"");
        var spare = Type("Spare");
        var off = new Dictionary<Guid, DrillOverride> { [spare.Id] = new() { DrillTypeId = spare.Id, Applies = false, Reason = "n/a" } };
        var view = DrillFleet.Build(today, [Vessel("Alpha", today, [fire, spare], [Held(fire, D(2026, 9, 1))], overrides: off), Vessel("Bravo", today, [fire, spare], [])], [], []);
        var lines = DrillReports.FleetCsv(view).TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("\"Vessel\",\"Drill\",\"Status\",\"Last held\",\"Next due\",\"People late\"", lines[0]);
        Assert.Equal(3, lines.Count - 1);                                                         // Alpha: fire only (spare does not apply there); Bravo: fire and spare
        Assert.Contains("\"Alpha\",\"Fire, \"\"muster\"\"\",\"Overdue\",\"2026-09-01\",\"2026-10-31\",\"0\"", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("\"Alpha\",\"Spare\""));
    }
}
