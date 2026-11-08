using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class PmsDriftTests
{
    private static MachineryItem Item(string name, MachineryItem? parent = null) => new() { Id = Guid.NewGuid(), Name = name, ParentId = parent?.Id };
    private static PlannedJob Job(MachineryItem item, string code, int months = 6, bool critical = false, int? version = null)
        => new() { Id = Guid.NewGuid(), MachineryItemId = item.Id, Code = code, Title = code + " job", CalendarInterval = months, CalendarUnit = PmsIntervalUnit.Months, IsCritical = critical, TemplateVersion = version };

    [Fact]
    public void MachineryIsMatchedByItsNameAlongTheTree_AndJobsByTheirCode()
    {
        var refRoom = Item("Engine room"); var refEngine = Item("Main engine", refRoom); var refPump = Item("Fire pump", refRoom);
        var room = Item("engine room"); var engine = Item("Main Engine", room); var generator = Item("Generator", room);
        var refJobs = new[] { Job(refEngine, "ME-1", 6), Job(refEngine, "ME-2", 12), Job(refPump, "FP-1") };
        var jobs = new[] { Job(engine, "ME-1", 3), Job(engine, "ME-3"), Job(generator, "GN-1") };

        var rows = PmsDrift.Compare([refRoom, refEngine, refPump], refJobs, [room, engine, generator], jobs);

        Assert.Contains(rows, r => r.Kind == DriftKind.Different && r.JobCode == "ME-1" && r.Detail.Contains("3 months here, 6 months on the reference"));
        Assert.Contains(rows, r => r.Kind == DriftKind.MissingHere && r.JobCode == "ME-2");                                       // the reference has it, this vessel does not
        Assert.Contains(rows, r => r.Kind == DriftKind.ExtraHere && r.JobCode == "ME-3");                                         // only this vessel has it
        Assert.Contains(rows, r => r.Kind == DriftKind.MissingHere && r.Machinery == "Engine room › Fire pump" && r.JobCode == "");
        Assert.Contains(rows, r => r.Kind == DriftKind.ExtraHere && r.Machinery == "engine room › Generator");
        Assert.Equal(5, rows.Count);                                                                                            // case-insensitive names matched, nothing else reported
        Assert.Empty(PmsDrift.Compare([refRoom, refEngine], [Job(refEngine, "ME-1")], [room, engine], [Job(engine, "me-1")]));  // codes match too
    }

    [Fact]
    public void CriticalAndLibraryVersionDifferencesAreReported()
    {
        var a = Item("Boiler"); var b = Item("Boiler");
        var rows = PmsDrift.Compare([a], [Job(a, "B-1", critical: true, version: 2)], [b], [Job(b, "B-1", critical: false, version: 1)]);
        var row = Assert.Single(rows);
        Assert.Contains("critical on the reference only", row.Detail);
        Assert.Contains("library version 1 here, 2 on the reference", row.Detail);
    }
}

