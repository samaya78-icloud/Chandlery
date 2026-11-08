using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// Planned maintenance in one story: the office builds a plan from the library, then the vessel and the office both work while apart (readings and a correction,
/// completions, deferrals, a plan change request, a defect, spares, a template update). When the link returns, both sides hold the same history, the same state
/// for every job and the same overdue counts; the office then decides what is waiting, and both sides agree again.
/// </summary>
public class PmsWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _vessel = null!, _shore = null!;
    private PmsIntegrationService _vesselLinks = null!, _shoreLinks = null!;
    private PmsReportService _vesselReports = null!, _shoreReports = null!;
    private WorklistService _vesselWorklist = null!, _shoreWorklist = null!;
    private CrewMember _master = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime Ago(int days) => Today.AddDays(-days);
    private Actor MasterActor => new(_master.UserId, _master.FullName);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new PmsService(_s.VesselFactory);
        _shore = new PmsService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselLinks = new PmsIntegrationService(_s.VesselFactory, _vessel, _vesselWorklist);
        _shoreLinks = new PmsIntegrationService(_s.ShoreFactory, _shore, _shoreWorklist);
        _vesselReports = new PmsReportService(_s.VesselFactory);
        _shoreReports = new PmsReportService(_s.ShoreFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Pms, PolicyModules.Worklist })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "CAPT8", "Capt. Walkthrough", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, false, IsMaster: true), _s.User);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, _master.Id, "Temp-pass-1", false, _s.User);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    /// <summary>Both sides hold exactly the same records, compared on the fields that matter.</summary>
    private async Task SameAsync<T>(string what, Func<ChandleryDbContext, IQueryable<T>> query, Func<T, object> shape) where T : class
    {
        var shore = (await OnShore(db => query(db).AsNoTracking().ToListAsync())).Select(shape).Select(x => x.ToString()).OrderBy(x => x).ToList();
        var vessel = (await OnVessel(db => query(db).AsNoTracking().ToListAsync())).Select(shape).Select(x => x.ToString()).OrderBy(x => x).ToList();
        Assert.True(shore.SequenceEqual(vessel), $"{what} differ between the sides.{Environment.NewLine}Office: {string.Join(" | ", shore)}{Environment.NewLine}Vessel: {string.Join(" | ", vessel)}");
    }

    private async Task AllRecordsAreTheSameAsync()
    {
        await SameAsync("Machinery", db => db.MachineryItems, m => (m.Id, m.Name, m.ParentId, m.OwnerStage, m.HasHourMeter, m.IsCritical));
        await SameAsync("Jobs", db => db.PlannedJobs, j => (j.Id, j.Code, j.Title, j.CalendarInterval, j.CalendarUnit, j.HoursInterval, j.IsCritical, j.TemplateVersion, j.OwnerStage, j.IsActive,
            // what each side worked out for itself
            j.NextDueDate, j.DueBasis, j.LastDoneDate, j.LastDoneHours, j.DeferredUntil, j.GraceUntil, j.HoursUnknown, j.HoursEstimated, j.LastReadingDate));
        await SameAsync("Readings", db => db.RunningHoursReadings, r => (r.Id, r.MachineryItemId, r.ReadingDate, r.Hours, r.IsCorrection, r.OwnerStage));
        await SameAsync("Completions", db => db.JobCompletions, c => (c.Id, c.PlannedJobId, c.DoneDate, c.HoursAtDone, c.PerformedBy, c.IsImported, c.OwnerStage));
        await SameAsync("Verifications", db => db.JobVerifications, v => (v.Id, v.CompletionId, v.VerifiedBy));
        await SameAsync("Deferral requests", db => db.JobDeferralRequests, r => (r.Id, r.PlannedJobId, r.RequestedDueDate, r.Reason, r.IsCritical));
        await SameAsync("Deferral decisions", db => db.JobDeferralDecisions, d => (d.Id, d.RequestId, d.Approved, d.ApprovedDueDate, d.DecidedByRole));
        await SameAsync("Plan change requests", db => db.PlanChangeRequests, r => (r.Id, r.Kind, r.PlannedJobId, r.Proposed));
        await SameAsync("Plan change decisions", db => db.PlanChangeDecisions, d => (d.Id, d.RequestId, d.Approved));
        await SameAsync("Spares", db => db.MachinerySpares, s => (s.Id, s.MachineryItemId, s.ItemId, s.Quantity, s.OwnerStage));
        await SameAsync("Worklist tasks", db => db.WorklistTasks, t => (t.Id, t.Code, t.Title, t.Priority));
    }

    /// <summary>A side's stored schedule is what the rules give for the records it holds.</summary>
    private static async Task ScheduleMatchesRecordsAsync(ChandleryDbContext db)
    {
        foreach (var job in await db.PlannedJobs.AsNoTracking().ToListAsync())
        {
            var completions = await db.JobCompletions.AsNoTracking().Where(c => c.PlannedJobId == job.Id).ToListAsync();
            var readings = await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == job.MachineryItemId).ToListAsync();
            var deferrals = await PmsProjection.DeferralsAsync(db, [job.Id]);
            var expected = PmsScheduler.Compute(PmsProjection.InputOf(job), completions.Select(PmsProjection.InputOf), readings.Select(PmsProjection.InputOf), deferrals[job.Id]);
            Assert.True((expected.NextDue, expected.DueBasis, expected.LastDoneDate, expected.DeferredTo, expected.GraceUntil) == (job.NextDueDate, job.DueBasis, job.LastDoneDate, job.DeferredUntil, job.GraceUntil),
                $"{job.Code}: the stored schedule is not what its records give.");
        }
    }

    private static JobEdit EditOf(PlannedJob j, int? calendar = null) => new(j.Title, j.Instructions, j.Type, calendar ?? j.CalendarInterval, j.CalendarUnit, j.HoursInterval, j.Anchor, j.GraceDays,
        j.IsCritical, j.DepartmentId, j.EstimatedMinutes, j.RequiresVerification, j.IsActive);

    [Fact]
    public async Task VesselAndOfficeWorkApart_AndEndWithTheSameHistory_Schedules_AndOverdueCounts()
    {
        // ---- linked: the office builds the plan from the library; the vessel adds its own ----
        var cls = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Main engine", null);
        var engineTemplate = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("ME-1", "Overhaul injectors", "Remove\nTest", PmsJobType.Overhaul, CalendarInterval: 6, HoursInterval: 500,
            IsCritical: true, MachineryClassId: cls.Id, RequiresVerification: true));
        var generatorTemplate = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("GN-1", "Check the generator", "Walk round", PmsJobType.Check, CalendarInterval: 1));
        var engine = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", MachineryClassId: cls.Id, HasHourMeter: true, IsCritical: true), _s.User);
        var generator = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Generator"), _s.User);
        var engineJob = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(engine.Id, engineTemplate.Id, StartDate: Ago(100), BaselineDoneDate: Ago(100), BaselineDoneHours: 1000), _s.User);
        var generatorJob = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(generator.Id, generatorTemplate.Id, StartDate: Ago(40)), _s.User);                 // due 10 days ago: overdue
        var officeJob = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(generator.Id, Code: "OF-1", Title: "Clean the filters", CalendarInterval: 3, StartDate: Ago(10)), _s.User);
        await _s.SettleUntilQuietAsync();

        var emergency = await _vessel.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Emergency generator"), _s.User);
        var emergencyJob = await _vessel.AddJobAsync(_s.CompanyId, new NewPlannedJob(emergency.Id, Code: "EG-1", Title: "Weekly start test", CalendarInterval: 1, CalendarUnit: PmsIntervalUnit.Weeks, StartDate: Ago(20)), _s.User);
        await _s.SettleUntilQuietAsync();
        await AllRecordsAreTheSameAsync();
        var emergencyDue = (await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == emergencyJob.Id))).NextDueDate!.Value;
        var generatorDue = (await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == generatorJob.Id))).NextDueDate!.Value;
        Assert.Equal((2, 0), ((await _shoreReports.FleetAsync(_s.CompanyId)).Single().Overdue, (await _shoreReports.FleetAsync(_s.CompanyId)).Single().CriticalOverdue));   // the generator check and the weekly test

        // ---- apart: nothing is sent between these steps ----
        // the vessel
        await _vessel.RecordReadingAsync(_s.CompanyId, engine.Id, Ago(30), 1100, false, "", _s.User);
        await _vessel.RecordReadingAsync(_s.CompanyId, engine.Id, Ago(1), 1700, false, "typed 1700 by mistake", _s.User);
        await Task.Delay(30);
        await _vessel.RecordReadingAsync(_s.CompanyId, engine.Id, Ago(1), 1400, true, "correction: the meter shows 1400", _s.User);
        var done = await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(engineJob.Id, Today, 1400, "Chief Engineer", 240, "Injectors tested at 350 bar"), _s.User);
        await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(officeJob.Id, Ago(1), PerformedBy: "Motorman"), _s.User);
        var emergencyAsk = await _vessel.RequestDeferralAsync(_s.CompanyId, emergencyJob.Id, emergencyDue.AddDays(14), "Heavy weather: the test cannot be run at sea", _s.User);
        await _vessel.DecideDeferralAsync(_s.CompanyId, emergencyAsk.Id, true, null, "Agreed", MasterActor);                                   // the Master approves within the limit
        var generatorAsk = await _vessel.RequestDeferralAsync(_s.CompanyId, generatorJob.Id, generatorDue.AddDays(10), "Part on order", _s.User);   // left for the office
        var change = await _vessel.RequestPlanChangeAsync(_s.CompanyId, new NewPlanChange(PlanChangeKind.JobInterval, engineJob.Id, null, new PlanChangeProposal(HoursInterval: 750), "The maker allows 750 hours"), _s.User);
        var defect = await _vesselLinks.RaiseDefectAsync(_s.CompanyId, engineJob.Id, null, "Fuel leak at injector 3", _s.User);
        await _vessel.AddSpareAsync(_s.CompanyId, emergency.Id, _s.ItemIds[0], 2, "Starter solenoid", _s.User);

        // the office, meanwhile
        await Task.Delay(30);
        await _shore.RecordReadingAsync(_s.CompanyId, engine.Id, Ago(5), 1300, false, "from the engineer's report", _s.User);
        await _shore.UpdateJobAsync(_s.CompanyId, officeJob.Id, EditOf(officeJob, calendar: 2), _s.User);                                        // the office shortens its own job, which the vessel has just done
        await _shore.AddSpareAsync(_s.CompanyId, engine.Id, _s.ItemIds[1], 3, "Injector nozzle", _s.User);
        await _shore.SaveJobTemplateAsync(_s.CompanyId, engineTemplate.Id, new TemplateInput("ME-1", "Overhaul injectors", "Remove\nTest\nRefit", PmsJobType.Overhaul, CalendarInterval: 6, HoursInterval: 600,
            IsCritical: true, MachineryClassId: cls.Id, RequiresVerification: true));                                                         // the library moves to version 2

        // ---- the link returns ----
        await _s.SettleUntilQuietAsync();

        await AllRecordsAreTheSameAsync();
        await OnShore(async db => { await ScheduleMatchesRecordsAsync(db); return 0; });
        await OnVessel(async db => { await ScheduleMatchesRecordsAsync(db); return 0; });

        // what the story should have produced, seen from either side
        foreach (var side in new[] { OnShore<List<PlannedJob>>, OnVessel<List<PlannedJob>> })
        {
            var jobs = (await side(db => db.PlannedJobs.AsNoTracking().ToListAsync())).ToDictionary(j => j.Code);
            Assert.Equal((Today, 1400m), (jobs["ME-1"].LastDoneDate, jobs["ME-1"].LastDoneHours));                                              // the completion, with the corrected hours behind it
            Assert.Equal("hours", jobs["ME-1"].DueBasis);
            Assert.Equal(Ago(1).AddMonths(2), jobs["OF-1"].NextDueDate);                                                                        // the vessel's completion and the office's new interval, both kept
            Assert.Equal(("deferred", emergencyDue.AddDays(14)), (jobs["EG-1"].DueBasis, jobs["EG-1"].NextDueDate));                              // the Master's approval
            Assert.Equal(PmsStatus.Overdue, PmsProjection.StatusOf(jobs["GN-1"], Today));                                                       // the request is still waiting
            Assert.Equal(PmsStatus.NotDue, PmsProjection.StatusOf(jobs["EG-1"], Today));
        }
        var readings = (await OnShore(db => db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == engine.Id).ToListAsync())).OrderBy(r => r.ReadingDate).ThenBy(r => r.At).ToList();
        Assert.Equal([1100m, 1300m, 1700m, 1400m], readings.Select(r => r.Hours));                                                              // every reading kept, the mistake and its correction
        Assert.Equal([1100m, 1300m, 1400m], PmsScheduler.EffectiveReadings(readings.Select(PmsProjection.InputOf)).Select(r => r.Hours));      // and the right figure is what counts

        // the same overdue counts on both sides, and the same things waiting
        var shoreRow = (await _shoreReports.FleetAsync(_s.CompanyId)).Single();
        var vesselRow = (await _vesselReports.FleetAsync(_s.CompanyId)).Single();
        Assert.Equal(shoreRow, vesselRow);
        Assert.Equal((4, 1, 0), (shoreRow.Jobs, shoreRow.Overdue, shoreRow.CriticalOverdue));                                                   // only the generator check is overdue
        Assert.Equal(1, (await _shoreReports.CountsAsync(_s.CompanyId))!.PendingDecisions);
        Assert.Equal(1, (await _vesselReports.CountsAsync(_s.CompanyId))!.PendingDecisions);
        Assert.Equal([generatorAsk.Id], (await _shore.PendingDeferralsAsync(_s.CompanyId)).Select(p => p.Request.Id));
        Assert.Equal([change.Id], (await _vessel.PlanChangesAsync(_s.CompanyId, pendingOnly: true)).Select(p => p.Request.Id));
        Assert.Equal([defect.Id], (await _shoreLinks.TasksForJobAsync(_s.CompanyId, engineJob.Id)).Select(t => t.Id));                           // the defect on the vessel's worklist reached the office
        Assert.Equal(2, (await _vessel.ListSparesAsync(_s.CompanyId, _s.VesselId)).Count);
        Assert.Equal(["ME-1"], (await _shore.TemplateUpdatesAsync(_s.CompanyId)).Select(u => u.Job.Code));                                      // the new library version is proposed, not forced
        Assert.False((await _vessel.TemplateUpdatesAsync(_s.CompanyId)).Single().CanApplyHere);

        // ---- the office decides what is waiting ----
        await _shore.VerifyCompletionAsync(_s.CompanyId, done.Id, "Seen the test sheet", _s.User);
        await _shore.DecideDeferralAsync(_s.CompanyId, generatorAsk.Id, false, null, "Do it this week", _s.User);
        await _shore.DecidePlanChangeAsync(_s.CompanyId, change.Id, true, "Agreed", _s.User);
        await _shore.ApplyTemplateUpdatesAsync(_s.CompanyId, [engineJob.Id], _s.User);                                                          // the office takes the new version for its job
        await _vessel.RecordReadingAsync(_s.CompanyId, engine.Id, Today, 1450, false, "", _s.User);                                             // and the vessel carries on meanwhile
        await _s.SettleUntilQuietAsync();

        await AllRecordsAreTheSameAsync();
        await OnShore(async db => { await ScheduleMatchesRecordsAsync(db); return 0; });
        await OnVessel(async db => { await ScheduleMatchesRecordsAsync(db); return 0; });
        foreach (var side in new[] { OnShore<PlannedJob>, OnVessel<PlannedJob> })
        {
            var job = await side(db => db.PlannedJobs.AsNoTracking().SingleAsync(j => j.Id == engineJob.Id));
            Assert.Equal((2, 600, "Overhaul injectors"), (job.TemplateVersion!.Value, job.HoursInterval!.Value, job.Title));                          // the template's version 2 won over the plan change, applied last
        }
        Assert.Contains(done.Id, (await _vessel.GetJobAsync(_s.CompanyId, engineJob.Id))!.VerifiedCompletionIds);                              // the sign-off reached the vessel
        Assert.Equal(PmsStatus.Overdue, PmsProjection.StatusOf((await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == generatorJob.Id))), Today));   // a refused request leaves the job overdue
        Assert.Empty(await _vessel.PendingDeferralsAsync(_s.CompanyId));
        Assert.Empty(await _shore.PlanChangesAsync(_s.CompanyId, pendingOnly: true));
        Assert.Empty(await _shore.TemplateUpdatesAsync(_s.CompanyId));
        Assert.Equal(await _shoreReports.FleetAsync(_s.CompanyId) is { } a ? a.Single() : null, (await _vesselReports.FleetAsync(_s.CompanyId)).Single());

        // nothing is left waiting or parked
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
        Assert.Equal(0, await _s.DirtyAsync(_s.ShoreFactory));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }
}
