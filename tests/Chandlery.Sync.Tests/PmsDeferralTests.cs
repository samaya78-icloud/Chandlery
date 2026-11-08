using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>How an approved deferral changes a job's due date. Pure.</summary>
public class PmsDeferralSchedulerTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    private static readonly PmsJobInput Monthly = new(1, PmsIntervalUnit.Months, null, PmsAnchor.FromCompletion, null, null, D(2026, 1, 1), null, null);
    private static PmsDeferralInput Granted(DateTime date, DateTime? cycle = null, int hour = 0) => new(date, cycle, D(2026, 2, 1).AddHours(hour), Guid.CreateVersion7());

    [Fact]
    public void AnApprovedDeferral_MovesTheDueDate_WithNoFurtherGrace()
    {
        var plain = PmsScheduler.Compute(Monthly, [], []);
        Assert.Equal(D(2026, 2, 1), plain.NextDue);

        var s = PmsScheduler.Compute(Monthly, [], [], [Granted(D(2026, 2, 20))]);
        Assert.Equal((D(2026, 2, 20), "deferred", D(2026, 2, 20), D(2026, 2, 20)), (s.NextDue, s.DueBasis, s.GraceUntil, s.DeferredTo));
        Assert.Equal(PmsStatus.NotDue, PmsScheduler.StatusOf(s, D(2026, 2, 19)));
        Assert.Equal(PmsStatus.Due, PmsScheduler.StatusOf(s, D(2026, 2, 20)));
        Assert.Equal(PmsStatus.Overdue, PmsScheduler.StatusOf(s, D(2026, 2, 21)));                      // the deferred date is a concession: overdue the day after
        Assert.Equal(PmsStatus.Due, PmsScheduler.StatusOf(plain, D(2026, 2, 3)));                       // without it the ordinary grace applies
    }

    [Fact]
    public void ADeferralForAnEarlierCycle_NoLongerApplies_OnceTheJobHasBeenDoneAgain()
    {
        var thisCycle = Granted(D(2026, 2, 20), cycle: null);
        var done = new PmsCompletionInput(D(2026, 2, 10), null, D(2026, 6, 1), Guid.CreateVersion7());
        var s = PmsScheduler.Compute(Monthly, [done], [], [thisCycle]);
        Assert.Equal((D(2026, 3, 10), "calendar", (DateTime?)null), (s.NextDue, s.DueBasis, s.DeferredTo));

        var nextCycle = Granted(D(2026, 3, 20), cycle: D(2026, 2, 10));                                  // asked after the 10 February completion
        Assert.Equal((D(2026, 3, 20), "deferred"), (PmsScheduler.Compute(Monthly, [done], [], [thisCycle, nextCycle]) is { } t ? (t.NextDue, t.DueBasis) : default));
    }

    [Fact]
    public void ADeferralThatDoesNotGoBeyondTheNaturalDate_ChangesNothing_AndTheLatestDecisionWins()
    {
        Assert.Null(PmsScheduler.Compute(Monthly, [], [], [Granted(D(2026, 1, 20))]).DeferredTo);

        var early = Granted(D(2026, 2, 10), hour: 1);
        var late = Granted(D(2026, 2, 25), hour: 2);
        Assert.Equal(D(2026, 2, 25), PmsScheduler.Compute(Monthly, [], [], [early, late]).NextDue);
        Assert.Equal(D(2026, 2, 25), PmsScheduler.Compute(Monthly, [], [], [late, early]).NextDue);       // the order they are listed in does not matter
        Assert.Equal(D(2026, 2, 10), PmsScheduler.Compute(Monthly, [], [], [late with { DecidedAt = D(2026, 2, 1) }, early with { DecidedAt = D(2026, 2, 2) }]).NextDue);   // the later decision, not the later date
    }

    [Fact]
    public void ADeferralWorksForJobsThatStartedFromABaseline_AndNeverForJobsWithoutAnySchedule()
    {
        var baselined = Monthly with { BaselineDoneDate = D(2025, 12, 15) };
        Assert.Equal(D(2026, 1, 25), PmsScheduler.Compute(baselined, [], [], [Granted(D(2026, 1, 25), cycle: D(2025, 12, 15))]).NextDue);
        var none = new PmsJobInput(null, PmsIntervalUnit.Months, null, PmsAnchor.FromCompletion, null, null, D(2026, 1, 1), null, null);
        Assert.Null(PmsScheduler.Compute(none, [], [], [Granted(D(2026, 3, 1))]).NextDue);
    }
}

