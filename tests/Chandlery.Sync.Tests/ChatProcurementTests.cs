using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The lines the purchasing screens write into a record's conversation (approved, delivered, cancelled), and that they appear on both sides and only where conversations are on.</summary>
public class ChatProcurementTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProcurementService _vessel = null!;
    private ApprovalService _vesselApprovals = null!;
    private DeliveryService _vesselDeliveries = null!;
    private ChatService _shoreChat = null!, _vesselChat = null!;
    private Guid _supplier;
    private CrewMember _master = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new ProcurementService(_s.VesselFactory);
        _vesselApprovals = new ApprovalService(_s.VesselFactory);
        _vesselDeliveries = new DeliveryService(_s.VesselFactory);
        _shoreChat = new ChatService(_s.ShoreFactory);
        _vesselChat = new ChatService(_s.VesselFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        _supplier = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).FirstAsync();
        db.VesselPolicies.Add(new VesselPolicy { CompanyId = _s.CompanyId, VesselId = _s.VesselId, CanRequisition = true, CanPurchaseDirect = true, MasterApprovalLimitUsd = 1000, Note = "test" });
        await db.SaveChangesAsync();
        _master = await _s.ShoreCrew.SaveAsync(_s.CompanyId,
            new CrewInput(null, "CAPT3", "Capt. Notes", "Master", "", null, "", null, null, "", "", _s.VesselId, null, true, false, IsMaster: true), _s.User);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, _master.Id, "Temp-pass-1", false, _s.User);
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Conversations, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private Task<PurchaseOrder> RaiseAsync() => _vessel.CreateSpotPoAsync(_s.CompanyId, new NewSpotPo
    {
        VesselId = _s.VesselId, DepartmentId = _s.DepartmentId, SupplierId = _supplier, Currency = "USD",
        Lines = [new NewRfqLine { ItemId = _s.ItemIds[0], Quantity = 10, UnitPrice = 5m }],
    }, _s.User);

    private Actor Master => new(_master.UserId, _master.FullName);

    private static List<string> Lines(ChatThreadView view) => view.Items.Select(i => i.Body).ToList();

    [Fact]
    public async Task AnApprovalAndADelivery_AreWrittenIntoTheOrdersConversation_AndBothSidesReadThem()
    {
        await SwitchOnAsync();
        var po = await RaiseAsync();
        await _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, Master, true, "");
        await _vesselDeliveries.RecordAsync(_s.CompanyId, po.Id, new NewDelivery { Lines = [new DeliveryOrderLine { ItemId = _s.ItemIds[0], Quantity = 4 }] }, Master);
        await _s.SettleUntilQuietAsync();

        var onboard = await _vesselChat.ThreadAsync(_s.CompanyId, Master, ChatModules.PurchaseOrder, po.Id);
        var ashore = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, po.Id);

        Assert.Equal(2, ashore.Items.Count);
        Assert.StartsWith("Approved", ashore.Items[0].Body);
        Assert.Contains("fully approved", ashore.Items[0].Body);
        Assert.StartsWith("Delivery", ashore.Items[1].Body);
        Assert.Contains("recorded by Capt. Notes", ashore.Items[1].Body);
        Assert.All(ashore.Items, i => Assert.Equal(ChatKind.System, i.Message.Kind));
        Assert.Equal(Lines(ashore), Lines(onboard));                              // the same lines, in the same order, on both sides
    }

    [Fact]
    public async Task WhereConversationsAreOff_NothingIsWritten()
    {
        var po = await RaiseAsync();
        await _vesselApprovals.DecideAsync(_s.CompanyId, po.Id, Master, true, "");
        await _s.SettleUntilQuietAsync();

        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        Assert.False(await db.ChatMessages.AnyAsync());
    }

    [Fact]
    public async Task ACancelledRequisition_SaysSoInItsConversation()
    {
        await SwitchOnAsync();
        var requisition = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition(), _s.User);
        await _s.ShoreRequisitions.CancelAsync(_s.CompanyId, requisition.Id, _s.User);

        var thread = await _shoreChat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.Requisition, requisition.Id);
        Assert.Single(thread.Items);
        Assert.StartsWith("Requisition cancelled", thread.Items[0].Body);
    }

    [Fact]
    public async Task TheInboxNamesTheOrder_AndOpensItOnEachSidesOwnPage()
    {
        await SwitchOnAsync();
        var po = await RaiseAsync();
        await _vesselChat.PostAsync(_s.CompanyId, Master, new ChatPost(ChatModules.PurchaseOrder, po.Id, _s.VesselId, ChatChannel.Internal, "Please confirm the price."));
        await _s.SettleUntilQuietAsync();

        var office = (await _shoreChat.InboxAsync(_s.CompanyId, _s.User)).Single(r => r.RecordId == po.Id);
        var ship = (await _vesselChat.InboxAsync(_s.CompanyId, Master)).Single(r => r.RecordId == po.Id);
        Assert.Equal($"Order {po.PoNumber}", office.Label);
        Assert.Equal($"/company/purchase-orders/{po.Id}#conversation", office.Href);
        Assert.Equal($"/vessel/orders/{po.Id}#conversation", ship.Href);
    }
}
