using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class PmsTemplateDiffTests
{
    [Fact]
    public void TheDiffListsOnlyWhatWouldChange_InPlainWords()
    {
        var job = new PlannedJob { Title = "Overhaul injectors", Instructions = "Remove\nTest", Type = PmsJobType.Overhaul, CalendarInterval = 6, CalendarUnit = PmsIntervalUnit.Months, HoursInterval = 500, IsCritical = false };
        var same = new JobTemplate { Title = "Overhaul injectors", Instructions = "Remove\nTest", Type = PmsJobType.Overhaul, CalendarInterval = 6, CalendarUnit = PmsIntervalUnit.Months, HoursInterval = 500 };
        Assert.Empty(PmsTemplateDiff.Of(job, same));

        var newer = new JobTemplate { Title = "Overhaul fuel injectors", Instructions = "Remove\nTest\nRefit", Type = PmsJobType.Overhaul, CalendarInterval = 3, CalendarUnit = PmsIntervalUnit.Months,
            HoursInterval = null, IsCritical = true, RequiresVerification = true };
        var changes = PmsTemplateDiff.Of(job, newer).ToDictionary(c => c.Field);
        Assert.Equal(("6 months", "3 months"), (changes["Calendar interval"].From, changes["Calendar interval"].To));
        Assert.Equal(("500 h", "none"), (changes["Running hours interval"].From, changes["Running hours interval"].To));
        Assert.Equal(("no", "yes"), (changes["Critical"].From, changes["Critical"].To));
        Assert.Equal(("not required", "required"), (changes["Second sign-off"].From, changes["Second sign-off"].To));
        Assert.True(changes.ContainsKey("Title") && changes.ContainsKey("Checklist"));
        Assert.DoesNotContain("Type", changes.Keys);
    }
}

