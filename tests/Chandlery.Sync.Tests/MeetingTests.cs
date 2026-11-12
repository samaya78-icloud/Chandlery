using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The meeting schedule worked out from the meetings held (no database).</summary>
public class MeetingScheduleTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static MeetingType Type(string name, bool scheduled, int count = 1, DrillIntervalKind kind = DrillIntervalKind.CalendarMonths, int warning = 7)
        => new() { Id = Guid.NewGuid(), Name = name, Scheduled = scheduled, IntervalKind = kind, IntervalCount = count, WarningDays = warning, IsActive = true };

    [Fact]
    public void AScheduledMeetingFallsDueAtTheEndOfTheNextMonth_AnUnscheduledOneNever()
    {
        var committee = Type("Safety committee", true);
        var toolbox = Type("Toolbox talk", false);
        var held = new Dictionary<Guid, DateTime> { [committee.Id] = new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc), [toolbox.Id] = D(2026, 10, 20) };
        var rows = MeetingSchedules.Build([committee, toolbox], held, D(2026, 11, 12));

        Assert.Equal((D(2026, 10, 5), D(2026, 11, 30), DrillStatus.NotDue), (rows[0].LastHeld, rows[0].Due, rows[0].Status));
        Assert.Equal((D(2026, 10, 20), null, DrillStatus.NotDue), (rows[1].LastHeld, rows[1].Due, rows[1].Status));
        Assert.Null(rows[1].Rule);
    }

    [Theory]
    [InlineData(2026, 11, 22, DrillStatus.NotDue)]
    [InlineData(2026, 11, 23, DrillStatus.DueSoon)]
    [InlineData(2026, 11, 30, DrillStatus.Due)]
    [InlineData(2026, 12, 1, DrillStatus.Overdue)]
    public void AScheduledMeetingFollowsTheSameStatusesAsADrill(int y, int m, int d, DrillStatus expected)
    {
        var committee = Type("Safety committee", true);
        var rows = MeetingSchedules.Build([committee], new Dictionary<Guid, DateTime> { [committee.Id] = D(2026, 10, 5) }, D(y, m, d));
        Assert.Equal(expected, rows[0].Status);
    }

    [Fact]
    public void AScheduledMeetingNeverHeldIsNeverHeld_AndAnUnscheduledOneIsNotNagged()
    {
        var committee = Type("Safety committee", true);
        var toolbox = Type("Toolbox talk", false);
        var rows = MeetingSchedules.Build([committee, toolbox], new Dictionary<Guid, DateTime>(), D(2026, 11, 12));
        Assert.Equal((DrillStatus.NeverHeld, null), (rows[0].Status, rows[0].Due));
        Assert.Equal((DrillStatus.NotDue, null), (rows[1].Status, rows[1].LastHeld));
    }

    [Fact]
    public void AnIntervalInDaysCountsFromTheDayOfTheLastMeeting()
    {
        var weekly = Type("Weekly", true, 7, DrillIntervalKind.Days, 1);
        var rows = MeetingSchedules.Build([weekly], new Dictionary<Guid, DateTime> { [weekly.Id] = new DateTime(2026, 11, 5, 23, 0, 0, DateTimeKind.Utc) }, D(2026, 11, 11));
        Assert.Equal((D(2026, 11, 12), DrillStatus.DueSoon), (rows[0].Due, rows[0].Status));
    }
}

