using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// Master data: the office keeps it, a vessel holds a read-only copy that it receives through sync. Each test starts from an
/// office and a vessel whose database began EMPTY and was filled by the baseline, so every test also proves a vessel can start
/// without a clone.
/// </summary>
public class MasterDataSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    public Task InitializeAsync() => _s.InitializeAsync();
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

    /// <summary>The seeded vessels sit in no group yet: the office creates a top group with a child under it and puts the vessel in the child.</summary>
    private async Task<(Guid Top, Guid Child)> PlaceVesselInGroupsAsync()
    {
        var top = new VesselGroup { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "Fleet", Level = 1 };
        var child = new VesselGroup { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, ParentId = top.Id, Name = "Tankers", Level = 2 };
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselGroups.AddRange(top, child);
            await db.SaveChangesAsync();
        }
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.Vessels.SingleAsync(v => v.Id == _s.VesselId)).GroupId = child.Id;
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        Assert.Equal(child.Id, (await OnVessel(db => db.Vessels.SingleAsync())).GroupId);
        return (top.Id, child.Id);
    }

    [Fact]
    public async Task AnEmptyVessel_FillsItselfWithWhatItNeeds_AndNothingElse()
    {
        var shoreItems = await OnShore(db => db.Items.CountAsync(i => i.CompanyId == _s.CompanyId));
        Assert.True(shoreItems > 5);
        Assert.Equal(shoreItems, await OnVessel(db => db.Items.CountAsync()));
        Assert.Equal(await OnShore(db => db.Categories.CountAsync()), await OnVessel(db => db.Categories.CountAsync()));
        Assert.Equal(await OnShore(db => db.Departments.CountAsync()), await OnVessel(db => db.Departments.CountAsync()));
        Assert.Equal(await OnShore(db => db.Ports.CountAsync()), await OnVessel(db => db.Ports.CountAsync()));
        Assert.Equal(await OnShore(db => db.Currencies.CountAsync()), await OnVessel(db => db.Currencies.CountAsync()));
        Assert.Equal(await OnShore(db => db.Uoms.CountAsync()), await OnVessel(db => db.Uoms.CountAsync()));
        Assert.Equal(await OnShore(db => db.DepartmentCategories.CountAsync()), await OnVessel(db => db.DepartmentCategories.CountAsync()));
        Assert.Equal(1, await OnVessel(db => db.Companies.CountAsync()));

        // Only its own vessel, never the rest of the fleet.
        var vessels = await OnVessel(db => db.Vessels.ToListAsync());
        Assert.Equal(_s.VesselId, Assert.Single(vessels).Id);
        Assert.True(await OnShore(db => db.Vessels.CountAsync()) > 1);

        // Its place in the fleet: its own group and the groups above it, not the rest of the tree.
        var unrelated = new VesselGroup { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "Elsewhere", Level = 1 };
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselGroups.Add(unrelated);
            await db.SaveChangesAsync();
        }
        var (top, child) = await PlaceVesselInGroupsAsync();
        var groups = (await OnVessel(db => db.VesselGroups.Select(g => g.Id).ToListAsync()));
        Assert.Contains(top, groups);
        Assert.Contains(child, groups);
        Assert.DoesNotContain(unrelated.Id, groups);

        // It can do real work: raise a requisition against the catalogue it received, which reaches the office.
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("from an empty start"), _s.User);
        await _s.SettleAsync();
        Assert.NotNull(await _s.FindAsync(_s.ShoreFactory, made.Id));

        // Nothing was left waiting or parked.
        var node = _s.VesselNode.SyncNodeId!;
        Assert.Empty(await _s.VesselSyncStore.GetDeferredAsync(new StreamKey(node, Direction.Down)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ChangingAnItemAtTheOffice_ReachesTheVessel_WithTheSameVersion()
    {
        var itemId = _s.ItemIds[0];
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var item = await db.Items.FirstAsync(i => i.Id == itemId);
            item.Name = "Renamed at the office";
            item.IsActive = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await _s.DirtyAsync(_s.ShoreFactory));          // the change left a note in its own transaction

        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.Items.SingleAsync(i => i.Id == itemId));
        Assert.Equal("Renamed at the office", onboard.Name);
        Assert.False(onboard.IsActive);
        Assert.Equal(await Version<Item>(_s.ShoreFactory, itemId), await Version<Item>(_s.VesselFactory, itemId));
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));         // applying it was not announced back
    }

    [Fact]
    public async Task ARemovedRecord_IsRemovedOnTheVessel()
    {
        Guid unusedItem;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var category = new Category { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "Temporary", CreatedAt = DateTime.UtcNow };
            var item = new Item { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Code = "TMP-1", Name = "Temporary item", CategoryId = category.Id, UnitOfMeasure = "Pcs" };
            unusedItem = item.Id;
            db.Categories.Add(category);
            db.Items.Add(item);
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        Assert.True(await OnVessel(db => db.Items.AnyAsync(i => i.Id == unusedItem)));

        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.Items.Remove(await db.Items.FirstAsync(i => i.Id == unusedItem));
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();

        Assert.False(await OnVessel(db => db.Items.AnyAsync(i => i.Id == unusedItem)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AChildArrivingBeforeItsParent_WaitsForIt()
    {
        // A new category and an item in it, delivered in the WRONG order: the item first.
        var category = new Category { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "Late parent", CreatedAt = DateTime.UtcNow };
        var item = new Item { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Code = "LATE-1", Name = "Early child", CategoryId = category.Id, UnitOfMeasure = "Pcs" };
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;                                     // saved without notes: this test sends the documents itself
            db.Categories.Add(category);
            db.Items.Add(item);
            await db.SaveChangesAsync();

            var hub = _s.Hub;
            var node = _s.VesselNode.SyncNodeId!;
            await hub.PublishToNodeAsync(node, Doc<Item>(db, item), Lane.Master);
            await hub.PublishToNodeAsync(node, Doc<Category>(db, category), Lane.Master);
        }

        await _s.SettleUntilQuietAsync();

        Assert.True(await OnVessel(db => db.Categories.AnyAsync(c => c.Id == category.Id)));
        Assert.True(await OnVessel(db => db.Items.AnyAsync(i => i.Id == item.Id)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task OtherVesselsData_NeverArrives_ButTheVesselsOwnAndItsGroupsPoliciesDo()
    {
        var (_, ownGroup) = await PlaceVesselInGroupsAsync();
        var other = await OnShore(db => db.Vessels.SingleAsync(v => v.Id == _s.OtherVesselId));
        var sibling = new VesselGroup { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "Another branch" };
        var forOther = new VesselPolicy { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = other.Id, CanRequisition = false, Note = "other ship" };
        var forSibling = new VesselPolicy { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, GroupId = sibling.Id, CanRequisition = false, Note = "other branch" };
        var forOwn = new VesselPolicy { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, CanRequisition = true, Note = "this ship" };
        var forOwnGroup = new VesselPolicy { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, GroupId = ownGroup, CanPurchaseDirect = true, Note = "this branch" };
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselGroups.Add(sibling);
            await db.SaveChangesAsync();
            db.VesselPolicies.AddRange(forOther, forSibling, forOwn, forOwnGroup);
            await db.SaveChangesAsync();
        }

        await _s.SettleUntilQuietAsync();

        var policies = await OnVessel(db => db.VesselPolicies.Select(p => p.Id).ToListAsync());
        Assert.Contains(forOwn.Id, policies);
        Assert.Contains(forOwnGroup.Id, policies);
        Assert.DoesNotContain(forOther.Id, policies);
        Assert.DoesNotContain(forSibling.Id, policies);
        Assert.False(await OnVessel(db => db.VesselGroups.AnyAsync(g => g.Id == sibling.Id)));
        Assert.False(await OnVessel(db => db.Vessels.AnyAsync(v => v.Id == other.Id)));

        // A change to the other ship's data does not reach this vessel either.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.Vessels.SingleAsync(v => v.Id == other.Id)).Name = "Renamed other ship";
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        Assert.False(await OnVessel(db => db.Vessels.AnyAsync(v => v.Id == other.Id)));
    }

    [Fact]
    public async Task AVesselInstallation_RefusesToChangeMasterData()
    {
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            var item = await db.Items.FirstAsync(i => i.Id == _s.ItemIds[0]);
            item.Name = "edited onboard";
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("master data", error.Message);
        }
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.Ports.Add(new Port { Name = "Nowhere", Country = "None" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.Items.Remove(await db.Items.FirstAsync(i => i.Id == _s.ItemIds[0]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
        Assert.NotEqual("edited onboard", (await OnVessel(db => db.Items.SingleAsync(i => i.Id == _s.ItemIds[0]))).Name);
    }

    [Fact]
    public async Task AVesselMovedToAnotherGroup_GetsThatBranchsPolicies_AndForgetsTheOldOnes()
    {
        var (_, oldGroup) = await PlaceVesselInGroupsAsync();
        var newGroup = new VesselGroup { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "New branch" };
        var oldPolicy = new VesselPolicy { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, GroupId = oldGroup, CanRequisition = true, Note = "old branch" };
        var newPolicy = new VesselPolicy { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, GroupId = newGroup.Id, CanRequisition = false, Note = "new branch" };
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselGroups.Add(newGroup);
            await db.SaveChangesAsync();
            db.VesselPolicies.AddRange(oldPolicy, newPolicy);
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        Assert.True(await OnVessel(db => db.VesselPolicies.AnyAsync(p => p.Id == oldPolicy.Id)));
        Assert.False(await OnVessel(db => db.VesselPolicies.AnyAsync(p => p.Id == newPolicy.Id)));

        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.Vessels.SingleAsync(v => v.Id == _s.VesselId)).GroupId = newGroup.Id;
            await db.SaveChangesAsync();
        }

        await _s.SettleUntilAsync(async () =>
            await OnVessel(db => db.VesselPolicies.AnyAsync(p => p.Id == newPolicy.Id))
            && !await OnVessel(db => db.VesselPolicies.AnyAsync(p => p.Id == oldPolicy.Id)));
        await _s.SettleUntilQuietAsync();

        Assert.Equal(newGroup.Id, (await OnVessel(db => db.Vessels.SingleAsync())).GroupId);
        Assert.True(await OnVessel(db => db.VesselGroups.AnyAsync(g => g.Id == newGroup.Id)));
        Assert.False(await OnVessel(db => db.VesselGroups.AnyAsync(g => g.Id == oldGroup)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task RecordsKeyedByCodeOrByTwoIds_SyncToo()
    {
        // A currency (keyed by its code), a company currency and a department/category link (keyed by two ids).
        var category = await OnShore(db => db.Categories.FirstAsync());
        var department = await OnShore(db => db.Departments.FirstAsync(d => d.CompanyId == _s.CompanyId));
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.Currencies.Add(new Currency { Code = "XTS", Name = "Test currency", DecimalPlaces = 3 });
            db.CompanyCurrencies.Add(new CompanyCurrency { CompanyId = _s.CompanyId, CurrencyCode = "XTS" });
            if (!await db.DepartmentCategories.AnyAsync(x => x.DepartmentId == department.Id && x.CategoryId == category.Id))
                db.DepartmentCategories.Add(new DepartmentCategory { CompanyId = _s.CompanyId, DepartmentId = department.Id, CategoryId = category.Id });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();

        Assert.Equal(3, (await OnVessel(db => db.Currencies.SingleAsync(c => c.Code == "XTS"))).DecimalPlaces);
        Assert.True(await OnVessel(db => db.CompanyCurrencies.AnyAsync(c => c.CurrencyCode == "XTS")));
        Assert.True(await OnVessel(db => db.DepartmentCategories.AnyAsync(x => x.DepartmentId == department.Id && x.CategoryId == category.Id)));

        // And their removal.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.CompanyCurrencies.Remove(await db.CompanyCurrencies.SingleAsync(c => c.CurrencyCode == "XTS"));
            db.Currencies.Remove(await db.Currencies.SingleAsync(c => c.Code == "XTS"));
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        Assert.False(await OnVessel(db => db.Currencies.AnyAsync(c => c.Code == "XTS")));
        Assert.False(await OnVessel(db => db.CompanyCurrencies.AnyAsync(c => c.CurrencyCode == "XTS")));
    }

    private static async Task<int> Version<T>(Microsoft.EntityFrameworkCore.IDbContextFactory<ChandleryDbContext> factory, Guid id) where T : class
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Set<T>().Where(e => EF.Property<Guid>(e, "Id") == id).Select(e => EF.Property<int>(e, "SyncVersion")).SingleAsync();
    }

    private SyncDocument Doc<T>(ChandleryDbContext db, T entity) where T : class
    {
        var entry = db.Entry(entity);
        var master = MasterData.Find(typeof(T))!;
        var payload = MasterData.ToJson(entry);
        var docId = MasterData.DocIdOf(entry.Metadata, p => entry.Property(p.Name).CurrentValue);
        return MasterData.Document(master, docId, 1, payload, 1, _s.ShoreNode.Id);
    }
}