/// <summary>Editing the plan, plan change requests and template updates on a real office and a real vessel.</summary>
public class PmsChangeTests : IAsyncLifetime
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
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Pms, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private static JobEdit EditOf(PlannedJob j, int? calendar = null, string? title = null) => new(title ?? j.Title, j.Instructions, j.Type, calendar ?? j.CalendarInterval, j.CalendarUnit, j.HoursInterval,
        j.Anchor, j.GraceDays, j.IsCritical, j.DepartmentId, j.EstimatedMinutes, j.RequiresVerification, j.IsActive);

    private async Task<(MachineryItem Item, PlannedJob Job)> OfficePlanAsync()
    {
        var item = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true), _s.User);
        var job = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "ME-1", Title: "Oil sample", CalendarInterval: 6, StartDate: Ago(30)), _s.User);
        await _s.SettleUntilQuietAsync();
        return (item, job);
    }

    [Fact]
    public async Task TheOwnerEditsAJob_TheScheduleFollowsOnBothSides_AndTheOtherSideCannotEditIt()
    {
        var (_, job) = await OfficePlanAsync();
        Assert.Equal(Ago(30).AddMonths(6), (await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id))).NextDueDate);

        await _shore.UpdateJobAsync(_s.CompanyId, job.Id, EditOf(job, calendar: 3, title: "Lube oil sample"), _s.User);
        await _s.SettleUntilQuietAsync();
        foreach (var j in new[] { await OnShore(db => db.PlannedJobs.SingleAsync(x => x.Id == job.Id)), await OnVessel(db => db.PlannedJobs.SingleAsync(x => x.Id == job.Id)) })
            Assert.Equal(("Lube oil sample", 3, Ago(30).AddMonths(3)), (j.Title, j.CalendarInterval, j.NextDueDate));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.UpdateJobAsync(_s.CompanyId, job.Id, EditOf(job, calendar: 12), _s.User));
        Assert.Contains("belongs to the office", refused.Message);

        // A job the vessel made itself is the vessel's to edit (and the office is the one who cannot).
        var own = await _vessel.AddJobAsync(_s.CompanyId, new NewPlannedJob((await _vessel.ListMachineryAsync(_s.CompanyId, _s.VesselId)).Single().Id, Code: "V-1", Title: "Weekly test", CalendarInterval: 1, CalendarUnit: PmsIntervalUnit.Weeks, StartDate: Ago(3)), _s.User);
        await _s.SettleUntilQuietAsync();
        await _vessel.UpdateJobAsync(_s.CompanyId, own.Id, EditOf(own, calendar: 2), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, (await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == own.Id))).CalendarInterval);
        Assert.Contains("belongs to the vessel", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.UpdateJobAsync(_s.CompanyId, own.Id, EditOf(own, calendar: 9), _s.User))).Message);
    }

    [Fact]
    public async Task AVesselAsksForAnIntervalChange_TheOfficeApproves_AndTheChangeReachesTheVessel()
    {
        var (_, job) = await OfficePlanAsync();
        var asked = await _vessel.RequestPlanChangeAsync(_s.CompanyId, new NewPlanChange(PlanChangeKind.JobInterval, job.Id, null, new PlanChangeProposal(CalendarInterval: 12, CalendarUnit: PmsIntervalUnit.Months), "Maker allows annual"), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(6, (await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id))).CalendarInterval);                  // asking changes nothing
        var pending = Assert.Single(await _shore.PlanChangesAsync(_s.CompanyId, pendingOnly: true));
        Assert.Equal((asked.Id, "Main engine"), (pending.Request.Id, "Main engine"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DecidePlanChangeAsync(_s.CompanyId, asked.Id, true, "", _s.User));   // the office decides
        await _shore.DecidePlanChangeAsync(_s.CompanyId, asked.Id, true, "Agreed", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecidePlanChangeAsync(_s.CompanyId, asked.Id, false, "no", _s.User));
        await _s.SettleUntilQuietAsync();

        foreach (var j in new[] { await OnShore(db => db.PlannedJobs.SingleAsync(x => x.Id == job.Id)), await OnVessel(db => db.PlannedJobs.SingleAsync(x => x.Id == job.Id)) })
            Assert.Equal((12, Ago(30).AddMonths(12)), (j.CalendarInterval, j.NextDueDate));
        Assert.Empty(await _vessel.PlanChangesAsync(_s.CompanyId, pendingOnly: true));
        Assert.True((await _vessel.PlanChangesAsync(_s.CompanyId, pendingOnly: false)).Single().Decision!.Approved);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ARefusedRequest_ChangesNothing_AndRequestsAreChecked()
    {
        var (item, job) = await OfficePlanAsync();
        var own = await _vessel.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Emergency generator"), _s.User);
        var ownJob = await _vessel.AddJobAsync(_s.CompanyId, new NewPlannedJob(own.Id, Code: "EG-1", Title: "Test", CalendarInterval: 1), _s.User);

        Assert.Contains("directly", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RequestPlanChangeAsync(_s.CompanyId,
            new NewPlanChange(PlanChangeKind.JobInterval, ownJob.Id, null, new PlanChangeProposal(CalendarInterval: 3), "x"), _s.User))).Message);                        // its own job
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RequestPlanChangeAsync(_s.CompanyId,
            new NewPlanChange(PlanChangeKind.JobInterval, job.Id, null, new PlanChangeProposal(CalendarInterval: 3), "x"), _s.User));                                  // the office does not ask
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RequestPlanChangeAsync(_s.CompanyId,
            new NewPlanChange(PlanChangeKind.JobInterval, job.Id, null, new PlanChangeProposal(CalendarInterval: 3), " "), _s.User));                                  // a reason is needed
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RequestPlanChangeAsync(_s.CompanyId,
            new NewPlanChange(PlanChangeKind.JobInterval, job.Id, null, new PlanChangeProposal(), "no interval"), _s.User));

        var asked = await _vessel.RequestPlanChangeAsync(_s.CompanyId, new NewPlanChange(PlanChangeKind.JobDeactivate, job.Id, null, new PlanChangeProposal(), "Equipment removed"), _s.User);
        await _s.SettleUntilQuietAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecidePlanChangeAsync(_s.CompanyId, asked.Id, false, "", _s.User));
        await _shore.DecidePlanChangeAsync(_s.CompanyId, asked.Id, false, "Still fitted, see the survey", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.True((await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id))).IsActive);
        _ = item;

        var again = await _vessel.RequestPlanChangeAsync(_s.CompanyId, new NewPlanChange(PlanChangeKind.JobDeactivate, job.Id, null, new PlanChangeProposal(), "Removed in the yard"), _s.User);
        await _s.SettleUntilQuietAsync();
        await _shore.DecidePlanChangeAsync(_s.CompanyId, again.Id, true, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.False((await OnVessel(db => db.PlannedJobs.SingleAsync(j => j.Id == job.Id))).IsActive);
        Assert.Empty(await _vessel.ListJobsAsync(_s.CompanyId, _s.VesselId) is { } l ? l.Where(j => j.Id == job.Id) : []);
    }

    [Fact]
    public async Task AMachineryChangeRequest_RenamesAndMarksCritical_ButTheHourMeterStaysWhileJobsNeedIt()
    {
        var (item, _) = await OfficePlanAsync();
        var rename = await _vessel.RequestPlanChangeAsync(_s.CompanyId, new NewPlanChange(PlanChangeKind.MachineryChange, null, item.Id, new PlanChangeProposal(Name: "Main engine No. 1", IsCritical: true), "Two engines now"), _s.User);
        await _s.SettleUntilQuietAsync();
        await _shore.DecidePlanChangeAsync(_s.CompanyId, rename.Id, true, "", _s.User);
        await _s.SettleUntilQuietAsync();
        var onboard = await OnVessel(db => db.MachineryItems.SingleAsync(m => m.Id == item.Id));
        Assert.Equal(("Main engine No. 1", true), (onboard.Name, onboard.IsCritical));

        // Jobs on this machinery run on hours: the meter cannot be taken away.
        var hoursJob = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: "ME-H", Title: "Hours job", HoursInterval: 500), _s.User);
        await _s.SettleUntilQuietAsync();
        var noMeter = await _vessel.RequestPlanChangeAsync(_s.CompanyId, new NewPlanChange(PlanChangeKind.MachineryChange, null, item.Id, new PlanChangeProposal(HasHourMeter: false), "Meter broken"), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Contains("hour meter", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.DecidePlanChangeAsync(_s.CompanyId, noMeter.Id, true, "", _s.User))).Message);
        _ = hoursJob;
    }

    [Fact]
    public async Task ANewerTemplate_IsProposedWithItsDifferences_AndOnlyTheOwnerBringsAJobUpToIt()
    {
        var cls = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Main engine", null);
        var template = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("ME-5", "Lube oil analysis", "Take sample", PmsJobType.Test, CalendarInterval: 6, MachineryClassId: cls.Id));
        var item = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", MachineryClassId: cls.Id), _s.User);
        var officeJob = await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, template.Id, StartDate: Ago(20)), _s.User);
        await _s.SettleUntilQuietAsync();
        var ownJob = await _vessel.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, template.Id, StartDate: Ago(20)), _s.User);        // the vessel made this one from the same template
        await _s.SettleUntilQuietAsync();
        Assert.Empty(await _shore.TemplateUpdatesAsync(_s.CompanyId));

        await _shore.SaveJobTemplateAsync(_s.CompanyId, template.Id, new TemplateInput("ME-5", "Lube oil analysis and report", "Take sample\nSend to the lab", PmsJobType.Test, CalendarInterval: 3, MachineryClassId: cls.Id));
        await _s.SettleUntilQuietAsync();

        foreach (var service in new[] { _shore, _vessel })
        {
            var proposals = await service.TemplateUpdatesAsync(_s.CompanyId);
            Assert.Equal(2, proposals.Count);                                                                                                  // both sides see the proposal
            var fields = proposals.First().Changes.Select(c => c.Field).ToList();
            Assert.Contains("Title", fields);
            Assert.Contains("Calendar interval", fields);
        }
        var atOffice = await _shore.TemplateUpdatesAsync(_s.CompanyId);
        Assert.Equal([true, false], atOffice.OrderByDescending(u => u.Job.Id == officeJob.Id).Select(u => u.CanApplyHere));                   // each side can apply only to its own jobs
        var atVessel = await _vessel.TemplateUpdatesAsync(_s.CompanyId);
        Assert.Equal([true, false], atVessel.OrderByDescending(u => u.Job.Id == ownJob.Id).Select(u => u.CanApplyHere));

        // The vessel updates its own job only; the office's job is untouched.
        Assert.Equal((1, 1), await _vessel.ApplyTemplateUpdatesAsync(_s.CompanyId, [officeJob.Id, ownJob.Id], _s.User));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(1, (await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == officeJob.Id))).TemplateVersion);
        Assert.Equal((2, 3, Ago(20).AddMonths(3)), (await OnShore(db => db.PlannedJobs.SingleAsync(j => j.Id == ownJob.Id))) is { } o ? (o.TemplateVersion!.Value, o.CalendarInterval!.Value, o.NextDueDate!.Value) : default);

        Assert.Equal((1, 0), await _shore.ApplyTemplateUpdatesAsync(_s.CompanyId, [officeJob.Id], _s.User));
        await _s.SettleUntilQuietAsync();
        foreach (var j in new[] { await OnShore(db => db.PlannedJobs.SingleAsync(x => x.Id == officeJob.Id)), await OnVessel(db => db.PlannedJobs.SingleAsync(x => x.Id == officeJob.Id)) })
            Assert.Equal(("Lube oil analysis and report", 2, 3, Ago(20).AddMonths(3)), (j.Title, j.TemplateVersion!.Value, j.CalendarInterval!.Value, j.NextDueDate!.Value));
        Assert.Empty(await _shore.TemplateUpdatesAsync(_s.CompanyId));
        Assert.Empty(await _vessel.TemplateUpdatesAsync(_s.CompanyId));
        Assert.Equal((0, 1), await _shore.ApplyTemplateUpdatesAsync(_s.CompanyId, [officeJob.Id], _s.User));                                  // already current
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }
}
