using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The replay that turns a task's history into its current state. Pure, so it can be checked exhaustively.</summary>
public class WorklistProjectionTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static WorklistEntry Entry(int minutes, WorklistEntryType type, string payload = "{}", string text = "", Guid? id = null)
        => new() { Id = id ?? Guid.CreateVersion7(), TaskId = Guid.Empty, Type = type, Payload = payload, Text = text, At = T0.AddMinutes(minutes) };

    private static WorklistEntry Created(int minutes = 0)
        => Entry(minutes, WorklistEntryType.Created, WorklistProjection.Json(("title", "Fix the davit"), ("description", "Corroded"), ("priority", WorklistPriority.Normal),
            ("target", new DateTime(2026, 12, 1)), ("assignee", "Bosun")));

    [Fact]
    public void TheCreatedEntry_GivesTheStartingState()
    {
        var s = WorklistProjection.Replay([Created()]);
        Assert.Equal("Fix the davit", s.Title);
        Assert.Equal(WorklistStatus.Open, s.Status);
        Assert.Equal(new DateTime(2026, 12, 1), s.Target);
        Assert.Equal("Bosun", s.Assignee);
        Assert.Equal(0, s.Percent);
    }

    [Fact]
    public void TheSameEntriesGiveTheSameStateInAnyOrderOfArrival()
    {
        var entries = new List<WorklistEntry>
        {
            Created(),
            Entry(5, WorklistEntryType.Percent, WorklistProjection.Json(("percent", 30))),
            Entry(9, WorklistEntryType.Target, WorklistProjection.Json(("target", new DateTime(2026, 12, 10))), "parts late"),
            Entry(12, WorklistEntryType.Priority, WorklistProjection.Json(("priority", WorklistPriority.Urgent))),
            Entry(15, WorklistEntryType.Defer, WorklistProjection.Json(("target", new DateTime(2027, 1, 5))), "yard slot"),
            Entry(20, WorklistEntryType.Comment, text: "waiting"),
        };
        var expected = WorklistProjection.Replay(entries);
        var rng = new Random(7);
        for (var i = 0; i < 50; i++)
            Assert.Equal(expected, WorklistProjection.Replay(entries.OrderBy(_ => rng.Next()).ToList()));

        Assert.Equal(new DateTime(2027, 1, 5), expected.Target);          // the latest date entry wins
        Assert.Equal(WorklistPriority.Urgent, expected.Priority);
        Assert.Equal(1, expected.Deferrals);
        Assert.Equal(WorklistStatus.InProgress, expected.Status);          // progress started the work
    }

    [Fact]
    public void TheLaterOfTwoConflictingEntries_Wins_AndTheEarlierStaysInTheHistory()
    {
        var early = Entry(10, WorklistEntryType.Target, WorklistProjection.Json(("target", new DateTime(2026, 12, 15))));
        var late = Entry(11, WorklistEntryType.Target, WorklistProjection.Json(("target", new DateTime(2026, 12, 20))));
        Assert.Equal(new DateTime(2026, 12, 20), WorklistProjection.Replay([Created(), late, early]).Target);
    }

    [Fact]
    public void Completing_SetsFullProgress_AndAnyLaterWorkReopensIt()
    {
        var done = WorklistProjection.Replay([Created(), Entry(5, WorklistEntryType.Completed, text: "replaced")]);
        Assert.Equal(WorklistStatus.Completed, done.Status);
        Assert.Equal(100, done.Percent);
        Assert.Equal(T0.AddMinutes(5), done.CompletedAt);

        var reopened = WorklistProjection.Replay([Created(), Entry(5, WorklistEntryType.Completed), Entry(8, WorklistEntryType.Percent, WorklistProjection.Json(("percent", 60)))]);
        Assert.Equal(WorklistStatus.InProgress, reopened.Status);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public void AVerification_CountsOnlyOnACompletedTask()
    {
        var early = WorklistProjection.Replay([Created(), Entry(2, WorklistEntryType.Verified), Entry(5, WorklistEntryType.Completed)]);
        Assert.Null(early.VerifiedAt);                                    // nothing was complete yet when it was written

        var verified = WorklistProjection.Replay([Created(), Entry(5, WorklistEntryType.Completed), Entry(9, WorklistEntryType.Verified)]);
        Assert.Equal(T0.AddMinutes(9), verified.VerifiedAt);

        var reopened = WorklistProjection.Replay([Created(), Entry(5, WorklistEntryType.Completed), Entry(9, WorklistEntryType.Verified),
            Entry(12, WorklistEntryType.Status, WorklistProjection.Json(("status", WorklistStatus.Waiting)))]);
        Assert.Null(reopened.VerifiedAt);                                 // work resumed: the old verification no longer applies
    }

    [Fact]
    public void CommentsAndLinks_ChangeNothing()
    {
        var plain = WorklistProjection.Replay([Created()]);
        var withNotes = WorklistProjection.Replay([Created(), Entry(3, WorklistEntryType.Comment, text: "checked"), Entry(4, WorklistEntryType.Link, WorklistProjection.Json(("linkType", "Requisition")))]);
        Assert.Equal(plain with { LastEntryAt = withNotes.LastEntryAt }, withNotes);
    }

    [Fact]
    public void OverdueMeansPastTheTargetAndNotFinished()
    {
        var task = new WorklistTask { TargetDate = new DateTime(2026, 11, 1), Status = WorklistStatus.Open };
        Assert.True(task.IsOverdue(new DateTime(2026, 11, 2)));
        Assert.False(task.IsOverdue(new DateTime(2026, 11, 1)));           // the target day itself is not yet late
        task.Status = WorklistStatus.Completed;
        Assert.False(task.IsOverdue(new DateTime(2026, 12, 1)));
        task.Status = WorklistStatus.Cancelled;
        Assert.False(task.IsOverdue(new DateTime(2026, 12, 1)));
    }
}