/// <summary>Deferral requests and decisions on a real office and a real vessel.</summary>
public class PmsDeferralSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _vessel = null!, _shore = null!;
    private CrewMember _master = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime Ago(int days) => Today.AddDays(-days);
    private Actor MasterActor => new(_master.UserId, _master.FullName);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new PmsService(_s.VesselFactory);
        _shore = new PmsService(_s.ShoreFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Pms, Enabled = true });
            await db.SaveChangesAsync();
        }
        _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "CAPT7", "Capt. Deferral", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, false, IsMaster: true), _s.User);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, _master.Id, "Temp-pass-1", false, _s.User);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    /// <summary>A monthly job started 40 days ago: due about 10 days ago, so overdue.</summary>
    private async Task<PlannedJob> OverdueJobAsync(string code = "J1", bool critical = false)
    {
        var item = (await _shore.ListMachineryAsync(_s.CompanyId, _s.VesselId)).FirstOrDefault()
            ?? await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true), _s.User);
        var job = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: code, Title: code + " job", CalendarInterval: 1, IsCritical: critical, StartDate: Ago(40)), _s.User);
        await _s.SettleUntilQuietAsync();
        return job;
    }

    private async Task<(PlannedJob Shore, PlannedJob Vessel)> BothAsync(Guid jobId)
        => (await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == jobId)), await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == jobId)));

    [Fact]
    public async Task AnUnapprovedRequestLeavesTheJobOverdue_AnApprovedOneMovesTheDueDateOnBothSides()
    {
        var kept = await OverdueJobAsync("J1");
        var moved = await OverdueJobAsync("J2");
        var natural = (await BothAsync(moved.Id)).Shore.NextDueDate;

        var asked = await _vessel.RequestDeferralAsync(_s.CompanyId, moved.Id, Today.AddDays(20), "Spare part arrives at the next port", _s.User);
        await _vessel.RequestDeferralAsync(_s.CompanyId, kept.Id, Today.AddDays(20), "Short of crew", _s.User);
        await _s.SettleUntilQuietAsync();

        // Asking changes nothing.
        foreach (var side in new[] { OnVessel<PlannedJob>, OnShore<PlannedJob> })
            Assert.Equal(PmsStatus.Overdue, PmsProjection.StatusOf(await side(db => db.PlannedJobs.SingleAsync(j => j.Id == moved.Id)), Today));
        Assert.Equal(2, (await _shore.PendingDeferralsAsync(_s.CompanyId)).Count);

        var decision = await _shore.DecideDeferralAsync(_s.CompanyId, asked.Id, true, Today.AddDays(15), "Fifteen days, then it must be done", _s.User);
        Assert.Equal(("office", Today.AddDays(15)), (decision.DecidedByRole, decision.ApprovedDueDate));
        await _s.SettleUntilQuietAsync();

        var (shore, vessel) = await BothAsync(moved.Id);
        foreach (var j in new[] { shore, vessel })
        {
            Assert.Equal((Today.AddDays(15), "deferred", Today.AddDays(15), PmsStatus.NotDue), (j.NextDueDate, j.DueBasis, j.DeferredUntil, PmsProjection.StatusOf(j, Today)));
            Assert.NotEqual(natural, j.NextDueDate);
        }
        // The other request is still waiting: that job stays overdue on both sides.
        var (stillShore, stillVessel) = await BothAsync(kept.Id);
        Assert.All(new[] { stillShore, stillVessel }, j => Assert.Equal(PmsStatus.Overdue, PmsProjection.StatusOf(j, Today)));
        Assert.Single(await _vessel.PendingDeferralsAsync(_s.CompanyId));

        // Refusing needs a reason; once refused the job stays overdue.
        var waiting = (await _shore.PendingDeferralsAsync(_s.CompanyId)).Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideDeferralAsync(_s.CompanyId, waiting.Request.Id, false, null, "", _s.User));
        await _shore.DecideDeferralAsync(_s.CompanyId, waiting.Request.Id, false, null, "Do it this week", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(PmsStatus.Overdue, PmsProjection.StatusOf((await BothAsync(kept.Id)).Vessel, Today));
        Assert.Empty(await _vessel.PendingDeferralsAsync(_s.CompanyId));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TheMasterApprovesOnboardWithinTheLimit_AndTheOfficeSeesIt()
    {
        var job = await OverdueJobAsync();
        var due = (await BothAsync(job.Id)).Vessel.NextDueDate!.Value;
        var small = await _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(10), "Weather", _s.User);

        // Only the Master may decide onboard.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _vessel.DecideDeferralAsync(_s.CompanyId, small.Id, true, null, "", _s.User));
        Assert.False(await _vessel.MayDecideDeferralsAsync(_s.CompanyId, _s.VesselId, _s.User));
        Assert.True(await _vessel.MayDecideDeferralsAsync(_s.CompanyId, _s.VesselId, MasterActor));

        var decision = await _vessel.DecideDeferralAsync(_s.CompanyId, small.Id, true, null, "Agreed", MasterActor);
        Assert.Equal("master", decision.DecidedByRole);
        await _s.SettleUntilQuietAsync();
        var (shore, vessel) = await BothAsync(job.Id);
        Assert.All(new[] { shore, vessel }, j => Assert.Equal((due.AddDays(10), "deferred"), (j.NextDueDate, j.DueBasis)));
        Assert.Equal("master", (await OnShore(db => db.JobDeferralDecisions.SingleAsync(d => d.RequestId == small.Id))).DecidedByRole);

        // Decided once.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideDeferralAsync(_s.CompanyId, small.Id, false, null, "no", _s.User));
    }

    [Fact]
    public async Task TheMasterCannotApproveWhatIsBeyondTheLimit_OrACriticalJob_ButTheOfficeCan()
    {
        var ordinary = await OverdueJobAsync("J1");
        var critical = await OverdueJobAsync("J2", critical: true);
        var due = (await BothAsync(ordinary.Id)).Vessel.NextDueDate!.Value;

        var longAsk = await _vessel.RequestDeferralAsync(_s.CompanyId, ordinary.Id, due.AddDays(PmsDeferralRules.MasterMaxDays + 1), "Yard slot", _s.User);
        var criticalAsk = await _vessel.RequestDeferralAsync(_s.CompanyId, critical.Id, (await BothAsync(critical.Id)).Vessel.NextDueDate!.Value.AddDays(3), "Waiting for a part", _s.User);
        Assert.True(criticalAsk.IsCritical);

        var tooLong = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DecideDeferralAsync(_s.CompanyId, longAsk.Id, true, null, "", MasterActor));
        Assert.Contains("office decides", tooLong.Message);
        var notCritical = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DecideDeferralAsync(_s.CompanyId, criticalAsk.Id, true, null, "", MasterActor));
        Assert.Contains("critical", notCritical.Message);

        // The Master may still refuse; the office approves what the Master could not.
        await _vessel.DecideDeferralAsync(_s.CompanyId, longAsk.Id, false, null, "Not this long", MasterActor);
        await _s.SettleUntilQuietAsync();
        await _shore.DecideDeferralAsync(_s.CompanyId, criticalAsk.Id, true, null, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal("deferred", (await BothAsync(critical.Id)).Vessel.DueBasis);
        Assert.Equal(PmsStatus.Overdue, PmsProjection.StatusOf((await BothAsync(ordinary.Id)).Vessel, Today));
    }

    [Fact]
    public async Task DoingTheJob_EndsTheDeferral_AndTheSameFatesApplyNextTime()
    {
        var job = await OverdueJobAsync();
        var due = (await BothAsync(job.Id)).Vessel.NextDueDate!.Value;
        var asked = await _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(12), "Parts", _s.User);
        await _s.SettleUntilQuietAsync();
        await _shore.DecideDeferralAsync(_s.CompanyId, asked.Id, true, null, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal("deferred", (await BothAsync(job.Id)).Vessel.DueBasis);

        await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Today), _s.User);
        await _s.SettleUntilQuietAsync();
        foreach (var j in new[] { (await BothAsync(job.Id)).Shore, (await BothAsync(job.Id)).Vessel })
            Assert.Equal((Today.AddMonths(1), "calendar", (DateTime?)null), (j.NextDueDate, j.DueBasis, j.DeferredUntil));   // counted from the completion; the old deferral is spent

        // A request made before the job was done can no longer be decided; a fresh one can be asked.
        var stale = await _shore.DeferralsOfJobAsync(_s.CompanyId, job.Id);
        Assert.Single(stale);
        Assert.NotNull(await _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, Today.AddMonths(1).AddDays(5), "Again", _s.User));
    }

    [Fact]
    public async Task ALaterRejection_CancelsAnEarlierApproval_AndRequestsAreChecked()
    {
        var job = await OverdueJobAsync();
        var due = (await BothAsync(job.Id)).Vessel.NextDueDate!.Value;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(5), "  ", _s.User));            // a reason is needed
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, due, "Same day", _s.User));                     // later than now due
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddYears(2), "Too far", _s.User));
        var asked = await _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(10), "Weather", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RequestDeferralAsync(_s.CompanyId, job.Id, due.AddDays(11), "Again", _s.User));          // one at a time
        await _s.SettleUntilQuietAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideDeferralAsync(_s.CompanyId, asked.Id, true, due.AddDays(11), "", _s.User));          // not more than asked
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecideDeferralAsync(_s.CompanyId, asked.Id, true, due.AddDays(-1), "", _s.User));           // after the current due date

        // The Master approves onboard; the office, not yet having seen that, refuses. The later decision counts, and both stay in the history.
        await _vessel.DecideDeferralAsync(_s.CompanyId, asked.Id, true, null, "ok", MasterActor);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;
            db.JobDeferralDecisions.Add(new JobDeferralDecision { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, RequestId = asked.Id, Approved = false, Comment = "No",
                DecidedByName = "Office", DecidedByRole = "office", At = DateTime.UtcNow.AddSeconds(5), OwnerStage = SyncStages.Office });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync()) await PmsProjection.RefreshJobAsync(db, job.Id);
        Assert.Equal("calendar", (await BothAsync(job.Id)).Shore.DueBasis);
        Assert.Equal(2, (await _shore.DeferralsOfJobAsync(_s.CompanyId, job.Id)).Single().AllDecisions.Count);
    }

    [Fact]
    public void ARequestOnACriticalJob_AndItsDecision_TravelOnTheUrgentLane()
    {
        Assert.Equal(Lane.Urgent, Chandlery.Services.Sync.SyncPublisher.LaneFor(new JobDeferralRequest { IsCritical = true }));
        Assert.Equal(Lane.Urgent, Chandlery.Services.Sync.SyncPublisher.LaneFor(new JobDeferralDecision { IsCritical = true }));
        Assert.Equal(Lane.Transaction, Chandlery.Services.Sync.SyncPublisher.LaneFor(new JobDeferralRequest()));
    }
}
