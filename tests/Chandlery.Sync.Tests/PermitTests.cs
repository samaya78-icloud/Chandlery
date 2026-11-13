using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Permit to work between a vessel and the office: request, checklist, gas, issue, entries, extension, closing, expiry, the types the office keeps, and both sides ending with the same permit.</summary>
public class PermitTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PermitService _shore = null!, _vessel = null!;
    private WorklistService _shoreWorklist = null!;
    private CrewMember _master = null!, _chief = null!, _second = null!, _ab1 = null!, _ab2 = null!, _bosun = null!;
    private DateTime _now = DateTime.UtcNow;
    private PermitType Enclosed => _types.Single(t => t.Name == "Enclosed space entry");
    private PermitType Hot => _types.Single(t => t.Name == "Hot work");
    private PermitType Cold => _types.Single(t => t.Name == "Cold work in a hazardous area");
    private List<PermitType> _types = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _shore = new PermitService(_s.ShoreFactory, _shoreWorklist) { Clock = () => _now };
        _vessel = new PermitService(_s.VesselFactory, new WorklistService(_s.VesselFactory)) { Clock = () => _now };
        await SwitchOnAsync(_s.VesselId, PolicyModules.PermitToWork, PolicyModules.Worklist);
        _master = await SignOnAsync("R2001", "Master", master: true);
        _chief = await SignOnAsync("R2002", "Chief Officer", authorities: CrewAuthority.IssuePermits);
        _second = await SignOnAsync("R2003", "Second Officer");
        _ab1 = await SignOnAsync("R2004", "Able Seaman");
        _ab2 = await SignOnAsync("R2005", "Able Seaman");
        _bosun = await SignOnAsync("R2006", "Bosun");
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

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false, string authorities = "")
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, master, Authorities: authorities), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    /// <summary>Edits a type as the office would and lets the vessel receive it. The risk assessment and the Master's approval are switched off where a test is about something else.</summary>
    private async Task RelaxAsync(PermitType type, bool risk = false, bool master = false)
    {
        type.RiskAssessmentRequired = risk;
        type.MasterApprovalRequired = master;
        await _shore.SaveTypeAsync(_s.CompanyId, type);
        await _s.SettleUntilQuietAsync();
    }

    private PermitInput Input(PermitType type, Guid[]? people = null, Guid? attendant = null, DateTime? from = null, DateTime? until = null, string location = "Cargo hold 2")
        => new(type.Id, location, "Inspect the bilge suction", from ?? _now.AddMinutes(-10), until ?? _now.AddHours(4), people ?? [_ab1.Id, _ab2.Id], attendant ?? _bosun.Id);

    private static PermitGasInput Gas(decimal o2 = 20.9m, decimal lel = 0m) => new("Gas detector GD-1", new DateTime(2026, 10, 1), "Hold 2 bottom", o2, lel);

    private async Task AnswerAllAsync(Permit permit, CrewMember by)
    {
        var detail = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(by)))!;
        await _vessel.AnswerAsync(_s.CompanyId, permit.Id, As(by), detail.Permit.Answers.Select(a => new PermitAnswerInput(a.Id, PermitAnswerKind.Yes, a.NoteRequired ? "Checked" : "")).ToList());
    }

    private async Task<string> RefusedAsync(Func<Task> act) => (await Assert.ThrowsAsync<InvalidOperationException>(act)).Message;

    private static string Line(PermitDetail d)
        => $"{d.Permit.Code}|{d.Permit.TypeName} v{d.Permit.ChecklistVersion}|{d.State}|valid {d.Validity.Valid}:{d.Validity.Reason}|req {d.Permit.RequesterName}|iss {d.Permit.IssuerName}|att {d.Permit.AttendantName}|"
           + $"answers {d.Permit.Answers.Count(a => a.Answer == PermitAnswerKind.Yes)}/{d.Permit.Answers.Count}|gas {d.Permit.Readings.Count}:{d.Gas}|events {string.Join(",", d.Permit.Events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => e.Kind))}|"
           + $"entries {string.Join(",", d.Permit.Entries.OrderBy(e => e.PersonName).Select(e => $"{e.PersonName}:{e.Out is null}"))}|until {d.Until:O}|comments {d.Comments.Count}|actions {d.Actions.Count}";

    // ---- the whole life of an enclosed space permit ----

    [Fact]
    public async Task AnEnclosedSpacePermit_IsRequested_Checked_Issued_Entered_Extended_AndClosed_AndTheOfficeSeesTheSameThing()
    {
        await RelaxAsync(Enclosed, risk: false, master: true);
        Assert.Contains("officer", await RefusedAsync(() => _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_ab1), Input(Enclosed))));
        Assert.Contains("onboard", await RefusedAsync(() => _shore.RequestAsync(_s.CompanyId, _s.VesselId, _s.User, Input(Enclosed))));

        var permit = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Enclosed));
        Assert.Contains("-PTW-ENC-", permit.Code);
        Assert.Equal(11, permit.Answers.Count);

        // Everything that stops it is listed: the checklist, the reading, the Master. The issuer must be authorised and not the requester.
        var check = await _vessel.CheckIssueAsync(_s.CompanyId, permit.Id, As(_chief));
        Assert.Equal(["Not answered: ", "A gas reading is needed", "The Master has not approved it"], check.Missing.Select(m => m.StartsWith("Not answered") ? "Not answered: " : m).Distinct());
        Assert.Contains((await _vessel.CheckIssueAsync(_s.CompanyId, permit.Id, As(_second))).Missing, m => m.Contains("not authorised"));
        Assert.Contains("cannot be issued yet", await RefusedAsync(() => _vessel.IssueAsync(_s.CompanyId, permit.Id, As(_chief))));

        await AnswerAllAsync(permit, _second);

        // A reading outside the limits is kept, and does not let the permit through.
        var bad = await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_second), Gas(o2: 19.0m, lel: 3m));
        Assert.False(bad.WithinLimits);
        Assert.Contains("Oxygen", bad.Failures);
        Assert.Contains("outside the limits", await RefusedAsync(() => _vessel.IssueAsync(_s.CompanyId, permit.Id, As(_master))));
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_second), Gas());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ApproveAsync(_s.CompanyId, permit.Id, As(_chief)));          // only the Master approves
        await _vessel.ApproveAsync(_s.CompanyId, permit.Id, As(_master));
        Assert.Contains("Only the Master", await RefusedAsync(() => _vessel.ApproveAsync(_s.CompanyId, permit.Id, As(_second))));
        var issued = await _vessel.IssueAsync(_s.CompanyId, permit.Id, As(_chief), conditions: "Ventilate for 30 minutes first.");
        Assert.Equal(_chief.FullName, issued.IssuerName);
        Assert.Contains("issued", await RefusedAsync(() => _vessel.IssueAsync(_s.CompanyId, permit.Id, As(_chief))));
        Assert.Contains("closed once", await RefusedAsync(() => _vessel.AnswerAsync(_s.CompanyId, permit.Id, As(_second), [])));

        // Starting and the entry log.
        await _vessel.StartAsync(_s.CompanyId, permit.Id, As(_second));
        var entry = await _vessel.EnterAsync(_s.CompanyId, permit.Id, As(_bosun), _ab1.Id);
        Assert.Contains("stays outside", await RefusedAsync(() => _vessel.EnterAsync(_s.CompanyId, permit.Id, As(_bosun), _bosun.Id)));
        Assert.Contains("already inside", await RefusedAsync(() => _vessel.EnterAsync(_s.CompanyId, permit.Id, As(_bosun), _ab1.Id)));
        Assert.Contains("not named", await RefusedAsync(() => _vessel.EnterAsync(_s.CompanyId, permit.Id, As(_bosun), _chief.Id)));
        Assert.Contains("still inside", await RefusedAsync(() => _vessel.FinishWorkAsync(_s.CompanyId, permit.Id, As(_second))));
        Assert.Contains("still inside", await RefusedAsync(() => _vessel.CancelAsync(_s.CompanyId, permit.Id, As(_master), "Stop")));

        // Gas goes out of date after the interval: the permit is not valid and nobody else may go in.
        _now = _now.AddMinutes(31);
        var detail = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!;
        Assert.False(detail.Validity.Valid);
        Assert.Equal(PermitGasStatus.Overdue, detail.Gas);
        Assert.Contains("overdue", await RefusedAsync(() => _vessel.EnterAsync(_s.CompanyId, permit.Id, As(_bosun), _ab2.Id)));
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_bosun), Gas());
        await _vessel.EnterAsync(_s.CompanyId, permit.Id, As(_bosun), _ab2.Id);

        // A reading outside the limits while people are inside suspends the permit and raises a task; it is resumed only with a clean reading by an issuer.
        _now = _now.AddMinutes(10);
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_bosun), Gas(lel: 6m));
        var suspended = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!;
        Assert.Equal(PermitState.Suspended, suspended.State);
        Assert.Contains(suspended.Actions, a => a.Title.Contains("Gas reading outside limits"));
        await _vessel.LeaveAsync(_s.CompanyId, permit.Id, As(_bosun), entry.Id);                                                   // leaving is always allowed
        Assert.Contains("gas reading", await RefusedAsync(() => _vessel.ResumeAsync(_s.CompanyId, permit.Id, As(_chief))));
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_bosun), Gas());
        Assert.Contains("issuer", await RefusedAsync(() => _vessel.ResumeAsync(_s.CompanyId, permit.Id, As(_second))));
        await _vessel.ResumeAsync(_s.CompanyId, permit.Id, As(_chief));

        // Extension needs a new end, a fresh reading, and an issuer.
        Assert.Contains("after the current end", await RefusedAsync(() => _vessel.ExtendAsync(_s.CompanyId, permit.Id, As(_chief), _now.AddHours(1))));
        await _vessel.ExtendAsync(_s.CompanyId, permit.Id, As(_chief), _now.AddHours(6));
        _now = _now.AddMinutes(40);
        Assert.Contains("out of date", await RefusedAsync(() => _vessel.ExtendAsync(_s.CompanyId, permit.Id, As(_chief), _now.AddHours(7))));

        // Closing: everyone out, the person in charge signs, then the issuer; then it is locked.
        var inside = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!.Inside.Single();
        await _vessel.LeaveAsync(_s.CompanyId, permit.Id, As(_bosun), inside.Id);
        Assert.Contains("signs that the work is finished first", await RefusedAsync(() => _vessel.CompleteAsync(_s.CompanyId, permit.Id, As(_chief))));
        Assert.Contains("person in charge", await RefusedAsync(() => _vessel.FinishWorkAsync(_s.CompanyId, permit.Id, As(_ab1))));
        await _vessel.FinishWorkAsync(_s.CompanyId, permit.Id, As(_second), "Bilge suction cleared, hold closed.");
        await _vessel.CompleteAsync(_s.CompanyId, permit.Id, As(_chief));
        Assert.Contains("completed", await RefusedAsync(() => _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_bosun), Gas())));
        Assert.Contains("completed", await RefusedAsync(() => _vessel.SuspendAsync(_s.CompanyId, permit.Id, As(_master), "Late")));
        Assert.Contains("completed", await RefusedAsync(() => _vessel.CancelAsync(_s.CompanyId, permit.Id, As(_master), "Too late")));

        await _shore.CommentAsync(_s.CompanyId, permit.Id, _s.User, "Please send the calibration certificate for GD-1.");
        Assert.Contains("office", await RefusedAsync(() => _vessel.CommentAsync(_s.CompanyId, permit.Id, As(_master), "Onboard does not comment")));
        Assert.Contains("onboard", await RefusedAsync(() => _shore.IssueAsync(_s.CompanyId, permit.Id, _s.User)));
        await _s.SettleUntilQuietAsync();

        var ashore = (await _shore.GetAsync(_s.CompanyId, permit.Id, _s.User))!;
        var onboard = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_ab2)))!;
        Assert.Equal(Line(ashore), Line(onboard));
        Assert.Equal(PermitState.Completed, ashore.State);
        Assert.True(ashore.Can.IsOffice && ashore.Can.Comment && !ashore.Can.Issue && !ashore.Can.Complete);
        Assert.Equal(2, ashore.Permit.Entries.Count);
        Assert.Contains(ashore.Permit.Events, e => e.Kind == PermitEventKind.Extended);
    }

    // ---- expiry ----

    [Fact]
    public async Task APermitPastItsWindowIsExpiredNotClosed_SweptOnce_RaisesATask_AndIsClosedWithTheStatusOfTheWork()
    {
        await RelaxAsync(Cold);
        var permit = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Cold, attendant: null, until: _now.AddHours(2)) with { PersonCrewIds = [_ab1.Id], AttendantCrewId = null });
        await AnswerAllAsync(permit, _second);
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_second), Gas());
        await _vessel.IssueAsync(_s.CompanyId, permit.Id, As(_chief));
        await _vessel.StartAsync(_s.CompanyId, permit.Id, As(_second));
        Assert.Equal(0, await _vessel.SweepAsync(_s.CompanyId, _s.VesselId));                                                       // still inside the window

        _now = _now.AddHours(3);
        await _s.SettleUntilQuietAsync();
        var ashoreEarly = (await _shore.GetAsync(_s.CompanyId, permit.Id, _s.User))!;
        Assert.Equal(PermitState.Expired, (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!.State);
        Assert.Equal(1, await _vessel.SweepAsync(_s.CompanyId, _s.VesselId));
        Assert.Equal(0, await _vessel.SweepAsync(_s.CompanyId, _s.VesselId));                                                       // once only
        var detail = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!;
        Assert.Single(detail.Actions);
        Assert.Contains("expired", detail.Actions[0].Title);
        Assert.Contains("Expired", string.Join(",", detail.Permit.Events.Select(e => e.Kind)));
        Assert.Contains("Expired", (await _vessel.BoardAsync(_s.CompanyId, As(_second))).Rows.Single().State.ToString());

        Assert.Contains("expired", await RefusedAsync(() => _vessel.ExtendAsync(_s.CompanyId, permit.Id, As(_chief), _now.AddHours(1))));       // closed, not extended
        Assert.Contains("state the work", await RefusedAsync(() => _vessel.CompleteAsync(_s.CompanyId, permit.Id, As(_chief))));
        await _vessel.CompleteAsync(_s.CompanyId, permit.Id, As(_chief), PermitCloseStatus.LeftUnsafe, "Hatch left open, guarded by the bosun.");
        await _s.SettleUntilQuietAsync();

        var ashore = (await _shore.GetAsync(_s.CompanyId, permit.Id, _s.User))!;
        Assert.Equal(PermitState.Completed, ashore.State);
        Assert.Contains("LeftUnsafe", ashore.Permit.Events.Single(e => e.Kind == PermitEventKind.Completed).Note);
        Assert.Equal(2, ashore.Actions.Count);                                                                                      // the expiry task and the unsafe close-out task
        Assert.Equal(PermitState.Expired, ashoreEarly.State);                                                                       // the office saw it expire from the clock alone
    }

    // ---- the office's types and checklists ----

    [Fact]
    public async Task ANewChecklistVersionNeverTouchesAPermitAlreadyRequested_AndAnEditedTypeAppliesOnlyToNewOnes()
    {
        await RelaxAsync(Cold);
        var first = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Cold, attendant: null) with { AttendantCrewId = null });
        Assert.Equal(1, first.ChecklistVersion);
        var firstCount = first.Answers.Count;

        var items = (await _shore.ChecklistAsync(_s.CompanyId, Cold.Id)).Select(i => new PermitChecklistInput(i.Section, i.Text, i.Mandatory, i.NoteRequired)).ToList();
        Assert.Equal(1, await _shore.SaveChecklistAsync(_s.CompanyId, Cold.Id, items));                                            // unchanged: no new version
        items.Add(new PermitChecklistInput("Area", "The area is lit", true, true));
        Assert.Equal(2, await _shore.SaveChecklistAsync(_s.CompanyId, Cold.Id, items));
        Assert.Contains("office", await RefusedAsync(() => _vessel.SaveChecklistAsync(_s.CompanyId, Cold.Id, items)));
        var cold = (await _shore.TypesAsync(_s.CompanyId)).Single(t => t.Id == Cold.Id);
        cold.GasRepeatMinutes = 15;
        await _shore.SaveTypeAsync(_s.CompanyId, cold);
        await _s.SettleUntilQuietAsync();

        var second = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Cold, attendant: null) with { AttendantCrewId = null });
        Assert.Equal((2, firstCount + 1), (second.ChecklistVersion, second.Answers.Count));
        var old = (await _vessel.GetAsync(_s.CompanyId, first.Id, As(_second)))!;
        Assert.Equal((1, firstCount, 60), (old.Permit.ChecklistVersion, old.Permit.Answers.Count, old.Terms.GasRepeatMinutes));       // the permit keeps the terms it was requested under
        Assert.Equal(15, (await _vessel.GetAsync(_s.CompanyId, second.Id, As(_second)))!.Terms.GasRepeatMinutes);
        Assert.Equal(3, (await _vessel.ChecklistAsync(_s.CompanyId, Cold.Id)).Count(i => i.Text == "The area is lit" || i.Text.StartsWith("The area has been")));
    }

    [Fact]
    public async Task ATypeIsCheckedWhenTheOfficeSavesIt()
    {
        var type = (await _shore.TypesAsync(_s.CompanyId)).Single(t => t.Id == Hot.Id);
        async Task<string> Refused(Action<PermitType> change) { change(type); return await RefusedAsync(() => _shore.SaveTypeAsync(_s.CompanyId, type)); }
        Assert.Contains("name", await Refused(t => t.Name = " "));
        type.Name = Hot.Name;
        Assert.Contains("code", await Refused(t => t.Code = "H"));
        Assert.Contains("used by another", await Refused(t => t.Code = Enclosed.Code));
        type.Code = Hot.Code;
        Assert.Contains("1 to 72", await Refused(t => t.MaxValidityHours = 100));
        type.MaxValidityHours = 8;
        Assert.Contains("oxygen", await Refused(t => (t.OxygenMin, t.OxygenMax) = (22m, 21m)));
        type.OxygenMin = 20.9m;
        type.OxygenMax = 22m;
        Assert.Contains("office", await RefusedAsync(() => _vessel.SaveTypeAsync(_s.CompanyId, type)));

        var added = await _shore.SaveTypeAsync(_s.CompanyId, new PermitType { Name = "Crane lifting", Code = "crn", MaxValidityHours = 12, RequesterRole = PermitRole.AnyCrew });
        Assert.Equal("CRN", added.Code);
        Assert.Contains("already in the list", await RefusedAsync(() => _shore.SaveTypeAsync(_s.CompanyId, new PermitType { Name = "Crane lifting", Code = "CRB" })));
    }

    // ---- simultaneous operations ----

    [Fact]
    public async Task ConflictingPermitsWarnTheIssuer_WhoMustAcknowledgeItAndItIsRecorded()
    {
        await RelaxAsync(Enclosed);
        await RelaxAsync(Hot);
        async Task<Permit> ReadyAsync(PermitType type, string location)
        {
            var p = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(type, location: location));
            await AnswerAllAsync(p, _second);
            await _vessel.RecordGasAsync(_s.CompanyId, p.Id, As(_second), Gas());
            return p;
        }
        var space = await ReadyAsync(Enclosed, "Cargo hold 2");
        await _vessel.IssueAsync(_s.CompanyId, space.Id, As(_chief));
        var hot = await ReadyAsync(Hot, "Engine room workshop");

        var check = await _vessel.CheckIssueAsync(_s.CompanyId, hot.Id, As(_chief));
        Assert.Equal(space.Code, Assert.Single(check.Conflicts).Code);
        Assert.Contains("open", await RefusedAsync(() => _vessel.IssueAsync(_s.CompanyId, hot.Id, As(_chief))));
        await _vessel.IssueAsync(_s.CompanyId, hot.Id, As(_chief), acknowledgeConflicts: true);
        Assert.Contains((await _vessel.GetAsync(_s.CompanyId, hot.Id, As(_chief)))!.Permit.Events, e => e.Kind == PermitEventKind.ConflictAcknowledged && e.Note.Contains(space.Code));
        var board = await _vessel.BoardAsync(_s.CompanyId, As(_second));
        Assert.Equal(2, board.Rows.Count);
        Assert.Single(board.Overlaps);
    }

    // ---- requesting is checked ----

    [Fact]
    public async Task ARequestIsRefusedWhenItsWindowItsPeopleOrItsThingsAreWrong()
    {
        await RelaxAsync(Enclosed);
        async Task<string> Refused(PermitInput input, CrewMember? by = null) => await RefusedAsync(() => _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(by ?? _second), input));
        Assert.Contains("after it starts", await Refused(Input(Enclosed, from: _now, until: _now)));
        Assert.Contains("at most 8 hours", await Refused(Input(Enclosed, until: _now.AddHours(9))));
        Assert.Contains("already have ended", await Refused(Input(Enclosed, from: _now.AddHours(-3), until: _now.AddHours(-1))));
        Assert.Contains("where", await Refused(Input(Enclosed, location: " ")));
        Assert.Contains("attendant cannot be one", await Refused(Input(Enclosed, people: [_ab1.Id, _bosun.Id], attendant: _bosun.Id)));
        Assert.Contains("not one of the vessel's crew", await Refused(Input(Enclosed, people: [Guid.NewGuid()])));
        Assert.Contains("tank is not on this vessel", await Refused(Input(Enclosed) with { TankId = Guid.NewGuid() }));
        Assert.Contains("equipment", await Refused(Input(Enclosed) with { MachineryItemId = Guid.NewGuid() }));
        Assert.Contains("risk assessment", await Refused(Input(Enclosed) with { RiskAssessmentId = Guid.NewGuid() }));
        Assert.Contains("Choose", await Refused(Input(Enclosed) with { PermitTypeId = Guid.NewGuid() }));

        var kept = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Enclosed));
        Assert.Contains("withdraws", await RefusedAsync(() => _vessel.WithdrawAsync(_s.CompanyId, kept.Id, As(_ab1), "Not mine")));
        await _vessel.WithdrawAsync(_s.CompanyId, kept.Id, As(_second), "Raised for the wrong hold.");
        Assert.Contains("withdrawn", await RefusedAsync(() => _vessel.WithdrawAsync(_s.CompanyId, kept.Id, As(_second), "Again")));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(PermitState.Withdrawn, (await _shore.GetAsync(_s.CompanyId, kept.Id, _s.User))!.State);                       // kept, never deleted
    }

    [Fact]
    public async Task APermitIsHandedOverBetweenShifts_AndNeverToTheIssuerOrTheAttendant()
    {
        await RelaxAsync(Cold);
        var permit = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Cold, attendant: _bosun.Id));
        await AnswerAllAsync(permit, _second);
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_second), Gas());
        await _vessel.IssueAsync(_s.CompanyId, permit.Id, As(_chief));
        await _vessel.StartAsync(_s.CompanyId, permit.Id, As(_second));

        Assert.Contains("issuer cannot also", await RefusedAsync(() => _vessel.HandOverAsync(_s.CompanyId, permit.Id, As(_second), _chief.Id, "Half done")));
        Assert.Contains("attendant", await RefusedAsync(() => _vessel.HandOverAsync(_s.CompanyId, permit.Id, As(_second), _bosun.Id, "Half done")));
        Assert.Contains("state", await RefusedAsync(() => _vessel.HandOverAsync(_s.CompanyId, permit.Id, As(_second), _master.Id, " ")));
        Assert.Contains("person in charge", await RefusedAsync(() => _vessel.HandOverAsync(_s.CompanyId, permit.Id, As(_ab1), _master.Id, "Half done")));
        await _vessel.HandOverAsync(_s.CompanyId, permit.Id, As(_second), _master.Id, "Pipe cleared, valve still to refit.");
        var handed = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_master)))!;
        Assert.Equal(_master.FullName, handed.Permit.RequesterName);
        Assert.Contains("to " + _master.FullName, handed.Permit.Events.Single(e => e.Kind == PermitEventKind.HandedOver).Note);
        Assert.True(handed.Can.FinishWork && handed.Can.HandOver);
        Assert.False((await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!.Can.FinishWork);                               // no longer theirs to sign
    }

    // ---- links to the home page, tasks, tank inspection, PMS and rest hours (P5) ----

    [Fact]
    public async Task APermitThatNeedsTheMasterRaisesATaskForHim_AndTheHomeCountsAgreeOnBothSides()
    {
        await RelaxAsync(Enclosed, risk: false, master: true);

        var permit = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Enclosed));
        var detail = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!;
        Assert.Contains(detail.Actions, a => a.Title.Contains("awaits the Master's approval") && a.AssigneeName == "Master");

        var onboard = (await _vessel.CountsAsync(_s.CompanyId, As(_ab1)))!;
        Assert.Equal((false, 0, 1, 0, 0), (onboard.IsOffice, onboard.Active, onboard.Awaiting, onboard.Suspended, onboard.Expired));
        await _s.SettleUntilQuietAsync();
        var ashore = (await _shore.CountsAsync(_s.CompanyId, _s.User))!;
        Assert.Equal((true, 0, 1, 0, 0), (ashore.IsOffice, ashore.Active, ashore.Awaiting, ashore.Suspended, ashore.Expired));
        Assert.Equal(0, await _shore.ExpiredCountAsync(_s.CompanyId, _s.User));
        Assert.Equal(0, await _vessel.ExpiredCountAsync(_s.CompanyId, As(_master)));                                                // onboard there is no fleet badge
    }

    [Fact]
    public async Task ATankInspectionSeesTheEnclosedSpacePermitForItsTank_AsAWarningOnly()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.TankInspection);
        await RelaxAsync(Enclosed);
        var tank = await new TankService(_s.VesselFactory).AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput("VS1", "Void space 1", TankType.VoidSpace), As(_master));

        var none = await _vessel.TankStatusAsync(_s.CompanyId, _s.VesselId, tank.Id, As(_second));
        Assert.True(none.ModuleOn);
        Assert.Null(none.Permit);                                                                                                  // a warning to show, nothing is blocked

        var permit = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Enclosed) with { TankId = tank.Id });
        await AnswerAllAsync(permit, _second);
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(_second), Gas());
        await _vessel.IssueAsync(_s.CompanyId, permit.Id, As(_chief));
        await _vessel.StartAsync(_s.CompanyId, permit.Id, As(_second));
        var ok = await _vessel.TankStatusAsync(_s.CompanyId, _s.VesselId, tank.Id, As(_second));
        Assert.Equal((permit.Id, true), (ok.Permit!.Permit.Id, ok.Permit.Valid));

        _now = _now.AddMinutes(45);                                                                                                // the gas reading is out of date
        var late = await _vessel.TankStatusAsync(_s.CompanyId, _s.VesselId, tank.Id, As(_second));
        Assert.Equal((permit.Id, false), (late.Permit!.Permit.Id, late.Permit.Valid));
        Assert.Null((await _vessel.TankStatusAsync(_s.CompanyId, _s.VesselId, Guid.NewGuid(), As(_second))).Permit);               // another tank: no permit

        // A permit of a kind without an entry log is not an enclosed space permit.
        await RelaxAsync(Cold);
        var cold = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Cold, attendant: null) with { AttendantCrewId = null, TankId = tank.Id });
        Assert.Equal(permit.Id, (await _vessel.TankStatusAsync(_s.CompanyId, _s.VesselId, tank.Id, As(_second))).Permit!.Permit.Id);
        Assert.NotEqual(cold.Id, permit.Id);
    }

    [Fact]
    public async Task APermitCanBeRaisedForAMaintenanceJob_AndTheJobListsIt_ButOnlyForAJobOnThisVessel()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.Pms);
        await RelaxAsync(Hot);
        var pms = new PmsService(_s.ShoreFactory);
        var item = await pms.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Bilge pump", "BP-1"), _s.User);
        var job = await pms.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "BP-1-A", Title: "Weld the pump base", CalendarInterval: 6, StartDate: _now.Date.AddDays(-30)), _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.Contains("maintenance job", await RefusedAsync(() => _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Hot) with { PlannedJobId = Guid.NewGuid() })));
        var permit = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Hot) with { PlannedJobId = job.Id, MachineryItemId = item.Id });
        Assert.Equal(job.Id, permit.PlannedJobId);
        Assert.Equal([permit.Id], (await _vessel.ForJobAsync(_s.CompanyId, _s.VesselId, job.Id, As(_second))).Select(r => r.Permit.Id));
        Assert.Empty(await _vessel.ForJobAsync(_s.CompanyId, _s.VesselId, Guid.NewGuid(), As(_second)));
        await _s.SettleUntilQuietAsync();
        Assert.Single(await _shore.ForJobAsync(_s.CompanyId, _s.VesselId, job.Id, _s.User));                                       // the office sees the same list
    }

    [Fact]
    public async Task TheIssuerAndTheMasterSeeTheHoursWorkedByThePeopleOnAPermit_NobodyElseDoes()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.RestHours);
        await RelaxAsync(Cold);
        var permit = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Cold, attendant: null, people: [_ab1.Id, _ab2.Id]) with { AttendantCrewId = null });
        var day = permit.ValidFrom.Date;
        var rest = new RestHoursService(_s.VesselFactory);
        await rest.RecordDayAsync(_s.CompanyId, _s.VesselId, _ab1.Id, day, "05:00-16:00", As(_ab1));                                // eleven hours
        await rest.RecordDayAsync(_s.CompanyId, _s.VesselId, _ab1.Id, day, "05:00-16:00,18:00-19:00", As(_ab1), reason: "Extra watch");   // a later entry replaces it: twelve hours

        var seen = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_chief)))!.Fatigue!;
        Assert.Equal([(_ab1.FullName, 12 * 60)], seen.Select(f => (f.Name, f.WorkMinutes)));                                        // the second man recorded nothing, so nothing is shown
        Assert.NotNull((await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_master)))!.Fatigue);
        Assert.Null((await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_second)))!.Fatigue);                                       // the person in charge is not an issuer
        Assert.Null((await _vessel.GetAsync(_s.CompanyId, permit.Id, As(_ab1)))!.Fatigue);
        await _s.SettleUntilQuietAsync();
        Assert.Null((await _shore.GetAsync(_s.CompanyId, permit.Id, _s.User))!.Fatigue);                                            // the office reads the hours on their own screens
    }
}
