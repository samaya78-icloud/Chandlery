using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Planned maintenance set against the work time the crew has left under the rules.</summary>
public class RestCapacityTests
{
    private static readonly RestLimits Limits = RestLimits.Default;
    private static readonly DateTime Today = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    // 8 h of work a day at sea (two 4-hour watches), 12 h in port.
    private static readonly ArrangementRow ThirdOfficer = new("Third Officer", "04:00-08:00,16:00-20:00", "", "08:00-20:00", "", "");
    private static readonly ArrangementRow Master = new("Master", "", "08:00-18:00", "", "08:00-18:00", "");
    private static readonly ArrangementRow Worker = new("Worker", "00:00-14:00", "", "00:00-14:00", "", "");

    [Fact]
    public void TheTimeLeftIsTheShorterOfWhatTheDayAndTheWeekAllow()
    {
        // 8 h worked: a day has 14 h - 8 h = 6 h left (42 h in a week), but the week allows 168 h - 77 h = 91 h of work, less 7 x 8 h = 35 h.
        Assert.Equal(35 * 60, RestCapacity.SparePerPerson(ThirdOfficer, Limits, inPort: false, 7));
        // 10 h worked: 4 h a day (28 h), the week leaves 91 - 70 = 21 h; the week is the tighter.
        Assert.Equal(21 * 60, RestCapacity.SparePerPerson(Master, Limits, inPort: false, 7));
        // 14 h worked: nothing left on the day, so nothing in the week.
        Assert.Equal(0, RestCapacity.SparePerPerson(Worker, Limits, inPort: false, 7));
    }

    [Fact]
    public void AnotherLengthOfWindowScalesTheWeek()
    {
        // Three days: 3 x 6 h = 18 h by the day; the allowance for three days is 3 x 24 h - 3 x 11 h = 39 h, less 3 x 8 h = 15 h.
        Assert.Equal(15 * 60, RestCapacity.SparePerPerson(ThirdOfficer, Limits, false, 3));
    }

    [Fact]
    public void ThePortColumnsAreUsedInPort()
    {
        // 12 h in port: 2 h a day (14 h); the week leaves 91 - 84 = 7 h; the week is the tighter.
        Assert.Equal(7 * 60, RestCapacity.SparePerPerson(ThirdOfficer, Limits, inPort: true, 7));
    }

    [Fact]
    public void AStricterRuleSetLeavesLessTime()
    {
        var strict = Limits with { MinRest24 = 12 * 60 };
        Assert.True(RestCapacity.SparePerPerson(ThirdOfficer, strict, false, 7) < RestCapacity.SparePerPerson(ThirdOfficer, Limits, false, 7));
    }

    private static CapacityJobs Jobs(int minutes, int count = 1, int without = 0, int overdue = 0) => new(count, minutes, without, overdue);

    [Fact]
    public void TheVerdictFollowsTheShareOfTheSpareTimeUsed()
    {
        var table = new[] { ThirdOfficer };
        var crew = new[] { "Third Officer", "third officer " };                      // two people, the name matched without case or spaces
        var spare = 2 * 35 * 60;                                                    // 4200 minutes

        var line = spare * RestCapacity.TightFromPercent / 100;                     // 2520
        var fits = RestCapacity.Compute(Today, 7, false, table, crew, Limits, Jobs(line));
        Assert.Equal((spare, CapacityVerdict.Fits), (fits.SpareMinutes, fits.Verdict));                   // exactly at the line is still fine

        var tight = RestCapacity.Compute(Today, 7, false, table, crew, Limits, Jobs(line + 1));
        Assert.Equal(CapacityVerdict.Tight, tight.Verdict);
        Assert.Equal(CapacityVerdict.Tight, RestCapacity.Compute(Today, 7, false, table, crew, Limits, Jobs(spare)).Verdict);        // all of it, and no more

        var over = RestCapacity.Compute(Today, 7, false, table, crew, Limits, Jobs(spare + 1));
        Assert.Equal(CapacityVerdict.Exceeds, over.Verdict);
        Assert.True(over.Ratio > 1);
    }

