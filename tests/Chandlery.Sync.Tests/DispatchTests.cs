using System.Text;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Getting an order to the supplier: through the system (recorded, and the vessel is told), by an email the vessel prepares, and the supplier's acknowledgement.</summary>
public class DispatchTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProcurementService _vessel = null!;
    private ApprovalService _vesselApprovals = null!;
    private ApprovalService _shoreApprovals = null!;
    private DispatchService _vesselDispatch = null!;
    private DispatchService _shoreDispatch = null!;
    private Guid _supplier;
    private CrewMember _master = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new ProcurementService(_s.VesselFactory);
        _vesselApprovals = new ApprovalService(_s.VesselFactory);
        _shoreApprovals = new ApprovalService(_s.ShoreFactory);
        _vesselDispatch = new DispatchService(_s.VesselFactory, new PoDocumentService(_s.VesselFactory));
        _shoreDispatch = new DispatchService(_s.ShoreFactory, new PoDocumentService(_s.ShoreFactory));
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        _supplier = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).FirstAsync();
        db.VesselPolicies.Add(new VesselPolicy { CompanyId = _s.CompanyId, VesselId = _s.VesselId, CanRequisition = true, CanPurchaseDirect = true, MasterApprovalLimitUsd = 1000, Note = "test" });
        await db.SaveChangesAsync();
        _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId,
            new CrewInput(null, "CAPT2", "Capt. Dispatch", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, false, IsMaster: true), _s.User);
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

    private Task<PurchaseOrder> RaiseAsync(decimal price) => _vessel.CreateSpotPoAsync(_s.CompanyId, new NewSpotPo
    {
        VesselId = _s.VesselId, DepartmentId = _s.DepartmentId, SupplierId = _supplier, Currency = "USD",
        Lines = [new NewRfqLine { ItemId = _s.ItemIds[0], Quantity = 10, UnitPrice = price }],
    }, _s.User);

    private async Task<PurchaseOrder> ApprovedOnboardAsync()
    {
        var po = await RaiseAsync(5m);
        await _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, new Actor(_master.UserId, _master.FullName), true, "");
        await _s.SettleUntilQuietAsync();
        return po;
    }

    [Fact]
    public async Task AnOrderApprovedOnboard_IsAvailableToTheSupplierAtOnce_AndTheVesselIsTold()
    {
        var po = await ApprovedOnboardAsync();

        var atOffice = await OnShore(db => db.PoDispatches.SingleAsync(d => d.PurchaseOrderId == po.Id));
        Assert.NotNull(atOffice.ViaSystemAt);
        Assert.Null(atOffice.AcknowledgedAt);
        var onboard = await _vesselDispatch.GetAsync(_s.CompanyId, po.Id);
        Assert.NotNull(onboard.ViaSystemAt);
        Assert.Equal("Sent through the system", onboard.Summary);
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AnOrderHandedOver_IsReleasedWhenTheOfficeApprovesIt()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.VesselPolicies.SingleAsync(p => p.VesselId == _s.VesselId)).MasterApprovalLimitUsd = 10;
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        var po = await RaiseAsync(5m);                                   // USD 50 > 10: handed over
        await _s.SettleUntilQuietAsync();
        Assert.False(await OnShore(db => db.PoDispatches.AnyAsync(d => d.PurchaseOrderId == po.Id)));   // nothing released before it is approved

        for (var guard = 0; guard < 6; guard++)
        {
            var next = await OnShore(db => db.PoApprovals.Where(a => a.PurchaseOrderId == po.Id && a.Decision == ApprovalDecision.Pending).OrderBy(a => a.Level).FirstOrDefaultAsync());
            if (next is null) break;
            await _shoreApprovals.DecideAsync(_s.CompanyId, po.Id, new Actor(next.ApproverUserId, "Office approver"), true, "ok");
        }
        await _s.SettleUntilQuietAsync();

        Assert.NotNull((await OnShore(db => db.PoDispatches.SingleAsync(d => d.PurchaseOrderId == po.Id))).ViaSystemAt);
        Assert.NotNull((await _vesselDispatch.GetAsync(_s.CompanyId, po.Id)).ViaSystemAt);
    }

    [Fact]
    public async Task TheSupplierAcknowledges_AndTheVesselSeesIt()
    {
        var po = await ApprovedOnboardAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreDispatch.AcknowledgeAsync(Guid.NewGuid(), po.Id, "Someone else"));   // not their order
        await _shoreDispatch.AcknowledgeAsync(_supplier, po.Id, "Sales Desk");
        await _s.SettleUntilQuietAsync();

        var view = await _vesselDispatch.GetAsync(_s.CompanyId, po.Id);
        Assert.Equal("Acknowledged by the supplier", view.Summary);
        Assert.Equal("Sales Desk", view.AcknowledgedBy);
    }

    [Fact]
    public async Task TheVesselPreparesAnEmail_WithTheOfficeInCopy_AndTheOfficeLearnsOfIt()
    {
        var po = await ApprovedOnboardAsync();
        var supplierEmail = await OnVessel(db => db.Suppliers.Where(s => s.Id == _supplier).Select(s => s.ContactEmail).SingleAsync());
        var officeEmail = await OnVessel(db => db.Companies.Select(c => c.ContactEmail).SingleAsync());

        var eml = await _vesselDispatch.BuildEmlAsync(_s.CompanyId, po.Id);
        Assert.NotNull(eml);
        var text = Encoding.UTF8.GetString(eml.Value.Bytes);
        Assert.Equal($"{po.PoNumber}.eml", eml.Value.FileName);
        Assert.Contains("X-Unsent: 1", text);
        Assert.Contains($"To: {supplierEmail}", text);
        Assert.Contains($"Cc: {officeEmail}", text);
        Assert.Contains($"Subject: Purchase order {po.PoNumber}", text);
        var base64 = string.Concat(text.Split("Content-Disposition: attachment")[1].Split("\r\n\r\n")[1].Split("--")[0].Split("\r\n"));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(Convert.FromBase64String(base64)[..8]));    // a real PDF is attached

        await _vesselDispatch.RecordEmailedAsync(_s.CompanyId, po.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        var atOffice = await OnShore(db => db.PurchaseOrders.SingleAsync(p => p.Id == po.Id));
        Assert.NotNull(atOffice.EmailedAt);
        Assert.Equal(supplierEmail, atOffice.EmailedTo);
        Assert.Equal("Sent through the system and by email", (await _vesselDispatch.GetAsync(_s.CompanyId, po.Id)).Summary);
        Assert.Equal("Sent through the system and by email", (await _shoreDispatch.GetAsync(_s.CompanyId, po.Id)).Summary);
    }

    [Fact]
    public async Task ADeliveryOfAnOrderTheOfficeOwns_IsTheOfficesToSend_NotTheVessels()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.VesselPolicies.SingleAsync(p => p.VesselId == _s.VesselId)).MasterApprovalLimitUsd = 10;
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        var po = await RaiseAsync(5m);
        await _s.SettleUntilQuietAsync();                                  // handed over: the office owns it now

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreDispatch.ResendAsync(_s.CompanyId, po.Id, _s.User));   // not approved yet
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselDispatch.ResendAsync(_s.CompanyId, po.Id, _s.User));   // and never the vessel's to do
    }

    [Fact]
    public async Task TheOfficeCanReleaseItAgain_AndTheVesselSeesTheNewTime()
    {
        var po = await ApprovedOnboardAsync();
        var first = (await _vesselDispatch.GetAsync(_s.CompanyId, po.Id)).ViaSystemAt!.Value;
        await Task.Delay(30);

        await _shoreDispatch.ResendAsync(_s.CompanyId, po.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.True((await _vesselDispatch.GetAsync(_s.CompanyId, po.Id)).ViaSystemAt!.Value > first);
        Assert.Contains(await OnShore(db => db.AuditEntries.Where(a => a.EntityId == po.Id).Select(a => a.Text).ToListAsync()), t => t.Contains("again"));
    }

    [Fact]
    public async Task TheVesselCannotWriteDispatchRecords_AndOtherVesselsNeverReceiveThem()
    {
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.PoDispatches.Add(new PoDispatch { CompanyId = _s.CompanyId, PurchaseOrderId = Guid.NewGuid(), ViaSystemAt = DateTime.UtcNow });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        // An order for the other ship, acknowledged: its dispatch record never goes to this vessel.
        Guid otherPo;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var order = new PurchaseOrder
            {
                CompanyId = _s.CompanyId, PoNumber = "OTHER-PO-1", SupplierId = _supplier, CreatedDate = DateTime.UtcNow, Status = PoStatus.Approved,
                VesselId = _s.OtherVesselId, Currency = "USD", Lines = [new PoLine { ItemId = _s.ItemIds[0], Quantity = 1, UnitPrice = 1 }],
            };
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();
            otherPo = order.Id;
        }
        await _shoreDispatch.AcknowledgeAsync(_supplier, otherPo, "Sales Desk");
        await _s.SettleUntilQuietAsync();

        Assert.False(await OnVessel(db => db.PoDispatches.AnyAsync(d => d.PurchaseOrderId == otherPo)));
    }
}
