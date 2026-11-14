using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The whole project story on a real office and a real vessel: the office plans a drydock and imports a Worklist task and a maintenance job,
/// the ship writes notes and asks for another job while the office tags a purchase order and a spot invoice and approves the budget, the
/// project is completed and closed and locks on both sides, and both ends hold the same specification, notes and status while the vessel
/// never receives a budget or a cost.
/// </summary>
public class ProjectWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!, _vessel = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;
    private PmsService _pms = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shore = new ProjectService(_s.ShoreFactory, _shoreWorklist);
        _vessel = new ProjectService(_s.VesselFactory, _vesselWorklist);
        _pms = new PmsService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApprovalRules.RemoveRange(db.ApprovalRules);      // the fixture seeds approval rules; the plain approval step needs none to apply
        foreach (var module in new[] { PolicyModules.Projects, PolicyModules.Worklist, PolicyModules.Pms })
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    /// <summary>What a side holds of a project, as one block of text: status, specification, notes and requests. Both sides must print the same.</summary>
    private static async Task<string> StoryAsync(ProjectService side, Guid companyId, Guid projectId)
    {
        var detail = (await side.GetAsync(companyId, projectId))!;
        var requests = await side.RequestsAsync(companyId, projectId);
        var lines = new List<string> { $"{detail.Project.Code} {detail.Project.Title} {detail.Status}" };
        lines.AddRange(detail.Project.Items.OrderBy(i => i.Description).Select(i => $"item {i.Source} {i.SourceRef} {i.Group}/{i.Description} {i.Status}"));
        lines.AddRange(detail.Notes.OrderBy(n => n.Text).Select(n => $"note {n.Text} {n.ProgressPct}"));
        lines.AddRange(requests.OrderBy(r => r.Request.Description).Select(r => $"request {r.Request.Description} {(r.Decision is null ? "waiting" : r.Decision.Accepted ? "accepted" : "declined")}"));
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task TheOfficePlansAndImports_TheShipReportsAndAsks_CostsFollowTheDocuments_AndBothSidesEndWithTheSameLockedProject()
    {
        // The office plans the drydock and fills the specification from the Worklist and from maintenance.
        var deferred = (await _shoreWorklist.GetListsAsync(_s.CompanyId)).Single(i => i.Kind == WorklistListKind.Nature && i.Name == "Deferred").Id;
        await _shore.SetDrydockNatureAsync(_s.CompanyId, deferred, _s.User);
        var task = await _shoreWorklist.CreateTaskAsync(_s.CompanyId, new NewWorklistTask(_s.VesselId, "Renew the ballast tank coating", NatureId: deferred), _s.User);
        var machinery = await _pms.AddMachineryItemAsync(_s.CompanyId, new NewMachineryItem(_s.VesselId, "Main engine", HasHourMeter: true), _s.User);
        var job = await _pms.AddJobAsync(_s.CompanyId, new NewPlannedJob(machinery.Id, Code: "J-ME", Title: "Overhaul the main engine", CalendarInterval: 1, StartDate: Today.AddDays(-40)), _s.User);
        await _s.SettleUntilQuietAsync();

        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027", PlannedStart: Today.AddDays(-3)), _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast and repaint the underwater hull")], _s.User);
        var imported = await _shore.ImportAsync(_s.CompanyId, project.Id, [task.Id], [job.Id], _s.User);
        Assert.Equal(2, imported.Added);
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 50_000m), new("Owner's costs", 8_000m)], _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today.AddDays(-2), "", _s.User);
        await _s.SettleUntilQuietAsync();

        // The vessel has the project, with its specification, and the imported task's link; it has no figures.
        var onboard = (await _vessel.GetAsync(_s.CompanyId, project.Id))!;
        Assert.Equal((ProjectStatus.Ongoing, 3), (onboard.Status, onboard.Project.Items.Count));
        Assert.Contains((await _vesselWorklist.GetAsync(_s.CompanyId, task.Id))!.Entries, e => e.Type == WorklistEntryType.Link && e.Payload.Contains(project.Id.ToString()));
        Assert.Empty(await _vessel.BudgetAsync(_s.CompanyId, project.Id));

        // Meanwhile the ship writes up progress and asks for a job it found, and the office answers.
        await _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "Hull blasting started on the port side", 15), _s.User);
        var grating = await _vessel.AddItemRequestAsync(_s.CompanyId, project.Id, "Renew the sea chest grating", "Wasted once blasted", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(1, (await _shore.CountsAsync(_s.CompanyId))!.RequestsWaiting);
        await _shore.DecideRequestAsync(_s.CompanyId, grating.Id, true, "Hull", "Agreed", _s.User);

        // The office tags an order and a spot invoice; cost follows them. The yard's own entry counts too.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var supplier = await db.Suppliers.Select(x => x.Id).FirstAsync();
            var po = new PurchaseOrder
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, PoNumber = "PO-WALK", SupplierId = supplier, VesselId = _s.VesselId, DepartmentId = _s.DepartmentId,
                CreatedDate = DateTime.UtcNow, Status = PoStatus.Approved, Currency = "USD", Lines = [new PoLine { ItemId = _s.ItemIds[0], Quantity = 1, UnitPrice = 12_000m }],
            };
            var invoice = new Invoice
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, InvoiceNumber = "INV-WALK", SupplierId = supplier, Kind = InvoiceKind.SpotInvoice, VesselId = _s.VesselId,
                InvoiceDate = Today, SubmittedAt = DateTime.UtcNow, Currency = "USD", InvoiceAmount = 3_000m, Status = InvoiceStatus.Finalized,
            };
            db.PurchaseOrders.Add(po);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            await _shore.LinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.PurchaseOrder, po.Id, _s.User);
            await _shore.LinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.Invoice, invoice.Id, _s.User);
        }
        await _shore.AddCostEntryAsync(_s.CompanyId, project.Id, Today, "Surveyor's attendance", "", 500m, _s.User);
        var cost = (await _shore.CostAsync(_s.CompanyId, project.Id)).Cost;
        Assert.Equal(new ProjectCost(Committed: 12_000m, Invoiced: 3_000m, Other: 500m), cost);
        await _s.SettleUntilQuietAsync();

        // The vessel sees the project and its status, and none of the money, even now that there is some.
        var row = Assert.Single(await _vessel.ListWithMoneyAsync(_s.CompanyId));
        Assert.Equal((ProjectStatus.Ongoing, 0m, 0m, 0m, false), (row.Row.Status, row.Budget, row.Contingency, row.Cost.ToDate, row.OverBudget));
        Assert.Empty(await _vessel.BudgetAsync(_s.CompanyId, project.Id));
        Assert.Empty(await _vessel.CostEntriesAsync(_s.CompanyId, project.Id));
        Assert.Equal(0, await Count(_s.VesselFactory, db => db.ProjectBudgetLines.CountAsync()));
        Assert.Equal(0, await Count(_s.VesselFactory, db => db.ProjectCostEntries.CountAsync()));
        Assert.Equal(0, await Count(_s.VesselFactory, db => db.ProjectBudgetApprovals.CountAsync()));

        // The work is finished: complete, reconcile and close. The ship's side is locked as soon as the news arrives.
        var everything = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        Assert.Equal(4, everything.Count);
        var notes = await _shore.SaveItemsAsync(_s.CompanyId, project.Id, everything.Select(i => new ProjectItemInput(i.Id, i.Group, i.Description, i.Source, i.SourceId, i.SourceRef, ProjectItemStatus.Done)).ToList(), _s.User);
        Assert.Contains(notes, n => n.Contains($"Worklist task {task.Code} is completed"));
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Closed, Today, "Reconciled with the yard", _s.User);
        await _s.SettleUntilQuietAsync();

        foreach (var side in new[] { _shore, _vessel })
        {
            Assert.Equal(ProjectStatus.Closed, (await side.GetAsync(_s.CompanyId, project.Id))!.Status);
            Assert.Contains("closed and locked", (await Assert.ThrowsAsync<InvalidOperationException>(() => side.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "Late note", null), _s.User))).Message);
            Assert.Contains("closed and locked", (await Assert.ThrowsAsync<InvalidOperationException>(() => side.AddItemRequestAsync(_s.CompanyId, project.Id, "Too late", "", _s.User))).Message);
        }

        // The same story at both ends, nothing stuck in quarantine.
        var shoreStory = await StoryAsync(_shore, _s.CompanyId, project.Id);
        Assert.Equal(shoreStory, await StoryAsync(_vessel, _s.CompanyId, project.Id));
        Assert.Contains("Closed", shoreStory);
        Assert.Contains("note Hull blasting started on the port side 15", shoreStory);
        Assert.Contains("request Renew the sea chest grating accepted", shoreStory);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    private static async Task<int> Count(IDbContextFactory<ChandleryDbContext> factory, Func<ChandleryDbContext, Task<int>> count)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await count(db);
    }
}