    [Fact]
    public void NoPlannedWorkAlwaysFits_AndWorkWithNoTimeLeftNeverDoes()
    {
        Assert.Equal(CapacityVerdict.Fits, RestCapacity.Compute(Today, 7, false, [ThirdOfficer], ["Third Officer"], Limits, Jobs(0, 0)).Verdict);
        var none = RestCapacity.Compute(Today, 7, false, [Worker], ["Worker"], Limits, Jobs(60));
        Assert.Equal((0, CapacityVerdict.Exceeds), (none.SpareMinutes, none.Verdict));
        Assert.Null(none.Ratio);
    }

    [Fact]
    public void WithoutATableOrAnyoneInIt_TheVerdictIsThatItCannotBeTold()
    {
        Assert.Equal(CapacityVerdict.Unknown, RestCapacity.Compute(Today, 7, false, null, ["Master"], Limits, Jobs(600)).Verdict);
        var nobody = RestCapacity.Compute(Today, 7, false, [ThirdOfficer], ["Cook"], Limits, Jobs(600));
        Assert.Equal(CapacityVerdict.Unknown, nobody.Verdict);
        Assert.Equal(["Cook"], nobody.Unmatched);
        Assert.True(nobody.HasTable);
    }

    [Fact]
    public void CrewWhoseRankIsNotInTheTableAreListedNotCounted()
    {
        var view = RestCapacity.Compute(Today, 7, false, [ThirdOfficer], ["Third Officer", "Bosun", "Bosun", ""], Limits, Jobs(60));
        Assert.Equal(["(no rank)", "Bosun"], view.Unmatched);
        Assert.Equal(1, view.Lines.Single().People);
    }

    [Fact]
    public void JobsInTheWindowIncludeWhatIsOverdue_AndSkipWhatIsInactiveOrLater()
    {
        var jobs = new (DateTime?, int?, bool)[]
        {
            (Today.AddDays(-3), 120, true),          // overdue: counts
            (Today, 60, true),                       // today
            (Today.AddDays(6), 30, true),            // the seventh day: counts
            (Today.AddDays(7), 500, true),           // the eighth: not yet
            (Today.AddDays(1), 500, false),          // not on the plan any more
            (null, 500, true),                       // no due date
            (Today.AddDays(2), null, true),          // due, but no estimate
        };
        var planned = RestCapacity.JobsIn(jobs, Today, 7);
        Assert.Equal((4, 210, 1, 1), (planned.Count, planned.Minutes, planned.WithoutEstimate, planned.Overdue));
    }
}

public class RestRetentionTests
{
    private static readonly Guid Vessel = Guid.NewGuid(), Crew = Guid.NewGuid(), OtherCrew = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    private static RestSignOff Sign(DateTime to, RestRole role = RestRole.Master, Guid? crew = null, DateTime? at = null, int days = 7)
        => new() { Id = Guid.CreateVersion7(), VesselId = Vessel, CrewMemberId = crew ?? Crew, CrewName = "A. Seafarer", FromDate = to.AddDays(1 - days), ToDate = to, Role = role, SignedByName = "Master M", SignedAt = at ?? to.AddDays(2) };

    private static RestEntry Entry(DateTime date, DateTime at, Guid? crew = null) => new() { Id = Guid.CreateVersion7(), VesselId = Vessel, CrewMemberId = crew ?? Crew, Date = date, EnteredAt = at };

    private static List<RestRetentionRow> Past(int months, IEnumerable<RestSignOff> signs, IEnumerable<RestEntry> entries) => RestRetention.PastRetention("MV One", Vessel, months, Today, signs, entries);

    private static readonly DateTime Old = new(2023, 3, 7, 0, 0, 0, DateTimeKind.Utc);          // 3 years 8 months ago

    [Fact]
    public void AFinishedOldPeriodIsListedWithItsAgeAndHowManyEntriesItHolds()
    {
        var sign = Sign(Old);
        var rows = Past(36, [sign], [Entry(Old.AddDays(-2), Old.AddDays(-1)), Entry(Old, Old.AddDays(1)), Entry(Old, Old.AddDays(1).AddHours(2))]);
        var row = Assert.Single(rows);
        Assert.Equal(("MV One", "A. Seafarer", Old.AddDays(-6), Old, 3, 44, 36), (row.Vessel, row.CrewName, row.From, row.To, row.Revisions, row.AgeMonths, row.RetentionMonths));
        Assert.Equal("Master M", row.SignedBy);
    }

