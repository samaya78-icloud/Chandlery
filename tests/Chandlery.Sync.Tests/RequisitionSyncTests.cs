using Chandlery.Models;
using Chandlery.Services;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Every test gets its own office and vessel databases, so nothing one test leaves behind can affect another.</summary>
public class RequisitionSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    public Task InitializeAsync() => _s.InitializeAsync();
    public Task DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task VesselRequisition_ReachesTheOffice_WhichThenOwnsIt_AndTheVesselIsTold()
    {
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("engine room consumables"), _s.User);
        Assert.Equal(SyncStages.Vessel, made.OwnerStage);
        Assert.Matches(@"^MVSF-REQ-\d{4}-\d{4}$", made.ReqnNumber);                 // vessel lane: no H
        Assert.Equal(1, await _s.DirtyAsync(_s.VesselFactory));                     // the change left a note in its own transaction

        await _s.SettleAsync();

        // The office holds an exact copy, owned by the office, one version further on.
        var office = await _s.FindAsync(_s.ShoreFactory, made.Id);
        Assert.NotNull(office);
        Assert.Equal(made.ReqnNumber, office.ReqnNumber);
        Assert.Equal("engine room consumables", office.Remarks);
        Assert.Equal(SyncStages.Office, office.OwnerStage);
        Assert.Equal(made.Lines.Select(l => (l.Id, l.ItemId, l.Quantity)).OrderBy(x => x.Id), office.Lines.Select(l => (l.Id, l.ItemId, l.Quantity)).OrderBy(x => x.Id));
        Assert.Equal(2, await _s.VersionAsync(_s.ShoreFactory, made.Id));

        // The vessel was told: same version, office-owned.
        var onboard = await _s.FindAsync(_s.VesselFactory, made.Id);
        Assert.Equal(SyncStages.Office, onboard!.OwnerStage);
        Assert.Equal(2, await _s.VersionAsync(_s.VesselFactory, made.Id));

        // Nothing is left to send, and nothing bounces between the two sides.
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
        Assert.Equal(0, await _s.DirtyAsync(_s.ShoreFactory));
        await _s.SettleAsync(3);
        Assert.Equal(2, await _s.VersionAsync(_s.ShoreFactory, made.Id));
        Assert.Equal(2, await _s.VersionAsync(_s.VesselFactory, made.Id));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task OfficeChanges_FlowBackToTheVessel()
    {
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("to be trimmed"), _s.User);
        await _s.SettleAsync();

        var first = made.Lines.OrderBy(l => l.Id).First();
        await _s.ShoreRequisitions.SetApprovedQuantitiesAsync(_s.CompanyId, made.Id, new Dictionary<Guid, decimal> { [first.Id] = 1 }, _s.User);
        await _s.ShoreRequisitions.CancelAsync(_s.CompanyId, made.Id, _s.User);
        await _s.SettleAsync();

        var onboard = await _s.FindAsync(_s.VesselFactory, made.Id);
        Assert.Equal(RequisitionStatus.Cancelled, onboard!.Status);
        Assert.Equal(1, onboard.Lines.Single(l => l.Id == first.Id).ApprovedQuantity);
        Assert.Equal(await _s.VersionAsync(_s.ShoreFactory, made.Id), await _s.VersionAsync(_s.VesselFactory, made.Id));
    }

    [Fact]
    public async Task OfficeEditingOnlyAQuantity_StillMovesTheVersion_AndReachesTheVessel()
    {
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("quantity only"), _s.User);
        await _s.SettleAsync();
        var before = await _s.VersionAsync(_s.ShoreFactory, made.Id);

        var line = made.Lines.OrderBy(l => l.Id).Last();
        await _s.ShoreRequisitions.SetApprovedQuantitiesAsync(_s.CompanyId, made.Id, new Dictionary<Guid, decimal> { [line.Id] = 1 }, _s.User);
        Assert.Equal(before + 1, await _s.VersionAsync(_s.ShoreFactory, made.Id));          // a line edit alone is a new version of the document
        await _s.SettleAsync();

        var onboard = await _s.FindAsync(_s.VesselFactory, made.Id);
        Assert.Equal(1, onboard!.Lines.Single(l => l.Id == line.Id).ApprovedQuantity);
        Assert.Equal(before + 1, await _s.VersionAsync(_s.VesselFactory, made.Id));
    }

    [Fact]
    public async Task OnceSentToTheOffice_TheVesselCannotChangeIt()
    {
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("locked after sending"), _s.User);
        await _s.SettleAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _s.VesselRequisitions.CancelAsync(_s.CompanyId, made.Id, _s.User));
        Assert.Contains("only the office", ex.Message);
        Assert.Equal(RequisitionStatus.Open, (await _s.FindAsync(_s.VesselFactory, made.Id))!.Status);
    }

    [Fact]
    public async Task RequisitionRaisedAshore_ReachesTheVessel_AndIsReadOnlyThere()
    {
        var made = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("raised by the office"), _s.User);
        Assert.Equal(SyncStages.Office, made.OwnerStage);
        Assert.Matches(@"^MVSF-REQ-\d{4}-H\d{4}$", made.ReqnNumber);                // office lane: H

        await _s.SettleAsync();

        var onboard = await _s.FindAsync(_s.VesselFactory, made.Id);
        Assert.NotNull(onboard);
        Assert.Equal(made.ReqnNumber, onboard.ReqnNumber);
        Assert.Equal(2, onboard.Lines.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.VesselRequisitions.CancelAsync(_s.CompanyId, made.Id, _s.User));
        Assert.Equal(0, await _s.DirtyAsync(_s.VesselFactory));
    }

    [Fact]
    public async Task RequisitionsForOtherVessels_AreNotSentToThisOne()
    {
        var made = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("another ship", _s.OtherVesselId), _s.User);

        await _s.SettleAsync();

        Assert.Null(await _s.FindAsync(_s.VesselFactory, made.Id));         // the other vessel has no node: nothing is routed anywhere
        Assert.Equal(0, await _s.DirtyAsync(_s.ShoreFactory));              // and the note was cleared rather than kept forever
    }

    [Fact]
    public async Task VesselCannotSubmitForAnotherVessel_AndTheOfficeSeesWhy()
    {
        // A requisition for the other ship, raised on this vessel's installation (which a vessel user cannot normally do).
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;
            var other = await db.Vessels.IgnoreQueryFilters().FirstAsync(v => v.Id == _s.OtherVesselId);
            other.NodeId = _s.VesselNode.Id;                        // locally it believes it runs both ships
            await db.SaveChangesAsync();
        }
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("not my ship", _s.OtherVesselId), _s.User);

        await _s.SettleAsync();

        Assert.Null(await _s.FindAsync(_s.ShoreFactory, made.Id));
        var parked = Assert.Single(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Contains("may not send requisitions for vessel", parked.Error);
    }

    [Fact]
    public async Task EditsMadeOnboardAfterTheOfficeTookOver_AreIgnored_AndOfficeWins()
    {
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("original"), _s.User);
        await _s.VesselPublisher.PublishPendingAsync();
        await _s.Agent.ExchangeAsync();                                      // the office now has it; the vessel has not heard back yet

        // Offline, the vessel edits its own copy once more (the service guard would stop a user; this simulates a stale edit).
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            var r = await db.Requisitions.FirstAsync(x => x.Id == made.Id);
            r.Remarks = "vessel edit after submission";
            await db.SaveChangesAsync();
        }

        await _s.SettleAsync();

        Assert.Equal("original", (await _s.FindAsync(_s.ShoreFactory, made.Id))!.Remarks);          // the office never saw the late edit
        var onboard = await _s.FindAsync(_s.VesselFactory, made.Id);
        Assert.Equal("original", onboard!.Remarks);                                                  // the vessel was brought into line
        Assert.Equal(SyncStages.Office, onboard.OwnerStage);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task MissingMasterData_ParksTheMessage_ForReplayAfterTheCauseIsFixed()
    {
        var made = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("needs a catalog item"), _s.User);
        var itemId = made.Lines.First().ItemId;

        // The office does not (yet) have one of the items. Remove it there, keeping a copy to put back.
        Item copy;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            copy = await db.Items.IgnoreQueryFilters().AsNoTracking().FirstAsync(i => i.Id == itemId);
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Items" WHERE "Id" = {0}""", itemId);
        }

        await _s.SettleAsync();

        Assert.Null(await _s.FindAsync(_s.ShoreFactory, made.Id));
        var parked = Assert.Single(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Contains("master data", parked.Error);

        // The item arrives (a later master-data sync, here done by hand); the operator replays the parked message.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;
            db.Items.Add(copy);
            await db.SaveChangesAsync();
        }
        var error = await _s.Hub.ReplayQuarantinedAsync(parked.Stream, parked.Envelope.Seq, "item restored");
        Assert.Null(error);
        await _s.SettleAsync();

        Assert.NotNull(await _s.FindAsync(_s.ShoreFactory, made.Id));
        Assert.Equal(SyncStages.Office, (await _s.FindAsync(_s.VesselFactory, made.Id))!.OwnerStage);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Theory]
    // receivingAtHub, localStage, localVersion, incomingStage, incomingVersion, expected
    [InlineData(true, null, null, "vessel", 1, true)]            // office sees a new vessel requisition
    [InlineData(true, "vessel", 1, "vessel", 2, true)]           // vessel kept editing before the office had it
    [InlineData(true, "vessel", 2, "vessel", 2, false)]          // same version again (resend)
    [InlineData(true, "office", 2, "vessel", 3, false)]          // vessel edit after the office took over
    [InlineData(true, null, null, "office", 1, false)]           // a vessel may not claim the office stage
    [InlineData(false, null, null, "office", 1, true)]           // vessel receives a new office requisition
    [InlineData(false, "vessel", 5, "office", 2, true)]          // submission acknowledged: office overrides vessel edits
    [InlineData(false, "office", 2, "office", 3, true)]          // office update
    [InlineData(false, "office", 3, "office", 3, false)]         // resend
    [InlineData(false, "office", 3, "office", 2, false)]         // out-of-order older update
    [InlineData(false, null, null, "vessel", 1, false)]          // hub never sends vessel-stage documents down
    public void OwnershipRules(bool atHub, string? localStage, int? localVersion, string incomingStage, int incomingVersion, bool expected)
        => Assert.Equal(expected, RequisitionOwnership.ShouldApply(atHub, localStage, localVersion, incomingStage, incomingVersion));

    [Fact]
    public void Document_RoundTripsThroughItsCanonicalJson()
    {
        var doc = new RequisitionDocument(Guid.NewGuid(), Guid.NewGuid(), "MVSF-REQ-2026-0001", RfqType.Spares, Guid.NewGuid(), null, Guid.NewGuid(),
            Urgency.Urgent, new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc), null, null, null, true, "r", RequisitionStatus.RfqPosted,
            Guid.NewGuid(), "A", new DateTime(2026, 11, 7, 1, 2, 3, DateTimeKind.Utc), SyncStages.Office, Guid.NewGuid(),
            new DateTime(2026, 11, 7, 1, 2, 3, DateTimeKind.Utc), new DateTime(2026, 11, 7, 2, 3, 4, DateTimeKind.Utc), 4,
            [new RequisitionLineDocument(Guid.NewGuid(), Guid.NewGuid(), 1234.567m, 0.001m, "Drum", "d", null)]);

        var json = doc.ToJson();

        Assert.Contains("\"quantity\":\"1234.567\"", json);          // decimals travel as strings
        Assert.Contains("\"urgency\":\"Urgent\"", json);             // enums by name
        Assert.Contains("\"syncVersion\":\"4\"", json);
        var back = RequisitionDocument.FromJson(json);
        Assert.Equal(doc.Id, back.Id);
        Assert.Equal(1234.567m, back.Lines[0].Quantity);
        Assert.Equal(0.001m, back.Lines[0].ApprovedQuantity);
        Assert.Equal(doc.SyncUpdatedAt, back.SyncUpdatedAt);
        Assert.Equal(DateTimeKind.Utc, back.ReqnDate.Kind);
    }
}