/// <summary>Spares, defects and sister vessels on a real office and a real vessel.</summary>
public class PmsLinkTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _vessel = null!, _shore = null!;
    private PmsIntegrationService _vesselLinks = null!, _shoreLinks = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime Ago(int days) => Today.AddDays(-days);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new PmsService(_s.VesselFactory);
        _shore = new PmsService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselLinks = new PmsIntegrationService(_s.VesselFactory, _vessel, _vesselWorklist);
        _shoreLinks = new PmsIntegrationService(_s.ShoreFactory, _shore, _shoreWorklist);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync(string module, params Guid[] vessels)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var vessel in vessels.Length == 0 ? [_s.VesselId] : vessels)
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<(MachineryItem Item, PlannedJob Job)> PlanAsync(bool critical = false)
    {
        var item = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true, IsCritical: critical), _s.User);
        var job = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "ME-1", Title: "Overhaul injectors", CalendarInterval: 6, IsCritical: critical, StartDate: Ago(30)), _s.User);
        await _s.SettleUntilQuietAsync();
        return (item, job);
    }

    // ---- spares ----

    [Fact]
    public async Task Spares_AreKeptByTheSideThatAddedThem_AndReachTheOtherSide()
    {
        await SwitchOnAsync(PolicyModules.Pms);
        var (item, _) = await PlanAsync();
        var spare = await _shore.AddSpareAsync(_s.CompanyId, item.Id, _s.ItemIds[0], 2, "Injector nozzle", _s.User);
        await _s.SettleUntilQuietAsync();

        var onboard = Assert.Single(await _vessel.ListSparesAsync(_s.CompanyId, _s.VesselId));
        Assert.Equal((spare.Id, 2m, "Injector nozzle"), (onboard.Spare.Id, onboard.Spare.Quantity, onboard.Spare.Note));
        Assert.False(string.IsNullOrEmpty(onboard.ItemName));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SetSpareQuantityAsync(_s.CompanyId, spare.Id, 0));
        Assert.Contains("belongs to the office", refused.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddSpareAsync(_s.CompanyId, item.Id, _s.ItemIds[0], 1, "", _s.User));    // already on the list
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddSpareAsync(_s.CompanyId, item.Id, _s.ItemIds[1], 0, "", _s.User));    // nothing to order
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddSpareAsync(_s.CompanyId, item.Id, Guid.NewGuid(), 1, "", _s.User));    // not in the catalog

        // The vessel keeps spares for its own machinery; removing one removes it at the other side too.
        var own = await _vessel.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Emergency generator"), _s.User);
        var ownSpare = await _vessel.AddSpareAsync(_s.CompanyId, own.Id, _s.ItemIds[1], 4, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, (await _shore.ListSparesAsync(_s.CompanyId, _s.VesselId)).Count);
        await _vessel.SetSpareQuantityAsync(_s.CompanyId, ownSpare.Id, 6);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(6m, (await OnShore(db => db.MachinerySpares.SingleAsync(x => x.Id == ownSpare.Id))).Quantity);
        await _vessel.SetSpareQuantityAsync(_s.CompanyId, ownSpare.Id, 0);
        await _s.SettleUntilQuietAsync();
        Assert.Single(await _shore.ListSparesAsync(_s.CompanyId, _s.VesselId));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ARequisitionIsFilledInFromAJob_WithItsSparesAsLines_AndCriticalMeansUrgent()
    {
        await SwitchOnAsync(PolicyModules.Pms);
        var (item, job) = await PlanAsync(critical: true);
        var other = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "ME-2", Title: "Check", CalendarInterval: 1, StartDate: Ago(5)), _s.User);
        Assert.Equal(0, (await _shoreLinks.RequisitionDraftAsync(_s.CompanyId, job.Id))!.SpareLines);                                        // no spares yet: a header only

        await _shore.AddSpareAsync(_s.CompanyId, item.Id, _s.ItemIds[0], 2, "Nozzle", _s.User);
        await _shore.AddSpareAsync(_s.CompanyId, item.Id, _s.ItemIds[1], 5, "", _s.User);
        await _s.SettleUntilQuietAsync();

        foreach (var links in new[] { _shoreLinks, _vesselLinks })
        {
            var draft = (await links.RequisitionDraftAsync(_s.CompanyId, job.Id))!;
            var r = draft.Requisition;
            Assert.Equal((RfqType.Spares, _s.VesselId, Urgency.Urgent, 2), (r.Type, r.VesselId, r.Urgency, draft.SpareLines));
            Assert.Contains("ME-1", r.Remarks);
            Assert.Equal([(_s.ItemIds[0], 2m, "Nozzle"), (_s.ItemIds[1], 5m, "")], r.Lines.Select(l => (l.ItemId, l.Quantity, l.Description)));
        }
        var ordinary = (await _shoreLinks.RequisitionDraftAsync(_s.CompanyId, other.Id))!;
        Assert.Equal(Urgency.Normal, ordinary.Requisition.Urgency);                                                                      // jobs on the same machinery share its spares
        Assert.Null(await _shoreLinks.RequisitionDraftAsync(_s.CompanyId, Guid.NewGuid()));
    }

    // ---- defects to the worklist ----

    [Fact]
    public async Task ADefectFoundOnAJob_RaisesAWorklistTask_LinkedToTheJobOnBothSides()
    {
        await SwitchOnAsync(PolicyModules.Pms);
        var (_, job) = await PlanAsync(critical: true);

        // The worklist is off: it says so, and nothing is created.
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselLinks.RaiseDefectAsync(_s.CompanyId, job.Id, null, "Leaking", _s.User));
        Assert.Contains("worklist is not switched on", off.Message);

        await SwitchOnAsync(PolicyModules.Worklist);
        var task = await _vesselLinks.RaiseDefectAsync(_s.CompanyId, job.Id, null, "Fuel leak at injector 3", _s.User);
        Assert.Equal(("Defect on Main engine: Overhaul injectors", WorklistPriority.Urgent), (task.Title, task.Priority));                  // critical job: urgent task
        Assert.Equal("Vessel", (await OnVessel(db => db.WorklistListItems.SingleAsync(i => i.Id == task.SourceId))).Name);
        await _s.SettleUntilQuietAsync();

        foreach (var links in new[] { _vesselLinks, _shoreLinks })
            Assert.Equal([task.Id], (await links.TasksForJobAsync(_s.CompanyId, job.Id)).Select(t => t.Id));
        var link = WorklistService.LinksOf((await _shoreWorklist.GetAsync(_s.CompanyId, task.Id))!.Entries).Single();
        Assert.Equal((WorklistLinkTypes.PmsJob, job.Id, $"/pms/jobs/{job.Id}"), (link.Type, link.Target, WorklistLinkTypes.Href(link.Type, link.Target, onVessel: true)));
        Assert.Equal("Fuel leak at injector 3", (await OnShore(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id))).Description);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    // ---- sister vessels ----

    private async Task<(MachineryItem Room, MachineryItem Engine, PlannedJob TemplateJob, PlannedJob OwnJob)> SourcePlanAsync()
    {
        var cls = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Main engine", null);
        var template = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("ME-5", "Lube oil analysis", "Take sample", PmsJobType.Test, CalendarInterval: 6, MachineryClassId: cls.Id));
        var room = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Engine room"), _s.User);
        var engine = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", ParentId: room.Id, HasHourMeter: true, IsCritical: true, MachineryClassId: cls.Id, Maker: "MAN"), _s.User);
        var fromTemplate = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(engine.Id, template.Id, StartDate: Ago(40)), _s.User);
        var own = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(engine.Id, Code: "ME-9", Title: "Annual survey prep", CalendarInterval: 12, Anchor: PmsAnchor.FixedCalendar,
            FixedAnchorDate: new DateTime(Today.Year + 1, 3, 1, 0, 0, 0, DateTimeKind.Utc), HoursInterval: 4000, IsCritical: true, StartDate: Ago(10)), _s.User);
        await _shore.AddSpareAsync(_s.CompanyId, engine.Id, _s.ItemIds[0], 3, "Filter", _s.User);
        await _shore.SaveJobTemplateAsync(_s.CompanyId, template.Id, new TemplateInput("ME-5", "Lube oil analysis", "Take sample\nSend to lab", PmsJobType.Test, CalendarInterval: 3, MachineryClassId: cls.Id));   // the library has moved on to version 2
        return (room, engine, fromTemplate, own);
    }

    [Fact]
    public async Task ThePlanIsClonedToASisterVessel_WithTheSourcesDefinitions_AndCanBeRepeatedSafely()
    {
        await SwitchOnAsync(PolicyModules.Pms, _s.VesselId, _s.OtherVesselId);
        var (_, engine, templateJob, ownJob) = await SourcePlanAsync();

        var results = await _shoreLinks.ClonePlanAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId, _s.VesselId], includeSpares: true, _s.User);
        var result = Assert.Single(results);                                                                                       // the source itself is ignored
        Assert.Equal((2, 2, 1, null), (result.ItemsAdded, result.JobsAdded, result.SparesAdded, result.Problem));

        var items = await OnShore(db => db.MachineryItems.Where(m => m.VesselId == _s.OtherVesselId).ToListAsync());
        var copiedEngine = items.Single(i => i.Name == "Main engine");
        Assert.Equal(items.Single(i => i.Name == "Engine room").Id, copiedEngine.ParentId);                                          // the tree is kept
        Assert.Equal(("MAN", true, true, SyncStages.Office), (copiedEngine.Maker, copiedEngine.HasHourMeter, copiedEngine.IsCritical, copiedEngine.OwnerStage));
        Assert.NotEqual(engine.Id, copiedEngine.Id);

        var jobs = await OnShore(db => db.PlannedJobs.Where(j => j.VesselId == _s.OtherVesselId).ToListAsync());
        var copiedTemplateJob = jobs.Single(j => j.Code == "ME-5");
        Assert.Equal((templateJob.TemplateId, 1, 6, "Lube oil analysis", Today), (copiedTemplateJob.TemplateId, copiedTemplateJob.TemplateVersion!.Value, copiedTemplateJob.CalendarInterval!.Value, copiedTemplateJob.Title, copiedTemplateJob.StartDate));
        Assert.Equal(Today.AddMonths(6), copiedTemplateJob.NextDueDate);                                                            // counts from today on the new vessel
        var copiedOwn = jobs.Single(j => j.Code == "ME-9");
        Assert.Equal((PmsAnchor.FixedCalendar, ownJob.FixedAnchorDate, 4000, true), (copiedOwn.Anchor, copiedOwn.FixedAnchorDate, copiedOwn.HoursInterval!.Value, copiedOwn.IsCritical));
        var spare = await OnShore(db => db.MachinerySpares.SingleAsync(x => x.VesselId == _s.OtherVesselId));
        Assert.Equal((copiedEngine.Id, 3m, "Filter"), (spare.MachineryItemId, spare.Quantity, spare.Note));

        // The copy shows up as a template update (it carries version 1 while the library is at 2), so nothing is silently upgraded.
        Assert.Equal(["ME-5"], (await _shore.TemplateUpdatesAsync(_s.CompanyId, _s.OtherVesselId)).Select(u => u.Job.Code));

        // Repeating adds nothing; a target that already has some of it keeps what it has.
        var again = Assert.Single(await _shoreLinks.ClonePlanAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId], true, _s.User));
        Assert.Equal((0, 0, 0), (again.ItemsAdded, again.JobsAdded, again.SparesAdded));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task CloningNeedsTheModuleOnForBoth_AndIsTheOfficesToDo()
    {
        await SwitchOnAsync(PolicyModules.Pms);
        await SourcePlanAsync();
        var off = Assert.Single(await _shoreLinks.ClonePlanAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId], false, _s.User));
        Assert.Equal((0, 0, 0), (off.ItemsAdded, off.JobsAdded, off.SparesAdded));
        Assert.Contains("off for this vessel", off.Problem);
        Assert.Empty(await OnShore(db => db.MachineryItems.Where(m => m.VesselId == _s.OtherVesselId).ToListAsync()));

        await _s.SettleUntilQuietAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselLinks.ClonePlanAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId], false, _s.User));
    }

    [Fact]
    public async Task TheDriftReport_ShowsHowASisterVesselHasMovedAwayFromTheReference()
    {
        await SwitchOnAsync(PolicyModules.Pms, _s.VesselId, _s.OtherVesselId);
        await SourcePlanAsync();
        await _shoreLinks.ClonePlanAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId], false, _s.User);
        Assert.Empty(await _shoreLinks.DriftAsync(_s.CompanyId, _s.VesselId, _s.OtherVesselId));                                   // a fresh copy has no drift

        var copied = (await _shore.ListJobsAsync(_s.CompanyId, _s.OtherVesselId)).Single(j => j.Code == "ME-5");
        await _shore.UpdateJobAsync(_s.CompanyId, copied.Id, new JobEdit(copied.Title, copied.Instructions, copied.Type, 3, copied.CalendarUnit, copied.HoursInterval, copied.Anchor, copied.GraceDays,
            copied.IsCritical, copied.DepartmentId, copied.EstimatedMinutes, copied.RequiresVerification), _s.User);
        var engineHere = (await _shore.ListMachineryAsync(_s.CompanyId, _s.OtherVesselId)).Single(m => m.Name == "Main engine");
        await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(engineHere.Id, Code: "ME-X", Title: "Only here", CalendarInterval: 1), _s.User);
        var removed = (await _shore.ListJobsAsync(_s.CompanyId, _s.OtherVesselId)).Single(j => j.Code == "ME-9");
        await _shore.UpdateJobAsync(_s.CompanyId, removed.Id, new JobEdit(removed.Title, removed.Instructions, removed.Type, removed.CalendarInterval, removed.CalendarUnit, removed.HoursInterval, removed.Anchor,
            removed.GraceDays, removed.IsCritical, removed.DepartmentId, removed.EstimatedMinutes, removed.RequiresVerification, IsActive: false), _s.User);

        var drift = await _shoreLinks.DriftAsync(_s.CompanyId, _s.VesselId, _s.OtherVesselId);
        Assert.Contains(drift, r => r.Kind == DriftKind.Different && r.JobCode == "ME-5" && r.Detail.Contains("3 months here, 6 months on the reference"));
        Assert.Contains(drift, r => r.Kind == DriftKind.ExtraHere && r.JobCode == "ME-X");
        Assert.Contains(drift, r => r.Kind == DriftKind.MissingHere && r.JobCode == "ME-9");
        Assert.Equal(3, drift.Count);
    }
}