    [Fact]
    public void ThePeriodIsKeptUntilItsLastDayIsOlderThanTheRetentionTime()
    {
        var exactly = Today.AddMonths(-36);
        Assert.Empty(Past(36, [Sign(exactly)], []));                                    // exactly the retention time: still kept
        Assert.Single(Past(36, [Sign(exactly.AddDays(-1))], []));                       // a day more: past it
        Assert.Single(Past(12, [Sign(Today.AddMonths(-13))], []));                      // another rule set, another time
        Assert.Empty(Past(48, [Sign(Old)], []));
    }

    [Fact]
    public void OnlyThePeriodsTheMasterSigned_AndNothingChangedAfterwards_AreListed()
    {
        Assert.Empty(Past(36, [Sign(Old, RestRole.Seafarer)], []));                                                   // the seafarer's signature alone is not enough
        Assert.Empty(Past(36, [Sign(Old)], [Entry(Old, Old.AddDays(5))]));                                            // a day was changed after the signature
        Assert.Single(Past(36, [Sign(Old)], [Entry(Old, Old.AddDays(1))]));                                           // changed before it: fine
        Assert.Single(Past(36, [Sign(Old)], [Entry(Old.AddDays(10), Old.AddDays(40))]));                              // a later change to a day outside the period does not matter
    }

    [Fact]
    public void ASignatureGivenAgainAfterAChangeMakesThePeriodFinalAgain()
    {
        var first = Sign(Old, at: Old.AddDays(2));
        var again = Sign(Old, at: Old.AddDays(9));
        var changed = Entry(Old, Old.AddDays(5));
        Assert.Empty(Past(36, [first], [changed]));
        var row = Assert.Single(Past(36, [first, again], [changed]));
        Assert.Equal(Old.AddDays(9), row.SignedAt);
    }

    [Fact]
    public void EachSeafarerIsJudgedOnTheirOwnEntries()
    {
        var rows = Past(36, [Sign(Old), Sign(Old, crew: OtherCrew)], [Entry(Old, Old.AddDays(5), OtherCrew)]);
        Assert.Equal(Crew, Assert.Single(rows).CrewId);
    }

    [Theory]
    [InlineData("2026-11-09", "2023-03-07", 44)]
    [InlineData("2026-11-06", "2023-03-07", 43)]
    [InlineData("2026-03-07", "2023-03-07", 36)]
    [InlineData("2026-03-06", "2023-03-07", 35)]
    public void AgeIsInWholeMonths(string today, string from, int expected) => Assert.Equal(expected, RestRetention.AgeInMonths(DateTime.Parse(from), DateTime.Parse(today)));

    [Fact]
    public void TheCsvHasOneRowPerPeriod_WithTheHeadersAsTheOtherReports()
    {
        var csv = RestReports.RetentionCsv(Past(36, [Sign(Old)], []));
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("\"Vessel\",\"Seafarer\",\"From\",\"To\",", lines[0]);
        Assert.Contains("MV One", lines[1]);
        Assert.Contains("2023-03-07", lines[1]);
    }
}

