using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>When a drill is due and where a ship and each person stand: the due dates in days and in calendar months, the warning, the joiner's grace, the quarter-of-crew rule, and the schedule built from the drills held.</summary>
public class DrillRulesTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DrillRule Monthly = new(DrillIntervalKind.CalendarMonths, 1, 7, true);

    // ---- the rule in force ----

    [Fact]
    public void AVesselMayHaveItsOwnInterval_ButHalfAnOverrideIsIgnored()
    {
        var type = new DrillType { IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 1, WarningDays = 7, EveryCrewMember = true };
        Assert.Equal(new DrillRule(DrillIntervalKind.Days, 45, 7, true), DrillRules.Effective(type, new DrillOverride { IntervalKind = DrillIntervalKind.Days, IntervalCount = 45 }));
        Assert.Equal(DrillRules.Of(type), DrillRules.Effective(type, new DrillOverride { IntervalKind = DrillIntervalKind.Days }));
        Assert.Equal(DrillRules.Of(type), DrillRules.Effective(type, new DrillOverride { IntervalKind = DrillIntervalKind.Days, IntervalCount = 0 }));
        Assert.Equal(DrillRules.Of(type), DrillRules.Effective(type, null));
        Assert.Equal(1, DrillRules.Of(new DrillType { IntervalCount = 0 }).Count);                                // never a zero interval
    }

    // ---- the due date ----

    [Fact]
    public void NothingIsDueUntilOneHasBeenHeld() => Assert.Null(DrillRules.DueDate(Monthly, null));

    [Theory]
    [InlineData(2026, 10, 5, 1, 2026, 11, 30)]      // monthly: by the end of the next month
    [InlineData(2026, 10, 31, 1, 2026, 11, 30)]
    [InlineData(2026, 10, 5, 2, 2026, 12, 31)]      // every two months
    [InlineData(2026, 12, 15, 1, 2027, 1, 31)]      // across a year
    [InlineData(2028, 1, 10, 1, 2028, 2, 29)]       // a leap February
    [InlineData(2027, 1, 10, 1, 2027, 2, 28)]
    [InlineData(2026, 11, 20, 3, 2027, 2, 28)]
    public void ACalendarMonthIntervalFallsDueAtTheEndOfTheMonth(int y, int m, int d, int count, int ey, int em, int ed)
        => Assert.Equal(D(ey, em, ed), DrillRules.DueDate(new DrillRule(DrillIntervalKind.CalendarMonths, count, 7, false), D(y, m, d)));

    [Fact]
    public void ADaysIntervalCountsFromTheDayOfTheLastDrill_WhateverTheTimeOfDay()
    {
        var rule = new DrillRule(DrillIntervalKind.Days, 30, 7, false);
        Assert.Equal(D(2026, 11, 9), DrillRules.DueDate(rule, new DateTime(2026, 10, 10, 23, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(D(2027, 1, 5), DrillRules.DueDate(rule, D(2026, 12, 6)));
    }

    // ---- the status ----

    [Fact]
    public void TheStatusRunsFromNotDueThroughDueSoonAndDueToOverdue()
    {
        var last = D(2026, 10, 5);                                       // due 30 November, warning from 23 November
        Assert.Equal(DrillStatus.NotDue, DrillRules.StatusOf(Monthly, last, D(2026, 11, 22)));
        Assert.Equal(DrillStatus.DueSoon, DrillRules.StatusOf(Monthly, last, D(2026, 11, 23)));
        Assert.Equal(DrillStatus.DueSoon, DrillRules.StatusOf(Monthly, last, D(2026, 11, 29)));
        Assert.Equal(DrillStatus.Due, DrillRules.StatusOf(Monthly, last, D(2026, 11, 30)));
        Assert.Equal(DrillStatus.Overdue, DrillRules.StatusOf(Monthly, last, D(2026, 12, 1)));
        Assert.Equal(DrillStatus.NeverHeld, DrillRules.StatusOf(Monthly, null, D(2026, 12, 1)));
    }

    [Fact]
    public void WithNoWarningDaysTheDayBeforeIsStillNotDue()
    {
        var rule = Monthly with { WarningDays = 0 };
        Assert.Equal(DrillStatus.NotDue, DrillRules.StatusOf(rule, D(2026, 10, 5), D(2026, 11, 29)));
        Assert.Equal(DrillStatus.Due, DrillRules.StatusOf(rule, D(2026, 10, 5), D(2026, 11, 30)));
    }

    [Fact]
    public void TheTimeOfDayDoesNotMoveTheStatus()
        => Assert.Equal(DrillStatus.Due, DrillRules.StatusOf(Monthly, D(2026, 10, 5), new DateTime(2026, 11, 30, 23, 59, 0, DateTimeKind.Utc)));

    [Fact]
    public void TheWorstStatusIsTheOneThatMostNeedsDoing()
    {
        Assert.Equal(DrillStatus.Overdue, DrillRules.Worst([DrillStatus.NotDue, DrillStatus.Overdue, DrillStatus.NeverHeld]));
        Assert.Equal(DrillStatus.NeverHeld, DrillRules.Worst([DrillStatus.Due, DrillStatus.NeverHeld, DrillStatus.DueSoon]));
        Assert.Equal(DrillStatus.Due, DrillRules.Worst([DrillStatus.DueSoon, DrillStatus.Due, DrillStatus.NotDue]));
        Assert.Equal(DrillStatus.DueSoon, DrillRules.Worst([DrillStatus.NotDue, DrillStatus.DueSoon]));
        Assert.Equal(DrillStatus.NotDue, DrillRules.Worst([]));
    }

    // ---- a person's own date ----

    [Fact]
    public void APersonsDueDateComesFromTheirOwnLastDrill_OrFromTheDayTheyJoinedPlusTheGrace()
    {
        Assert.Equal(D(2026, 11, 30), DrillRules.PersonDue(Monthly, D(2026, 10, 5), D(2026, 1, 1)));        // joined long ago, took part in October
        Assert.Equal(D(2026, 11, 14), DrillRules.PersonDue(Monthly, null, D(2026, 10, 31)));                // never took part: joined 31 October + 14 days
        Assert.Null(DrillRules.PersonDue(Monthly, null, null));                                             // nothing to count from
        Assert.Equal(DrillStatus.NeverHeld, DrillRules.PersonStatus(Monthly, null, null, D(2026, 11, 1)));
    }

    [Fact]
    public void ANewJoinerIsNotLateUntilTheGraceHasPassed_AndAnOldHandWhoNeverTookPartIs()
    {
        var joined = D(2026, 11, 1);
        Assert.Equal(DrillStatus.Due, DrillRules.PersonStatus(Monthly, null, joined, D(2026, 11, 15)));          // the fourteenth day
        Assert.Equal(DrillStatus.Overdue, DrillRules.PersonStatus(Monthly, null, joined, D(2026, 11, 16)));
        Assert.Equal(DrillStatus.NotDue, DrillRules.PersonStatus(Monthly, null, joined, D(2026, 11, 1)));
        Assert.Equal(DrillStatus.Overdue, DrillRules.PersonStatus(Monthly, null, D(2026, 5, 1), D(2026, 11, 12)));
    }

    // ---- the quarter of the crew ----

    [Fact]
    public void MoreThanAQuarterNotTakingPartTriggersTheRule_ExactlyAQuarterDoesNot()
    {
        var crew = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();
        var exactly = DrillRules.QuarterRule(crew, crew.Skip(2));                          // 2 of 8
        Assert.Equal((8, 2, false, 25), (exactly.CrewCount, exactly.NotTakenPart, exactly.Triggered, exactly.Percent));
        var more = DrillRules.QuarterRule(crew, crew.Skip(3));                             // 3 of 8
        Assert.True(more.Triggered);
        Assert.Equal(38, more.Percent);
        Assert.False(DrillRules.QuarterRule(crew, crew).Triggered);
        Assert.True(DrillRules.QuarterRule(crew, []).Triggered);
    }

    [Fact]
    public void WithNoCrewTheRuleIsNeverTriggered() => Assert.False(DrillRules.QuarterRule([], []).Triggered);

    [Fact]
    public void PeopleWhoAreNotAboardAreNotCountedAsHavingTakenPart()
    {
        var crew = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var q = DrillRules.QuarterRule(crew, [crew[0], Guid.NewGuid(), Guid.NewGuid()]);              // two strangers took part: they do not count for the crew
        Assert.Equal(3, q.NotTakenPart);
    }

    [Fact]
    public void ThePreviousMonthIsWholeCalendarMonthBeforeToday()
    {
        Assert.Equal((D(2026, 10, 1), D(2026, 10, 31)), DrillRules.PreviousMonth(D(2026, 11, 12)));
        Assert.Equal((D(2026, 12, 1), D(2026, 12, 31)), DrillRules.PreviousMonth(D(2027, 1, 1)));
        Assert.Equal((D(2028, 2, 1), D(2028, 2, 29)), DrillRules.PreviousMonth(D(2028, 3, 31)));
    }

    // ---- duplicates and times ----

    [Fact]
    public void TwoDrillsAnHourApartAreAPossibleDuplicate_ASecondLaterIsNot()
    {
        var a = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(DrillRules.PossibleDuplicate(a, a));
        Assert.True(DrillRules.PossibleDuplicate(a, a.AddHours(1)));
        Assert.True(DrillRules.PossibleDuplicate(a.AddHours(1), a));
        Assert.False(DrillRules.PossibleDuplicate(a, a.AddHours(1).AddMinutes(1)));
    }

    [Fact]
    public void TheTimesOfADrillAreChecked()
    {
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(DrillRules.TimeProblem(now.AddHours(-2), now.AddHours(-1), now));
        Assert.Null(DrillRules.TimeProblem(now.AddHours(-2), null, now));
        Assert.Null(DrillRules.TimeProblem(now.AddDays(1), null, now));                                         // time zones: a day's leeway
        Assert.Contains("future", DrillRules.TimeProblem(now.AddDays(1).AddMinutes(1), null, now));
        Assert.Contains("before it starts", DrillRules.TimeProblem(now.AddHours(-2), now.AddHours(-3), now));
        Assert.Null(DrillRules.TimeProblem(now.AddHours(-13), now.AddHours(-1), now));                           // exactly 12 hours
        Assert.Contains("12 hours", DrillRules.TimeProblem(now.AddHours(-13), now.AddMinutes(-59), now));
    }

    // ---- the schedule ----

    private static CrewMember Crew(string name, DateTime signedOn) => new() { Id = Guid.NewGuid(), FullName = name, Rank = "Rank", SignOnDate = signedOn, VesselId = Guid.NewGuid(), IsActive = true };

    private static Drill Held(DrillType type, DateTime on, params CrewMember[] present)
        => new() { Id = Guid.NewGuid(), DrillTypeId = type.Id, StartedAt = on, Participants = [.. present.Select(c => new DrillParticipant { CrewMemberId = c.Id, Name = c.FullName, Attendance = DrillAttendance.Present })] };

    [Fact]
    public void TheScheduleShowsEachTypeWithItsShipAndItsPeople_OverdueFirstInTheWorstOfThem()
    {
        var fire = new DrillType { Id = Guid.NewGuid(), Name = "Fire", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 1, WarningDays = 7, EveryCrewMember = true, IsActive = true };
        var mob = new DrillType { Id = Guid.NewGuid(), Name = "Man overboard", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 3, WarningDays = 7, IsActive = true };
        var today = D(2026, 11, 12);
        var took = Crew("Took part", D(2026, 1, 1));
        var never = Crew("Never took part", D(2026, 5, 1));
        var joiner = Crew("Joined last week", today.AddDays(-5));
        var s = new DrillSnapshot([fire, mob], new Dictionary<Guid, DrillOverride>(), [Held(fire, D(2026, 10, 20), took)], [took, never, joiner]);

        var schedule = DrillSchedules.Build(Guid.NewGuid(), "MV One", today, s);
        var fireRow = schedule.Rows.Single(r => r.Type == fire);
        Assert.Equal((D(2026, 10, 20), D(2026, 11, 30), DrillStatus.NotDue), (fireRow.LastHeld, fireRow.Due, fireRow.ShipStatus));                // the ship is fine
        Assert.Equal(["Never took part"], fireRow.People.Select(p => p.Name));                                                                       // the joiner has time; the one who took part is not due
        Assert.Equal(DrillStatus.Overdue, fireRow.Status);                                                                                            // the person drags the row to the worst
        Assert.Equal(DrillStatus.Overdue, fireRow.People[0].Status);
        Assert.Equal(DrillStatus.NeverHeld, schedule.Rows.Single(r => r.Type == mob).ShipStatus);
        Assert.Empty(schedule.Rows.Single(r => r.Type == mob).People);                                                                               // a ship drill has no personal dates
        Assert.Equal(DrillStatus.Overdue, schedule.Worst);
    }

    [Fact]
    public void ATypeThatDoesNotApplyToAVesselIsLeftOut_AndAVesselIntervalIsUsed()
    {
        var a = new DrillType { Id = Guid.NewGuid(), Name = "A", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 1, IsActive = true };
        var b = new DrillType { Id = Guid.NewGuid(), Name = "B", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 1, IsActive = true };
        var overrides = new Dictionary<Guid, DrillOverride>
        {
            [a.Id] = new() { DrillTypeId = a.Id, Applies = false, Reason = "No lifeboat on this vessel" },
            [b.Id] = new() { DrillTypeId = b.Id, IntervalKind = DrillIntervalKind.Days, IntervalCount = 10 },
        };
        var schedule = DrillSchedules.Build(Guid.NewGuid(), "MV One", D(2026, 11, 12), new DrillSnapshot([a, b], overrides, [Held(b, D(2026, 11, 5))], []));
        var row = Assert.Single(schedule.Rows);
        Assert.Equal((b, D(2026, 11, 15), DrillStatus.DueSoon), (row.Type, row.Due, row.Status));
    }

    [Fact]
    public void TheQuarterRuleReadsLastMonthsDrillsOfTheTypesEveryoneAttendsOnly()
    {
        var fire = new DrillType { Id = Guid.NewGuid(), Name = "Fire", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 1, EveryCrewMember = true, IsActive = true };
        var mob = new DrillType { Id = Guid.NewGuid(), Name = "MOB", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 3, IsActive = true };
        var crew = Enumerable.Range(0, 4).Select(i => Crew($"C{i}", D(2026, 1, 1))).ToArray();
        var today = D(2026, 11, 12);
        var drills = new[]
        {
            Held(fire, D(2026, 10, 15), crew[0], crew[1]),                 // last month, two of four
            Held(mob, D(2026, 10, 16), crew[2], crew[3]),                  // not a type everyone attends: not counted
            Held(fire, D(2026, 9, 30), crew[2], crew[3]),                  // the month before: not counted
            Held(fire, D(2026, 11, 3), crew[2], crew[3]),                  // this month: not counted
        };
        var q = DrillSchedules.Build(Guid.NewGuid(), "MV One", today, new DrillSnapshot([fire, mob], new Dictionary<Guid, DrillOverride>(), drills, crew)).Quarter;
        Assert.Equal((4, 2, true), (q.CrewCount, q.NotTakenPart, q.Triggered));
    }

    [Fact]
    public void AnAbsentOrObservingPersonHasNotTakenPart()
    {
        var fire = new DrillType { Id = Guid.NewGuid(), Name = "Fire", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 1, WarningDays = 0, EveryCrewMember = true, IsActive = true };
        var person = Crew("P", D(2026, 1, 1));
        var drill = new Drill { Id = Guid.NewGuid(), DrillTypeId = fire.Id, StartedAt = D(2026, 10, 15), Participants = [new DrillParticipant { CrewMemberId = person.Id, Attendance = DrillAttendance.Absent, Absence = DrillAbsence.OnWatch }] };
        var row = DrillSchedules.Build(Guid.NewGuid(), "MV One", D(2026, 11, 12), new DrillSnapshot([fire], new Dictionary<Guid, DrillOverride>(), [drill], [person])).Rows.Single();
        Assert.Equal(DrillStatus.Overdue, Assert.Single(row.People).Status);                                       // an excused absence does not reset the clock
        Assert.Null(row.People[0].LastTookPart);
        Assert.Equal(D(2026, 10, 15), row.LastHeld);                                                              // the ship did hold the drill
    }
}
