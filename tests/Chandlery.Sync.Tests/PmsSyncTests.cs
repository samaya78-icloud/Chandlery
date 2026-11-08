using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The maintenance plan on a real office and a real vessel: who owns what, what travels, and both sides working out the same schedule.</summary>
public class PmsSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _vessel = null!, _shore = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime Ago(int days) => Today.AddDays(-days);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new PmsService(_s.VesselFactory);
        _shore = new PmsService(_s.ShoreFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync(params Guid[] vessels)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var vessel in vessels.Length == 0 ? [_s.VesselId] : vessels)
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.Pms, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    /// <summary>The office's plan: a main engine with an hour meter and a job on it (every 6 months or 500 hours, last done 100 days ago at 1000 hours).</summary>
    private async Task<(MachineryItem Item, PlannedJob Job, JobTemplate Template)> OfficePlanAsync(Guid? vesselId = null)
    {
        var cls = await OnShore(db => db.MachineryClasses.FirstOrDefaultAsync(c => c.Name == "Main engine")) ?? await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Main engine", null);
        var template = await OnShore(db => db.JobTemplates.FirstOrDefaultAsync(t => t.Code == "ME-101")) ?? await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("ME-101", "Overhaul fuel injectors", "Remove\nTest\nRefit", PmsJobType.Overhaul,
            CalendarInterval: 6, HoursInterval: 500, IsCritical: true, MachineryClassId: cls.Id, RequiresVerification: true));
        var item = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(vesselId ?? _s.VesselId, "Main engine", "ME", MachineryClassId: cls.Id, IsCritical: true, HasHourMeter: true), _s.User);
        var job = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, template.Id, StartDate: Ago(100), BaselineDoneDate: Ago(100), BaselineDoneHours: 1000), _s.User);
        return (item, job, template);
    }

    /// <summary>The invariant behind everything: a side's stored schedule is what the rules give for the records it holds.</summary>
    private static async Task AssertScheduleMatchesRecordsAsync(ChandleryDbContext db, Guid jobId)
    {
        var job = await db.PlannedJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        var completions = await db.JobCompletions.AsNoTracking().Where(c => c.PlannedJobId == jobId).ToListAsync();
        var readings = await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == job.MachineryItemId).ToListAsync();
        var expected = PmsScheduler.Compute(PmsProjection.InputOf(job), completions.Select(PmsProjection.InputOf), readings.Select(PmsProjection.InputOf));
        Assert.Equal((expected.NextDue, expected.DueBasis, expected.LastDoneDate, expected.LastDoneHours, expected.HoursEstimated, expected.HoursUnknown, expected.GraceUntil),
                     (job.NextDueDate, job.DueBasis, job.LastDoneDate, job.LastDoneHours, job.HoursEstimated, job.HoursUnknown, job.GraceUntil));
    }

    private SyncDocument Doc(ChandleryDbContext db, object root, string docType, string stage, long version = 1)
        => Chandlery.Services.Sync.Aggregates.Document(Chandlery.Services.Sync.Aggregates.Find(docType)!, (Guid)db.Entry(root).Property("Id").CurrentValue!, version,
            Chandlery.Services.Sync.Aggregates.ToJson(db, root), stage, 1, null);

    [Fact]
    public async Task TheModuleIsOffUntilTheOfficeSwitchesItOn()
    {
        var item = new NewMachineryItem(_s.VesselId, "Main engine");
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddMachineryItemAsync(_s.CompanyId, item, _s.User));
        Assert.Contains("not switched on", off.Message);
        await SwitchOnAsync();
        Assert.NotNull(await _vessel.AddMachineryItemAsync(_s.CompanyId, item, _s.User));
    }

    [Fact]
    public async Task TheLibraryReachesTheVessel_ReadOnly()
    {
        await SwitchOnAsync();
        var (_, _, template) = await OfficePlanAsync();
        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.JobTemplates.SingleAsync(t => t.Id == template.Id));
        Assert.Equal(("ME-101", 1, 500, 6), (onboard.Code, onboard.Version, onboard.HoursInterval, onboard.CalendarInterval));
        Assert.True(await OnVessel(db => db.MachineryClasses.AnyAsync(c => c.Name == "Main engine")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveJobTemplateAsync(_s.CompanyId, template.Id, new TemplateInput("ME-101", "Changed onboard", CalendarInterval: 1)));

        // A change at the office raises the version and reaches the vessel.
        await _shore.SaveJobTemplateAsync(_s.CompanyId, template.Id, new TemplateInput("ME-101", "Overhaul fuel injectors", CalendarInterval: 12, HoursInterval: 750));
        await _s.SettleUntilQuietAsync();
        Assert.Equal((2, 750), (await OnVessel(db => db.JobTemplates.SingleAsync(t => t.Id == template.Id))) is { } t ? (t.Version, t.HoursInterval) : default);
    }

    [Fact]
    public async Task TheOfficesPlanReachesTheVessel_WithTheSameSchedule_AndTheVesselCannotEditIt()
    {
        await SwitchOnAsync();
        var (item, job, _) = await OfficePlanAsync();
        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id));
        var atOffice = await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id));
        Assert.Equal(SyncStages.Office, onboard.OwnerStage);
        Assert.Equal((atOffice.NextDueDate, atOffice.DueBasis, atOffice.Title, atOffice.TemplateVersion), (onboard.NextDueDate, onboard.DueBasis, onboard.Title, onboard.TemplateVersion));
        Assert.Equal(Ago(100).AddMonths(6), onboard.NextDueDate);                                         // no readings yet: the calendar decides
        Assert.True(await OnVessel(db => db.MachineryItems.AnyAsync(m => m.Id == item.Id && m.HasHourMeter)));

        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        var held = await db.PlannedJobs.SingleAsync(j => j.Id == job.Id);
        held.Title = "Changed onboard";
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("belongs to the office", refused.Message);
    }

    [Fact]
    public async Task ReadingsAndCompletionsMadeOnboard_ReachTheOffice_AndBothSidesWorkOutTheSameSchedule()
    {
        await SwitchOnAsync();
        var (item, job, _) = await OfficePlanAsync();
        await _s.SettleUntilQuietAsync();

        await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Ago(30), 1100, false, "", _s.User);
        await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Today, 1400, false, "", _s.User);
        await _s.SettleUntilQuietAsync();

        foreach (var side in new[] { OnVessel<PlannedJob>, OnShore<PlannedJob> })
        {
            var j = await side(db => db.PlannedJobs.SingleAsync(x => x.Id == job.Id));
            Assert.Equal((Today.AddDays(10), "hours", true), (j.NextDueDate, j.DueBasis, j.HoursEstimated));     // 10 hours a day, 100 hours to go
        }

        await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Today, 1400, "Chief Engineer", 240, "Injectors tested at 350 bar"), _s.User);
        await _s.SettleUntilQuietAsync();
        foreach (var side in new[] { OnVessel<PlannedJob>, OnShore<PlannedJob> })
        {
            var j = await side(db => db.PlannedJobs.SingleAsync(x => x.Id == job.Id));
            Assert.Equal((Today, 1400m, Today.AddDays(50)), (j.LastDoneDate, j.LastDoneHours, j.NextDueDate));  // 500 hours at 10 a day
        }
        await OnVessel(async db => { await AssertScheduleMatchesRecordsAsync(db, job.Id); return 0; });
        await OnShore(async db => { await AssertScheduleMatchesRecordsAsync(db, job.Id); return 0; });
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ACorrectedReading_ChangesTheScheduleOnBothSides_AndTheOriginalStays()
    {
        await SwitchOnAsync();
        var (item, job, _) = await OfficePlanAsync();
        await _s.SettleUntilQuietAsync();
        await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Ago(30), 1100, false, "", _s.User);
        var wrong = await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Today, 1900, false, "typo", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(Today, (await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id))).NextDueDate);   // 1900 would mean the interval is already reached

        // Hours cannot go down; a mistake is corrected, not overwritten.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Today.AddDays(0), 1400, false, "", _s.User));
        await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Today, 1400, true, "was 1900", _s.User);
        await _s.SettleUntilQuietAsync();

        foreach (var side in new[] { OnVessel<PlannedJob>, OnShore<PlannedJob> })
            Assert.Equal(Today.AddDays(10), (await side(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id))).NextDueDate);
        Assert.Equal(1900m, (await OnShore(db => db.RunningHoursReadings.SingleAsync(r => r.Id == wrong.Id))).Hours);   // the wrong reading is still there
        Assert.Equal(3, await OnShore(db => db.RunningHoursReadings.CountAsync()));
    }

    [Fact]
    public async Task History_CannotBeChanged_Locally_OrThroughSync()
    {
        await SwitchOnAsync();
        var (item, job, _) = await OfficePlanAsync();
        await _s.SettleUntilQuietAsync();
        var done = await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Ago(1), Comments: "original"), _s.User);
        var reading = await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Ago(1), 1200, false, "", _s.User);
        await _s.SettleUntilQuietAsync();

        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            (await db.JobCompletions.SingleAsync(c => c.Id == done.Id)).Comments = "rewritten";
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("history", refused.Message);
        }
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.RunningHoursReadings.Remove(await db.RunningHoursReadings.SingleAsync(r => r.Id == reading.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        // A newer, altered copy of the completion arriving through sync is ignored.
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            var json = Chandlery.Services.Sync.Aggregates.ToJson(db, await db.JobCompletions.SingleAsync(c => c.Id == done.Id));
            json["Comments"] = "rewritten through sync";
            await _s.Agent.PublishAsync(Chandlery.Services.Sync.Aggregates.Document(Chandlery.Services.Sync.Aggregates.Find(nameof(JobCompletion))!, done.Id, 9, json, SyncStages.Vessel, 99, null), Lane.Transaction);
        }
        await _s.SettleUntilQuietAsync();
        Assert.Equal("original", (await OnShore(db => db.JobCompletions.SingleAsync(c => c.Id == done.Id))).Comments);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AVesselsOwnPlan_ReachesTheOffice_WhichCannotEditIt_ButEitherSideCanRecordWorkOnIt()
    {
        await SwitchOnAsync();
        var item = await _vessel.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Emergency generator", "EG", HasHourMeter: true), _s.User);
        var job = await _vessel.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "EG-01", Title: "Weekly start test", Type: PmsJobType.Test, CalendarInterval: 1, CalendarUnit: PmsIntervalUnit.Weeks, StartDate: Ago(10)), _s.User);
        await _s.SettleUntilQuietAsync();

        var atOffice = await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id));
        Assert.Equal((SyncStages.Vessel, "Weekly start test", Ago(10).AddDays(7)), (atOffice.OwnerStage, atOffice.Title, atOffice.NextDueDate));

        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.PlannedJobs.SingleAsync(j => j.Id == job.Id)).Title = "Changed ashore";
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("belongs to the vessel", refused.Message);
        }

        await _shore.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Ago(2), PerformedBy: "Superintendent (visit)"), _s.User);   // the office records work it saw
        await _s.SettleUntilQuietAsync();
        Assert.Equal(Ago(2).AddDays(7), (await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id))).NextDueDate);
    }

    [Fact]
    public async Task AJobWithASecondSignOff_IsVerifiedOnce_AndTheVerificationTravels()
    {
        await SwitchOnAsync();
        var (_, job, _) = await OfficePlanAsync();
        await _s.SettleUntilQuietAsync();
        var done = await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(job.Id, Ago(1)), _s.User);
        await _s.SettleUntilQuietAsync();

        await _shore.VerifyCompletionAsync(_s.CompanyId, done.Id, "checked the test sheet", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.VerifyCompletionAsync(_s.CompanyId, done.Id, "again", _s.User));
        await _s.SettleUntilQuietAsync();
        Assert.Single(await OnVessel(db => db.JobVerifications.Where(v => v.CompletionId == done.Id).ToListAsync()));
        Assert.Contains(done.Id, (await _vessel.GetJobAsync(_s.CompanyId, job.Id))!.VerifiedCompletionIds);
    }

    [Fact]
    public async Task RecordsArrivingBeforeWhatTheyBelongTo_WaitForIt_InsteadOfBeingParked()
    {
        await SwitchOnAsync();
        Guid jobId, completionId;
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;                                                                         // saved without notes: this test sends the documents itself, worst order first
            var item = new MachineryItem { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Name = "Boiler", HasHourMeter = true, OwnerStage = SyncStages.Vessel, CreatedAt = DateTime.UtcNow };
            var job = new PlannedJob { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, MachineryItemId = item.Id, Code = "B-1", Title = "Clean burner", CalendarInterval = 3, StartDate = Ago(200), OwnerStage = SyncStages.Vessel, CreatedAt = DateTime.UtcNow };
            var completion = new JobCompletion { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, PlannedJobId = job.Id, DoneDate = Ago(20), AuthorName = "Crew", At = DateTime.UtcNow, OwnerStage = SyncStages.Vessel };
            var verification = new JobVerification { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, CompletionId = completion.Id, VerifiedBy = "Chief", At = DateTime.UtcNow, OwnerStage = SyncStages.Vessel };
            db.AddRange(item, job, completion, verification);
            await db.SaveChangesAsync();
            (jobId, completionId) = (job.Id, completion.Id);

            await _s.Agent.PublishAsync(Doc(db, verification, nameof(JobVerification), SyncStages.Vessel), Lane.Transaction);
            await _s.Agent.PublishAsync(Doc(db, completion, nameof(JobCompletion), SyncStages.Vessel), Lane.Transaction);
            await _s.Agent.PublishAsync(Doc(db, job, nameof(PlannedJob), SyncStages.Vessel), Lane.Transaction);
            await _s.Agent.PublishAsync(Doc(db, item, nameof(MachineryItem), SyncStages.Vessel), Lane.Transaction);
        }
        await _s.SettleUntilQuietAsync();

        Assert.True(await OnShore(db => db.JobVerifications.AnyAsync(v => v.CompletionId == completionId)));
        Assert.Equal(Ago(20).AddMonths(3), (await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == jobId))).NextDueDate);     // worked out once the completion was there
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AnotherVesselsPlan_NeverReachesThisVessel()
    {
        await SwitchOnAsync(_s.VesselId, _s.OtherVesselId);
        var (otherItem, otherJob, _) = await OfficePlanAsync(_s.OtherVesselId);
        var (ownItem, _, _) = await OfficePlanAsync(_s.VesselId);
        await _s.SettleUntilQuietAsync();

        Assert.True(await OnVessel(db => db.MachineryItems.AnyAsync(m => m.Id == ownItem.Id)));
        Assert.False(await OnVessel(db => db.MachineryItems.AnyAsync(m => m.Id == otherItem.Id)));
        Assert.False(await OnVessel(db => db.PlannedJobs.AnyAsync(j => j.Id == otherJob.Id)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AVesselStartedFromNothing_ReceivesThePlanAndTheTasksAlreadyThere_WithTheirSchedules()
    {
        await SwitchOnAsync();
        Guid childId, jobId, taskId;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;                                                                         // existed before this vessel connected: nothing announced
            var parent = new MachineryItem { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Name = "Engine room", OwnerStage = SyncStages.Office, CreatedAt = DateTime.UtcNow };
            var child = new MachineryItem { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, ParentId = parent.Id, Name = "Main engine", HasHourMeter = true, OwnerStage = SyncStages.Office, CreatedAt = DateTime.UtcNow };
            var job = new PlannedJob { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, MachineryItemId = child.Id, Code = "ME-1", Title = "Oil sample", CalendarInterval = 3, HoursInterval = 500, StartDate = Ago(60), OwnerStage = SyncStages.Office, CreatedAt = DateTime.UtcNow };
            var readings = new[]
            {
                new RunningHoursReading { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, MachineryItemId = child.Id, ReadingDate = Ago(30), Hours = 5000, AuthorName = "Chief", At = DateTime.UtcNow, OwnerStage = SyncStages.Office },
                new RunningHoursReading { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, MachineryItemId = child.Id, ReadingDate = Today, Hours = 5300, AuthorName = "Chief", At = DateTime.UtcNow, OwnerStage = SyncStages.Office },
            };
            var completion = new JobCompletion { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, PlannedJobId = job.Id, DoneDate = Ago(30), HoursAtDone = 5000, AuthorName = "Chief", At = DateTime.UtcNow, OwnerStage = SyncStages.Office };
            db.AddRange(parent, child, job, completion);
            db.AddRange(readings);
            await db.SaveChangesAsync();
            await PmsProjection.RefreshJobAsync(db, job.Id);
            (childId, jobId) = (child.Id, job.Id);

            var task = new WorklistTask { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Code = "TEST-WL-9", CreatedByName = "Office", CreatedAt = DateTime.UtcNow, OwnerStage = SyncStages.Office };
            var created = new WorklistEntry { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, TaskId = task.Id, Type = WorklistEntryType.Created, AuthorName = "Office", At = DateTime.UtcNow, OwnerStage = SyncStages.Office, Payload = WorklistProjection.Json(("title", "Existing task")) };
            db.AddRange(task, created);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }
        Assert.False(await OnVessel(db => db.PlannedJobs.AnyAsync(j => j.Id == jobId)));

        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        Assert.True(await OnVessel(db => db.MachineryItems.AnyAsync(m => m.Id == childId && m.ParentId != null)));
        var atShore = await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == jobId));
        var onboard = await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == jobId));
        Assert.NotNull(onboard.NextDueDate);
        Assert.Equal((atShore.NextDueDate, atShore.DueBasis), (onboard.NextDueDate, onboard.DueBasis));
        Assert.Equal(2, await OnVessel(db => db.RunningHoursReadings.CountAsync()));
        Assert.Equal("Existing task", (await OnVessel(db => db.WorklistTasks.SingleAsync(t => t.Id == taskId))).Title);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }
}
