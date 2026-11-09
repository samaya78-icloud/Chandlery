using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Signing the hours and dealing with breaches, between a vessel and the office: who may sign, what a signature holds, the Master's lock, the worklist follow-up.</summary>
public class RestSignTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RestHoursService _shoreRest = null!, _vesselRest = null!;
    private RestReviewService _shore = null!, _vessel = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private const string Normal = "08:00-12:00,13:00-17:00";
    private const string TooLong = "05:00-12:00,13:00-20:15";           // 14 h 15 of work: 9 h 45 of rest on the day, 15 minutes short, and no other rule broken

    // The month before this one, so the tests read the same whatever day they run on. With the default rule set it has signing periods 1-7 and 8-14.
    private static readonly DateTime First = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    private static DateTime Day(int n) => First.AddDays(n - 1);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreRest = new RestHoursService(_s.ShoreFactory);
        _vesselRest = new RestHoursService(_s.VesselFactory);
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shore = new RestReviewService(_s.ShoreFactory, _shoreRest, _shoreWorklist);
        _vessel = new RestReviewService(_s.VesselFactory, _vesselRest, _vesselWorklist);
        await SwitchOnAsync(PolicyModules.RestHours);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(string module)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<CrewMember> SignOnAsync(string code, string rank = "Third Officer", bool master = false, bool admin = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, admin, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private async Task RecordAsync(CrewMember by, CrewMember crew, int from, int to, string spans = Normal)
    {
        for (var d = from; d <= to; d++) await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(by), crew.Id, Day(d), spans);
    }

    private Task<RestSignOff> SignAsync(CrewMember by, CrewMember crew, int from, int to, RestRole role) => _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(by), crew.Id, Day(from), Day(to), role);

    private async Task<RestPeriod> PeriodAsync(CrewMember crew, int from, bool office = false)
    {
        var month = await (office ? _shore : _vessel).MonthAsync(_s.CompanyId, _s.VesselId, crew.Id, First.Year, First.Month);
        return month.Periods.Single(p => p.From == Day(from));
    }

    /// <summary>Days 1 to 14 recorded for the seafarer by the seafarer, all ordinary except the 10th, which breaks the 24-hour rule by 15 minutes.</summary>
    private async Task<(CrewMember Crew, CrewMember Master)> BreachAsync()
    {
        var crew = await SignOnAsync("S5001");
        var master = await SignOnAsync("S5002", "Master", master: true);
        await RecordAsync(crew, crew, 1, 9);
        await RecordAsync(crew, crew, 10, 10, TooLong);
        await RecordAsync(crew, crew, 11, 14);
        await _s.SettleUntilQuietAsync();
        return (crew, master);
    }

    // ---- signing ----

    [Fact]
    public async Task AWeekIsSignedBySeafarerThenMaster_OnlyOnceEveryDayIsRecorded_AndAChangeAfterwardsTakesBothSignaturesBack()
    {
        var crew = await SignOnAsync("S1001");
        var master = await SignOnAsync("S1002", "Master", master: true);
        await RecordAsync(crew, crew, 1, 3);
        await RecordAsync(crew, crew, 5, 7);

        var gap = await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(crew, crew, 1, 7, RestRole.Seafarer));
        Assert.Contains("Record every day first", gap.Message);
        var period = await PeriodAsync(crew, 1);
        Assert.Equal([Day(4)], period.Gaps);

        await RecordAsync(crew, crew, 4, 4);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(crew, crew, 1, 6, RestRole.Seafarer));          // not a signing period of the month
        Assert.Contains("Only the seafarer", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(master, crew, 1, 7, RestRole.Seafarer))).Message);
        Assert.Contains("Only the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(crew, crew, 1, 7, RestRole.Master))).Message);
        Assert.Contains("has not signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(master, crew, 1, 7, RestRole.Master))).Message);        // the Master comes second

        var signed = await SignAsync(crew, crew, 1, 7, RestRole.Seafarer);
        Assert.Equal((crew.FullName, RestRole.Seafarer), (signed.SignedByName, signed.Role));
        Assert.Contains("already signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(crew, crew, 1, 7, RestRole.Seafarer))).Message);
        await SignAsync(master, crew, 1, 7, RestRole.Master);
        Assert.True((await PeriodAsync(crew, 1)).Complete);
        Assert.False((await PeriodAsync(crew, 8)).Complete);                                                               // the next week is its own period

        // The office is not a seafarer aboard: it reads the signatures and cannot give them.
        await _s.SettleUntilQuietAsync();
        var seen = await PeriodAsync(crew, 1, office: true);
        Assert.True(seen.Complete);
        Assert.Equal([crew.FullName, master.FullName], new[] { seen.Seafarer!.Sign.SignedByName, seen.Master!.Sign.SignedByName });
        Assert.Contains("onboard", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SignAsync(_s.CompanyId, _s.VesselId, _s.User, crew.Id, Day(1), Day(7), RestRole.Seafarer))).Message);

        // Once the Master has signed, only the Master may change a day in the week.
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(3), "08:00-12:00,13:00-18:00", "Forgot an hour"));
        Assert.Contains("signed by the Master", locked.Message);
        Assert.NotNull((await _vesselRest.WindowAsync(_s.CompanyId, _s.VesselId, crew.Id, Day(3))).MasterLock);
        Assert.Null((await _vesselRest.WindowAsync(_s.CompanyId, _s.VesselId, crew.Id, Day(8))).MasterLock);

        await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(3), "08:00-12:00,13:00-18:00", "Seafarer worked an extra hour");
        var after = await PeriodAsync(crew, 1);
        Assert.True(after.OutOfDate);
        Assert.False(after.SeafarerSigned || after.MasterSigned);
        Assert.Null((await _vesselRest.WindowAsync(_s.CompanyId, _s.VesselId, crew.Id, Day(3))).MasterLock);              // the signature no longer holds, so nothing is locked

        Assert.Contains("out of date", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(master, crew, 1, 7, RestRole.Master))).Message);
        await SignAsync(crew, crew, 1, 7, RestRole.Seafarer);
        await SignAsync(master, crew, 1, 7, RestRole.Master);
        Assert.True((await PeriodAsync(crew, 1)).Complete);

        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        db.CurrentCompanyId = _s.CompanyId;
        Assert.Equal(4, await db.RestSignOffs.CountAsync(x => x.CrewMemberId == crew.Id));                                 // the first two signatures are still there
    }

    [Fact]
    public async Task TheMasterSignsTheirOwnHoursAsSeafarerFirst_AndSignedWeeksAreCountedForTheHomePage()
    {
        var crew = await SignOnAsync("S2001");
        var master = await SignOnAsync("S2002", "Master", master: true);
        await RecordAsync(crew, crew, 1, 7);
        await RecordAsync(master, master, 1, 7);

        var before = (await _vessel.AttentionAsync(_s.CompanyId, As(crew)))!;
        Assert.Equal((false, 1), (before.IsMaster, before.ToSign));
        Assert.Equal(1, (await _vessel.AttentionAsync(_s.CompanyId, As(master)))!.ToSign);                                   // the Master has their own week to sign
        Assert.Null(await _shore.AttentionAsync(_s.CompanyId, _s.User));                                                      // the office is not aboard

        Assert.Contains("has not signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(master, master, 1, 7, RestRole.Master))).Message);      // not before signing as seafarer
        await SignAsync(master, master, 1, 7, RestRole.Seafarer);
        await SignAsync(master, master, 1, 7, RestRole.Master);
        await SignAsync(crew, crew, 1, 7, RestRole.Seafarer);

        var crewAfter = (await _vessel.AttentionAsync(_s.CompanyId, As(crew)))!;
        Assert.Equal(0, crewAfter.ToSign);
        var masterAfter = (await _vessel.AttentionAsync(_s.CompanyId, As(master)))!;
        Assert.Equal((0, 1), (masterAfter.ToSign, masterAfter.ToEndorse));                                                    // the crew member's week waits for the Master

        await SignAsync(master, crew, 1, 7, RestRole.Master);
        Assert.Equal(0, (await _vessel.AttentionAsync(_s.CompanyId, As(master)))!.ToEndorse);
    }

    [Fact]
    public async Task TheHomePageCountsTheDaysLeftOutSinceTheFirstRecord_AndNothingElseWhenNothingIsWaiting()
    {
        var crew = await SignOnAsync("S3001");
        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, today.AddDays(-3), Normal);
        await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, today.AddDays(-1), Normal);
        var attention = (await _vessel.AttentionAsync(_s.CompanyId, As(crew)))!;
        Assert.Equal(1, attention.MissingDays);                          // from the first record (3 days ago) to yesterday: only the day between is missing; the days before the first record are not owed
        Assert.Equal((0, 0, 0), (attention.MyUnexplained, attention.ToAcknowledge, attention.ToEndorse));
        Assert.True(attention.Any);
    }

    // ---- breaches ----

    [Fact]
    public async Task ABreachIsExplainedByTheSeafarer_AcknowledgedByTheMaster_AndTheWeekCanThenBeSigned()
    {
        var (crew, master) = await BreachAsync();
        var other = await SignOnAsync("S5003");
        var found = Assert.Single(await _vessel.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14)));
        Assert.Equal((crew.Id, RestRule.Rest24, Day(10), BreachStatus.Unexplained, 15), (found.CrewId, found.Finding.Rule, found.Finding.Date, found.Status, found.OwedMinutes));

        Assert.Contains("explained before", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24))).Message);
        Assert.Contains("Master or a crew admin", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(other), crew.Id, Day(10), RestRule.Rest24, "Not mine to explain"))).Message);
        Assert.Contains("Say what happened", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, "x"))).Message);

        var explained = await _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, "Cargo operation ran over at the berth");
        Assert.Null(explained.Task);                                                                                         // an explanation raises nothing
        Assert.Equal(BreachStatus.Explained, Assert.Single(await _vessel.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14))).Status);
        Assert.Contains("Only the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24))).Message);
        Assert.Contains("Only the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DeclareExceptionAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, RestExceptionKind.Emergency, "Because"))).Message);

        // The seafarer signs the week with the breach still open; the Master cannot until it has been dealt with.
        await SignAsync(crew, crew, 8, 14, RestRole.Seafarer);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(master, crew, 8, 14, RestRole.Master));
        Assert.Contains("still need an explanation", blocked.Message);

        var result = await _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24);
        Assert.Null(result.Task);
        Assert.Contains("not switched on", result.TaskProblem);                                                              // the worklist is off here: acknowledged all the same
        Assert.Equal(BreachStatus.Acknowledged, Assert.Single(await _vessel.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14))).Status);
        Assert.Contains("Already acknowledged", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24))).Message);
        await SignAsync(master, crew, 8, 14, RestRole.Master);

        // The office sees the same breach, as acknowledged, and cannot act on it.
        await _s.SettleUntilQuietAsync();
        var seen = Assert.Single(await _shore.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14)));
        Assert.Equal((BreachStatus.Acknowledged, 2), (seen.Status, seen.Notes.Count));
        Assert.Contains("onboard", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AcknowledgeAsync(_s.CompanyId, _s.VesselId, _s.User, crew.Id, Day(10), RestRule.Rest24))).Message);

        // Correcting the day so that it complies removes the breach; there is then nothing to explain or acknowledge.
        await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), "06:00-20:00", "Wrong end time");
        Assert.Empty(await _vessel.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14)));
        Assert.Contains("no breach", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, "Still explaining"))).Message);
        Assert.True((await PeriodAsync(crew, 8)).OutOfDate);
    }

    [Fact]
    public async Task AnAcknowledgedBreachRaisesOneWorklistTask_AnException_OwesCompensatoryRest_AndChangedDaysSendTheBreachBackToTheMaster()
    {
        await SwitchOnAsync(PolicyModules.Worklist);
        var (crew, master) = await BreachAsync();
        await _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, "Cargo operation ran over at the berth");

        var acknowledged = await _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24);
        var task = acknowledged.Task!;
        Assert.Null(acknowledged.TaskProblem);
        Assert.Equal(task.Id, acknowledged.Note.WorklistTaskId);
        Assert.Contains(crew.FullName, task.Title);
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.CurrentCompanyId = _s.CompanyId;
            Assert.Equal("Vessel", (await db.WorklistListItems.SingleAsync(i => i.Id == task.SourceId)).Name);
        }
        var detail = (await _vesselWorklist.GetAsync(_s.CompanyId, task.Id))!;
        var link = Assert.Single(WorklistService.LinksOf(detail.Entries));
        Assert.Equal((WorklistLinkTypes.RestBreach, acknowledged.Note.Id), (link.Type, link.Target));
        Assert.Equal($"/rest/breaches?note={acknowledged.Note.Id}", WorklistLinkTypes.Href(link.Type, link.Target, onVessel: true));

        // The office has the task, and the note it points at.
        await _s.SettleUntilQuietAsync();
        Assert.NotNull(await _shoreWorklist.GetAsync(_s.CompanyId, task.Id));
        Assert.Equal(crew.Id, (await _shore.NoteAsync(_s.CompanyId, acknowledged.Note.Id))!.CrewMemberId);

        // The day before the breach is revised: the acknowledgement no longer holds, and acknowledging again does not raise a second task.
        await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(9), "08:00-12:00,13:00-17:30", "Half an hour more");
        Assert.Equal(BreachStatus.Stale, Assert.Single(await _vessel.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14))).Status);
        var again = await _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24);
        Assert.Null(again.Task);
        Assert.Equal(task.Id, again.Note.WorklistTaskId);
        Assert.Single(await _vesselWorklist.ListAsync(_s.CompanyId, _s.VesselId));

        // The Master then declares it an exception: compensatory rest is owed until the Master confirms it was given.
        Assert.Contains("reason", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DeclareExceptionAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24, RestExceptionKind.Drill, ""))).Message);
        // The exception replaces the acknowledgement; it raises no second task either.
        var exception = await _vessel.DeclareExceptionAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24, RestExceptionKind.OperationalNeed, "Mooring in a sudden squall");
        Assert.Null(exception.Task);
        var excepted = Assert.Single(await _vessel.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14)));
        Assert.Equal((BreachStatus.Excepted, true, 15), (excepted.Status, excepted.CompensatoryOutstanding, excepted.OwedMinutes));
        Assert.Contains("Only the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ConfirmCompensatoryAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, "Given"))).Message);
        await _vessel.ConfirmCompensatoryAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24, "Given on the 11th: an extra 30 minutes of rest");
        Assert.False(Assert.Single(await _vessel.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14))).CompensatoryOutstanding);
        Assert.Contains("No compensatory rest is outstanding", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ConfirmCompensatoryAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24, "Again"))).Message);

        // Both sides end with the same notes.
        await _s.SettleUntilQuietAsync();
        var onShore = Assert.Single(await _shore.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14)));
        Assert.Equal(excepted.Notes.Count + 1, onShore.Notes.Count);
        Assert.Equal(BreachStatus.Excepted, onShore.Status);
    }
}
