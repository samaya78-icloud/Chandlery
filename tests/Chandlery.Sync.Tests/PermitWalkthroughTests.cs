using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// Permit to work as one story between a real office database and a real vessel database: the office sets the rules, the vessel works an enclosed space entry and a hot work permit
/// side by side (a conflict the issuer acknowledges, gas outside limits, an extension, a hand-over), one permit is closed properly, one expires and is closed with the work left unsafe,
/// one is withdrawn; at the end both sides read the same permits, the same CSV files and the same counts. A vessel holds nothing of another vessel's permits, even after a full baseline.
/// </summary>
public class PermitWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PermitService _shore = null!, _vessel = null!;
    private CrewMember _master = null!, _chief = null!, _second = null!, _ab1 = null!, _ab2 = null!, _bosun = null!;
    private DateTime _now = DateTime.UtcNow;
    private List<PermitType> _types = null!;
    private PermitType Enclosed => _types.Single(t => t.Name == "Enclosed space entry");
    private PermitType Hot => _types.Single(t => t.Name == "Hot work");

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new PermitService(_s.ShoreFactory, new WorklistService(_s.ShoreFactory)) { Clock = () => _now };
        _vessel = new PermitService(_s.VesselFactory, new WorklistService(_s.VesselFactory)) { Clock = () => _now };
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(Guid vesselId)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.PermitToWork, PolicyModules.Worklist }) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vesselId, Module = module, Enabled = true });
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

    private PermitInput Input(PermitType type, DateTime? from = null, DateTime? until = null, string location = "Cargo hold 2")
        => new(type.Id, location, "Repair the bilge suction", from ?? _now.AddMinutes(-10), until ?? _now.AddHours(4), [_ab1.Id, _ab2.Id], _bosun.Id);

    private static PermitGasInput Gas(decimal lel = 0m) => new("Gas detector GD-1", new DateTime(2026, 10, 1), "Hold 2", 20.9m, lel);

    private async Task ReadyAsync(Permit permit, CrewMember by)
    {
        var d = (await _vessel.GetAsync(_s.CompanyId, permit.Id, As(by)))!;
        await _vessel.AnswerAsync(_s.CompanyId, permit.Id, As(by), d.Permit.Answers.Select(a => new PermitAnswerInput(a.Id, PermitAnswerKind.Yes, a.NoteRequired ? "Checked" : "")).ToList());
        await _vessel.RecordGasAsync(_s.CompanyId, permit.Id, As(by), Gas());
    }

    private static string Line(PermitDetail d)
        => $"{d.Permit.Code}|{d.Permit.TypeName} v{d.Permit.ChecklistVersion}|{d.State}|valid {d.Validity.Valid}:{d.Validity.Reason}|req {d.Permit.RequesterName}|iss {d.Permit.IssuerName}|att {d.Permit.AttendantName}|"
           + $"answers {d.Permit.Answers.Count(a => a.Answer == PermitAnswerKind.Yes)}/{d.Permit.Answers.Count}|gas {d.Permit.Readings.Count}:{d.Permit.Readings.Count(r => !r.WithinLimits)}:{d.Gas}|"
           + $"events {string.Join(",", d.Permit.Events.OrderBy(e => e.At).ThenBy(e => e.Id).Select(e => e.Kind))}|entries {string.Join(",", d.Permit.Entries.OrderBy(e => e.PersonName).ThenBy(e => e.In).Select(e => $"{e.PersonName}:{e.Out is null}"))}|"
           + $"until {d.Until:O}|comments {d.Comments.Count}|actions {string.Join(";", d.Actions.Select(a => a.Title).Order())}";

    [Fact]
    public async Task TheOfficeSetsTheRules_TheVesselWorksSideBySidePermits_AndBothSidesEndWithTheSamePermitsFilesAndCounts()
    {
        await SwitchOnAsync(_s.VesselId);
        _master = await SignOnAsync("W3001", "Master", master: true);
        _chief = await SignOnAsync("W3002", "Chief Officer", authorities: CrewAuthority.IssuePermits);
        _second = await SignOnAsync("W3003", "Second Officer");
        _ab1 = await SignOnAsync("W3004", "Able Seaman");
        _ab2 = await SignOnAsync("W3005", "Able Seaman");
        _bosun = await SignOnAsync("W3006", "Bosun");

        // The office sets the rules: the Master approves enclosed space entries, hot work does not need a signed risk assessment, and the enclosed space checklist gets a second version.
        _types = await _shore.TypesAsync(_s.CompanyId);
        foreach (var (type, master) in new[] { (Enclosed, true), (Hot, false) })
        {
            type.RiskAssessmentRequired = false;
            type.MasterApprovalRequired = master;
            await _shore.SaveTypeAsync(_s.CompanyId, type);
        }
        var items = (await _shore.ChecklistAsync(_s.CompanyId, Enclosed.Id)).Select(i => new PermitChecklistInput(i.Section, i.Text, i.Mandatory, i.NoteRequired)).ToList();
        items.Add(new PermitChecklistInput("Rescue", "The rescue team has been named", true, true));
        Assert.Equal(2, await _shore.SaveChecklistAsync(_s.CompanyId, Enclosed.Id, items));
        await _s.SettleUntilQuietAsync();

        // The entry: requested, checked, approved by the Master, issued, started, two people in.
        var entry = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Enclosed));
        Assert.Equal(2, entry.ChecklistVersion);
        await ReadyAsync(entry, _second);
        await _vessel.ApproveAsync(_s.CompanyId, entry.Id, As(_master));
        await _vessel.IssueAsync(_s.CompanyId, entry.Id, As(_chief));
        await _vessel.StartAsync(_s.CompanyId, entry.Id, As(_second));
        var in1 = await _vessel.EnterAsync(_s.CompanyId, entry.Id, As(_bosun), _ab1.Id);
        var in2 = await _vessel.EnterAsync(_s.CompanyId, entry.Id, As(_bosun), _ab2.Id);

        // Hot work beside it conflicts with the open space: the issuer must acknowledge.
        var hot = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Hot, location: "Engine room workshop"));
        await ReadyAsync(hot, _second);
        Assert.Contains("conflicts", await RefusedAsync(() => _vessel.IssueAsync(_s.CompanyId, hot.Id, As(_chief))));
        await _vessel.IssueAsync(_s.CompanyId, hot.Id, As(_chief), acknowledgeConflicts: true);
        await _vessel.StartAsync(_s.CompanyId, hot.Id, As(_second));

        // Gas goes out of limits in the space: the permit stops, both come out, a clean reading and the issuer put it back.
        _now = _now.AddMinutes(10);
        await _vessel.RecordGasAsync(_s.CompanyId, entry.Id, As(_bosun), Gas(lel: 7m));
        Assert.Equal(PermitState.Suspended, (await _vessel.GetAsync(_s.CompanyId, entry.Id, As(_second)))!.State);
        await _vessel.LeaveAsync(_s.CompanyId, entry.Id, As(_bosun), in1.Id);
        await _vessel.LeaveAsync(_s.CompanyId, entry.Id, As(_bosun), in2.Id);
        await _vessel.RecordGasAsync(_s.CompanyId, entry.Id, As(_bosun), Gas());
        await _vessel.ResumeAsync(_s.CompanyId, entry.Id, As(_chief));
        var back = await _vessel.EnterAsync(_s.CompanyId, entry.Id, As(_bosun), _ab1.Id);

        // The job runs longer: a fresh reading and a new end; the person in charge hands over to the Master for the night.
        _now = _now.AddMinutes(15);
        await _vessel.RecordGasAsync(_s.CompanyId, entry.Id, As(_bosun), Gas());
        await _vessel.ExtendAsync(_s.CompanyId, entry.Id, As(_chief), _now.AddHours(5));
        await _vessel.HandOverAsync(_s.CompanyId, entry.Id, As(_second), _master.Id, "Bilge suction cleared, valve to refit.");
        await _vessel.LeaveAsync(_s.CompanyId, entry.Id, As(_bosun), back.Id);
        await _vessel.FinishWorkAsync(_s.CompanyId, entry.Id, As(_master), "Valve refitted, hold closed.");
        await _vessel.CompleteAsync(_s.CompanyId, entry.Id, As(_chief));

        // Hours later the hot work permit is past its window and nobody closed it: it expires, a task is raised, and the issuer closes it with the work left unsafe.
        _now = _now.AddHours(6);
        Assert.Equal(1, await _vessel.SweepAsync(_s.CompanyId, _s.VesselId));
        await _vessel.CompleteAsync(_s.CompanyId, hot.Id, As(_chief), PermitCloseStatus.LeftUnsafe, "Weld not finished, area barriered.");

        // A permit raised in error is withdrawn, never deleted.
        var wrong = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(Hot, from: _now, until: _now.AddHours(2)));
        await _vessel.WithdrawAsync(_s.CompanyId, wrong.Id, As(_second), "Raised for the wrong hold.");

        await _s.SettleUntilQuietAsync();                                                                                          // the office needs the permits before it can comment
        await _shore.CommentAsync(_s.CompanyId, entry.Id, _s.User, "Please keep the gas detector calibration record with the permit.");
        await _s.SettleUntilQuietAsync();

        // Both sides read the same permits, event for event.
        foreach (var id in new[] { entry.Id, hot.Id, wrong.Id })
            Assert.Equal(Line((await _vessel.GetAsync(_s.CompanyId, id, As(_ab2)))!), Line((await _shore.GetAsync(_s.CompanyId, id, _s.User))!));
        Assert.Equal([PermitState.Completed, PermitState.Completed, PermitState.Withdrawn],
            new[] { entry.Id, hot.Id, wrong.Id }.Select(id => _shore.GetAsync(_s.CompanyId, id, _s.User).Result!.State));
        Assert.Equal(1, (await _shore.GetAsync(_s.CompanyId, entry.Id, _s.User))!.Comments.Count);

        // The same files from either side.
        var onboardRows = await _vessel.ListAsync(_s.CompanyId, As(_master));
        var ashoreRows = await _shore.ListAsync(_s.CompanyId, _s.User, _s.VesselId);
        Assert.Equal(PermitReports.RegisterCsv(onboardRows), PermitReports.RegisterCsv(ashoreRows));
        Assert.Equal(PermitReports.EventsCsv(onboardRows), PermitReports.EventsCsv(ashoreRows));
        Assert.Equal(3, ashoreRows.Count);

        // The same counts: the office's fleet view for this vessel is what the vessel works out from its own permits.
        var from = _now.AddDays(-1);
        var to = _now.AddDays(1);
        var fleet = await _shore.FleetAsync(_s.CompanyId, _s.User, from, to);
        var own = PermitFleet.Build(from, to, onboardRows);
        Assert.Equal(own.Vessels, fleet.Vessels);
        Assert.Equal(PermitReports.FleetCsv(own), PermitReports.FleetCsv(fleet));
        Assert.Equal(PermitReports.MonthsCsv(own), PermitReports.MonthsCsv(fleet));
        var counts = (await _shore.CountsAsync(_s.CompanyId, _s.User))!;
        var mine = (await _vessel.CountsAsync(_s.CompanyId, As(_master)))!;
        Assert.Equal((counts.Active, counts.Awaiting, counts.Suspended, counts.Expired, counts.GasOutside30Days), (mine.Active, mine.Awaiting, mine.Suspended, mine.Expired, mine.GasOutside30Days));
        Assert.Equal(1, mine.GasOutside30Days);                                                                                    // the one reading outside the limits
        Assert.Equal(0, mine.Expired);                                                                                             // it was closed
        Assert.Equal(1, fleet.Vessels.Single().GasExceedances);
        Assert.Contains(fleet.Attention, a => a.Row.Permit.Id == hot.Id && a.Why.Contains("unsafe"));                              // the office is told of the unsafe close-out

        // A closed permit is locked on both sides.
        Assert.Contains("completed", await RefusedAsync(() => _vessel.SuspendAsync(_s.CompanyId, entry.Id, As(_master), "Late")));
        Assert.Contains("onboard", await RefusedAsync(() => _shore.IssueAsync(_s.CompanyId, wrong.Id, _s.User)));
    }

    [Fact]
    public async Task TheVesselHoldsOnlyItsOwnPermits_NotEvenAfterAFullBaseline_WhileTheOfficeKeepsBothApart()
    {
        await SwitchOnAsync(_s.VesselId);
        await SwitchOnAsync(_s.OtherVesselId);
        _master = await SignOnAsync("W4001", "Master", master: true);
        _second = await SignOnAsync("W4002", "Second Officer");
        _ab1 = await SignOnAsync("W4003", "Able Seaman");
        _ab2 = await SignOnAsync("W4004", "Able Seaman");
        _bosun = await SignOnAsync("W4005", "Bosun");
        _types = await _shore.TypesAsync(_s.CompanyId);
        var cold = _types.Single(t => t.Name == "Cold work in a hazardous area");
        cold.GasTestRequired = false;
        await _shore.SaveTypeAsync(_s.CompanyId, cold);
        await _s.SettleUntilQuietAsync();
        var ours = await _vessel.RequestAsync(_s.CompanyId, _s.VesselId, As(_second), Input(cold) with { AttendantCrewId = null });

        // The other vessel's permit, and the office's comment on it, are written straight into the office database, as that vessel's sync would.
        var theirs = new Permit
        {
            Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, Code = "OTHER-PTW-1", PermitTypeId = cold.Id, TypeName = cold.Name, ChecklistVersion = 1,
            Terms = PermitRules.Serialize(PermitRules.TermsOf(cold)), Location = "Their hold", Description = "Theirs", RequesterName = "Other officer", ValidFrom = _now.AddHours(-1), ValidUntil = _now.AddHours(3), CreatedAt = _now,
            Events = [new PermitEvent { Id = Guid.CreateVersion7(), Kind = PermitEventKind.Requested, At = _now, ByName = "Other officer" }],
            Answers = [new PermitAnswer { Id = Guid.CreateVersion7(), Text = "Their item", Mandatory = true }],
        };
        await using (var shore = await _s.ShoreFactory.CreateDbContextAsync())
        {
            shore.Permits.Add(theirs);
            shore.PermitComments.Add(new PermitComment { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, PermitId = theirs.Id, Text = "For them only", ByName = "Office", At = _now });
            await shore.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        await _shore.CommentAsync(_s.CompanyId, ours.Id, _s.User, "For us");
        await _s.SettleUntilQuietAsync();
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        await using var vessel = await _s.VesselFactory.CreateDbContextAsync();
        Assert.Equal([ours.Id], await vessel.Permits.IgnoreQueryFilters().Select(p => p.Id).ToListAsync());
        Assert.Empty(await vessel.PermitEvents.IgnoreQueryFilters().Where(e => e.PermitId == theirs.Id).ToListAsync());
        Assert.Empty(await vessel.PermitAnswers.IgnoreQueryFilters().Where(a => a.PermitId == theirs.Id).ToListAsync());
        Assert.Equal(["For us"], await vessel.PermitComments.IgnoreQueryFilters().Select(c => c.Text).ToListAsync());               // the office's comment on our permit arrived; the other vessel's did not
        Assert.NotEmpty(await vessel.PermitTypes.IgnoreQueryFilters().ToListAsync());                                              // the types and checklists are everyone's

        // The office has both, apart.
        var all = await _shore.ListAsync(_s.CompanyId, _s.User);
        Assert.Equal(new[] { _s.VesselId, _s.OtherVesselId }.Order(), all.Select(r => r.Permit.VesselId).Order());
        Assert.Single(await _vessel.ListAsync(_s.CompanyId, As(_master)));
        Assert.Null(await _vessel.GetAsync(_s.CompanyId, theirs.Id, As(_master)));
    }

    private async Task<string> RefusedAsync(Func<Task> act) => (await Assert.ThrowsAsync<InvalidOperationException>(act)).Message;
}
