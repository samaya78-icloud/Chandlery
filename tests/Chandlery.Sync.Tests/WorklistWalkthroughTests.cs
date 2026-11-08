using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The whole worklist in one story: the vessel and the office both raise and work on tasks while apart, and when the link returns both sides
/// hold the same history, the same state for every task and the same overdue counts.
/// </summary>
public class WorklistWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private WorklistService _vessel = null!, _shore = null!;
    private Guid _classSource, _auditSource;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new WorklistService(_s.VesselFactory);
        _shore = new WorklistService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        _classSource = await db.WorklistListItems.Where(i => i.Kind == WorklistListKind.Source && i.Name == "Class").Select(i => i.Id).SingleAsync();
        _auditSource = await db.WorklistListItems.Where(i => i.Kind == WorklistListKind.Source && i.Name == "Internal audit").Select(i => i.Id).SingleAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private static DateTime Day(int offset) => DateTime.UtcNow.Date.AddDays(offset);

    private NewWorklistTask New(string title, Guid source, DateTime? target, string assignee = "") => new(_s.VesselId, title, "", source, Target: target, Assignee: assignee);

    /// <summary>The same numbers the fleet view shows, worked out from one side's own data.</summary>
    private async Task<VesselWorklistRow> RowAsync(WorklistService side)
    {
        var tasks = await side.ListAsync(_s.CompanyId);
        return WorklistReports.PerVessel(tasks, [(_s.VesselId, "MV Sea Falcon", (Guid?)null)], DateTime.UtcNow.Date).Single();
    }

    private async Task<Dictionary<string, WorklistDetail>> ByTitleAsync(WorklistService side)
    {
        var result = new Dictionary<string, WorklistDetail>();
        foreach (var task in await side.ListAsync(_s.CompanyId)) result[task.Title] = (await side.GetAsync(_s.CompanyId, task.Id))!;
        return result;
    }

    [Fact]
    public async Task VesselAndOfficeWorkApart_AndEndWithTheSameHistory_State_AndOverdueCounts()
    {
        // The office switches the module on for the vessel.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Worklist, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();

        // Linked: the vessel raises three tasks, the office one.
        var survey = await _vessel.CreateTaskAsync(_s.CompanyId, New("Class survey item: lifeboat davit", _classSource, Day(-10)), _s.User);
        var gasket = await _vessel.CreateTaskAsync(_s.CompanyId, New("Replace cooler gasket", _auditSource, Day(3)), _s.User);
        var charts = await _vessel.CreateTaskAsync(_s.CompanyId, New("Update paper charts", _auditSource, null), _s.User);
        var drill = await _shore.CreateTaskAsync(_s.CompanyId, New("Record the fire drill", _auditSource, Day(-2), "Chief Officer"), _s.User);
        await _s.SettleUntilQuietAsync();

        var before = new[] { await RowAsync(_vessel), await RowAsync(_shore) };
        Assert.Equal(before[0], before[1]);
        Assert.Equal((4, 2), (before[0].Open, before[0].Overdue));                        // the survey item and the drill record are overdue
        Assert.Equal(3, before[0].NewFromVessel);

        // Apart: nothing is sent between these steps.
        await _vessel.AddEntryAsync(_s.CompanyId, survey.Id, new(WorklistEntryType.Percent, Percent: 60), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, survey.Id, new(WorklistEntryType.Completed, "Davit repaired and load tested"), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, gasket.Id, new(WorklistEntryType.Defer, "Part delayed to the next port", Date: Day(20)), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, charts.Id, new(WorklistEntryType.Comment, "Corrections received by post"), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, drill.Id, new(WorklistEntryType.Percent, Percent: 30), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, drill.Id, new(WorklistEntryType.Defer, "Drill moved for weather", Date: Day(5)), _s.User);
        await Task.Delay(50);
        await _shore.AddEntryAsync(_s.CompanyId, gasket.Id, new(WorklistEntryType.Priority, Priority: WorklistPriority.Urgent), _s.User);
        await _shore.AddEntryAsync(_s.CompanyId, charts.Id, new(WorklistEntryType.Target, "Needed before the next voyage", Date: Day(1)), _s.User);
        await _shore.AddEntryAsync(_s.CompanyId, charts.Id, new(WorklistEntryType.Assignee, Assignee: "Bosun"), _s.User);
        await _shore.AddEntryAsync(_s.CompanyId, drill.Id, new(WorklistEntryType.Target, "Office wants it sooner", Date: Day(-1)), _s.User);   // later than the vessel's deferral
        var circular = await _shore.CreateTaskAsync(_s.CompanyId, New("Read the new circular", _auditSource, Day(30)), _s.User);

        // The link returns.
        await _s.SettleUntilQuietAsync();

        var atVessel = await ByTitleAsync(_vessel);
        var atOffice = await ByTitleAsync(_shore);
        Assert.Equal(5, atVessel.Count);
        Assert.Equal(atVessel.Keys.OrderBy(k => k), atOffice.Keys.OrderBy(k => k));
        foreach (var (title, detail) in atVessel)
        {
            var other = atOffice[title];
            Assert.Equal(detail.Entries.Select(e => e.Id), other.Entries.Select(e => e.Id));                       // the same history, in the same order
            Assert.Equal(detail.Entries.Select(e => (e.Type, e.Payload, e.Text, e.AuthorName)), other.Entries.Select(e => (e.Type, e.Payload, e.Text, e.AuthorName)));
            var (a, b) = (detail.Task, other.Task);
            Assert.Equal((a.Status, a.PercentComplete, a.Priority, a.TargetDate, a.AssigneeName, a.DeferralCount, a.AwaitingVerification, a.Title),
                         (b.Status, b.PercentComplete, b.Priority, b.TargetDate, b.AssigneeName, b.DeferralCount, b.AwaitingVerification, b.Title));
        }

        // What each task ended as.
        var done = atOffice["Class survey item: lifeboat davit"].Task;
        Assert.True(done.AwaitingVerification);                                                                    // completed onboard, waiting for the office
        var cooler = atOffice["Replace cooler gasket"].Task;
        Assert.Equal((Day(20), 1, WorklistPriority.Urgent), (cooler.TargetDate, cooler.DeferralCount, cooler.Priority));
        var chartsTask = atOffice["Update paper charts"].Task;
        Assert.Equal((Day(1), "Bosun"), (chartsTask.TargetDate, chartsTask.AssigneeName));
        var fire = atOffice["Record the fire drill"].Task;
        Assert.Equal((Day(-1), 1, 30), (fire.TargetDate, fire.DeferralCount, fire.PercentComplete));              // both kept; the later (office) date wins; the deferral still counts

        // The same overdue counts on both sides.
        var rows = new[] { await RowAsync(_vessel), await RowAsync(_shore) };
        Assert.Equal(rows[0], rows[1]);
        Assert.Equal((5, 1, 1, 1, 1, 3), (rows[0].Open, rows[0].Overdue, rows[0].DueThisWeek, rows[0].AwaitingVerification, rows[0].Urgent, rows[0].NewFromVessel));
        var escalated = WorklistReports.Escalations(await _shore.ListAsync(_s.CompanyId), DateTime.UtcNow.Date, 0);
        Assert.Equal(["Record the fire drill"], escalated.Select(t => t.Title));

        // The office verifies the completed survey item; both sides close it.
        await _shore.AddEntryAsync(_s.CompanyId, survey.Id, new(WorklistEntryType.Verified, "Seen the certificate"), _s.User);
        await _s.SettleUntilQuietAsync();
        foreach (var side in new[] { _vessel, _shore })
        {
            var t = (await side.GetAsync(_s.CompanyId, survey.Id))!.Task;
            Assert.True(t.IsClosed);
            Assert.NotNull(t.VerifiedAt);
            var row = await RowAsync(side);
            Assert.Equal((4, 0), (row.Open, row.AwaitingVerification));
        }

        // Nothing is left waiting or parked, and the vessel never saw anything it should not.
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
        Assert.Equal(0, await _s.DirtyAsync(_s.ShoreFactory));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Equal(circular.Id, (await _vessel.ListAsync(_s.CompanyId)).Single(t => t.Title == "Read the new circular").Id);
    }
}
