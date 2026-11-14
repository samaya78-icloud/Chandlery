using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// Drills and meetings as one story between a real office database and a real vessel database: the office sets its intervals and a vessel's own rule, the vessel holds drills and a
/// meeting, the crew changes, the quarter-of-crew rule shows up, the office comments, and at the end both sides read the same schedule, participation and counts. The vessel must
/// hold nothing of another vessel's drills or rules, even after a full baseline.
/// </summary>
public class DrillWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private DrillService _shore = null!, _vessel = null!;
    private MeetingService _shoreMeetings = null!, _vesselMeetings = null!;
    private WorklistService _shoreWorklist = null!;
    private CrewMember _master = null!, _officer = null!, _cook = null!, _joiner = null!, _leaver = null!;
    private List<DrillType> _types = null!;
    private DrillType Fire => _types.Single(t => t.Name == "Fire drill");
    private DrillType Abandon => _types.Single(t => t.Name == "Abandon ship drill");

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        var vesselWorklist = new WorklistService(_s.VesselFactory);
        _shore = new DrillService(_s.ShoreFactory, _shoreWorklist);
        _vessel = new DrillService(_s.VesselFactory, vesselWorklist);
        _shoreMeetings = new MeetingService(_s.ShoreFactory, _shoreWorklist);
        _vesselMeetings = new MeetingService(_s.VesselFactory, vesselWorklist);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(Guid vesselId)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Drills, PolicyModules.Worklist }) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<CrewMember> SignOnAsync(string code, string rank, DateTime? signedOn, bool master = false, string authorities = "")
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, signedOn, true, false, master, Authorities: authorities), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static DrillParticipantInput Present(CrewMember c) => new(c.Id, "", "", DrillAttendance.Present);

    private DrillInput Input(DrillType type, DateTime started, params CrewMember[] present)
        => new(type.Id, started, started.AddHours(1), IncidentShipStatus.AtSea, "Muster stations", "A fire in the galley", null, "Fire pump", "", "", "", [.. present.Select(Present)]);

    /// <summary>The schedule as one string per row so the two sides can be compared: nothing about it may depend on which database it was worked out in.</summary>
    private static string Show(DrillSchedule s)
        => $"{s.VesselName}|{s.Worst}|quarter {s.Quarter.CrewCount}/{s.Quarter.NotTakenPart}/{s.Quarter.Triggered}|" + string.Join(";", s.Rows.Select(r =>
            $"{r.Type.Name}:{r.Rule.Count}{r.Rule.Kind}:{r.LastHeld:yyyy-MM-dd}:{r.Due:yyyy-MM-dd}:{r.Status}:[{string.Join(",", r.People.Select(p => $"{p.Name}={p.Status}"))}]"));

    private static string Show(DrillParticipation p)
        => string.Join("|", p.Crew.Select(c => $"{c.Name}:{string.Join(",", c.Standing.Select(x => $"{x.Type.Name}={x.Status}/{x.LastTookPart:yyyy-MM-dd}/{x.Due:yyyy-MM-dd}"))}"));

    [Fact]
    public async Task TheOfficeSetsTheRules_TheVesselDrillsAndMeets_TheCrewChanges_AndBothSidesEndWithTheSameScheduleParticipationAndCounts()
    {
        await SwitchOnAsync(_s.VesselId);
        _master = await SignOnAsync("W1001", "Master", DateTime.UtcNow.Date.AddMonths(-8), master: true);
        _officer = await SignOnAsync("W1002", "Chief Officer", DateTime.UtcNow.Date.AddMonths(-8), authorities: CrewAuthority.ConductDrills);
        _cook = await SignOnAsync("W1003", "Cook", DateTime.UtcNow.Date.AddMonths(-8));
        _leaver = await SignOnAsync("W1004", "Able Seaman", DateTime.UtcNow.Date.AddMonths(-8));
        _joiner = await SignOnAsync("W1005", "Ordinary Seaman", DateTime.UtcNow.Date.AddDays(-2));                  // joined this week: inside the 14-day grace, and more than the fire drill's 10-day warning from the end of it
        _types = await _shore.TypesAsync(_s.CompanyId);

        // The office changes its mind about the fire drill (every two months, a longer warning) and gives this vessel no abandon ship drill.
        var fire = Fire;
        fire.IntervalKind = DrillIntervalKind.CalendarMonths;
        fire.IntervalCount = 2;
        fire.WarningDays = 10;
        await _shore.SaveTypeAsync(_s.CompanyId, fire);
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.VesselId, Abandon.Id, applies: false, null, null, "No lifeboat: davit-launched liferafts only");
        await _s.SettleUntilQuietAsync();

        var onboard = await _vessel.ScheduleAsync(_s.CompanyId, _s.VesselId, As(_master));
        Assert.DoesNotContain(onboard.Rows, r => r.Type.Id == Abandon.Id);                                           // the vessel reads the office's rule
        Assert.Equal((2, DrillIntervalKind.CalendarMonths, 10), (onboard.Rows.Single(r => r.Type.Id == Fire.Id).Rule.Count, onboard.Rows.Single(r => r.Type.Id == Fire.Id).Rule.Kind, onboard.Rows.Single(r => r.Type.Id == Fire.Id).Rule.WarningDays));

        // Last month only the master took part in a fire drill: more than a quarter of the five crew missed it.
        var lastMonth = DrillRules.PreviousMonth(DateTime.UtcNow.Date).From.AddDays(9).AddHours(10);
        var first = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(Fire, lastMonth, _master));
        await _vessel.SignAsync(_s.CompanyId, first.Id, As(_master));
        await _s.SettleUntilQuietAsync();
        Assert.True((await _shore.ScheduleAsync(_s.CompanyId, _s.VesselId, _s.User)).Quarter.Triggered);

        // The crew changes: the leaver signs off, so they stop being counted and stop being late.
        await _s.ShoreCrew.SignOffAsync(_s.CompanyId, _leaver.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        var afterChange = await _shore.ScheduleAsync(_s.CompanyId, _s.VesselId, _s.User);
        Assert.Equal(4, afterChange.Quarter.CrewCount);
        Assert.DoesNotContain(afterChange.Rows.SelectMany(r => r.People), p => p.CrewId == _leaver.Id);
        Assert.DoesNotContain(afterChange.Rows.SelectMany(r => r.People), p => p.CrewId == _joiner.Id);            // inside the joiner's grace

        // This month everyone aboard takes part, an officer with the authority conducting.
        var today = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input(Fire, DateTime.UtcNow.AddHours(-3), _master, _officer, _cook, _joiner));
        await _vessel.SignAsync(_s.CompanyId, today.Id, As(_master));
        await _vessel.AddActionAsync(_s.CompanyId, today.Id, As(_master), new NewDrillAction("Re-test the fire pump", "Pressure was low."));
        await _s.SettleUntilQuietAsync();
        await _shore.CommentAsync(_s.CompanyId, today.Id, _s.User, "Please send the pump test record.");

        // A safety committee meeting with an action, then the next one shows the action still open.
        var committee = (await _shoreMeetings.TypesAsync(_s.CompanyId)).Single(t => t.Name == "Safety committee");
        var meeting = await _vesselMeetings.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), new MeetingInput(committee.Id, DateTime.UtcNow.AddHours(-2), "Officers' mess", null, "Name of W1002", "Galley rail to be fixed.",
            [new(_master.Id, "", "", DrillAttendance.Present), new(_officer.Id, "", "", DrillAttendance.Present)], [new(MeetingItemKind.Agenda, "Unsafe conditions", "The galley rail is loose.")]));
        await _vesselMeetings.SignAsync(_s.CompanyId, meeting.Id, As(_master));
        await _vesselMeetings.AddActionAsync(_s.CompanyId, meeting.Id, As(_master), new NewMeetingAction("Fix the galley rail", "Before the next meeting."));
        await _s.SettleUntilQuietAsync();
        Assert.Single(await _vesselMeetings.OpenActionsAsync(_s.CompanyId, _s.VesselId, committee.Id, As(_master)));

        // Both sides read the same thing.
        var day = DateTime.UtcNow.Date;
        var ashoreSchedule = await _shore.ScheduleAsync(_s.CompanyId, _s.VesselId, _s.User, day);
        var onboardSchedule = await _vessel.ScheduleAsync(_s.CompanyId, _s.VesselId, As(_cook), day);
        Assert.Equal(Show(ashoreSchedule), Show(onboardSchedule));
        Assert.Equal(Show(await _shore.ParticipationAsync(_s.CompanyId, _s.VesselId, _s.User, day)), Show(await _vessel.ParticipationAsync(_s.CompanyId, _s.VesselId, As(_master), day)));
        Assert.Equal(Show(ashoreSchedule), Show(await _vessel.ScheduleAsync(_s.CompanyId, _s.VesselId, As(_master), day)));

        // The fire drill is now fine for everyone, so only the types never held show up.
        var fireRow = ashoreSchedule.Rows.Single(r => r.Type.Id == Fire.Id);
        Assert.Equal((DrillStatus.NotDue, 0), (fireRow.Status, fireRow.People.Count));
        var drillDetailAshore = (await _shore.GetAsync(_s.CompanyId, today.Id, _s.User))!;
        var drillDetailOnboard = (await _vessel.GetAsync(_s.CompanyId, today.Id, As(_cook)))!;
        Assert.Equal((1, 1, true), (drillDetailAshore.Actions.Count, drillDetailAshore.Comments.Count, drillDetailAshore.Drill.IsSigned));
        Assert.Equal((drillDetailAshore.Actions.Count, drillDetailAshore.Comments.Count, drillDetailAshore.Drill.Participants.Count), (drillDetailOnboard.Actions.Count, drillDetailOnboard.Comments.Count, drillDetailOnboard.Drill.Participants.Count));

        // The home page counts and the fleet table agree with the schedule.
        var fleet = new DrillFleetService(_shore, _shoreMeetings);
        var counts = (await fleet.CountsAsync(_s.CompanyId, _s.User))!;
        Assert.Equal((ashoreSchedule.Rows.Count(r => r.Status == DrillStatus.Overdue), ashoreSchedule.Rows.Count(r => r.ShipStatus == DrillStatus.NeverHeld), ashoreSchedule.Quarter.Triggered ? 1 : 0),
            (counts.Overdue, counts.NeverHeld, counts.QuarterVessels));
        var view = await fleet.FleetAsync(_s.CompanyId, _s.User, day);
        Assert.Equal(ashoreSchedule.Rows.Count, view.Vessels.Single().Cells.Count);
        Assert.Equal(view.Overdue, counts.Overdue);
    }

    [Fact]
    public async Task TheVesselHoldsOnlyItsOwnDrillsAndRules_NotEvenAfterAFullBaseline_WhileTheOfficeKeepsBothApart()
    {
        await SwitchOnAsync(_s.VesselId);
        await SwitchOnAsync(_s.OtherVesselId);
        _master = await SignOnAsync("X1001", "Master", DateTime.UtcNow.Date.AddMonths(-8), master: true);
        _types = await _shore.TypesAsync(_s.CompanyId);
        var ours = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(Fire, DateTime.UtcNow.AddHours(-3), _master));

        // The other vessel has a rule of its own and a drill of its own (written straight into the office database, as the other vessel's sync would).
        await _shore.SaveOverrideAsync(_s.CompanyId, _s.OtherVesselId, Fire.Id, true, DrillIntervalKind.Days, 20, "Flag state rule");
        var theirs = new Drill
        {
            Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, Code = "OTHER-DR-1", DrillTypeId = Fire.Id, TypeName = Fire.Name, Category = Fire.Category,
            StartedAt = DateTime.UtcNow.AddHours(-5), EndedAt = DateTime.UtcNow.AddHours(-4), CreatedAt = DateTime.UtcNow, CreatedByName = "Other master",
            Participants = [new DrillParticipant { Id = Guid.CreateVersion7(), Name = "Someone else", Attendance = DrillAttendance.Present }],
        };
        await using (var shore = await _s.ShoreFactory.CreateDbContextAsync())
        {
            shore.Drills.Add(theirs);
            await shore.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        await using var vessel = await _s.VesselFactory.CreateDbContextAsync();
        Assert.Equal([ours.Id], await vessel.Drills.IgnoreQueryFilters().Select(d => d.Id).ToListAsync());
        Assert.Empty(await vessel.DrillParticipants.IgnoreQueryFilters().Where(p => p.DrillId == theirs.Id).ToListAsync());
        Assert.Empty(await vessel.DrillOverrides.IgnoreQueryFilters().Where(o => o.VesselId == _s.OtherVesselId).ToListAsync());

        // The office has both, apart, and the other vessel's rule is its own.
        var all = await _shore.ListAsync(_s.CompanyId, _s.User);
        Assert.Equal(new[] { _s.VesselId, _s.OtherVesselId }.Order(), all.Select(r => r.Drill.VesselId).Order());
        var theirRule = (await _shore.ScheduleAsync(_s.CompanyId, _s.OtherVesselId, _s.User)).Rows.Single(r => r.Type.Id == Fire.Id).Rule;
        var ourRule = (await _shore.ScheduleAsync(_s.CompanyId, _s.VesselId, _s.User)).Rows.Single(r => r.Type.Id == Fire.Id).Rule;
        Assert.Equal((20, DrillIntervalKind.Days), (theirRule.Count, theirRule.Kind));
        Assert.Equal((1, DrillIntervalKind.CalendarMonths), (ourRule.Count, ourRule.Kind));
        Assert.Single(await _vessel.ListAsync(_s.CompanyId, As(_master)));
    }
}
