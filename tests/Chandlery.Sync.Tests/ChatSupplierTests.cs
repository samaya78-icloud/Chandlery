using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The supplier's side of a conversation. A supplier reads and writes only its own thread, only on the shared channel, only on an RFQ it was invited to or an approved
/// order that is its own: never the company's internal talk, never another supplier's thread, never a record that is not its own.
/// </summary>
public class ChatSupplierTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProcurementService _procurement = null!;
    private ChatService _chat = null!;
    private Guid _a, _b, _c;
    private Actor _supplierA = null!, _supplierB = null!, _supplierC = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _procurement = new ProcurementService(_s.ShoreFactory);
        _chat = new ChatService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var linked = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).ToListAsync();
        (_a, _b) = (linked[0], linked[1]);
        var other = new Supplier { Name = "Other Marine Co", ContactEmail = "other@example.com" };
        db.Suppliers.Add(other);
        _c = other.Id;
        foreach (var module in new[] { PolicyModules.Conversations })
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
        await db.SaveChangesAsync();
        _supplierA = await SupplierUserAsync(_a, "Anna");
        _supplierB = await SupplierUserAsync(_b, "Bram");
        _supplierC = await SupplierUserAsync(_c, "Cleo");
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Actor> SupplierUserAsync(Guid supplierId, string name)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name}{Guid.NewGuid():N}@supplier.example.com", PasswordHash = "x", Role = UserRole.SupplierUser, DisplayName = name, SupplierId = supplierId, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new Actor(user.Id, name);
    }

    private async Task<Rfq> RfqAsync(params Guid[] invited)
    {
        var requisition = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("spares"), _s.User);
        return await _procurement.CreateRfqAsync(_s.CompanyId, new NewRfq
        {
            RequisitionId = requisition.Id, BuyerName = "buyer", InvitedSupplierIds = invited.ToList(),
            Lines = requisition.Lines.Select(l => new NewRfqLine { ItemId = l.ItemId, Quantity = l.Quantity, RequisitionLineId = l.Id }).ToList(),
        }, _s.User);
    }

    private ChatPost Post(Guid rfq, string body, ChatChannel channel = ChatChannel.Shared, string topic = "", Guid? vessel = null)
        => new(ChatModules.Rfq, rfq, vessel ?? _s.VesselId, channel, body, topic);

    private Task<ChatThreadView> ReadAsync(Actor who, Guid rfq) => _chat.ThreadAsync(_s.CompanyId, who, ChatModules.Rfq, rfq);

    [Fact]
    public async Task EachSupplierReadsOnlyItsOwnThread_NeverTheInternalTalk_NeverAnotherSuppliersWords()
    {
        var rfq = await RfqAsync(_a, _b);
        await _chat.PostAsync(_s.CompanyId, _s.User, Post(rfq.Id, "Is the price valid for 30 days?", topic: ChatRules.SupplierTopic(_a)));
        await _chat.PostAsync(_s.CompanyId, _s.User, Post(rfq.Id, "Can you beat the other quote?", topic: ChatRules.SupplierTopic(_b)));
        await _chat.PostAsync(_s.CompanyId, _s.User, Post(rfq.Id, "Internal: supplier B is cheaper", ChatChannel.Internal));
        await _chat.PostAsync(_s.CompanyId, _s.User, Post(rfq.Id, "Shared but on the main thread", ChatChannel.Shared));
        await _chat.PostAsync(_s.CompanyId, _s.User, Post(rfq.Id, "Office only note", ChatChannel.OfficeOnly));

        Assert.Equal(["Is the price valid for 30 days?"], (await ReadAsync(_supplierA, rfq.Id)).Items.Select(i => i.Body));
        Assert.Equal(["Can you beat the other quote?"], (await ReadAsync(_supplierB, rfq.Id)).Items.Select(i => i.Body));

        // The office sees every thread, and each supplier thread is its own topic.
        var topics = await _chat.TopicsAsync(_s.CompanyId, _s.User, ChatModules.Rfq, rfq.Id);
        Assert.Contains(ChatRules.SupplierTopic(_a), topics);
        Assert.Contains(ChatRules.SupplierTopic(_b), topics);
    }

    [Fact]
    public async Task ASupplierPosts_IntoItsOwnThread_OnTheSharedChannelOnly_AndTheOfficeSeesWhoItIs()
    {
        var rfq = await RfqAsync(_a, _b);
        // Whatever topic, channel or vessel the supplier names, the words land in its own thread, shared, on the record's own vessel.
        await _chat.PostAsync(_s.CompanyId, _supplierA, Post(rfq.Id, "Valid for 45 days. @office please confirm. @Priya", topic: ChatRules.SupplierTopic(_b), vessel: Guid.NewGuid()));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.PostAsync(_s.CompanyId, _supplierA, Post(rfq.Id, "sneaky", ChatChannel.Internal)));
        Assert.Contains("channel is not open", refused.Message);

        var own = (await ReadAsync(_supplierA, rfq.Id)).Items.Single();
        Assert.Equal(ChatChannel.Shared, own.Message.Channel);
        Assert.Equal(ChatRules.SupplierTopic(_a), own.Message.Topic);
        Assert.Equal(_s.VesselId, own.Message.VesselId);
        Assert.Contains("(", own.Message.AuthorName);                                           // the supplier company is named beside the person
        Assert.Equal("r:office", own.Message.Mentions);                                         // a supplier can name the office, nobody by name
        Assert.Empty((await ReadAsync(_supplierB, rfq.Id)).Items);

        var ashore = await _chat.ThreadAsync(_s.CompanyId, _s.User, ChatModules.Rfq, rfq.Id, ChatRules.SupplierTopic(_a));
        Assert.True(ashore.Items.Single().FromSupplier);
        var row = (await _chat.InboxAsync(_s.CompanyId, _s.User)).Single(r => r.RecordId == rfq.Id);
        Assert.Contains("with ", row.Label);                                                    // the inbox says whose thread it is
        Assert.Equal(1, row.UnreadMentions);
    }

    [Fact]
    public async Task ASupplierHasNothingOnARecordItWasNotInvitedToOrAnOrderNotYetApproved_OrAnythingElse()
    {
        var rfq = await RfqAsync(_a);
        await _chat.PostAsync(_s.CompanyId, _s.User, Post(rfq.Id, "To Anna", topic: ChatRules.SupplierTopic(_a)));
        await _chat.PostAsync(_s.CompanyId, _s.User, Post(rfq.Id, "To Cleo", topic: ChatRules.SupplierTopic(_c)));

        Assert.Empty((await ReadAsync(_supplierC, rfq.Id)).Items);                              // not invited, even to a thread addressed to it
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.PostAsync(_s.CompanyId, _supplierC, Post(rfq.Id, "Let me in")));
        Assert.Contains("not open to you", refused.Message);

        // An order is the supplier's only once approved, and only its own.
        var po = await _procurement.CreateSpotPoAsync(_s.CompanyId, new NewSpotPo
        {
            VesselId = _s.VesselId, DepartmentId = _s.DepartmentId, SupplierId = _a, Currency = "USD",
            Lines = [new NewRfqLine { ItemId = _s.ItemIds[0], Quantity = 1, UnitPrice = 5m }],
        }, _s.User);
        var onOrder = new ChatPost(ChatModules.PurchaseOrder, po.Id, _s.VesselId, ChatChannel.Shared, "About this order");
        await _chat.PostAsync(_s.CompanyId, _s.User, onOrder with { Topic = ChatRules.SupplierTopic(_a) });
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var saved = await db.PurchaseOrders.SingleAsync(p => p.Id == po.Id);
            saved.Status = PoStatus.PendingApproval;
            await db.SaveChangesAsync();
        }
        Assert.Empty((await _chat.ThreadAsync(_s.CompanyId, _supplierA, ChatModules.PurchaseOrder, po.Id)).Items);   // not approved yet
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.PurchaseOrders.SingleAsync(p => p.Id == po.Id)).Status = PoStatus.Approved;
            await db.SaveChangesAsync();
        }
        Assert.Single((await _chat.ThreadAsync(_s.CompanyId, _supplierA, ChatModules.PurchaseOrder, po.Id)).Items);
        Assert.Empty((await _chat.ThreadAsync(_s.CompanyId, _supplierB, ChatModules.PurchaseOrder, po.Id)).Items);   // another supplier's order

        // Nothing but its own RFQs and orders is open to a supplier, and it has no inbox.
        Assert.Empty((await _chat.ThreadAsync(_s.CompanyId, _supplierA, ChatModules.Worklist, rfq.Id)).Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.PostAsync(_s.CompanyId, _supplierA, new ChatPost(ChatModules.Worklist, Guid.NewGuid(), _s.VesselId, ChatChannel.Shared, "hello")));
        Assert.Null(await _chat.InboxCountsAsync(_s.CompanyId, _supplierA));
    }
}
