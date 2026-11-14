using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class ProjectInheritanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static ProjectLink Link(Guid project, bool added, int hours) => new() { Id = Guid.CreateVersion7(), ProjectId = project, Added = added, At = T0.AddHours(hours) };

    [Fact]
    public void ADocumentWithNoLinkOfItsOwnTakesTheProjectOfWhatItCameFrom_AndItsOwnLatestLinkAlwaysWins()
    {
        var parent = Guid.CreateVersion7();
        var own = Guid.CreateVersion7();

        Assert.Equal((parent, true), ProjectInheritance.Resolve([], parent));
        Assert.Equal((null, false), ProjectInheritance.Resolve([], null));

        Assert.Equal((own, false), ProjectInheritance.Resolve([Link(own, true, 1)], parent));                              // its own link overrides what it came from
        Assert.Equal((null, false), ProjectInheritance.Resolve([Link(own, true, 1), Link(own, false, 2)], parent));        // taken off: not even the requisition's project comes back
        Assert.Equal((own, false), ProjectInheritance.Resolve([Link(own, false, 1), Link(own, true, 2)], null));           // put back
        Assert.Equal((own, false), ProjectInheritance.Resolve([Link(own, true, 2), Link(own, false, 1)], parent));         // the order they are held in does not matter
    }
}