/// <summary>Tasks and their history on a real office and a real vessel: both add to a task while apart, nothing is overwritten, history cannot be changed.</summary>
public class WorklistSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private WorklistService _vessel = null!;
    private WorklistService _shore = null!;
    private Guid _classSource, _auditSource;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _vessel = new WorklistService(_s.VesselFactory);
        _shore = new WorklistService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        _classSource = await db.WorklistListItems.Where(i => i.Kind == WorklistListKind.Source && i.Name == "Class").Select(i => i.Id).SingleAsync();
        _auditSource = await db.WorklistListItems.Where(i => i.Kind == WorklistListKind.Source && i.Name == "Internal audit").Select(i => i.Id).SingleAsync();
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

    private async Task SwitchOnAsync()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Worklist, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private NewWorklistTask NewTask(Guid? source = null, DateTime? target = null, string title = "Renew the fire hose")
        => new(_s.VesselId, title, "found at drill", source ?? _auditSource, Target: target ?? new DateTime(2026, 12, 1), Assignee: "Bosun");

    private Task<WorklistTask> OnboardTaskAsync(Guid? source = null) => _vessel.CreateTaskAsync(_s.CompanyId, NewTask(source), _s.User);

    [Fact]
    public async Task TheOfficeMaintainsTheLists_AndTasksAlreadyRaisedKeepWhatTheyHad()
    {
        await SwitchOnAsync();
        Assert.Equal([(_s.VesselId, (await OnShore(db => db.Vessels.SingleAsync(v => v.Id == _s.VesselId))).Name)], await _shore.EnabledVesselsAsync(_s.CompanyId));
        Assert.Equal(_s.VesselId, (await _vessel.EnabledVesselsAsync(_s.CompanyId)).Single().Id);

        var task = await OnboardTaskAsync(_classSource);
        Assert.True(task.RequiresVerification);

        var added = await _shore.AddListItemAsync(_s.CompanyId, WorklistListKind.Source, "Flag state", requiresVerification: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddListItemAsync(_s.CompanyId, WorklistListKind.Source, " flag state".Trim()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddListItemAsync(_s.CompanyId, WorklistListKind.Nature, " "));
        await _s.SettleUntilQuietAsync();
        Assert.Contains(await _vessel.GetListsAsync(_s.CompanyId), i => i.Id == added.Id && i.RequiresVerification);

        // Switching Class off, and removing its verification, does not change the task raised from it.
        await _shore.UpdateListItemAsync(_s.CompanyId, _classSource, "Class society", isActive: false, requiresVerification: false);
        await _s.SettleUntilQuietAsync();
        Assert.DoesNotContain(await _vessel.GetListsAsync(_s.CompanyId), i => i.Id == _classSource);
        Assert.Contains(await _vessel.GetListsAsync(_s.CompanyId, includeInactive: true), i => i.Id == _classSource && i.Name == "Class society" && !i.IsActive);
        Assert.True((await OnVessel(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id))).RequiresVerification);

        // A vessel never edits the lists.
        await Assert.ThrowsAnyAsync<Exception>(() => _vessel.AddListItemAsync(_s.CompanyId, WorklistListKind.Nature, "Made onboard"));
    }

    [Fact]
    public async Task ATaskCanBeLinkedToARequisition_AndTheLinkReachesTheOfficeAndWorksBackwards()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync();
        Guid reqnId;
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
            reqnId = await db.Requisitions.Select(r => r.Id).FirstOrDefaultAsync();
        if (reqnId == Guid.Empty) reqnId = Guid.NewGuid();                                  // the link only records the target; it need not exist for the test

        await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Requisition, LinkTarget: reqnId, LinkLabel: "Requisition X"), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Requisition, LinkTarget: reqnId, LinkLabel: "Requisition X"), _s.User);
        await _s.SettleUntilQuietAsync();

        var atOffice = await _shore.GetAsync(_s.CompanyId, task.Id);
        var links = WorklistService.LinksOf(atOffice!.Entries);
        Assert.Equal([(WorklistLinkTypes.Requisition, reqnId, "Requisition X")], links.Select(l => (l.Type, l.Target, l.Label)));   // linking twice shows one link
        Assert.Equal([task.Id], (await _shore.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.Requisition, reqnId)).Select(t => t.Id));
        Assert.Equal([task.Id], (await _vessel.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.Requisition, reqnId)).Select(t => t.Id));
        Assert.Empty(await _shore.TasksLinkedToAsync(_s.CompanyId, WorklistLinkTypes.Order, reqnId));
        Assert.Contains("/vessel/orders/", WorklistLinkTypes.Href(WorklistLinkTypes.Order, reqnId, onVessel: true));
    }

    [Fact]
    public async Task TheOfficeCopiesATaskToSisterVessels_WithLinksBothWays_AndTheVesselCannot()
    {
        await SwitchOnAsync();
        var sister = Guid.NewGuid();
        var offSister = Guid.NewGuid();
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.Vessels.Add(new Vessel { Id = sister, CompanyId = _s.CompanyId, Name = "MV Grey Heron", ShortCode = "GH" });
            db.Vessels.Add(new Vessel { Id = offSister, CompanyId = _s.CompanyId, Name = "MV Quiet Wren", ShortCode = "QW" });
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = sister, Module = PolicyModules.Worklist, Enabled = true });
            await db.SaveChangesAsync();
        }
        var origin = await OnboardTaskAsync(_classSource);
        await _s.SettleUntilQuietAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.TransferAsync(_s.CompanyId, origin.Id, [sister], _s.User));

        var result = await _shore.TransferAsync(_s.CompanyId, origin.Id, [sister, offSister, _s.VesselId, sister], _s.User);
        var copy = Assert.Single(result.Created);
        Assert.Equal(["MV Quiet Wren"], result.Skipped);                                     // module off there; the origin's own vessel and duplicates are ignored
        Assert.Equal(sister, copy.VesselId);
        Assert.StartsWith("GH-WL-", copy.Code);
        Assert.Equal((origin.Title, origin.Description, origin.SourceId, origin.TargetDate, true, ""), (copy.Title, copy.Description, copy.SourceId, copy.TargetDate, copy.RequiresVerification, copy.AssigneeName));

        var copyLinks = WorklistService.LinksOf((await _shore.GetAsync(_s.CompanyId, copy.Id))!.Entries);
        Assert.Equal([(WorklistLinkTypes.Task, origin.Id)], copyLinks.Select(l => (l.Type, l.Target)));
        Assert.Contains("Copied from", copyLinks.Single().Label);

        await _s.SettleUntilQuietAsync();
        var originLinks = WorklistService.LinksOf((await _vessel.GetAsync(_s.CompanyId, origin.Id))!.Entries);    // the vessel sees where its task was copied
        Assert.Equal([(WorklistLinkTypes.Task, copy.Id)], originLinks.Select(l => (l.Type, l.Target)));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task HistoryArrivingAgainWithDifferentContent_IsIgnored_OnTheSyncPathToo()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync();
        var comment = await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Comment, "original wording"), _s.User);
        await _s.SettleUntilQuietAsync();

        // A document for the same entry, newer, with other text and a different date for the task it belongs to.
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            var type = Chandlery.Services.Sync.Aggregates.Find(nameof(WorklistEntry))!;
            var json = Chandlery.Services.Sync.Aggregates.ToJson(db, await db.WorklistEntries.SingleAsync(e => e.Id == comment.Id));
            json["Text"] = "rewritten history";
            await _s.Agent.PublishAsync(Chandlery.Services.Sync.Aggregates.Document(type, comment.Id, 9, json, SyncStages.Vessel, 99, null), Lane.Transaction);
        }
        await _s.SettleUntilQuietAsync();

        Assert.Equal("original wording", (await OnShore(db => db.WorklistEntries.SingleAsync(e => e.Id == comment.Id))).Text);
        Assert.Equal("original wording", (await OnVessel(db => db.WorklistEntries.SingleAsync(e => e.Id == comment.Id))).Text);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AnEntryArrivingWhileAnotherIsBeingAdded_IsPartOfTheResultingState()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync();

        // The office's entry is stored (as the applier does, which then refreshes the task) at the very moment the vessel's comment is being saved.
        _vessel.BeforeEntrySaved = async () =>
        {
            await using var db = await _s.VesselFactory.CreateDbContextAsync();
            db.ApplyingSync = true;
            db.WorklistEntries.Add(new WorklistEntry
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, TaskId = task.Id, Type = WorklistEntryType.Target, Text = "class deadline", AuthorName = "Office",
                At = DateTime.UtcNow, OwnerStage = SyncStages.Office, Payload = WorklistProjection.Json(("target", new DateTime(2026, 12, 24))),
            });
            await db.SaveChangesAsync();
        };
        await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Comment, "carrying on"), _s.User);
        _vessel.BeforeEntrySaved = null;

        Assert.Equal(new DateTime(2026, 12, 24), (await OnVessel(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id))).TargetDate);   // the comment did not wipe it out
    }

    [Fact]
    public async Task TheModuleIsOffUntilTheOfficeSwitchesItOn()
    {
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CreateTaskAsync(_s.CompanyId, NewTask(), _s.User));
        Assert.Contains("not switched on", off.Message);
        Assert.Equal(0, await OnVessel(db => db.WorklistTasks.CountAsync()));

        await SwitchOnAsync();
        Assert.NotNull(await OnboardTaskAsync());
        Assert.True(await _shore.IsEnabledForCompanyAsync(_s.CompanyId));
    }

    [Fact]
    public async Task ATaskRaisedOnboard_ReachesTheOffice_WithItsHistoryAndTheSameState()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync();
        await _s.SettleUntilQuietAsync();

        Assert.Equal($"MVSF-WL-{DateTime.UtcNow.Year}-0001", task.Code);
        var atOffice = await OnShore(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id));
        Assert.Equal(SyncStages.Vessel, atOffice.OwnerStage);
        var diag = $"entries {await OnShore(db => db.WorklistEntries.CountAsync())} payload {string.Join("|", await OnShore(db => db.WorklistEntries.Select(e => e.Payload + "@" + e.Type).ToListAsync()))} quarantine {string.Join("|", (await _s.ShoreSyncStore.GetQuarantineAsync()).Select(q => q.Envelope.Document.DocType + ":" + q.Error))} dirtyV {await _s.DirtyAsync(_s.VesselFactory)}";
        Assert.True(atOffice.Title == "Renew the fire hose", diag);                      // worked out at the office from the entries that arrived
        Assert.Equal(new DateTime(2026, 12, 1), atOffice.TargetDate);
        Assert.Equal("Bosun", atOffice.AssigneeName);
        Assert.Equal(1, await OnShore(db => db.WorklistEntries.CountAsync(e => e.TaskId == task.Id && e.Type == WorklistEntryType.Created)));
        Assert.Equal(0, await _s.DirtyAsync(_s.ShoreFactory));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ATaskRaisedAtTheOffice_ReachesTheVessel_WithAnOfficeCode()
    {
        await SwitchOnAsync();
        var task = await _shore.CreateTaskAsync(_s.CompanyId, NewTask(title: "Class condition of class"), _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.Contains("-WL-", task.Code);
        Assert.Contains("-H", task.Code);                                        // raised ashore: the office lane letter
        var onboard = await OnVessel(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id));
        Assert.Equal(SyncStages.Office, onboard.OwnerStage);
        Assert.Equal("Class condition of class", onboard.Title);
    }

    [Fact]
    public async Task BothSidesWorkWhileApart_NothingIsOverwritten_AndBothEndWithTheSameHistory()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync();
        await _s.SettleUntilQuietAsync();

        // Apart: the vessel reports progress; the office raises the priority and moves the date.
        await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Percent, Percent: 40), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Comment, "hose ordered"), _s.User);
        await Task.Delay(20);
        await _shore.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Priority, Priority: WorklistPriority.Urgent), _s.User);
        await _shore.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Target, "class deadline", Date: new DateTime(2026, 11, 20)), _s.User);
        await _s.SettleUntilQuietAsync();

        var atVessel = (await _vessel.GetAsync(_s.CompanyId, task.Id))!;
        var atOffice = (await _shore.GetAsync(_s.CompanyId, task.Id))!;
        Assert.Equal(5, atVessel.Entries.Count);                                 // created, two from the vessel, two from the office
        Assert.Equal(atVessel.Entries.Select(e => e.Id), atOffice.Entries.Select(e => e.Id));      // the same history in the same order
        foreach (var t in new[] { atVessel.Task, atOffice.Task })
        {
            Assert.Equal(40, t.PercentComplete);
            Assert.Equal(WorklistPriority.Urgent, t.Priority);
            Assert.Equal(new DateTime(2026, 11, 20), t.TargetDate);
            Assert.Equal(WorklistStatus.InProgress, t.Status);
        }
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TwoDateChangesWhileApart_BothStayInTheHistory_TheLaterWins_OnBothSides()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync();
        await _s.SettleUntilQuietAsync();

        await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Defer, "no yard slot", Date: new DateTime(2027, 1, 10)), _s.User);
        await Task.Delay(30);
        await _shore.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Target, "management decision", Date: new DateTime(2026, 12, 20)), _s.User);
        await _s.SettleUntilQuietAsync();

        foreach (var detail in new[] { (await _vessel.GetAsync(_s.CompanyId, task.Id))!, (await _shore.GetAsync(_s.CompanyId, task.Id))! })
        {
            Assert.Equal(new DateTime(2026, 12, 20), detail.Task.TargetDate);    // the office's, written later
            Assert.Equal(1, detail.Task.DeferralCount);                          // the vessel's deferral still counts
            Assert.Equal(2, detail.Entries.Count(e => e.Type is WorklistEntryType.Defer or WorklistEntryType.Target));
        }
    }

    [Fact]
    public async Task History_CannotBeChangedOrDeleted_ByAnyone()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync();
        await _s.SettleUntilQuietAsync();

        foreach (var factory in new[] { _s.VesselFactory, _s.ShoreFactory })
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                (await db.WorklistEntries.FirstAsync(e => e.TaskId == task.Id)).Text = "rewritten";
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
                Assert.Contains("history", error.Message);
            }
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.WorklistEntries.Remove(await db.WorklistEntries.FirstAsync(e => e.TaskId == task.Id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
            await using (var db = await factory.CreateDbContextAsync())
            {
                (await db.WorklistTasks.FirstAsync(t => t.Id == task.Id)).Code = "CHANGED";                  // identity cannot change either
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.WorklistTasks.Remove(await db.WorklistTasks.FirstAsync(t => t.Id == task.Id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            }
        }
        Assert.Equal(task.Code, (await OnShore(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id))).Code);
    }

    [Fact]
    public async Task WhoMayDoWhat_OnATheOfficeRaised_AndOnAVesselRaisedTask()
    {
        await SwitchOnAsync();
        var fromOffice = await _shore.CreateTaskAsync(_s.CompanyId, NewTask(title: "From the office"), _s.User);
        await _s.SettleUntilQuietAsync();

        // The vessel adds progress to an office task, but does not reshape it.
        await _vessel.AddEntryAsync(_s.CompanyId, fromOffice.Id, new(WorklistEntryType.Comment, "started"), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, fromOffice.Id, new(WorklistEntryType.Percent, Percent: 20), _s.User);
        await _vessel.AddEntryAsync(_s.CompanyId, fromOffice.Id, new(WorklistEntryType.Defer, "parts", Date: new DateTime(2027, 1, 1)), _s.User);
        foreach (var shaping in new WorklistEntryInput[]
                 {
                     new(WorklistEntryType.Priority, Priority: WorklistPriority.Urgent),
                     new(WorklistEntryType.Target, "x", Date: new DateTime(2027, 2, 1)),
                     new(WorklistEntryType.Assignee, Assignee: "Someone"),
                     new(WorklistEntryType.Status, Status: WorklistStatus.Cancelled),
                 })
            await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddEntryAsync(_s.CompanyId, fromOffice.Id, shaping, _s.User));

        // On a task the vessel raised itself, it may shape it; the office may too.
        var own = await OnboardTaskAsync();
        await _vessel.AddEntryAsync(_s.CompanyId, own.Id, new(WorklistEntryType.Priority, Priority: WorklistPriority.Urgent), _s.User);
        await _s.SettleUntilQuietAsync();
        await _shore.AddEntryAsync(_s.CompanyId, own.Id, new(WorklistEntryType.Assignee, Assignee: "Chief Officer"), _s.User);

        // Rules for what an entry must say.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddEntryAsync(_s.CompanyId, own.Id, new(WorklistEntryType.Comment, " "), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddEntryAsync(_s.CompanyId, own.Id, new(WorklistEntryType.Percent, Percent: 120), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddEntryAsync(_s.CompanyId, own.Id, new(WorklistEntryType.Defer, "", Date: new DateTime(2027, 1, 1)), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddEntryAsync(_s.CompanyId, own.Id, new(WorklistEntryType.Completed, ""), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddEntryAsync(_s.CompanyId, own.Id, new(WorklistEntryType.Created), _s.User));
    }

    [Fact]
    public async Task ATaskFromASourceThatNeedsVerification_IsNotClosedUntilTheOfficeVerifiesIt()
    {
        await SwitchOnAsync();
        var task = await OnboardTaskAsync(_classSource);
        Assert.True(task.RequiresVerification);
        await _s.SettleUntilQuietAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Verified), _s.User));   // not completed yet
        await _vessel.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Completed, "hose replaced, tested"), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.True((await OnShore(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id))).AwaitingVerification);
        Assert.True((await OnVessel(db => db.WorklistTasks.SingleAsync(t => t.Id == task.Id))).AwaitingVerification);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Verified), _s.User));   // the vessel cannot
        await _shore.AddEntryAsync(_s.CompanyId, task.Id, new(WorklistEntryType.Verified, "seen and accepted"), _s.User);
        await _s.SettleUntilQuietAsync();
        foreach (var t in new[] { await OnShore(db => db.WorklistTasks.SingleAsync(x => x.Id == task.Id)), await OnVessel(db => db.WorklistTasks.SingleAsync(x => x.Id == task.Id)) })
        {
            Assert.False(t.AwaitingVerification);
            Assert.True(t.IsClosed);
            Assert.NotNull(t.VerifiedAt);
        }

        // A source that does not ask for verification closes on completion.
        var simple = await OnboardTaskAsync(_auditSource);
        await _vessel.AddEntryAsync(_s.CompanyId, simple.Id, new(WorklistEntryType.Completed, "done"), _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddEntryAsync(_s.CompanyId, simple.Id, new(WorklistEntryType.Verified), _s.User));
        Assert.True((await _vessel.GetAsync(_s.CompanyId, simple.Id))!.Task.IsClosed);
    }

    [Fact]
    public async Task TasksForOtherVessels_NeverArrive_AndTheListsDo()
    {
        await SwitchOnAsync();
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, Module = PolicyModules.Worklist, Enabled = true });
            await db.SaveChangesAsync();
        }
        var other = await _shore.CreateTaskAsync(_s.CompanyId, new NewWorklistTask(_s.OtherVesselId, "Other ship's job", Target: new DateTime(2026, 12, 1)), _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.False(await OnVessel(db => db.WorklistTasks.AnyAsync(t => t.Id == other.Id)));
        Assert.False(await OnVessel(db => db.WorklistEntries.AnyAsync(e => e.TaskId == other.Id)));

        // The configurable lists reached the vessel, which cannot change them.
        Assert.Equal(await OnShore(db => db.WorklistListItems.CountAsync()), await OnVessel(db => db.WorklistListItems.CountAsync()));
        await using var vdb = await _s.VesselFactory.CreateDbContextAsync();
        (await vdb.WorklistListItems.FirstAsync()).Name = "renamed onboard";
        await Assert.ThrowsAsync<InvalidOperationException>(() => vdb.SaveChangesAsync());
    }

    [Fact]
    public async Task AnEntryArrivingBeforeItsTask_WaitsForIt_AndTheTaskIsThenComplete()
    {
        await SwitchOnAsync();
        Guid taskId;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;                                              // saved without notes: this test sends the documents itself
            var task = new WorklistTask
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Code = "TEST-WL-1", CreatedByName = "Office", CreatedAt = DateTime.UtcNow,
                OwnerStage = SyncStages.Office,
            };
            var entry = new WorklistEntry
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, TaskId = task.Id, Type = WorklistEntryType.Created, AuthorName = "Office", At = DateTime.UtcNow,
                OwnerStage = SyncStages.Office, Payload = WorklistProjection.Json(("title", "Late task"), ("target", new DateTime(2026, 12, 1))),
            };
            db.WorklistTasks.Add(task);
            db.WorklistEntries.Add(entry);
            await db.SaveChangesAsync();
            taskId = task.Id;

            var node = _s.VesselNode.SyncNodeId!;
            await _s.Hub.PublishToNodeAsync(node, Aggregates(db, entry, nameof(WorklistEntry)), Lane.Transaction);
            await _s.Hub.PublishToNodeAsync(node, Aggregates(db, task, nameof(WorklistTask)), Lane.Transaction);
        }

        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.WorklistTasks.SingleAsync(t => t.Id == taskId));
        Assert.Equal("Late task", onboard.Title);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AnEntryReachingTheOfficeBeforeItsTask_WaitsForIt_InsteadOfBeingParked()
    {
        await SwitchOnAsync();
        Guid taskId;
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;                                              // saved without notes: this test sends the documents itself, entry first
            var task = new WorklistTask
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Code = "TEST-WL-2", CreatedByName = "Crew", CreatedAt = DateTime.UtcNow,
                OwnerStage = SyncStages.Vessel,
            };
            var entry = new WorklistEntry
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, TaskId = task.Id, Type = WorklistEntryType.Created, AuthorName = "Crew", At = DateTime.UtcNow,
                OwnerStage = SyncStages.Vessel, Payload = WorklistProjection.Json(("title", "Entry first"), ("target", new DateTime(2026, 12, 1))),
            };
            db.WorklistTasks.Add(task);
            db.WorklistEntries.Add(entry);
            await db.SaveChangesAsync();
            taskId = task.Id;

            await _s.Agent.PublishAsync(Aggregates(db, entry, nameof(WorklistEntry), SyncStages.Vessel), Lane.Transaction);
            await _s.Agent.PublishAsync(Aggregates(db, task, nameof(WorklistTask), SyncStages.Vessel), Lane.Transaction);
        }

        await _s.SettleUntilQuietAsync();

        Assert.Equal("Entry first", (await OnShore(db => db.WorklistTasks.SingleAsync(t => t.Id == taskId))).Title);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    private SyncDocument Aggregates(ChandleryDbContext db, object root, string docType, string stage = SyncStages.Office)
    {
        var type = Chandlery.Services.Sync.Aggregates.Find(docType)!;
        var entry = db.Entry(root);
        return Chandlery.Services.Sync.Aggregates.Document(type, (Guid)entry.Property("Id").CurrentValue!, 1, Chandlery.Services.Sync.Aggregates.ToJson(db, root), stage, 1, null);
    }
}