/// <summary>Safety meetings between a vessel and the office: who records and signs, what a meeting holds, the actions carried to the next meeting, and the types the office sets.</summary>
public class MeetingTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private MeetingService _shore = null!, _vessel = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private CrewMember _master = null!, _officer = null!, _cook = null!;
    private List<MeetingType> _types = null!;
    private MeetingType Committee => _types.Single(t => t.Name == "Safety committee");
    private MeetingType Toolbox => _types.Single(t => t.Name == "Toolbox talk");

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shore = new MeetingService(_s.ShoreFactory, _shoreWorklist);
        _vessel = new MeetingService(_s.VesselFactory, _vesselWorklist);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Drills, PolicyModules.Worklist })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        _master = await SignOnAsync("M1001", "Master", master: true);
        _officer = await SignOnAsync("M1002", "Chief Officer", authorities: CrewAuthority.ConductDrills);
        _cook = await SignOnAsync("M1003", "Cook");
        _types = await _shore.TypesAsync(_s.CompanyId);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false, string authorities = "")
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, master, Authorities: authorities), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static MeetingAttendeeInput Present(CrewMember c) => new(c.Id, "", "", DrillAttendance.Present);

    private MeetingInput Input(MeetingType? type = null, DateTime? held = null, IReadOnlyList<MeetingAttendeeInput>? attendees = null, IReadOnlyList<MeetingItemInput>? items = null, Guid? chair = null)
        => new((type ?? Committee).Id, held ?? DateTime.UtcNow.AddHours(-2), "Officers' mess", chair, "Name of M1002", "Actions agreed on the galley rail and the fire pump.",
            attendees ?? [Present(_master), Present(_officer), Present(_cook)],
            items ?? [new(MeetingItemKind.Agenda, "Actions from the last meeting", "None open."), new(MeetingItemKind.Agenda, "Unsafe conditions", "The galley rail is loose."), new(MeetingItemKind.Suggestion, "A hook for the oilskins", "Raised in the mess.", "Name of M1003")]);

    private static string Line(MeetingDetail d)
        => $"{d.Meeting.Code}|{d.Meeting.TypeName}|signed {d.Meeting.IsSigned}:{d.Meeting.SignedByName}|{d.Meeting.ChairName}|att {string.Join(",", d.Meeting.Attendees.OrderBy(a => a.Name).Select(a => $"{a.Name}:{a.Attendance}"))}|"
           + $"items {string.Join(",", d.Meeting.Items.OrderBy(i => i.Position).Select(i => $"{i.Position}:{i.Kind}:{i.Title}"))}|notes {d.Meeting.Notes.Count}|comments {d.Comments.Count}|actions {d.Actions.Count}|carried {d.Carried.Count}";

    [Fact]
    public async Task TheMasterAndAnAuthorisedOfficerRecordAMeeting_TheMasterSignsTheMinutes_AndBothSidesHoldTheSame()
    {
        Assert.Contains("authority", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_cook), Input()))).Message);
        Assert.Contains("onboard", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, _s.VesselId, _s.User, Input()))).Message);

        var meeting = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_officer), Input());
        Assert.Contains("-MT-", meeting.Code);
        Assert.Equal((_officer.FullName, 3, 3), (meeting.ChairName, meeting.Attendees.Count, meeting.Items.Count));

        await _vessel.UpdateAsync(_s.CompanyId, meeting.Id, As(_officer), Input(items: [new(MeetingItemKind.Agenda, "Actions from the last meeting", "None open."), new(MeetingItemKind.Agenda, "Unsafe conditions", "The galley rail is loose and the guard is missing.")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.UpdateAsync(_s.CompanyId, meeting.Id, As(_cook), Input()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignAsync(_s.CompanyId, meeting.Id, As(_officer)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, meeting.Id, As(_officer), "Unsigned"));
        await _vessel.AddActionAsync(_s.CompanyId, meeting.Id, As(_officer), new NewMeetingAction("Fit the galley rail guard", "Before the next meal service.", DateTime.UtcNow.AddDays(3)));
        await _vessel.SignAsync(_s.CompanyId, meeting.Id, As(_master));
        Assert.Contains("signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.UpdateAsync(_s.CompanyId, meeting.Id, As(_master), Input()))).Message);
        Assert.Contains("already", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignAsync(_s.CompanyId, meeting.Id, As(_master)))).Message);
        await _vessel.AddNoteAsync(_s.CompanyId, meeting.Id, As(_officer), "Rail guard fitted and checked.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, meeting.Id, As(_cook), "Not mine"));
        await _s.SettleUntilQuietAsync();

        await _shore.CommentAsync(_s.CompanyId, meeting.Id, _s.User, "Thanks: please send the rail repair photo.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CommentAsync(_s.CompanyId, meeting.Id, As(_master), "Onboard it is a note"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SignAsync(_s.CompanyId, meeting.Id, _s.User));
        await _s.SettleUntilQuietAsync();

        var ashore = (await _shore.GetAsync(_s.CompanyId, meeting.Id, _s.User))!;
        var onboard = (await _vessel.GetAsync(_s.CompanyId, meeting.Id, As(_cook)))!;
        Assert.Equal(Line(ashore), Line(onboard));
        Assert.Equal((true, 1, 1, 1, 2), (ashore.Meeting.IsSigned, ashore.Meeting.Notes.Count, ashore.Comments.Count, ashore.Actions.Count, ashore.Meeting.Items.Count));
        Assert.True(ashore.Can.IsOffice && ashore.Can.Comment && ashore.Can.AddAction && !ashore.Can.Edit && !ashore.Can.Sign);
        var master = (await _vessel.GetAsync(_s.CompanyId, meeting.Id, As(_master)))!.Can;
        Assert.True(master.Note && master.AddAction && !master.Edit && !master.Sign && !master.Comment);
    }

    [Fact]
    public async Task AMeetingIsRefusedWhenItsPeopleItemsOrTimeAreWrong()
    {
        async Task<string> Refused(MeetingInput input, CrewMember? by = null) => (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(by ?? _officer), input))).Message;

        Assert.Contains("kind of meeting", await Refused(Input() with { MeetingTypeId = Guid.NewGuid() }));
        Assert.Contains("future", await Refused(Input(held: DateTime.UtcNow.AddDays(3))));
        Assert.Contains("at least one person", await Refused(Input(attendees: [new MeetingAttendeeInput(_cook.Id, "", "", DrillAttendance.Absent, DrillAbsence.Sick)])));
        Assert.Contains("twice", await Refused(Input(attendees: [Present(_cook), Present(_cook)])));
        Assert.Contains("not one of the vessel's crew", await Refused(Input(attendees: [Present(_cook), new MeetingAttendeeInput(Guid.NewGuid(), "", "", DrillAttendance.Present)])));
        Assert.Contains("observer", await Refused(Input(attendees: [Present(_cook), new MeetingAttendeeInput(null, "A visitor", "", DrillAttendance.Present)])));
        Assert.Contains("at least one item", await Refused(Input(items: [])));
        Assert.Contains("title", await Refused(Input(items: [new(MeetingItemKind.Agenda, " ")])));
        Assert.Contains("Master names", await Refused(Input(chair: _master.Id), by: _officer));
        var named = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(chair: _officer.Id));
        Assert.Equal(_officer.FullName, named.ChairName);
    }

    [Fact]
    public async Task AnOpenActionIsPutInFrontOfTheNextMeetingOfTheSameKind_UntilItIsDone()
    {
        var first = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(held: DateTime.UtcNow.AddDays(-30)));
        var task = await _vessel.AddActionAsync(_s.CompanyId, first.Id, As(_master), new NewMeetingAction("Fix the mess door closer", "", DateTime.UtcNow.AddDays(10)));
        var closed = await _vessel.AddActionAsync(_s.CompanyId, first.Id, As(_master), new NewMeetingAction("Order the new notice board", ""));
        await _vesselWorklist.AddEntryAsync(_s.CompanyId, closed.Id, new WorklistEntryInput(WorklistEntryType.Status, Status: WorklistStatus.Cancelled, Text: "Not needed"), As(_master));
        await _s.SettleUntilQuietAsync();

        var carried = await _vessel.OpenActionsAsync(_s.CompanyId, _s.VesselId, Committee.Id, As(_officer));
        var one = Assert.Single(carried);
        Assert.Equal((task.Id, first.Id), (one.Task.Id, one.FromMeetingId));

        // Another kind of meeting does not carry them.
        Assert.Empty(await _vessel.OpenActionsAsync(_s.CompanyId, _s.VesselId, Toolbox.Id, As(_officer)));

        // The next meeting shows what was open when it was held, and the earlier one shows none.
        var second = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(held: DateTime.UtcNow.AddHours(-3)));
        await _s.SettleUntilQuietAsync();
        Assert.Single((await _shore.GetAsync(_s.CompanyId, second.Id, _s.User))!.Carried);
        Assert.Empty((await _vessel.GetAsync(_s.CompanyId, first.Id, As(_master)))!.Carried);

        await _vesselWorklist.AddEntryAsync(_s.CompanyId, task.Id, new WorklistEntryInput(WorklistEntryType.Status, Status: WorklistStatus.Cancelled, Text: "Done"), As(_master));
        Assert.Empty(await _vessel.OpenActionsAsync(_s.CompanyId, _s.VesselId, Committee.Id, As(_officer)));
    }

    [Fact]
    public async Task TheScheduleFollowsTheMeetingsHeld_OnBothSides()
    {
        var before = await _vessel.ScheduleAsync(_s.CompanyId, _s.VesselId, As(_cook));
        Assert.Equal(DrillStatus.NeverHeld, before.Single(r => r.Type.Id == Committee.Id).Status);
        Assert.Equal(DrillStatus.NotDue, before.Single(r => r.Type.Id == Toolbox.Id).Status);

        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(held: DateTime.UtcNow.AddDays(-3)));
        await _s.SettleUntilQuietAsync();
        var onboard = await _vessel.ScheduleAsync(_s.CompanyId, _s.VesselId, As(_cook));
        var ashore = await _shore.ScheduleAsync(_s.CompanyId, _s.VesselId, _s.User);
        string Show(List<MeetingScheduleRow> rows) => string.Join("|", rows.Select(r => $"{r.Type.Name}:{r.LastHeld:yyyy-MM-dd}:{r.Due:yyyy-MM-dd}:{r.Status}"));
        Assert.Equal(Show(ashore), Show(onboard));
        var row = onboard.Single(r => r.Type.Id == Committee.Id);
        Assert.Equal(DateTime.UtcNow.AddDays(-3).Date, row.LastHeld);
        Assert.NotEqual(DrillStatus.NeverHeld, row.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ScheduleAsync(_s.CompanyId, _s.OtherVesselId, As(_cook)));
    }

    [Fact]
    public async Task ChangesToTheAttendeesAndItemsMadeAfterTheMeetingHasAlreadyTravelled_TravelToo()
    {
        var meeting = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input(attendees: [Present(_master), Present(_cook)]));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, (await _shore.GetAsync(_s.CompanyId, meeting.Id, _s.User))!.Meeting.Attendees.Count);

        await _vessel.UpdateAsync(_s.CompanyId, meeting.Id, As(_master), Input(attendees: [Present(_master), Present(_cook), Present(_officer)], items: [new(MeetingItemKind.Agenda, "One item only", "")]));
        await _s.SettleUntilQuietAsync();
        var seen = (await _shore.GetAsync(_s.CompanyId, meeting.Id, _s.User))!.Meeting;
        Assert.Equal((3, 1), (seen.Attendees.Count, seen.Items.Count));

        await _vessel.SignAsync(_s.CompanyId, meeting.Id, As(_master));
        await _s.SettleUntilQuietAsync();
        await _vessel.AddNoteAsync(_s.CompanyId, meeting.Id, As(_master), "A late addition to the minutes.");
        await _s.SettleUntilQuietAsync();
        Assert.Equal((true, 1), ((await _shore.GetAsync(_s.CompanyId, meeting.Id, _s.User))!.Meeting.IsSigned, (await _shore.GetAsync(_s.CompanyId, meeting.Id, _s.User))!.Meeting.Notes.Count));
    }

    [Fact]
    public async Task TheOfficeKeepsTheMeetingTypes_AndAVesselNeverHoldsAnotherVesselsMeetings()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveTypeAsync(_s.CompanyId, new MeetingType { Name = "Own meeting" }));
        Assert.Contains("name", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new MeetingType { Name = " " }))).Message);
        Assert.Contains("already", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new MeetingType { Name = "Safety committee" }))).Message);
        Assert.Contains("1 to 24 months", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveTypeAsync(_s.CompanyId, new MeetingType { Name = "Quarterly review", Scheduled = true, IntervalCount = 30 }))).Message);
        var added = await _shore.SaveTypeAsync(_s.CompanyId, new MeetingType { Name = "Officers' meeting", Scheduled = true, IntervalKind = DrillIntervalKind.Days, IntervalCount = 14, WarningDays = 3, StandardAgenda = "Plan for the fortnight", IsActive = true });
        await _s.SettleUntilQuietAsync();
        Assert.Contains(await _vessel.TypesAsync(_s.CompanyId), t => t.Id == added.Id && t.IntervalCount == 14);

        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, Module = PolicyModules.Drills, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        var theirs = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "M9001", "Other Master", "Master", "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.OtherVesselId, null, true, false, true), _s.User);
        await _s.SettleUntilQuietAsync();
        var ours = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(_master), Input());
        await _s.SettleUntilQuietAsync();
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        await using var vessel = await _s.VesselFactory.CreateDbContextAsync();
        Assert.Equal([ours.Id], await vessel.Meetings.IgnoreQueryFilters().Select(m => m.Id).ToListAsync());
        Assert.Single(await _shore.ListAsync(_s.CompanyId, _s.User));
        Assert.Empty(await _shore.ListAsync(_s.CompanyId, _s.User, _s.OtherVesselId));
        Assert.NotEqual(Guid.Empty, theirs.Id);
    }

    [Fact]
    public async Task ANewCompanyStartsWithTheCommonMeetingTypes_TheCommitteeMonthly()
    {
        Assert.Equal(5, _types.Count);
        Assert.Equal((true, DrillIntervalKind.CalendarMonths, 1), (Committee.Scheduled, Committee.IntervalKind, Committee.IntervalCount));
        Assert.Contains("Actions from the last meeting", Committee.StandardAgenda);
        Assert.False(Toolbox.Scheduled);
        Assert.Equal(6, _types.Single(t => t.Name == "Management review onboard").IntervalCount);
        await Task.CompletedTask;
    }
}
