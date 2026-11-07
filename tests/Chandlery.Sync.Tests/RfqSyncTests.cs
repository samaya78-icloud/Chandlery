using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// RFQs and quotes. An RFQ raised onboard stays with the vessel (it compares quotes and awards) while suppliers quote through the
/// office; the office's own RFQs never share their quotes with a vessel.
/// </summary>
public class RfqSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProcurementService _shore = null!;
    private ProcurementService _vessel = null!;
    private Guid _supplierA, _supplierB;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProcurementService(_s.ShoreFactory);
        _vessel = new ProcurementService(_s.VesselFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var suppliers = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).Take(2).ToListAsync();
        (_supplierA, _supplierB) = (suppliers[0], suppliers[1]);
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

    private static NewRfq RfqFrom(Requisition requisition, params Guid[] suppliers) => new()
    {
        RequisitionId = requisition.Id,
        BuyerName = "Test buyer",
        InvitedSupplierIds = suppliers.ToList(),
        Lines = requisition.Lines.Select(l => new NewRfqLine { ItemId = l.ItemId, Quantity = l.Quantity, RequisitionLineId = l.Id }).ToList(),
    };

    private static QuoteInput PriceAll(Rfq rfq, decimal price) => new()
    {
        Currency = "USD", SupplierRef = "Q-1", DeliveryDays = 3,
        Lines = rfq.Lines.Select(l => new QuoteLine { RfqLineId = l.Id, UnitPrice = price }).ToList(),
    };

    /// <summary>A requisition raised onboard and already at the office, and an RFQ raised from it onboard.</summary>
    private async Task<(Requisition Requisition, Rfq Rfq)> VesselRfqAsync(params Guid[] suppliers)
    {
        var requisition = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("for an RFQ"), _s.User);
        await _s.SettleUntilQuietAsync();
        var rfq = await _vessel.CreateRfqAsync(_s.CompanyId, RfqFrom(requisition, suppliers.Length == 0 ? [_supplierA, _supplierB] : suppliers), _s.User);
        await _s.SettleUntilQuietAsync();
        return (requisition, rfq);
    }

    [Fact]
    public async Task AnRfqRaisedOnboard_ReachesTheOffice_WhereTheSuppliersSeeIt_AndStaysWithTheVessel()
    {
        var (requisition, rfq) = await VesselRfqAsync();

        var atOffice = await OnShore(db => db.Rfqs.Include(r => r.Lines).SingleAsync(r => r.Id == rfq.Id));
        Assert.Equal(SyncStages.Vessel, atOffice.OwnerStage);
        Assert.Equal(rfq.RfqNumber, atOffice.RfqNumber);
        Assert.Equal(rfq.Lines.Count, atOffice.Lines.Count);
        Assert.Equal(rfq.Lines.Select(l => l.Id).Order(), atOffice.Lines.Select(l => l.Id).Order());   // lines keep their ids, quotes refer to them
        Assert.Equal(new[] { _supplierA, _supplierB }.Order(), atOffice.InvitedSupplierIds.Order());
        Assert.NotNull(await _shore.GetRfqForSupplierAsync(_supplierA, rfq.Id));                         // the supplier portal shows it
        Assert.Equal(RequisitionStatus.Open, (await OnVessel(db => db.Requisitions.SingleAsync(r => r.Id == requisition.Id))).Status);  // the requisition was not touched
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ASuppliersQuote_ReachesTheVessel_AndTheRfqStatusFollowsBothWays()
    {
        var (_, rfq) = await VesselRfqAsync();

        await _shore.SubmitQuoteAsync(rfq.Id, _supplierA, PriceAll(rfq, 5m));       // through the office portal
        await _s.SettleUntilQuietAsync();

        var quote = await OnVessel(db => db.Quotes.SingleAsync(q => q.RfqId == rfq.Id));
        Assert.Equal(_supplierA, quote.SupplierId);
        Assert.Equal(SyncStages.Office, quote.OwnerStage);
        Assert.Equal(rfq.Lines.Count, quote.Lines.Count);
        Assert.Equal(RfqStatus.QuotesReceived, (await OnVessel(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id))).Status);   // the vessel notes it ...
        Assert.Equal(RfqStatus.QuotesReceived, (await OnShore(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id))).Status);     // ... and tells the office
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AQuoteEnteredOnboard_ReachesTheOffice()
    {
        var (_, rfq) = await VesselRfqAsync();

        await _vessel.SubmitQuoteAsync(rfq.Id, _supplierB, PriceAll(rfq, 7m));      // received by email or phone, typed in on the ship
        await _s.SettleUntilQuietAsync();

        var atOffice = await OnShore(db => db.Quotes.SingleAsync(q => q.RfqId == rfq.Id && q.SupplierId == _supplierB));
        Assert.Equal(SyncStages.Vessel, atOffice.OwnerStage);
        Assert.Equal(rfq.Lines.Count, atOffice.Lines.Count);
        Assert.Equal(7m, atOffice.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task TheVesselAwardsOnboard_AndTheOfficeSeesTheWinners()
    {
        var (_, rfq) = await VesselRfqAsync();
        await _shore.SubmitQuoteAsync(rfq.Id, _supplierA, PriceAll(rfq, 5m));
        await _vessel.SubmitQuoteAsync(rfq.Id, _supplierB, PriceAll(rfq, 7m));
        await _s.SettleUntilQuietAsync();
        var version = await OnShore(db => db.Rfqs.Where(r => r.Id == rfq.Id).Select(r => EF.Property<int>(r, "SyncVersion")).SingleAsync());

        var set = await _vessel.AwardLinesAsync(_s.CompanyId, rfq.Id, rfq.Lines.ToDictionary(l => l.Id, _ => _supplierA), _s.User, "lowest total");
        await _s.SettleUntilQuietAsync();

        Assert.Equal(rfq.Lines.Count, set);
        var atOffice = await OnShore(db => db.Rfqs.Include(r => r.Lines).SingleAsync(r => r.Id == rfq.Id));
        Assert.All(atOffice.Lines, l => Assert.Equal(_supplierA, l.WinningSupplierId));     // a change to the lines alone is a new version of the RFQ
        Assert.True(await OnShore(db => db.Rfqs.Where(r => r.Id == rfq.Id).Select(r => EF.Property<int>(r, "SyncVersion")).SingleAsync()) > version);
    }

    [Fact]
    public async Task TheOfficeCannotChangeAVesselRfq_UntilItTakesItOver()
    {
        var (_, rfq) = await VesselRfqAsync();
        await _vessel.SubmitQuoteAsync(rfq.Id, _supplierB, PriceAll(rfq, 7m));
        await _s.SettleUntilQuietAsync();

        var actor = _s.User;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AwardLineAsync(_s.CompanyId, rfq.Id, rfq.Lines[0].Id, _supplierB, actor));

        await _shore.TakeOverRfqAsync(_s.CompanyId, rfq.Id, actor);
        await _s.SettleUntilQuietAsync();
        var node = _s.VesselNode.SyncNodeId!;
        var diag = $"deferred {(await _s.VesselSyncStore.GetDeferredAsync(new StreamKey(node, Direction.Down))).Count} quarantine {(await _s.VesselSyncStore.GetQuarantineAsync()).Count} "
            + string.Join("|", (await _s.VesselSyncStore.GetQuarantineAsync()).Select(q => q.Error))
            + $" officeStage {(await OnShore(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id))).OwnerStage} officeV {await OnShore(db => db.Rfqs.Where(r => r.Id == rfq.Id).Select(r => EF.Property<int>(r, "SyncVersion")).SingleAsync())} vesselV {await OnVessel(db => db.Rfqs.Where(r => r.Id == rfq.Id).Select(r => EF.Property<int>(r, "SyncVersion")).SingleAsync())} applied {await _s.VesselSyncStore.GetAppliedVersionAsync("Rfq", rfq.Id)}";
        Assert.True((await OnVessel(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id))).OwnerStage == SyncStages.Office, diag);   // the vessel now holds a read-only copy

        await _shore.AwardLineAsync(_s.CompanyId, rfq.Id, rfq.Lines[0].Id, _supplierB, actor);                               // the office may now
        await _s.SettleUntilQuietAsync();
        Assert.Equal(_supplierB, (await OnVessel(db => db.Rfqs.Include(r => r.Lines).SingleAsync(r => r.Id == rfq.Id))).Lines.First(l => l.Id == rfq.Lines[0].Id).WinningSupplierId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AwardLineAsync(_s.CompanyId, rfq.Id, rfq.Lines[0].Id, _supplierA, actor));   // and the vessel may not
        Assert.Equal(1, await OnShore(db => db.AuditEntries.CountAsync(a => a.EntityId == rfq.Id && a.Text.StartsWith("TOOK OVER"))));
    }

    [Fact]
    public async Task AnRfqTheOfficeRunsItself_ReachesTheVessel_ButItsQuotesNeverDo()
    {
        var requisition = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("raised ashore"), _s.User);
        await _s.SettleUntilQuietAsync();
        var rfq = await _shore.CreateRfqAsync(_s.CompanyId, RfqFrom(requisition, _supplierA), _s.User);
        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.Rfqs.SingleAsync(r => r.Id == rfq.Id));
        Assert.Equal(SyncStages.Office, onboard.OwnerStage);                                 // a summary for the requisition's lineage

        await _shore.SubmitQuoteAsync(rfq.Id, _supplierA, PriceAll(rfq, 4m));
        await _s.SettleUntilQuietAsync();

        Assert.Equal(0, await OnVessel(db => db.Quotes.CountAsync()));                       // supplier prices on the office's RFQs stay at the office
        Assert.Equal(1, await OnShore(db => db.Quotes.CountAsync(q => q.RfqId == rfq.Id)));
    }

    [Fact]
    public async Task RfqsForOtherVessels_NeverArrive()
    {
        var requisition = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("other ship", _s.OtherVesselId), _s.User);
        var rfq = await _shore.CreateRfqAsync(_s.CompanyId, RfqFrom(requisition, _supplierA), _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.False(await OnVessel(db => db.Rfqs.AnyAsync(r => r.Id == rfq.Id)));
    }

    [Fact]
    public async Task DirectPurchasingOnboard_IsRefusedWhenThePolicyDoesNotAllowIt()
    {
        var requisition = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("policy"), _s.User);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselPolicies.Add(new VesselPolicy { CompanyId = _s.CompanyId, VesselId = _s.VesselId, CanRequisition = true, CanPurchaseDirect = false, Note = "office buys" });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CreateRfqAsync(_s.CompanyId, RfqFrom(requisition, _supplierA), _s.User));
        Assert.Contains("Policy", error.Message);
        Assert.Equal(0, await OnVessel(db => db.Rfqs.CountAsync()));
    }
}
