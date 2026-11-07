using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// One working trip through everything built for procurement sync, on a real office and a real vessel that started empty:
/// requisition, RFQ, a supplier's portal quote and one typed in onboard, award, order, Master approval, dispatch, acknowledgement,
/// delivery in two parts, and then an order above the Master's limit that the office approves. Whatever each side should and
/// should not hold is checked at the end.
/// </summary>
public class ProcurementEndToEndTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProcurementService _vessel = null!;
    private ProcurementService _shore = null!;
    private ApprovalService _vesselApprovals = null!;
    private ApprovalService _shoreApprovals = null!;
    private DispatchService _vesselDispatch = null!;
    private DispatchService _shoreDispatch = null!;
    private DeliveryService _vesselDeliveries = null!;
    private Guid _supplierA, _supplierB;
    private CrewMember _master = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new ProcurementService(_s.VesselFactory);
        _shore = new ProcurementService(_s.ShoreFactory);
        _vesselApprovals = new ApprovalService(_s.VesselFactory);
        _shoreApprovals = new ApprovalService(_s.ShoreFactory);
        _vesselDispatch = new DispatchService(_s.VesselFactory, new PoDocumentService(_s.VesselFactory));
        _shoreDispatch = new DispatchService(_s.ShoreFactory, new PoDocumentService(_s.ShoreFactory));
        _vesselDeliveries = new DeliveryService(_s.VesselFactory);

        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var suppliers = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).Take(2).ToListAsync();
        (_supplierA, _supplierB) = (suppliers[0], suppliers[1]);
        db.VesselPolicies.Add(new VesselPolicy { CompanyId = _s.CompanyId, VesselId = _s.VesselId, CanRequisition = true, CanPurchaseDirect = true, MasterApprovalLimitUsd = 1000, Note = "e2e" });
        await db.SaveChangesAsync();

        _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId,
            new CrewInput(null, "CAPT9", "Capt. Journey", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, true, IsMaster: true), _s.User);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, _master.Id, "Temp-pass-1", false, _s.User);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        return await read(db);
    }

    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read)
    {
        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        return await read(db);
    }

    private Actor MasterActor => new(_master.UserId, _master.FullName);

    [Fact]
    public async Task FromRequisitionToDelivery_ThroughEveryStep_AndAnOrderTheOfficeApproves()
    {
        // 1. The crew raise a requisition onboard; the office has it.
        var requisition = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("end to end"), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.NotNull(await _s.FindAsync(_s.ShoreFactory, requisition.Id));

        // 2. The vessel asks two suppliers to quote. The office portal shows the RFQ to them.
        var rfq = await _vessel.CreateRfqAsync(_s.CompanyId, new NewRfq
        {
            RequisitionId = requisition.Id, BuyerName = "Chief Officer", InvitedSupplierIds = [_supplierA, _supplierB],
            Lines = requisition.Lines.Select(l => new NewRfqLine { ItemId = l.ItemId, Quantity = l.Quantity, RequisitionLineId = l.Id }).ToList(),
        }, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.NotNull(await _shore.GetRfqForSupplierAsync(_supplierA, rfq.Id));

        // 3. Supplier A quotes through the portal (at the office); supplier B's quote arrives by email and is typed in onboard.
        QuoteInput Prices(decimal price) => new() { Currency = "USD", DeliveryDays = 5, Lines = rfq.Lines.Select(l => new QuoteLine { RfqLineId = l.Id, UnitPrice = price }).ToList() };
        await _shore.SubmitQuoteAsync(rfq.Id, _supplierA, Prices(5m));
        await _vessel.SubmitQuoteAsync(rfq.Id, _supplierB, Prices(7m));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, await OnVessel(db => db.Quotes.CountAsync(q => q.RfqId == rfq.Id)));
        Assert.Equal(2, await OnShore(db => db.Quotes.CountAsync(q => q.RfqId == rfq.Id)));
        Assert.Equal(RfqStatus.QuotesReceived, (await OnShore(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id))).Status);

        // 4. The vessel awards the cheaper supplier and raises the order; it is within the Master's limit, so the Master approves it.
        await _vessel.AwardLinesAsync(_s.CompanyId, rfq.Id, rfq.Lines.ToDictionary(l => l.Id, _ => _supplierA), _s.User, "lowest");
        var order = Assert.Single(await _vessel.RaisePurchaseOrdersAsync(_s.CompanyId, rfq.Id, _s.User));
        Assert.Equal(_supplierA, order.SupplierId);
        Assert.False(order.HandoverRequested);
        await _vesselApprovals.DecideAsync(_s.CompanyId, order.Id, MasterActor, true, "");
        await _s.SettleUntilQuietAsync();

        // 5. The office sees it approved onboard and flagged for review; the supplier's portal has it at once, and the supplier acknowledges.
        var atOffice = await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == order.Id));
        Assert.Equal(PoStatus.Approved, atOffice.Status);
        Assert.Equal(SyncStages.Vessel, atOffice.OwnerStage);
        Assert.True(atOffice.ForReview);
        Assert.Contains(await _shore.GetPurchaseOrdersForSupplierAsync(_supplierA), p => p.Id == order.Id);
        Assert.Equal("Sent through the system", (await _vesselDispatch.GetAsync(_s.CompanyId, order.Id)).Summary);
        await _shoreDispatch.AcknowledgeAsync(_supplierA, order.Id, "Sales Desk");

        // 6. The vessel also prepares an email to the supplier.
        await _vesselDispatch.RecordEmailedAsync(_s.CompanyId, order.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal("Acknowledged by the supplier", (await _vesselDispatch.GetAsync(_s.CompanyId, order.Id)).Summary);
        Assert.NotNull((await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == order.Id))).EmailedAt);

        // 7. The goods arrive in two parts; the office follows.
        NewDelivery Part(decimal fraction) => new()
        {
            ReceivedDate = DateTime.UtcNow.Date, Place = "Singapore",
            Lines = order.Lines.Select(l => new DeliveryOrderLine { ItemId = l.ItemId, Quantity = l.Quantity * fraction }).ToList(),
        };
        await _vesselDeliveries.RecordAsync(_s.CompanyId, order.Id, Part(0.5m), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(DeliveryStatus.Partial, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == order.Id))).DeliveryStatus);
        await _vesselDeliveries.RecordAsync(_s.CompanyId, order.Id, Part(0.5m), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(DeliveryStatus.Delivered, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == order.Id))).DeliveryStatus);
        Assert.Equal(DeliveryStatus.Delivered, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == order.Id))).DeliveryStatus);

        // 8. The office reviews it: kept at the office, nothing goes back.
        var reviewedVersion = await OnShore(db => db.PurchaseOrders.Where(p => p.Id == order.Id).Select(p => EF.Property<int>(p, "SyncVersion")).SingleAsync());
        await _shore.MarkOnboardReviewedAsync(_s.CompanyId, order.Id, _s.User);
        Assert.Equal(reviewedVersion, await OnShore(db => db.PurchaseOrders.Where(p => p.Id == order.Id).Select(p => EF.Property<int>(p, "SyncVersion")).SingleAsync()));

        // 9. A bigger order, above the Master's limit, goes to the office, which approves it through its own matrix and releases it.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.VesselPolicies.SingleAsync(p => p.VesselId == _s.VesselId)).MasterApprovalLimitUsd = 10;
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        var big = await _vessel.CreateSpotPoAsync(_s.CompanyId, new NewSpotPo
        {
            VesselId = _s.VesselId, DepartmentId = _s.DepartmentId, SupplierId = _supplierB, Currency = "USD",
            Lines = [new NewRfqLine { ItemId = _s.ItemIds[0], Quantity = 10, UnitPrice = 20m }],
        }, _s.User);
        Assert.True(big.HandoverRequested);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(SyncStages.Office, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == big.Id))).OwnerStage);
        for (var guard = 0; guard < 6; guard++)
        {
            var next = await OnShore(db => db.PoApprovals.Where(a => a.PurchaseOrderId == big.Id && a.Decision == ApprovalDecision.Pending).OrderBy(a => a.Level).FirstOrDefaultAsync());
            if (next is null) break;
            await _shoreApprovals.DecideAsync(_s.CompanyId, big.Id, new Actor(next.ApproverUserId, "Office approver"), true, "ok");
        }
        await _s.SettleUntilQuietAsync();
        Assert.Equal(PoStatus.Approved, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == big.Id))).Status);
        Assert.NotNull((await _vesselDispatch.GetAsync(_s.CompanyId, big.Id)).ViaSystemAt);

        // Everything is settled: nothing parked, waiting or left unsent, and the vessel holds only what it should.
        var node = _s.VesselNode.SyncNodeId!;
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.ShoreSyncStore.GetDeferredAsync(new StreamKey(node, Direction.Up)));
        Assert.Empty(await _s.VesselSyncStore.GetDeferredAsync(new StreamKey(node, Direction.Down)));
        Assert.Equal(0, await _s.DirtyAsync(_s.ShoreFactory));
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
        Assert.Equal(0, await OnVessel(db => db.SupplierBankAccounts.CountAsync()));
        Assert.Equal(1, await OnVessel(db => db.Vessels.CountAsync()));
        Assert.Equal(1, await OnVessel(db => db.Companies.CountAsync()));
    }
}