public class RestCapacitySiteTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private WorkArrangementService _shoreTable = null!;
    private RestCapacityService _vessel = null!, _shore = null!;
    private RestFleetService _vesselFleet = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreTable = new WorkArrangementService(_s.ShoreFactory);
        _vessel = new RestCapacityService(_s.VesselFactory);
        _shore = new RestCapacityService(_s.ShoreFactory);
        _vesselFleet = new RestFleetService(_s.VesselFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RestHours, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task SignOnAsync(string code, string rank)
    {
        await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false), _s.User);
        await _s.SettleUntilQuietAsync();
    }

    private static PlannedJob Job(int daysFromNow, int? minutes) => new() { Id = Guid.CreateVersion7(), NextDueDate = DateTime.UtcNow.Date.AddDays(daysFromNow), EstimatedMinutes = minutes, IsActive = true };

    [Fact]
    public async Task TheViewIsNullWhereRestHoursIsOff_AndHasNoTimeLeftToCountUntilThereIsATable()
    {
        Assert.Null(await _vessel.ForVesselAsync(_s.CompanyId, _s.VesselId, [Job(1, 60)]));

        await SwitchOnAsync();
        await SignOnAsync("C1001", "Third Officer");
        var without = (await _vessel.ForVesselAsync(_s.CompanyId, _s.VesselId, [Job(1, 60)]))!;
        Assert.False(without.HasTable);
        Assert.Equal(CapacityVerdict.Unknown, without.Verdict);
    }

    [Fact]
    public async Task TheOfficesTableAndTheVesselsCrewAndStatusGiveTheSameViewOnBothSides()
    {
        await SwitchOnAsync();
        await SignOnAsync("C2001", "Third Officer");
        await SignOnAsync("C2002", "Third Officer");
        await SignOnAsync("C2003", "Cook");
        await _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, new ArrangementInput(DateTime.UtcNow.Date.AddDays(-30), "", "", "",
            [new ArrangementRow("Third Officer", "04:00-08:00,16:00-20:00", "", "08:00-20:00", "", "")]), _s.User);
        await _s.SettleUntilQuietAsync();

        var jobs = new[] { Job(-2, 600), Job(3, 900), Job(5, null), Job(20, 5000) };
        var onboard = (await _vessel.ForVesselAsync(_s.CompanyId, _s.VesselId, jobs))!;
        var ashore = (await _shore.ForVesselAsync(_s.CompanyId, _s.VesselId, jobs))!;

        foreach (var view in new[] { onboard, ashore })
        {
            Assert.True(view.HasTable);
            Assert.False(view.InPort);                                           // at sea until told otherwise
            Assert.Equal(["Cook"], view.Unmatched);
            Assert.Equal(2 * 35 * 60, view.SpareMinutes);
            Assert.Equal((3, 1500, 1, 1), (view.Jobs.Count, view.Jobs.Minutes, view.Jobs.WithoutEstimate, view.Jobs.Overdue));
            Assert.Equal(CapacityVerdict.Fits, view.Verdict);
        }

        // In port the table has them work longer, so less is left.
        await new RestHoursService(_s.VesselFactory).SetDayStatusAsync(_s.CompanyId, _s.VesselId, DateTime.UtcNow.Date, VesselDayKind.InPort, "Singapore", _s.User);
        var inPort = (await _vessel.ForVesselAsync(_s.CompanyId, _s.VesselId, jobs))!;
        Assert.True(inPort.InPort);
        Assert.Equal(2 * 7 * 60, inPort.SpareMinutes);
        Assert.Equal(CapacityVerdict.Exceeds, inPort.Verdict);                   // 1500 minutes against 840
    }

    [Fact]
    public async Task TheRetentionListReadsEachVesselsOwnRuleSet_AndOnlyFinishedOldPeriods()
    {
        await SwitchOnAsync();
        var crewId = Guid.CreateVersion7();
        var old = DateTime.UtcNow.Date.AddMonths(-40);
        var recent = DateTime.UtcNow.Date.AddMonths(-6);
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            foreach (var to in new[] { old, recent })
                db.RestSignOffs.Add(new RestSignOff { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, CrewMemberId = crewId, CrewName = "Old Hand", FromDate = to.AddDays(-6), ToDate = to, Role = RestRole.Master, SignedByName = "Master M", SignedAt = to.AddDays(1) });
            db.RestEntries.Add(new RestEntry { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, CrewMemberId = crewId, CrewName = "Old Hand", Date = old, WorkSpans = "08:00-12:00", EnteredAt = old });
            await db.SaveChangesAsync();
        }

        var rows = await _vesselFleet.RetentionAsync(_s.CompanyId);
        var row = Assert.Single(rows);                                           // the default is 36 months: only the 40-month-old period is past it
        Assert.Equal((old, 1, 36), (row.To, row.Revisions, row.RetentionMonths));

        // A rule set that keeps records for 60 months leaves nothing to review.
        await new RestHoursService(_s.ShoreFactory).SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Long", RetentionMonths = 60, IsDefault = true, IsActive = true });
        await _s.SettleUntilQuietAsync();
        Assert.Empty(await _vesselFleet.RetentionAsync(_s.CompanyId));
    }
}
