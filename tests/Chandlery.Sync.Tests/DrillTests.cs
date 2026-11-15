using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Drills between a vessel and the office: who records and signs, what a drill holds, the schedule worked out on both sides, and the types and vessel rules the office sets.</summary>
public class DrillTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private DrillService _shore = null!, _vessel = null!;
    private WorklistService _shoreWorklist = null!;
    private CrewMember _master = null!, _officer = null!, _cook = null!, _oldHand = null!;
    private List<DrillType> _types = null!;
    private DrillType Fire => _types.Single(t => t.Name == "Fire drill");
    private DrillType Mob => _types.Single(t => t.Name == "Man overboard");

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _shore = new DrillService(_s.ShoreFactory, _shoreWorklist);
        _vessel = new DrillService(_s.VesselFactory, new WorklistService(_s.VesselFactory));
        await SwitchOnAsync(_s.VesselId, PolicyModules.Drills, PolicyModules.Worklist);
        _master = await SignOnAsync("R1001", "Master", master: true);
        _officer = await SignOnAsync("R1002", "Chief Officer", authorities: CrewAuthority.ConductDrills);
        _cook = await SignOnAsync("R1003", "Cook");
        _oldHand = await SignOnAsync("R1004", "Able Seaman", signedOn: DateTime.UtcNow.Date.AddMonths(-6));
        _types = await _shore.TypesAsync(_s.CompanyId);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(Guid vesselId, params string[] modules)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in modules) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false, string authorities = "", DateTime? signedOn = null, Guid? vessel = null)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", vessel ?? _s.VesselId, signedOn, true, false, master, Authorities: authorities), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static DrillParticipantInput Present(CrewMember c) => new(c.Id, "", "", DrillAttendance.Present);

    private DrillInput Input(DrillType? type = null, DateTime? started = null, DateTime? ended = null, IReadOnlyList<DrillParticipantInput>? people = null, Guid? conductor = null,
        string findings = "", string deficiencies = "")
        => new((type ?? Fire).Id, (started ??= DateTime.UtcNow.AddHours(-3)), ended ?? started.Value.AddHours(1), IncidentShipStatus.AtSea, "Muster stations", "A fire in the galley", conductor,
            "Fire pump, hydrants", findings, deficiencies, "", people ?? [Present(_officer), Present(_master), Present(_cook)]);

    private static string Line(DrillDetail d)
        => $"{d.Drill.Code}|{d.Drill.TypeName}|signed {d.Drill.IsSigned}:{d.Drill.SignedByName}|{d.Drill.ConductedByName}|people {string.Join(",", d.Drill.Participants.OrderBy(p => p.Name).Select(p => $"{p.Name}:{p.Attendance}:{p.Absence}"))}|"
           + $"notes {d.Drill.Notes.Count}|comments {d.Comments.Count}|actions {d.Actions.Count}|dup {d.PossibleDuplicate}|find {d.Drill.Findings}|def {d.Drill.Deficiencies}";

    // ---- recording and signing ----

    [Fact]
    public async Task TheMasterAndAnAuthorisedOfficerRecordADrill_TheMasterSigns_AndBothSidesHoldTheSameThing()
    {
        Assert.Contains("authority", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_cook), Input()))).Message);
        Assert.Contains("onboard", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, _s.VesselId, _s.User, Input()))).Message);

        var people = new[] { Present(_officer), Present(_master), Present(_cook), new DrillParticipantInput(_oldHand.Id, "", "", DrillAttendance.Absent, DrillAbsence.OnWatch),
                             new DrillParticipantInput(null, "Port State Control inspector", "Visitor", DrillAttendance.Observer) };
        var drill = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(people: people, findings: "Muster was slow at the aft station.", deficiencies: "Fire pump pressure low"));
        Assert.Contains("-DR-", drill.Code);
        Assert.Equal((_officer.FullName, false, 5), (drill.ConductedByName, drill.IsSigned, drill.Participants.Count));
        Assert.Equal(DrillAbsence.OnWatch, drill.Participants.Single(p => p.CrewMemberId == _oldHand.Id).Absence);

        // Until the Master signs, its conductor or the Master may change it; nobody else.
        await _vessel.UpdateAsync(_s.CompanyId, drill.Id, As(_officer), Input(people: people, findings: "Muster was slow at the aft station and the foam monitor stuck.", deficiencies: "Fire pump pressure low"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.UpdateAsync(_s.CompanyId, drill.Id, As(_cook), Input(people: people)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignAsync(_s.CompanyId, drill.Id, As(_officer)));               // only the Master signs
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, drill.Id, As(_officer), "Not yet signed"));
        await _vessel.SignAsync(_s.CompanyId, drill.Id, As(_master));
        Assert.Contains("signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.UpdateAsync(_s.CompanyId, drill.Id, As(_master), Input(people: people)))).Message);
        Assert.Contains("already", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignAsync(_s.CompanyId, drill.Id, As(_master)))).Message);
        await _vessel.AddNoteAsync(_s.CompanyId, drill.Id, As(_officer), "Foam monitor freed and tested again at 16:00.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, drill.Id, As(_cook), "Not mine"));
        await _vessel.AddActionAsync(_s.CompanyId, drill.Id, As(_master), new NewDrillAction("Test the fire pump and clear the foam monitor", "Before the next drill.", DateTime.UtcNow.AddDays(7)));
        await _s.SettleUntilQuietAsync();

        await _shore.CommentAsync(_s.CompanyId, drill.Id, _s.User, "Please send the pump test record.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CommentAsync(_s.CompanyId, drill.Id, As(_master), "Onboard comments are notes"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SignAsync(_s.CompanyId, drill.Id, _s.User));
        await _s.SettleUntilQuietAsync();

        var ashore = (await _shore.GetAsync(_s.CompanyId, drill.Id, _s.User))!;
        var onboard = (await _vessel.GetAsync(_s.CompanyId, drill.Id, As(_cook)))!;                      // any crew member sees the vessel's drills
        Assert.Equal(Line(ashore), Line(onboard));
        Assert.Equal((true, 1, 1, 1), (ashore.Drill.IsSigned, ashore.Drill.Notes.Count, ashore.Comments.Count, ashore.Actions.Count));
        Assert.Equal(TimeSpan.FromHours(1), ashore.Duration);
        Assert.True(ashore.Can.IsOffice && ashore.Can.Comment && ashore.Can.AddAction && !ashore.Can.Edit && !ashore.Can.Sign);
        var master = (await _vessel.GetAsync(_s.CompanyId, drill.Id, As(_master)))!.Can;
        Assert.True(master.Note && master.AddAction && !master.Edit && !master.Sign && !master.Comment);          // signed: notes only
    }

    [Fact]
    public async Task ADrillIsRefusedWhenItsTimesItsPeopleOrItsTypeAreWrong()
    {
        async Task<string> Refused(DrillInput input, CrewMember? by = null) => (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(by ?? _officer), input))).Message;

        Assert.Contains("future", await Refused(Input(started: DateTime.UtcNow.AddDays(3))));
        Assert.Contains("before it starts", await Refused(Input(started: DateTime.UtcNow.AddHours(-3), ended: DateTime.UtcNow.AddHours(-4))));
        Assert.Contains("12 hours", await Refused(Input(started: DateTime.UtcNow.AddHours(-20), ended: DateTime.UtcNow.AddHours(-2))));
        Assert.Contains("kind of drill", await Refused(Input() with { DrillTypeId = Guid.NewGuid() }));
        Assert.Contains("at least one", await Refused(Input(people: [new DrillParticipantInput(_cook.Id, "", "", DrillAttendance.Absent, DrillAbsence.Sick)])));
        Assert.Contains("twice", await Refused(Input(people: [Present(_cook), Present(_cook)])));
        Assert.Contains("not one of the vessel's crew", await Refused(Input(people: [Present(_cook), new DrillParticipantInput(Guid.NewGuid(), "", "", DrillAttendance.Present)])));
        Assert.Contains("observer", await Refused(Input(people: [Present(_cook), new DrillParticipantInput(null, "A visitor", "", DrillAttendance.Present)])));
        Assert.Contains("absent", await Refused(Input(people: [Present(_cook), new DrillParticipantInput(_master.Id, "", "", DrillAttendance.Present, DrillAbsence.Sick)])));
        Assert.Contains("not one of the vessel's crew", await Refused(Input(conductor: Guid.NewGuid())));
        Assert.Contains("Master names", await Refused(Input(conductor: _master.Id), by: _officer));                       // an officer names themselves only
        Assert.Contains("authority", await Refused(Input(conductor: _cook.Id), by: _master));                              // the cook has not been given it

        // The Master may name an officer with the authority as the conductor.
        var named = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(conductor: _officer.Id));
        Assert.Equal(_officer.FullName, named.ConductedByName);
    }

    [Fact]
    public async Task ATypeThatNoLongerApplies_OrIsSwitchedOff_CannotBeRecorded()
    {
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, Mob.Id, applies: false, null, null, "No rescue boat on this vessel");
        var inactive = _types.Single(t => t.Name == "Blackout");
        await _shore.SaveTypeAsync(_s.CompanyId, new DrillType { Id = inactive.Id, Name = inactive.Name, Category = inactive.Category, IntervalKind = inactive.IntervalKind, IntervalCount = inactive.IntervalCount, WarningDays = inactive.WarningDays, EveryCrewMember = inactive.EveryCrewMember, IsActive = false });
        await _s.SettleUntilQuietAsync();
        Assert.Contains("does not apply", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(Mob)))).Message);
        Assert.Contains("no longer in use", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(inactive)))).Message);
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(Fire));
    }

    // ---- the schedule ----

    [Fact]
    public async Task TheScheduleIsTheSameOnBothSides_AnAbsentPersonKeepsTheirClock_AndOnlyApplicableTypesAreShown()
    {
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, Mob.Id, applies: false, null, null, "No rescue boat on this vessel");
        await _s.SettleUntilQuietAsync();
        var today = DateTime.UtcNow.Date;
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(Fire, started: today.AddDays(-2).AddHours(10),
            people: [Present(_master), Present(_officer), Present(_cook), new DrillParticipantInput(_oldHand.Id, "", "", DrillAttendance.Absent, DrillAbsence.OnWatch)]));
        await _s.SettleUntilQuietAsync();

        var onboard = await _vessel.ScheduleAsync(_s.CompanyId, _s.VesselId, As(_cook));
        var ashore = await _shore.ScheduleAsync(_s.CompanyId, _s.VesselId, _s.User);
        string Show(DrillSchedule s) => string.Join("|", s.Rows.Select(r => $"{r.Type.Name}:{r.LastHeld:yyyy-MM-dd}:{r.Due:yyyy-MM-dd}:{r.ShipStatus}:{r.Status}:{string.Join(",", r.People.Select(p => $"{p.Name}={p.Status}"))}")) + $"|{s.Worst}|{s.Quarter}";
        Assert.Equal(Show(ashore), Show(onboard));

        Assert.DoesNotContain(onboard.Rows, r => r.Type.Id == Mob.Id);                                             // not applicable here
        var fire = onboard.Rows.Single(r => r.Type.Id == Fire.Id);
        Assert.Equal((today.AddDays(-2), DrillStatus.NotDue), (fire.LastHeld, fire.ShipStatus));
        Assert.Equal([_oldHand.FullName], fire.People.Select(p => p.Name));                                        // joined six months ago and has never taken part: late, though the ship is not
        Assert.Equal(DrillStatus.Overdue, fire.People[0].Status);
        Assert.Equal(DrillStatus.Overdue, fire.Status);
        Assert.Equal(DrillStatus.NeverHeld, onboard.Rows.Single(r => r.Type.Name == "Abandon ship drill").ShipStatus);
        Assert.Equal(DrillStatus.Overdue, onboard.Worst);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ScheduleAsync(_s.CompanyId, _s.OtherVesselId, As(_cook)));                  // not their vessel
    }

    [Fact]
    public async Task TheQuarterRuleFollowsLastMonthsDrillsOfTheTypesEveryoneAttends()
    {
        var lastMonth = DrillRules.PreviousMonth(DateTime.UtcNow.Date).From.AddDays(9);
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(Fire, started: lastMonth.AddHours(10), people: [Present(_master), Present(_officer)]));      // two of four took part
        await _s.SettleUntilQuietAsync();
        var q = (await _shore.ScheduleAsync(_s.CompanyId, _s.VesselId, _s.User)).Quarter;
        Assert.Equal((4, 2, true), (q.CrewCount, q.NotTakenPart, q.Triggered));                                    // two of four is half: more than a quarter
    }

    [Fact]
    public async Task ACrewMemberSeesTheirOwnDrills_TheMasterAndTheOfficeAnyones()
    {
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(Fire, people: [Present(_master), Present(_cook)]));
        await _s.SettleUntilQuietAsync();

        var mine = await _vessel.PersonalAsync(_s.CompanyId, _s.VesselId, _cook.Id, As(_cook));
        Assert.Equal(["Fire drill", "Abandon ship drill"], mine.Select(r => r.Type.Name));
        Assert.NotNull(mine[0].LastTookPart);
        Assert.Null(mine[1].LastTookPart);
        Assert.Equal(DrillStatus.NotDue, mine[0].Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.PersonalAsync(_s.CompanyId, _s.VesselId, _master.Id, As(_cook)));       // not someone else's
        Assert.Equal(DrillStatus.Overdue, (await _vessel.PersonalAsync(_s.CompanyId, _s.VesselId, _oldHand.Id, As(_master))).First().Status);        // joined long ago, never took part
        Assert.Equal(2, (await _shore.PersonalAsync(_s.CompanyId, _s.VesselId, _oldHand.Id, _s.User)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.PersonalAsync(_s.CompanyId, _s.OtherVesselId, _cook.Id, As(_cook)));        // not their vessel
    }

    // ---- types and the vessel's own rules ----

    [Fact]
    public async Task TheOfficeKeepsTheTypes_TheyReachTheVessel_AndAVesselRuleReachesOnlyThatVessel()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveTypeAsync(_s.CompanyId, new DrillType { Name = "Own drill" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveOverrideAsync(_s.CompanyId, _s.VesselId, Fire.Id, false, null, null, "No"));
        Assert.Contains("name", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new DrillType { Name = " " }))).Message);
        Assert.Contains("already", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new DrillType { Name = "Fire drill" }))).Message);
        Assert.Contains("1 to 24 months", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new DrillType { Name = "X drill", IntervalKind = DrillIntervalKind.CalendarMonths, IntervalCount = 25 }))).Message);
        Assert.Contains("1 to 730 days", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new DrillType { Name = "X drill", IntervalKind = DrillIntervalKind.Days, IntervalCount = 0 }))).Message);
        Assert.Contains("warning", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new DrillType { Name = "X drill", IntervalCount = 1, WarningDays = 120 }))).Message);

        var added = await _shore.SaveTypeAsync(_s.CompanyId, new DrillType { Name = "Crane failure", Category = DrillCategory.Emergency, IntervalKind = DrillIntervalKind.Days, IntervalCount = 90, WarningDays = 14, Checklist = "Stop the crane\nLower the load", IsActive = true });
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, added.Id, true, DrillIntervalKind.Days, 45, "A busier crane on this vessel");
        Assert.Contains("why", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, added.Id, true, DrillIntervalKind.Days, 30, " "))).Message);
        Assert.Contains("both", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, added.Id, true, DrillIntervalKind.Days, null, "Why"))).Message);
        await _s.SettleUntilQuietAsync();

        var onboardTypes = await _vessel.TypesAsync(_s.CompanyId);
        Assert.Contains(onboardTypes, t => t.Id == added.Id && t.IntervalCount == 90);
        var over = Assert.Single(await _vessel.OverridesAsync(_s.CompanyId, _s.VesselId));
        Assert.Equal((added.Id, 45), (over.DrillTypeId, over.IntervalCount));

        // Putting the vessel back on the company's rule needs no reason.
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, added.Id, true, null, null, "");
        await _s.SettleUntilQuietAsync();
        Assert.Null(Assert.Single(await _vessel.OverridesAsync(_s.CompanyId, _s.VesselId)).IntervalCount);
    }

    // ---- what travels ----

    [Fact]
    public async Task ChangesToThePeopleAndNotesMadeAfterTheDrillHasAlreadyTravelled_TravelToo()
    {
        var drill = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(people: [Present(_master), Present(_cook)]));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, (await _shore.GetAsync(_s.CompanyId, drill.Id, _s.User))!.Drill.Participants.Count);

        await _vessel.UpdateAsync(_s.CompanyId, drill.Id, As(_master), Input(people: [Present(_master), Present(_cook), Present(_officer), Present(_oldHand)]));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(4, (await _shore.GetAsync(_s.CompanyId, drill.Id, _s.User))!.Drill.Participants.Count);

        await _vessel.SignAsync(_s.CompanyId, drill.Id, As(_master));
        await _s.SettleUntilQuietAsync();
        await _vessel.AddNoteAsync(_s.CompanyId, drill.Id, As(_master), "Two late arrivals joined after the alarm.");
        await _s.SettleUntilQuietAsync();
        var ashore = (await _shore.GetAsync(_s.CompanyId, drill.Id, _s.User))!.Drill;
        Assert.Equal((true, 1), (ashore.IsSigned, ashore.Notes.Count));
    }

    [Fact]
    public async Task AVesselNeverHoldsAnotherVesselsDrillsOrRules_NotEvenAfterAFullBaseline()
    {
        await SwitchOnAsync(_s.OtherVesselId, PolicyModules.Drills);
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input());
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.OtherVesselId, Fire.Id, true, DrillIntervalKind.Days, 20, "A smaller crew");
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, Fire.Id, true, DrillIntervalKind.Days, 25, "A busy vessel");
        await _s.SettleUntilQuietAsync();
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        await using var vessel = await _s.VesselFactory.CreateDbContextAsync();
        Assert.All(await vessel.Drills.IgnoreQueryFilters().ToListAsync(), d => Assert.Equal(_s.VesselId, d.VesselId));
        var overrides = await vessel.DrillOverrides.IgnoreQueryFilters().ToListAsync();
        Assert.Equal([_s.VesselId], overrides.Select(o => o.VesselId));
        Assert.Equal(25, overrides.Single().IntervalCount);
        Assert.Equal(_types.Count, await vessel.DrillTypes.IgnoreQueryFilters().CountAsync());                     // the types are the company's: every vessel has them all
        Assert.Single(await _shore.ListAsync(_s.CompanyId, _s.User, _s.VesselId));
        Assert.Empty(await _shore.ListAsync(_s.CompanyId, _s.User, _s.OtherVesselId));
        Assert.Single(await _vessel.ListAsync(_s.CompanyId, As(_cook)));
    }

    [Fact]
    public async Task ANewCompanyStartsWithTheCommonDrillTypes_EachWithAnIntervalAndAReferenceToVerify()
    {
        Assert.Equal(11, _types.Count);
        Assert.All(_types, t => Assert.Contains("to verify", t.Reference));
        Assert.Equal((DrillIntervalKind.CalendarMonths, 1, true), (Fire.IntervalKind, Fire.IntervalCount, Fire.EveryCrewMember));
        Assert.Equal(2, _types.Single(t => t.Name.StartsWith("Enclosed space")).IntervalCount);
        Assert.Equal(2, _types.Count(t => t.EveryCrewMember));                                                       // fire and abandon ship
        await Task.CompletedTask;
    }

    // ---- the office's views (C4) ----

    [Fact]
    public async Task TheOfficeSeesTheFleetAndWhoHasNotTakenPart_AndOnboardOnlyTheMasterSeesTheCrewGrid()
    {
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(people: [Present(_officer), Present(_master), Present(_cook)]));
        await _s.SettleUntilQuietAsync();
        var fleet = new DrillFleetService(_shore, new MeetingService(_s.ShoreFactory, _shoreWorklist));

        var view = await fleet.FleetAsync(_s.CompanyId, _s.User);
        var vessel = Assert.Single(view.Vessels);
        var cell = vessel.Cells[Fire.Id];
        Assert.Equal(DateTime.UtcNow.AddHours(-3).Date, cell.LastHeld);
        Assert.Equal(1, cell.PeopleLate);                                                                            // the old hand was not there
        Assert.Equal(DrillStatus.Overdue, cell.Status);                                                                  // someone past the joiner grace never took part

        var ashore = await _shore.ParticipationAsync(_s.CompanyId, _s.VesselId, _s.User);
        var onboard = await _vessel.ParticipationAsync(_s.CompanyId, _s.VesselId, As(_master));
        string Show(DrillParticipation p) => string.Join("|", p.Crew.Select(c => $"{c.Name}:{string.Join(",", c.Standing.Select(x => $"{x.Type.Name}={x.Status}/{x.LastTookPart:yyyy-MM-dd}"))}"));
        Assert.Equal(Show(ashore), Show(onboard));
        Assert.Equal(DrillStatus.Overdue, ashore.Crew.Single(c => c.CrewId == _oldHand.Id).Standing.Single(x => x.Type.Id == Fire.Id).Status);
        Assert.Contains("Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ParticipationAsync(_s.CompanyId, _s.VesselId, As(_officer)))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ParticipationAsync(_s.CompanyId, _s.VesselId, As(_cook)));
    }

    [Fact]
    public async Task TheMenuCountOnlyCountsWhatIsOverdue_AndTheRecordsBecomeAPdfAndACsv()
    {
        var fleet = new DrillFleetService(_shore, new MeetingService(_s.ShoreFactory, _shoreWorklist));
        Assert.Equal(2, await fleet.OverdueAsync(_s.CompanyId, _s.User));                                            // nothing held: the old hand has never taken part in the two drills everyone attends

        var old = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(started: DateTime.UtcNow.AddHours(-5), people: [Present(_officer), Present(_master), Present(_cook), Present(_oldHand)]));
        await _vessel.SignAsync(_s.CompanyId, old.Id, As(_master));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(1, await fleet.OverdueAsync(_s.CompanyId, _s.User));                                            // fire is done by all four; abandon ship still is not

        var detail = (await _shore.GetAsync(_s.CompanyId, old.Id, _s.User))!;
        var pdf = DrillPdf.Build(detail, "Test Co");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal($"drill-{old.Code}.pdf", DrillPdf.FileName(detail));
        var rows = await _shore.ListAsync(_s.CompanyId, _s.User, _s.VesselId);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(DrillPdf.BuildLog(rows, "MV Test", "Test Co", DateTime.UtcNow.AddYears(-1), DateTime.UtcNow), 0, 4));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(DrillPdf.BuildLog([], "MV Test", "Test Co", DateTime.UtcNow.AddYears(-1), DateTime.UtcNow), 0, 4));      // an empty period still prints

        var csv = DrillReports.RegisterCsv(rows).TrimEnd().Split('\n');
        Assert.Equal(2, csv.Length);
        Assert.Contains(old.Code, csv[1]);
        Assert.Contains("4,0", csv[1].Replace("\"", ""));                                                            // four took part, none absent
        Assert.Equal(1 + 4 * 2, DrillReports.ParticipationCsv(await _shore.ParticipationAsync(_s.CompanyId, _s.VesselId, _s.User)).TrimEnd().Split('\n').Length);      // four crew by the two drills everyone attends
    }

    [Fact]
    public async Task TheHomePageCountsCoverTheFleet_OverdueNeverHeldAndCrewWithoutADrill()
    {
        var fleet = new DrillFleetService(_shore, new MeetingService(_s.ShoreFactory, _shoreWorklist));
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(people: [Present(_officer), Present(_master), Present(_cook), Present(_oldHand)]));
        await _s.SettleUntilQuietAsync();

        var c = (await fleet.CountsAsync(_s.CompanyId, _s.User))!;
        Assert.True(c.IsOffice);
        Assert.Equal(1, c.Overdue);                                                                                  // abandon ship: the old hand has never taken part
        Assert.Equal(_types.Count - 1, c.NeverHeld);                                                                 // every type but the fire drill has no record on the ship
        Assert.True(c.CrewLate >= 1);
        Assert.Equal(0, c.MeetingsOverdue);                                                                          // a meeting never held is not overdue
        Assert.Null(await new DrillFleetService(_shore, new MeetingService(_s.ShoreFactory, _shoreWorklist)).CountsAsync(Guid.NewGuid(), _s.User));   // a company with the module on nowhere has no card
    }

    // ---- links to PMS and rest hours (C5b) ----

    [Fact]
    public async Task ADeficiencyCanNameTheEquipment_FromThisVesselsPlanOnly_AndTheTaskCarriesIt()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.Pms);
        var pms = new PmsService(_s.ShoreFactory);
        var pump = await pms.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Emergency fire pump", "FF-01"), _s.User);
        var drill = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(deficiencies: "Fire pump pressure low"));
        await _s.SettleUntilQuietAsync();

        var task = await _shore.AddActionAsync(_s.CompanyId, drill.Id, _s.User, new NewDrillAction("Repair the pump", "Pressure low at the test.", MachineryItemId: pump.Id));
        Assert.Contains("Emergency fire pump (FF-01)", task.Description);
        var entries = (await _shoreWorklist.GetAsync(_s.CompanyId, task.Id))!.Entries;
        var links = entries.Where(e => e.Type == WorklistEntryType.Link).Select(e => e.Payload).ToList();
        Assert.Contains(links, l => l.Contains(WorklistLinkTypes.Machinery) && l.Contains(pump.Id.ToString()));
        Assert.Contains(links, l => l.Contains(WorklistLinkTypes.Drill));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddActionAsync(_s.CompanyId, drill.Id, _s.User, new NewDrillAction("Repair something", "", MachineryItemId: Guid.NewGuid())));
        Assert.Contains("maintenance plan", refused.Message);
    }

    private async Task<(DrillRestService Service, RestHoursService Hours, Drill Drill)> DrillOnADayOfWorkAsync(DateTime start, DateTime end, params (CrewMember Who, string Work)[] days)
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.RestHours);
        var rest = new RestHoursService(_s.VesselFactory);
        foreach (var (who, work) in days) await rest.RecordDayAsync(_s.CompanyId, _s.VesselId, who.Id, start, work, As(who));
        var drill = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(started: start, ended: end, people: [Present(_officer), Present(_master), Present(_cook)]));
        return (new DrillRestService(_s.VesselFactory, _vessel, rest), rest, drill);
    }

    [Fact]
    public async Task ADrillDuringRecordedRestIsOfferedForTheHours_NeverDoneSilently_AndAddingItIsDoneOnce()
    {
        var day = DateTime.UtcNow.Date.AddDays(-1);
        var (service, rest, drill) = await DrillOnADayOfWorkAsync(day.AddHours(10), day.AddHours(11), (_officer, "06:00-08:00"), (_cook, "08:00-12:00"));   // the master recorded nothing

        var offer = (await service.OfferAsync(_s.CompanyId, drill.Id, As(_master)))!;
        Assert.Equal([_officer.FullName], offer.Changes.Select(c => c.Name));                                // the cook was already working; the Master has no record
        Assert.Equal(("06:00-08:00", "06:00-08:00,10:00-11:00"), (offer.Changes[0].Before, offer.Changes[0].After));
        Assert.Equal("06:00-08:00", (await rest.HistoryAsync(_s.CompanyId, _officer.Id, day, day)).Single().WorkSpans);   // offering changed nothing

        var result = await service.ApplyAsync(_s.CompanyId, drill.Id, As(_master));
        Assert.Equal((1, 0), (result.Applied, result.Problems.Count));
        var entries = (await rest.HistoryAsync(_s.CompanyId, _officer.Id, day, day)).OrderBy(e => e.EnteredAt).ToList();
        Assert.Equal(["06:00-08:00", "06:00-08:00,10:00-11:00"], entries.Select(e => e.WorkSpans));          // both kept: nothing overwritten
        Assert.Contains(drill.Code, entries[1].Reason);
        Assert.True(entries[1].OnBehalf);                                                                    // the officer still agrees it when signing

        var again = (await service.OfferAsync(_s.CompanyId, drill.Id, As(_master)))!;
        Assert.Empty(again.Changes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(_s.CompanyId, drill.Id, As(_master)));
    }

    [Fact]
    public async Task AnOfficerWhoIsNotTheMasterCannotAddToOthersHours_AndTheRefusalIsReportedNotThrown()
    {
        var day = DateTime.UtcNow.Date.AddDays(-1);
        var (service, rest, drill) = await DrillOnADayOfWorkAsync(day.AddHours(10), day.AddHours(11), (_cook, "06:00-08:00"));
        var result = await service.ApplyAsync(_s.CompanyId, drill.Id, As(_officer));
        Assert.Equal(0, result.Applied);
        Assert.Contains("own hours", Assert.Single(result.Problems));
        Assert.Single(await rest.HistoryAsync(_s.CompanyId, _cook.Id, day, day));
    }

    [Fact]
    public async Task WithoutTheRestModuleOrAnEndTimeThereIsNothingToOffer()
    {
        var drill = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input());
        var service = new DrillRestService(_s.VesselFactory, _vessel, new RestHoursService(_s.VesselFactory));
        Assert.Null(await service.OfferAsync(_s.CompanyId, drill.Id, As(_master)));                           // rest hours are not switched on
        await SwitchOnAsync(_s.VesselId, PolicyModules.RestHours);
        var open = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(started: DateTime.UtcNow.AddHours(-9)) with { EndedAt = null });
        Assert.Null(await service.OfferAsync(_s.CompanyId, open.Id, As(_master)));                            // no end time, no duration
    }
}
