using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// Direct purchasing onboard. The Master approves within the limit and monthly budget the office sets; anything above, or with no limit
/// set, or with no Master aboard, is handed over to the office and approved through its matrix.
/// </summary>
public class OnboardPurchasingTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProcurementService _vessel = null!;
    private ProcurementService _shore = null!;
    private ApprovalService _vesselApprovals = null!;
    private ApprovalService _shoreApprovals = null!;
    private Guid _supplier;
    private CrewMember? _master;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new ProcurementService(_s.VesselFactory);
        _shore = new ProcurementService(_s.ShoreFactory);
        _vesselApprovals = new ApprovalService(_s.VesselFactory);
        _shoreApprovals = new ApprovalService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        _supplier = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).FirstAsync();
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

    /// <summary>The office's rules for the vessel (a catch-all row) and, optionally, a Master aboard.</summary>
    private async Task SetUpAsync(decimal? limit, decimal? budget = null, bool master = true, bool policyRows = true)
    {
        if (policyRows)
        {
            await using var db = await _s.ShoreFactory.CreateDbContextAsync();
            db.VesselPolicies.Add(new VesselPolicy
            {
                CompanyId = _s.CompanyId, VesselId = _s.VesselId, CanRequisition = true, CanPurchaseDirect = true,
                MasterApprovalLimitUsd = limit, MonthlyBudgetUsd = budget, Note = "test",
            });
            await db.SaveChangesAsync();
        }
        if (master)
        {
            _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId,
                new CrewInput(null, "CAPT1", "Capt. Test", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, false, IsMaster: true), _s.User);
            await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, _master.Id, "Temp-pass-1", false, _s.User);
        }
        await _s.SettleUntilQuietAsync();
    }

    private NewSpotPo Order(decimal price = 5m, string? emergency = null) => new()
    {
        VesselId = _s.VesselId, DepartmentId = _s.DepartmentId, SupplierId = _supplier, Currency = "USD", EmergencyJustification = emergency ?? "",
        Lines = [new NewRfqLine { ItemId = _s.ItemIds[0], Quantity = 10, UnitPrice = price }],
    };

    private Actor MasterActor => new(_master!.UserId, _master.FullName);

    private async Task ApproveAtOfficeAsync(Guid poId)
    {
        for (var guard = 0; guard < 6; guard++)
        {
            var next = await OnShore(db => db.PoApprovals.Where(a => a.PurchaseOrderId == poId && a.Decision == ApprovalDecision.Pending).OrderBy(a => a.Level).FirstOrDefaultAsync());
            if (next is null) return;
            await _shoreApprovals.DecideAsync(_s.CompanyId, poId, new Actor(next.ApproverUserId, "Office approver"), true, "ok");
        }
    }

    [Fact]
    public async Task WithinTheMastersLimit_TheMasterApprovesOnboard_AndTheOfficeSeesItForReview()
    {
        await SetUpAsync(limit: 1000);
        var po = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(5m), _s.User);                 // USD 50

        Assert.False(po.HandoverRequested);
        Assert.Equal(PoStatus.PendingApproval, po.Status);
        Assert.Equal(1, await OnVessel(db => db.PoApprovals.CountAsync(a => a.PurchaseOrderId == po.Id && a.ApproverUserId == _master!.UserId)));

        await _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, MasterActor, true, "");
        await _s.SettleUntilQuietAsync();

        var atOffice = await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id));
        Assert.Equal(SyncStages.Vessel, atOffice.OwnerStage);
        Assert.Equal(PoStatus.Approved, atOffice.Status);
        Assert.Equal("Capt. Test", atOffice.ApprovedOnboardByName);
        Assert.True(atOffice.ForReview);
        Assert.Contains(await _shore.GetPurchaseOrdersForSupplierAsync(_supplier), p => p.Id == po.Id);   // the supplier portal shows it

        // The office marks it reviewed: kept at the office, no new version, never sent back.
        var version = await OnShore(db => db.PurchaseOrders.Where(p => p.Id == po.Id).Select(p => EF.Property<int>(p, "SyncVersion")).SingleAsync());
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var row = await db.PurchaseOrders.SingleAsync(p => p.Id == po.Id);
            row.ReviewedAt = DateTime.UtcNow;
            row.ReviewedByName = "Office";
            await db.SaveChangesAsync();
        }
        Assert.Equal(version, await OnShore(db => db.PurchaseOrders.Where(p => p.Id == po.Id).Select(p => EF.Property<int>(p, "SyncVersion")).SingleAsync()));
        await _s.SettleUntilQuietAsync();
        Assert.Null((await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).ReviewedAt);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AboveTheLimit_TheOrderIsHandedOver_AndTheOfficeApprovesItThroughItsMatrix()
    {
        await SetUpAsync(limit: 10);
        var po = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(5m), _s.User);                 // USD 50 > 10

        Assert.True(po.HandoverRequested);
        Assert.Equal(0, await OnVessel(db => db.PoApprovals.CountAsync(a => a.PurchaseOrderId == po.Id)));   // no Master slot: it is not his to approve

        await _s.SettleUntilQuietAsync();
        var atOffice = await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id));
        Assert.Equal(SyncStages.Office, atOffice.OwnerStage);                                        // the office took it over
        Assert.Equal(SyncStages.Office, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).OwnerStage);   // and told the vessel

        await ApproveAtOfficeAsync(po.Id);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(PoStatus.Approved, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).Status);
        Assert.Equal(PoStatus.Approved, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).Status);

        // From the hand-over on, the vessel cannot change it.
        await using var vdb = await _s.VesselFactory.CreateDbContextAsync();
        (await vdb.PurchaseOrders.SingleAsync(p => p.Id == po.Id)).Remarks = "edited onboard";
        await Assert.ThrowsAsync<InvalidOperationException>(() => vdb.SaveChangesAsync());
    }

    [Fact]
    public async Task AnEmergency_LetsTheMasterApproveAboveTheLimit_WithAJustification_AndTheOfficeReviewsIt()
    {
        await SetUpAsync(limit: 10);
        var po = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(5m, emergency: "Steering gear failure, spare needed before departure"), _s.User);

        Assert.False(po.HandoverRequested);
        Assert.True(po.IsEmergency);
        await _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, MasterActor, true, "");
        await _s.SettleUntilQuietAsync();

        var atOffice = await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id));
        Assert.Equal(PoStatus.Approved, atOffice.Status);
        Assert.Equal(SyncStages.Vessel, atOffice.OwnerStage);
        Assert.True(atOffice.IsEmergency);
        Assert.True(atOffice.ForReview);
        Assert.Contains("Steering gear", atOffice.EmergencyJustification);
    }

    [Fact]
    public async Task TheMonthlyBudget_CountsEarlierOrders_AndHandsOverWhenItWouldBeExceeded()
    {
        await SetUpAsync(limit: 1000, budget: 60);
        var first = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(5m), _s.User);              // USD 50 of 60
        var second = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(5m), _s.User);             // 100 of 60

        Assert.False(first.HandoverRequested);
        Assert.True(second.HandoverRequested);
        Assert.Contains("monthly budget", (await OnVessel(db => db.AuditEntries.Where(a => a.EntityId == second.Id).Select(a => a.Text).ToListAsync())).First(t => t.StartsWith("HANDED OVER")));
    }

    [Fact]
    public async Task WithNoLimitSetOrNoMaster_TheOfficeDecides()
    {
        // A policy row that allows buying but states no limit: the Master has no authority.
        await SetUpAsync(limit: null);
        var noLimit = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(), _s.User);
        Assert.True(noLimit.HandoverRequested);
    }

    [Fact]
    public async Task WithNoMasterAboard_TheOrderIsHandedOver_EvenWithinTheLimit()
    {
        await SetUpAsync(limit: 1000, master: false);
        var po = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(), _s.User);
        Assert.True(po.HandoverRequested);
    }

    [Fact]
    public async Task OnlyTheMaster_CanApproveAnOrderTheMasterIsToApprove()
    {
        await SetUpAsync(limit: 1000);
        var po = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(), _s.User);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, _s.User, true, ""));
        Assert.Contains("not your turn", error.Message);
        Assert.Equal(PoStatus.PendingApproval, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).Status);

        // The Master can also refuse, with a reason, and the office learns of it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, MasterActor, false, ""));
        await _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, MasterActor, false, "Not needed after all");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(PoStatus.Rejected, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).Status);
    }

    [Fact]
    public async Task TheOfficeCannotChangeAnOrderTheVesselStillOwns()
    {
        await SetUpAsync(limit: 1000);
        var po = await _vessel.CreateSpotPoAsync(_s.CompanyId, Order(), _s.User);
        await _s.SettleUntilQuietAsync();

        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        (await db.PurchaseOrders.SingleAsync(p => p.Id == po.Id)).Remarks = "edited at the office";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("belongs to the vessel", error.Message);
    }

    [Fact]
    public async Task AnAwardOnboard_BecomesAnOrder_WithTheQuotedPrices()
    {
        await SetUpAsync(limit: 1000);
        var requisition = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("for an order"), _s.User);
        await _s.SettleUntilQuietAsync();
        var rfq = await _vessel.CreateRfqAsync(_s.CompanyId, new NewRfq
        {
            RequisitionId = requisition.Id, BuyerName = "buyer", InvitedSupplierIds = [_supplier],
            Lines = requisition.Lines.Select(l => new NewRfqLine { ItemId = l.ItemId, Quantity = l.Quantity, RequisitionLineId = l.Id }).ToList(),
        }, _s.User);
        await _vessel.SubmitQuoteAsync(rfq.Id, _supplier, new QuoteInput { Currency = "USD", Lines = rfq.Lines.Select(l => new QuoteLine { RfqLineId = l.Id, UnitPrice = 2m }).ToList() });
        await _vessel.AwardLinesAsync(_s.CompanyId, rfq.Id, rfq.Lines.ToDictionary(l => l.Id, _ => _supplier), _s.User, "test");

        var orders = await _vessel.RaisePurchaseOrdersAsync(_s.CompanyId, rfq.Id, _s.User);

        var po = Assert.Single(orders);
        Assert.Equal(rfq.Lines.Count, po.Lines.Count);
        Assert.All(po.Lines, l => Assert.Equal(2m, l.UnitPrice));
        Assert.Equal(PoStatus.PendingApproval, po.Status);                                           // with the Master
        Assert.Equal(RfqStatus.Awarded, (await OnVessel(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id))).Status);
        Assert.Equal(RequisitionStatus.Open, (await OnVessel(db => db.Requisitions.SingleAsync(r => r.Id == requisition.Id))).Status);   // the requisition is the office's to close

        await _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, MasterActor, true, "");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(PoStatus.Approved, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).Status);
        Assert.Equal(RfqStatus.Awarded, (await OnShore(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id))).Status);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }
}
