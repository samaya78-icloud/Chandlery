using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The office side of PMS: applying the library to a vessel, the fleet figures and the reports.</summary>
public class PmsOfficeTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PmsService _vessel = null!, _shore = null!;
    private PmsReportService _vesselReports = null!, _shoreReports = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime Ago(int days) => Today.AddDays(-days);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new PmsService(_s.VesselFactory);
        _shore = new PmsService(_s.ShoreFactory);
        _vesselReports = new PmsReportService(_s.VesselFactory);
        _shoreReports = new PmsReportService(_s.ShoreFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Pms, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    // ---- applying the library ----

    [Fact]
    public async Task TheLibrary_IsAppliedToMappedMachinery_IncludingTemplatesOfParentClasses_AllOrNothing_AndOnlyOnce()
    {
        var engine = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Engines", null);
        var main = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Main engine", engine.Id);
        var pump = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Pumps", null);
        var general = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("EN-1", "Check engine room bilges", CalendarInterval: 1, MachineryClassId: engine.Id));
        var specific = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("ME-1", "Overhaul injectors", CalendarInterval: 6, HoursInterval: 500, MachineryClassId: main.Id));
        var other = await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("PU-1", "Pump seal check", CalendarInterval: 3, MachineryClassId: pump.Id));
        await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("XX-1", "Retired job", CalendarInterval: 3, MachineryClassId: main.Id, IsActive: false));
        await _shore.SaveJobTemplateAsync(_s.CompanyId, null, new TemplateInput("GN-1", "No class, never automatic", CalendarInterval: 3));

        var withMeter = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true, MachineryClassId: main.Id), _s.User);
        var noMeter = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Aux engine", MachineryClassId: main.Id), _s.User);
        var unmapped = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Bilge pump"), _s.User);

        var preview = await _shore.PreviewLibraryAsync(_s.CompanyId, _s.VesselId);
        Assert.Equal(["EN-1", "ME-1"], preview.Where(p => p.Item.Id == withMeter.Id).Select(p => p.Template.Code));        // its class and the class above, not other classes, retired or unclassed ones
        Assert.DoesNotContain(preview, p => p.Item.Id == unmapped.Id);
        Assert.True(preview.Single(p => p.Item.Id == noMeter.Id && p.Template.Id == specific.Id).NeedsHourMeter);           // an hours job needs a meter

        // All or nothing: one job cannot be added, so none is.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ApplyLibraryAsync(_s.CompanyId, _s.VesselId,
            [(withMeter.Id, general.Id), (noMeter.Id, specific.Id)], _s.User));
        Assert.Contains("hour meter", refused.Message);
        Assert.Empty(await _shore.ListJobsAsync(_s.CompanyId, _s.VesselId));

        var created = await _shore.ApplyLibraryAsync(_s.CompanyId, _s.VesselId, [(withMeter.Id, general.Id), (withMeter.Id, specific.Id), (noMeter.Id, general.Id), (withMeter.Id, general.Id)], _s.User);
        Assert.Equal(3, created.Count);                                                                                    // the duplicate pair counts once
        Assert.All(created, j => Assert.Equal((SyncStages.Office, PmsAnchor.FromCompletion), (j.OwnerStage, j.Anchor)));

        // Applying again adds nothing; the preview now shows them as planned.
        Assert.Empty(await _shore.ApplyLibraryAsync(_s.CompanyId, _s.VesselId, [(withMeter.Id, general.Id), (withMeter.Id, specific.Id)], _s.User));
        Assert.All((await _shore.PreviewLibraryAsync(_s.CompanyId, _s.VesselId)).Where(p => p.Item.Id == withMeter.Id), p => Assert.True(p.AlreadyPlanned));
        _ = other;

        await _s.SettleUntilQuietAsync();
        Assert.Equal(3, (await _vessel.ListJobsAsync(_s.CompanyId, _s.VesselId)).Count);                                  // they reach the vessel
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());

        // The vessel cannot apply the library.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ApplyLibraryAsync(_s.CompanyId, _s.VesselId, [(withMeter.Id, specific.Id)], _s.User));
    }

    [Fact]
    public async Task OnlyTheSideThatMadeTheMachinery_CanMapItToAClass()
    {
        var cls = await _shore.SaveMachineryClassAsync(_s.CompanyId, null, "Main engine", null);
        var officeItem = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine"), _s.User);
        var vesselItem = await _vessel.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Emergency generator"), _s.User);
        await _s.SettleUntilQuietAsync();

        await _shore.SetMachineryClassAsync(_s.CompanyId, officeItem.Id, cls.Id);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(cls.Id, (await OnVessel(db => db.MachineryItems.SingleAsync(m => m.Id == officeItem.Id))).MachineryClassId);          // the mapping reaches the vessel

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SetMachineryClassAsync(_s.CompanyId, vesselItem.Id, cls.Id));
        Assert.Contains("belongs to the vessel", refused.Message);
    }

    // ---- fleet figures and reports ----

    private async Task<Dictionary<string, PlannedJob>> BuildFleetPlanAsync()
    {
        var item = await _shore.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true), _s.User);
        async Task<PlannedJob> Job(string code, int startAgo, int months, bool critical = false, int? hours = null) =>
            await _shore.AddJobAsync(_s.CompanyId, new NewPlannedJob(item.Id, Code: code, Title: code + " job", CalendarInterval: months > 0 ? months : null, CalendarUnit: PmsIntervalUnit.Months, HoursInterval: hours,
                IsCritical: critical, StartDate: Ago(startAgo)), _s.User);
        var jobs = new Dictionary<string, PlannedJob>
        {
            ["overdue-critical"] = await Job("J1", 90, 1, critical: true),
            ["overdue"] = await Job("J2", 60, 1),
            ["this-week"] = await Job("J3", 27, 1),
            ["later"] = await Job("J4", 0, 12),
            ["hours"] = await Job("J5", 0, 0, hours: 500),
        };
        await _s.SettleUntilQuietAsync();
        return jobs;
    }

    [Fact]
    public async Task TheFleetFigures_AreTheSameOnBothSides_AndMatchTheJobsStatuses()
    {
        var jobs = await BuildFleetPlanAsync();

        foreach (var reports in new[] { _shoreReports, _vesselReports })
        {
            var row = Assert.Single(await reports.FleetAsync(_s.CompanyId));
            Assert.Equal((5, 2, 1, 1, 1, 60), (row.Jobs, row.Overdue, row.CriticalOverdue, row.DueThisWeek, row.HoursOutOfDate, row.OnSchedulePercent));
            var counts = (await reports.CountsAsync(_s.CompanyId))!;
            Assert.Equal((2, 1, 1, 1), (counts.Overdue, counts.CriticalOverdue, counts.DueThisWeek, counts.HoursOutOfDate));
        }

        // The database figures agree with the rule applied job by job.
        var all = await _shore.ListJobsAsync(_s.CompanyId, _s.VesselId);
        Assert.Equal(2, all.Count(j => PmsProjection.StatusOf(j, Today) == PmsStatus.Overdue));
        Assert.Equal(PmsStatus.NoSchedule, PmsProjection.StatusOf(jobs["hours"], Today));

        var overdue = await _shoreReports.OverdueAsync(_s.CompanyId);
        Assert.Equal(["J1", "J2"], overdue.Select(o => o.Job.Code));                                                         // critical first
        Assert.True(overdue[0].DaysOverdue > overdue[1].DaysOverdue - 40 && overdue[0].DaysOverdue >= 50);
        Assert.Equal(["J3"], (await _shoreReports.DueWithinAsync(_s.CompanyId, 30)).Select(d => d.Job.Code));
        Assert.Equal(["J3", "J4"], (await _shoreReports.DueWithinAsync(_s.CompanyId, 400)).Select(d => d.Job.Code));
    }

    [Fact]
    public async Task TheDoneReport_LeavesOutImportedHistory_AndTheHoursSummaryWorksOutTheAverage()
    {
        var jobs = await BuildFleetPlanAsync();
        var item = (await _shore.ListMachineryAsync(_s.CompanyId, _s.VesselId)).Single();
        await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(jobs["overdue"].Id, Ago(2), PerformedBy: "Bosun", Comments: "done"), _s.User);
        await _vessel.CompleteJobAsync(_s.CompanyId, new NewCompletion(jobs["later"].Id, Ago(1), IsImported: true), _s.User);
        await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Ago(30), 1000, false, "", _s.User);
        await _vessel.RecordReadingAsync(_s.CompanyId, item.Id, Today, 1300, false, "", _s.User);
        await _s.SettleUntilQuietAsync();

        var done = await _shoreReports.DoneAsync(_s.CompanyId, Ago(7), Today);
        var row = Assert.Single(done);
        Assert.Equal(("J2", "Bosun"), (row.Job.Code, row.Completion.PerformedBy));
        Assert.Empty(await _shoreReports.DoneAsync(_s.CompanyId, Ago(60), Ago(10)));

        var hours = Assert.Single(await _shoreReports.HoursSummaryAsync(_s.CompanyId));
        Assert.Equal((1300m, 0, 2, 10m), (hours.LatestHours, hours.DaysSinceReading, hours.Readings, hours.AverageDaily));
        Assert.Equal(0, (await _shoreReports.FleetAsync(_s.CompanyId)).Single().HoursOutOfDate);                              // the reading refreshed the hours job

        var csv = PmsReports.DoneCsv(done);
        Assert.Contains("\"J2\"", csv);
        Assert.StartsWith("\"Vessel\",\"Machinery\",\"Code\",\"Job\",\"Done\"", csv);
        Assert.Contains("\"1300.0\"", PmsReports.HoursCsv(await _shoreReports.HoursSummaryAsync(_s.CompanyId)));
    }

    [Fact]
    public async Task AVesselWithPlannedMaintenanceOff_IsNotCounted()
    {
        await BuildFleetPlanAsync();
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var sw = await db.VesselModuleSwitches.SingleAsync(m => m.Module == PolicyModules.Pms);
            sw.Enabled = false;
            await db.SaveChangesAsync();
        }
        Assert.Empty(await _shoreReports.FleetAsync(_s.CompanyId));
        Assert.Null(await _shoreReports.CountsAsync(_s.CompanyId));
        Assert.Empty(await _shoreReports.OverdueAsync(_s.CompanyId));
    }
}
