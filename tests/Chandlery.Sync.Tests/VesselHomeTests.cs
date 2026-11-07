using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class SyncStatusTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARecentSuccess_IsShownAsSynced()
    {
        var view = SyncStatusText.Describe(Now, Now.AddSeconds(-10), 0, null, 0);
        Assert.Equal("Synced just now", view.Text);
        Assert.Equal("success", view.Tone);
        Assert.False(view.Offline);
    }

    [Fact]
    public void ChangesWaiting_AreCounted_AndWarn()
    {
        Assert.Equal("Synced 5 min ago · 1 change waiting", SyncStatusText.Describe(Now, Now.AddMinutes(-5), 0, null, 1).Text);
        var many = SyncStatusText.Describe(Now, Now.AddHours(-3), 1, "x", 14);
        Assert.Equal("Synced 3 h ago · 14 changes waiting", many.Text);
        Assert.Equal("warning", many.Tone);
        Assert.False(many.Offline);                    // one failed attempt is not yet "offline"
    }

    [Fact]
    public void RepeatedFailures_MeanOffline_WithTheLastSuccessAndWhatIsWaiting()
    {
        var view = SyncStatusText.Describe(Now, Now.AddHours(-30), 4, "no route", 3);
        Assert.True(view.Offline);
        Assert.Equal("danger", view.Tone);
        Assert.Equal("Offline · last synced 30 h ago · 3 changes waiting", view.Text);
        Assert.Equal("Offline · never connected", SyncStatusText.Describe(Now, null, 5, "no route", 0).Text);
    }

    [Fact]
    public void BeforeTheFirstExchange_SaysSo()
        => Assert.Equal("Not synced yet · 2 changes waiting", SyncStatusText.Describe(Now, null, 0, null, 2).Text);
}

/// <summary>The vessel home numbers and port-call view, worked out from what the vessel itself holds.</summary>
public class VesselHomeTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProcurementService _vessel = null!;
    private ApprovalService _vesselApprovals = null!;
    private VesselHomeService _home = null!;
    private Guid _supplier;
    private CrewMember _master = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new ProcurementService(_s.VesselFactory);
        _vesselApprovals = new ApprovalService(_s.VesselFactory);
        _home = new VesselHomeService(_s.VesselFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        _supplier = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).FirstAsync();
        db.VesselPolicies.Add(new VesselPolicy { CompanyId = _s.CompanyId, VesselId = _s.VesselId, CanRequisition = true, CanPurchaseDirect = true, MasterApprovalLimitUsd = 100, Note = "test" });
        await db.SaveChangesAsync();
        _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId,
            new CrewInput(null, "CAPT3", "Capt. Home", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, false, IsMaster: true), _s.User);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, _master.Id, "Temp-pass-1", false, _s.User);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private Task<PurchaseOrder> RaiseAsync(decimal price) => _vessel.CreateSpotPoAsync(_s.CompanyId, new NewSpotPo
    {
        VesselId = _s.VesselId, DepartmentId = _s.DepartmentId, SupplierId = _supplier, Currency = "USD",
        Lines = [new NewRfqLine { ItemId = _s.ItemIds[0], Quantity = 10, UnitPrice = price }],
    }, _s.User);

    [Fact]
    public async Task TheHome_CountsWhatTheVesselIsWaitingOn()
    {
        var empty = await _home.GetAsync(_s.CompanyId, _master.UserId);
        Assert.Equal(0, empty.OpenRequisitions);
        Assert.Empty(empty.Deliveries);

        var requisition = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("home"), _s.User);
        var rfq = await _vessel.CreateRfqAsync(_s.CompanyId, new NewRfq
        {
            RequisitionId = requisition.Id, BuyerName = "b", InvitedSupplierIds = [_supplier],
            Lines = requisition.Lines.Select(l => new NewRfqLine { ItemId = l.ItemId, Quantity = l.Quantity, RequisitionLineId = l.Id }).ToList(),
        }, _s.User);
        var afterRfq = await _home.GetAsync(_s.CompanyId, _master.UserId);
        Assert.Equal(1, afterRfq.RfqsAwaitingQuotes);
        Assert.Equal(0, afterRfq.QuotesToEvaluate);

        await _vessel.SubmitQuoteAsync(rfq.Id, _supplier, new QuoteInput { Currency = "USD", Lines = rfq.Lines.Select(l => new QuoteLine { RfqLineId = l.Id, UnitPrice = 3m }).ToList() });
        var afterQuote = await _home.GetAsync(_s.CompanyId, _master.UserId);
        Assert.Equal(0, afterQuote.RfqsAwaitingQuotes);
        Assert.Equal(1, afterQuote.QuotesToEvaluate);
        Assert.Equal(1, afterQuote.OpenRequisitions);

        var small = await RaiseAsync(5m);                                  // USD 50, within 100: the Master decides
        var large = await RaiseAsync(50m);                                 // USD 500, above 100: the office decides
        var home = await _home.GetAsync(_s.CompanyId, _master.UserId);
        Assert.Equal(1, home.AwaitingMaster);
        Assert.Equal(1, home.AwaitingOffice);
        Assert.Equal([small.Id], home.NeedsMyApproval.Select(o => o.Id));
        Assert.Empty((await _home.GetAsync(_s.CompanyId, _s.User.UserId)).NeedsMyApproval);   // someone else sees none

        await _vesselApprovals.DecideAsync(_s.CompanyId, small.Id, new Actor(_master.UserId, _master.FullName), true, "");
        var delivery = Assert.Single((await _home.GetAsync(_s.CompanyId, _master.UserId)).Deliveries);
        Assert.Equal(small.PoNumber, delivery.PoNumber);
        Assert.Null(delivery.Port);                                         // a direct order with no port named: the home says so
        Assert.NotEqual(Guid.Empty, large.Id);
    }

    [Fact]
    public async Task PortCalls_GroupWhatIsWaiting_ByPortAndEta()
    {
        var eta = DateTime.UtcNow.Date.AddDays(5);
        var input = _s.NewRequisition("for a port call");
        input.VesselEta = eta;
        var requisition = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, input, _s.User);

        var calls = await _home.PortCallsAsync(_s.CompanyId);

        var call = Assert.Single(calls);
        Assert.Equal(_s.PortId, call.PortId);
        Assert.Equal(eta, call.Eta!.Value.Date);
        Assert.Equal([requisition.Id], call.Requisitions.Select(r => r.Id));
        Assert.Empty(call.Orders);

        // An order raised without its own port is due where the vessel asked for delivery.
        var order = await RaiseAsync(5m);
        await _vesselApprovals.DecideAsync(_s.CompanyId, order.Id, new Actor(_master.UserId, _master.FullName), true, "");
        Assert.Equal(1, (await _home.PortCallsAsync(_s.CompanyId)).Sum(c => c.Orders.Count));
    }
}