/// <summary>What a project has cost, worked out from the orders and invoices that belong to it, on a real office and a real vessel.</summary>
public class ProjectCostTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!, _vessel = null!;
    private Guid _supplier;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _vessel = new ProjectService(_s.VesselFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApprovalRules.RemoveRange(db.ApprovalRules);      // the fixture seeds approval rules; the plain approval step needs none to apply
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
        await db.SaveChangesAsync();
        _supplier = await db.Suppliers.Select(s => s.Id).FirstAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<PurchaseOrder> OrderAsync(string number, decimal total, PoStatus status = PoStatus.Approved, Guid? requisition = null, string currency = "USD", decimal? rate = null, bool dummy = false)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var po = new PurchaseOrder
        {
            Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, PoNumber = number, SupplierId = _supplier, VesselId = _s.VesselId, DepartmentId = _s.DepartmentId, RequisitionId = requisition,
            CreatedDate = DateTime.UtcNow, Status = status, Currency = currency, UsdRate = rate, IsDummy = dummy,
            Lines = [new PoLine { ItemId = _s.ItemIds[0], Quantity = 1, UnitPrice = total }],
        };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();
        return po;
    }

    private async Task<Invoice> InvoiceAsync(string number, decimal amount, InvoiceStatus status, Guid? order = null, Guid? vessel = null)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var invoice = new Invoice
        {
            Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, InvoiceNumber = number, SupplierId = _supplier, Kind = order is null ? InvoiceKind.SpotInvoice : InvoiceKind.PoInvoice,
            PurchaseOrderId = order, VesselId = vessel, InvoiceDate = DateTime.UtcNow.Date, SubmittedAt = DateTime.UtcNow, Currency = "USD", InvoiceAmount = amount, Status = status,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    [Fact]
    public async Task CostFollowsTheOrdersAndInvoicesOnTheProject_ByTheirOwnLinkOrByWhatTheyCameFrom_AndNothingIsCountedTwice()
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027"), _s.User);
        var requisition = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("yard stores"), _s.User);
        await _shore.LinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.Requisition, requisition.Id, _s.User);

        // Orders raised from the linked requisition belong to the project without anyone linking them.
        var fromRequisition = await OrderAsync("PO-T1", 1_000m, requisition: requisition.Id);          // later invoiced
        var notYetApproved = await OrderAsync("PO-T3", 700m, PoStatus.PendingApproval, requisition.Id);
        var takenOff = await OrderAsync("PO-T4", 5_000m, requisition: requisition.Id);
        var recordOnly = await OrderAsync("PO-T7", 300m, requisition: requisition.Id, dummy: true);
        var inherited = await _shore.ProjectOfDocAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, fromRequisition.Id);
        Assert.Equal((project.Id, true), (inherited!.ProjectId, inherited.Inherited));
        await _shore.UnlinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.PurchaseOrder, takenOff.Id, _s.User);          // an order can be taken off although it only inherited its project
        Assert.Null(await _shore.ProjectOfAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, takenOff.Id));

        // Orders linked on their own, in another currency (with its rate, and with none), and one that belongs to nobody.
        var euro = await OrderAsync("PO-T2", 400m, currency: "EUR", rate: 0.5m);                                     // 400 EUR at 0.5 per dollar is 800 dollars
        var noRate = await OrderAsync("PO-T6", 50m, currency: "XTS");
        var stranger = await OrderAsync("PO-T5", 9_999m);
        await _shore.LinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.PurchaseOrder, euro.Id, _s.User);
        await _shore.LinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.PurchaseOrder, noRate.Id, _s.User);

        // Invoices: one settles the first order, a spot invoice is linked on its own, one is not finalized yet.
        var settling = await InvoiceAsync("INV-T1", 900m, InvoiceStatus.Finalized, order: fromRequisition.Id);
        var spot = await InvoiceAsync("INV-T2", 250m, InvoiceStatus.Finalized, vessel: _s.VesselId);
        var waiting = await InvoiceAsync("INV-T3", 400m, InvoiceStatus.PendingApproval, order: euro.Id);
        await _shore.LinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.Invoice, spot.Id, _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, project.Id, Today, "Surveyor's attendance", "", 100m, _s.User);

        var summary = await _shore.CostAsync(_s.CompanyId, project.Id);

        // Committed: the euro order (800) and the one with no rate (50, at its own amount). Invoiced: 900 + 250. The first order is replaced by its invoice, the pending ones are not counted yet.
        Assert.Equal(new ProjectCost(Committed: 850m, Invoiced: 1_150m, Other: 100m), summary.Cost);
        Assert.Equal(2_100m, summary.Cost.ToDate);
        Assert.Equal(1, summary.Unconverted);

        var byNumber = summary.Lines.ToDictionary(l => l.Number);
        Assert.Equal(ProjectCostBucket.Settled, byNumber["PO-T1"].Bucket);
        Assert.Equal(ProjectCostBucket.Committed, byNumber["PO-T2"].Bucket);
        Assert.Equal((400m, 800m, "EUR"), (byNumber["PO-T2"].Amount, byNumber["PO-T2"].AmountUsd, byNumber["PO-T2"].Currency));
        Assert.Equal(ProjectCostBucket.NotCounted, byNumber["PO-T3"].Bucket);                                       // pending approval
        Assert.Equal(ProjectCostBucket.NotCounted, byNumber["PO-T7"].Bucket);                                       // record-only orders carry no value
        Assert.Equal(ProjectCostBucket.Invoiced, byNumber["INV-T1"].Bucket);
        Assert.Equal(ProjectCostBucket.Invoiced, byNumber["INV-T2"].Bucket);
        Assert.Equal(ProjectCostBucket.NotCounted, byNumber["INV-T3"].Bucket);                                      // not finalized
        Assert.DoesNotContain("PO-T4", byNumber.Keys);                                                                // taken off
        Assert.DoesNotContain("PO-T5", byNumber.Keys);                                                                // never on the project
        Assert.Equal(100m, Assert.Single(summary.Entries).AmountUsd);

        // The documents that belong to the project are the ones counted, whichever way they got there.
        var belong = await _shore.LinksOfAsync(_s.CompanyId, project.Id);
        Assert.Equal(new[] { fromRequisition.Id, notYetApproved.Id, recordOnly.Id, euro.Id, noRate.Id }.Order(), belong.Where(b => b.Kind == ProjectLinkKind.PurchaseOrder).Select(b => b.DocId).Order());
        Assert.Equal(new[] { settling.Id, spot.Id, waiting.Id }.Order(), belong.Where(b => b.Kind == ProjectLinkKind.Invoice).Select(b => b.DocId).Order());
        Assert.DoesNotContain(belong, b => b.DocId == stranger.Id);

        // When the last invoice is finalized the second order is settled too; an order moved to another project takes its cost with it.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
            await db.Invoices.Where(i => i.Id == waiting.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, InvoiceStatus.Finalized));
        Assert.Equal(new ProjectCost(Committed: 50m, Invoiced: 1_550m, Other: 100m), (await _shore.CostAsync(_s.CompanyId, project.Id)).Cost);
        var other = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.NonDrydock, "Pump replacement"), _s.User);
        await _shore.SetProjectAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, noRate.Id, other.Id, _s.User);
        Assert.Equal(new ProjectCost(Committed: 0m, Invoiced: 1_550m, Other: 100m), (await _shore.CostAsync(_s.CompanyId, project.Id)).Cost);
        Assert.Equal(50m, (await _shore.CostAsync(_s.CompanyId, other.Id)).Cost.Committed);

        // Cost is the office's business: a vessel is never shown it.
        await _s.SettleUntilQuietAsync();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CostAsync(_s.CompanyId, project.Id));
        Assert.Contains("office", refused.Message);
    }

    [Fact]
    public async Task APersonPicksTheProjectOnADocument_SetProjectMovesOrRemovesIt_AndASavedDocumentKeepsItsOwnChoiceWhenItsRequisitionMoves()
    {
        var first = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027"), _s.User);
        var second = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.NonDrydock, "Pump replacement"), _s.User);
        Assert.Equal(["Drydock 2027", "Pump replacement"], (await _shore.OpenProjectsAsync(_s.CompanyId, _s.VesselId)).Select(p => p.Title).Order());
        Assert.Empty(await _shore.OpenProjectsAsync(_s.CompanyId, _s.OtherVesselId));                                 // the module is off for the other vessel

        var requisition = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("stores"), _s.User);
        var order = await OrderAsync("PO-T9", 100m, requisition: requisition.Id);
        await _shore.SetProjectAsync(_s.CompanyId, ProjectLinkKind.Requisition, requisition.Id, first.Id, _s.User);
        Assert.Equal(first.Id, await _shore.ProjectOfAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, order.Id));      // by its requisition

        await _shore.SetProjectAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, order.Id, first.Id, _s.User);           // already so by its requisition: nothing is written
        await _shore.LinkAsync(_s.CompanyId, first.Id, ProjectLinkKind.PurchaseOrder, order.Id, _s.User);                 // linking it outright pins it to the project
        await _shore.SetProjectAsync(_s.CompanyId, ProjectLinkKind.Requisition, requisition.Id, second.Id, _s.User);      // the requisition moves...
        Assert.Equal(first.Id, await _shore.ProjectOfAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, order.Id));       // ...the order chosen on its own stays
        await _shore.SetProjectAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, order.Id, null, _s.User);              // taken off
        Assert.Null(await _shore.ProjectOfAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, order.Id));
        await _shore.SetProjectAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, order.Id, null, _s.User);              // doing it again changes nothing

        // A closed project can take nothing more, and no longer appears in the choices.
        await _shore.RecordAsync(_s.CompanyId, second.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, second.Id, ProjectEventKind.Started, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, second.Id, ProjectEventKind.Completed, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, second.Id, ProjectEventKind.Closed, Today, "Done", _s.User);
        Assert.Equal(["Drydock 2027"], (await _shore.OpenProjectsAsync(_s.CompanyId, _s.VesselId)).Select(p => p.Title));
        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SetProjectAsync(_s.CompanyId, ProjectLinkKind.PurchaseOrder, order.Id, second.Id, _s.User));
        Assert.Contains("closed and locked", closed.Message);
    }
}
