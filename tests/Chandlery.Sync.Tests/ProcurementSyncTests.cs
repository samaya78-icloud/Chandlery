using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Purchase orders go down to the vessel they are for; deliveries recorded onboard come up. Each side owns what it created.</summary>
public class ProcurementSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private DeliveryService _shoreDeliveries = null!;
    private DeliveryService _vesselDeliveries = null!;
    private int _counter;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreDeliveries = new DeliveryService(_s.ShoreFactory);
        _vesselDeliveries = new DeliveryService(_s.VesselFactory);
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

    /// <summary>An approved order raised at the office, for the given vessel, for three catalogue items.</summary>
    private async Task<PurchaseOrder> RaiseOrderAsync(Guid vesselId, bool announce = true)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApplyingSync = !announce;
        var supplier = await db.Suppliers.OrderBy(s => s.Name).FirstAsync();
        var po = new PurchaseOrder
        {
            CompanyId = _s.CompanyId, PoNumber = $"TEST-PO-{++_counter:D4}", SupplierId = supplier.Id, CreatedDate = DateTime.UtcNow,
            Status = PoStatus.Approved, VesselId = vesselId, DepartmentId = _s.DepartmentId, Currency = "USD",
            Lines = _s.ItemIds.Select((id, i) => new PoLine { ItemId = id, Quantity = 10 + i, UnitPrice = 2.5m + i, Uom = "Pcs" }).ToList(),
        };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();
        return po;
    }

    private static NewDelivery Delivery(PurchaseOrder po, decimal fraction, string place = "Singapore")
        => new() { ReceivedDate = DateTime.UtcNow.Date, Place = place, Lines = po.Lines.Select(l => new DeliveryOrderLine { ItemId = l.ItemId, Quantity = l.Quantity * fraction }).ToList() };

    [Fact]
    public async Task AnOrderRaisedAtTheOffice_ReachesItsVessel_WithPricesAndTheSupplier()
    {
        var po = await RaiseOrderAsync(_s.VesselId);
        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id));
        Assert.Equal(po.PoNumber, onboard.PoNumber);
        Assert.Equal(PoStatus.Approved, onboard.Status);
        Assert.Equal(po.Lines.Count, onboard.Lines.Count);
        Assert.Equal(po.Total, onboard.Total);                                         // prices travel: the vessel sees its own orders in full
        Assert.Equal(SyncStages.Office, onboard.OwnerStage);
        Assert.Equal(await OnShore(db => db.Suppliers.Where(s => s.Id == po.SupplierId).Select(s => s.Name).SingleAsync()),
            await OnVessel(db => db.Suppliers.Where(s => s.Id == po.SupplierId).Select(s => s.Name).SingleAsync()));
        Assert.Equal(0, await OnVessel(db => db.SupplierBankAccounts.CountAsync()));   // bank details never reach a vessel
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));                        // applying it was not announced back
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task OrdersForOtherVessels_NeverArrive()
    {
        var mine = await RaiseOrderAsync(_s.VesselId);
        var theirs = await RaiseOrderAsync(_s.OtherVesselId);
        await _s.SettleUntilQuietAsync();

        var ids = await OnVessel(db => db.PurchaseOrders.Select(p => p.Id).ToListAsync());
        Assert.Equal([mine.Id], ids);
        Assert.DoesNotContain(theirs.Id, ids);
    }

    [Fact]
    public async Task ADeliveryRecordedOnboard_ReachesTheOffice_AndBothSidesWorkOutTheStatus()
    {
        var po = await RaiseOrderAsync(_s.VesselId);
        await _s.SettleUntilQuietAsync();

        var partial = await _vesselDeliveries.RecordAsync(_s.CompanyId, po.Id, Delivery(po, 0.5m), _s.User);
        await _s.SettleUntilQuietAsync();

        var atOffice = await OnShore(db => db.DeliveryOrders.SingleAsync(d => d.Id == partial.Id));
        Assert.Equal(SyncStages.Vessel, atOffice.OwnerStage);
        Assert.Equal(partial.DoNumber, atOffice.DoNumber);
        Assert.Equal(po.Lines.Count, atOffice.Lines.Count);
        Assert.Equal(DeliveryStatus.Partial, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).DeliveryStatus);
        Assert.Equal(DeliveryStatus.Partial, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).DeliveryStatus);
        Assert.Contains(await OnShore(db => db.AuditEntries.Where(a => a.EntityId == po.Id).Select(a => a.Text).ToListAsync()), t => t.Contains(partial.DoNumber) && t.Contains("vessel"));

        // The delivery number is the vessel's own (no office lane letter), so both sides can number without colliding.
        Assert.DoesNotContain("-H", partial.DoNumber);

        // The rest arrives; the office order is now fully delivered, and nothing about the order itself was re-sent.
        var orderVersion = await OnShore(db => db.PurchaseOrders.Where(p => p.Id == po.Id).Select(p => EF.Property<int>(p, "SyncVersion")).SingleAsync());
        await _vesselDeliveries.RecordAsync(_s.CompanyId, po.Id, Delivery(po, 0.5m), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(DeliveryStatus.Delivered, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).DeliveryStatus);
        Assert.Equal(DeliveryStatus.Delivered, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).DeliveryStatus);
        Assert.Equal(orderVersion, await OnShore(db => db.PurchaseOrders.Where(p => p.Id == po.Id).Select(p => EF.Property<int>(p, "SyncVersion")).SingleAsync()));
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ADeliveryRecordedAtTheOffice_ReachesTheVessel()
    {
        var po = await RaiseOrderAsync(_s.VesselId);
        await _s.SettleUntilQuietAsync();

        var made = await _shoreDeliveries.RecordAsync(_s.CompanyId, po.Id, Delivery(po, 1m, "Fujairah"), _s.User);
        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.DeliveryOrders.SingleAsync(d => d.Id == made.Id));
        Assert.Equal(SyncStages.Office, onboard.OwnerStage);
        Assert.Equal("Fujairah", onboard.Place);
        Assert.Equal(DeliveryStatus.Delivered, (await OnVessel(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).DeliveryStatus);
    }

    [Fact]
    public async Task EachSideOwnsWhatItCreated()
    {
        var po = await RaiseOrderAsync(_s.VesselId);
        await _s.SettleUntilQuietAsync();
        var fromShip = await _vesselDeliveries.RecordAsync(_s.CompanyId, po.Id, Delivery(po, 0.25m), _s.User);
        var fromOffice = await _shoreDeliveries.RecordAsync(_s.CompanyId, po.Id, Delivery(po, 0.25m), _s.User);
        await _s.SettleUntilQuietAsync();

        // The vessel cannot change the office's order or the office's delivery, nor remove it.
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            (await db.PurchaseOrders.SingleAsync(p => p.Id == po.Id)).Remarks = "edited onboard";
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("office", error.Message);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselDeliveries.DeleteAsync(_s.CompanyId, fromOffice.Id, _s.User));

        // The office cannot change or remove what the vessel recorded.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreDeliveries.DeleteAsync(_s.CompanyId, fromShip.Id, _s.User));
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.DeliveryOrders.SingleAsync(d => d.Id == fromShip.Id)).Remarks = "edited at the office";
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("belongs to the vessel", error.Message);
        }

        // But the invoice lock is the office's own business and does not travel: it can mark the vessel's delivery without a new version.
        var version = await OnShore(db => db.DeliveryOrders.Where(d => d.Id == fromShip.Id).Select(d => EF.Property<int>(d, "SyncVersion")).SingleAsync());
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var invoice = new Invoice { CompanyId = _s.CompanyId, InvoiceNumber = "TEST-INV-1", SupplierId = po.SupplierId, PurchaseOrderId = po.Id, InvoiceDate = DateTime.UtcNow, Currency = "USD" };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            (await db.DeliveryOrders.SingleAsync(d => d.Id == fromShip.Id)).InvoiceId = invoice.Id;
            await db.SaveChangesAsync();
        }
        Assert.Equal(version, await OnShore(db => db.DeliveryOrders.Where(d => d.Id == fromShip.Id).Select(d => EF.Property<int>(d, "SyncVersion")).SingleAsync()));
        await _s.SettleUntilQuietAsync();
        Assert.Null((await OnVessel(db => db.DeliveryOrders.SingleAsync(d => d.Id == fromShip.Id))).InvoiceId);
    }

    [Fact]
    public async Task ADeliveryTheVesselRemoves_IsRemovedAtTheOffice_AndTheStatusGoesBack()
    {
        var po = await RaiseOrderAsync(_s.VesselId);
        await _s.SettleUntilQuietAsync();
        var made = await _vesselDeliveries.RecordAsync(_s.CompanyId, po.Id, Delivery(po, 1m), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(DeliveryStatus.Delivered, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).DeliveryStatus);

        await _vesselDeliveries.DeleteAsync(_s.CompanyId, made.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.False(await OnShore(db => db.DeliveryOrders.AnyAsync(d => d.Id == made.Id)));
        Assert.Equal(DeliveryStatus.Pending, (await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id))).DeliveryStatus);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ADeliveryArrivingBeforeItsOrder_WaitsForIt()
    {
        var po = await RaiseOrderAsync(_s.VesselId, announce: false);       // saved without notes: this test sends the documents itself
        Guid deliveryId;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;
            var delivery = new DeliveryOrder
            {
                CompanyId = _s.CompanyId, DoNumber = "TEST-DO-1", PurchaseOrderId = po.Id, SupplierId = po.SupplierId, ReceivedDate = DateTime.UtcNow.Date,
                CreatedAt = DateTime.UtcNow, Lines = [new DeliveryOrderLine { ItemId = po.Lines[0].ItemId, Quantity = 1 }],
            };
            db.DeliveryOrders.Add(delivery);
            await db.SaveChangesAsync();
            deliveryId = delivery.Id;

            var node = _s.VesselNode.SyncNodeId!;
            var dto = Aggregates.Find(typeof(DeliveryOrder))!;
            var pto = Aggregates.Find(typeof(PurchaseOrder))!;
            await _s.Hub.PublishToNodeAsync(node, Aggregates.Document(dto, delivery.Id, 1, Aggregates.ToJson(db, delivery), SyncStages.Office, 1, null), Lane.Transaction);
            await _s.Hub.PublishToNodeAsync(node, Aggregates.Document(pto, po.Id, 1, Aggregates.ToJson(db, await db.PurchaseOrders.SingleAsync(p => p.Id == po.Id)), SyncStages.Office, 1, null), Lane.Transaction);
        }

        await _s.SettleUntilQuietAsync();

        Assert.True(await OnVessel(db => db.PurchaseOrders.AnyAsync(p => p.Id == po.Id)));
        Assert.True(await OnVessel(db => db.DeliveryOrders.AnyAsync(d => d.Id == deliveryId)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AVesselStartedFromNothing_AlsoReceivesTheOrdersAlreadyThere()
    {
        var po = await RaiseOrderAsync(_s.VesselId, announce: false);       // an order that existed before this vessel connected
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        Assert.True(await OnVessel(db => db.PurchaseOrders.AnyAsync(p => p.Id == po.Id)));
    }

    [Fact]
    public async Task ANewlyLinkedSupplier_ReachesTheVessels()
    {
        Guid supplierId;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var supplier = new Supplier { Name = "Late Supplier Ltd", ContactEmail = "late@example.com" };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();
            db.CompanySuppliers.Add(new CompanySupplier { CompanyId = _s.CompanyId, SupplierId = supplier.Id, AddedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            supplierId = supplier.Id;
        }
        await _s.SettleUntilQuietAsync();

        Assert.Equal("Late Supplier Ltd", await OnVessel(db => db.Suppliers.Where(s => s.Id == supplierId).Select(s => s.Name).SingleAsync()));
        Assert.True(await OnVessel(db => db.CompanySuppliers.AnyAsync(l => l.SupplierId == supplierId)));
    }
}
