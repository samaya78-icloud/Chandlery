using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The rules behind projects: status from events, who may do what next, links, cost without double counting, and days.</summary>
public class ProjectRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

    private static ProjectStatusEvent Event(ProjectEventKind kind, int hours, string? date = null, Guid? id = null)
        => new() { Id = id ?? Guid.CreateVersion7(), Kind = kind, At = T0.AddHours(hours), Date = date is null ? T0.Date : DateTime.Parse(date) };

    [Fact]
    public void TheStatusIsTheLatestEvent_InTimeOrder_WhateverOrderTheyAreHeldIn()
    {
        Assert.Equal(ProjectStatus.Planning, ProjectStatusRules.Of([]));
        var events = new List<ProjectStatusEvent> { Event(ProjectEventKind.Started, 3), Event(ProjectEventKind.Approved, 1), Event(ProjectEventKind.Completed, 5) };
        Assert.Equal(ProjectStatus.Complete, ProjectStatusRules.Of(events));
        events.Reverse();
        Assert.Equal(ProjectStatus.Complete, ProjectStatusRules.Of(events));
        Assert.Equal(ProjectStatus.Closed, ProjectStatusRules.Of([.. events, Event(ProjectEventKind.Closed, 9)]));

        // Withdrawing an approval takes it back to Planning, and approving again moves on.
        var withdrawn = new[] { Event(ProjectEventKind.Approved, 1), Event(ProjectEventKind.Withdrawn, 2) };
        Assert.Equal(ProjectStatus.Planning, ProjectStatusRules.Of(withdrawn));
        Assert.Equal(ProjectStatus.Approved, ProjectStatusRules.Of([.. withdrawn, Event(ProjectEventKind.Approved, 3)]));

        // Two events at the same moment settle by id, so both sides agree.
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var tie = new[] { Event(ProjectEventKind.Withdrawn, 4, id: high), Event(ProjectEventKind.Approved, 4, id: low) };
        Assert.Equal(ProjectStatus.Planning, ProjectStatusRules.Of(tie));
        Assert.Equal(ProjectStatus.Planning, ProjectStatusRules.Of(tie.Reverse()));
    }

    [Fact]
    public void OnlyTheNextStepIsAllowed()
    {
        var kinds = Enum.GetValues<ProjectEventKind>();
        var allowed = Enum.GetValues<ProjectStatus>().SelectMany(s => kinds.Where(k => ProjectStatusRules.Allows(s, k)).Select(k => (s, k))).ToList();
        Assert.Equal(
        [
            (ProjectStatus.Planning, ProjectEventKind.Approved),
            (ProjectStatus.Approved, ProjectEventKind.Started), (ProjectStatus.Approved, ProjectEventKind.Withdrawn),
            (ProjectStatus.Ongoing, ProjectEventKind.Completed),
            (ProjectStatus.Complete, ProjectEventKind.Closed),
        ], allowed);
        Assert.False(ProjectStatusRules.IsEditable(ProjectStatus.Closed));
        Assert.All(new[] { ProjectStatus.Planning, ProjectStatus.Approved, ProjectStatus.Ongoing, ProjectStatus.Complete }, s => Assert.True(ProjectStatusRules.IsEditable(s)));
    }

    [Fact]
    public void ActualDatesComeFromTheEventsThatSaidSo_AndClosingNeedsEveryItemSettledAndANote()
    {
        var events = new[] { Event(ProjectEventKind.Approved, 1), Event(ProjectEventKind.Started, 2, "2026-11-03"), Event(ProjectEventKind.Completed, 3, "2026-11-20") };
        Assert.Equal((new DateTime(2026, 11, 3), new DateTime(2026, 11, 20)), (ProjectStatusRules.ActualStart(events), ProjectStatusRules.ActualEnd(events)));
        Assert.Null(ProjectStatusRules.ActualStart([Event(ProjectEventKind.Approved, 1)]));

        ProjectItem Item(ProjectItemStatus s) => new() { Id = Guid.CreateVersion7(), Description = "x", Status = s };
        Assert.Empty(ProjectStatusRules.CloseProblems([Item(ProjectItemStatus.Done), Item(ProjectItemStatus.Dropped)], "Within budget"));
        Assert.Equal(2, ProjectStatusRules.CloseProblems([Item(ProjectItemStatus.Done), Item(ProjectItemStatus.Open)], " ").Count);
        Assert.Contains("1 specification item", ProjectStatusRules.CloseProblems([Item(ProjectItemStatus.Open)], "ok").Single());
    }

    [Fact]
    public void ADocumentBelongsToTheProjectOfItsLatestLink_AndNoneOnceRemoved()
    {
        var doc = Guid.CreateVersion7();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        ProjectLink Link(Guid project, bool added, int hours, ProjectLinkKind kind = ProjectLinkKind.PurchaseOrder, Guid? target = null)
            => new() { Id = Guid.CreateVersion7(), ProjectId = project, Kind = kind, DocId = target ?? doc, Added = added, At = T0.AddHours(hours) };

        var links = new List<ProjectLink> { Link(a, true, 1) };
        Assert.Equal(a, ProjectLinkRules.Effective(links)[(ProjectLinkKind.PurchaseOrder, doc)]);

        links.Add(Link(b, true, 2));                                                                                  // moved to another project
        Assert.Equal(b, ProjectLinkRules.Effective(links)[(ProjectLinkKind.PurchaseOrder, doc)]);
        links.Reverse();                                                                                              // the order they are held in does not matter
        Assert.Equal(b, ProjectLinkRules.Effective(links)[(ProjectLinkKind.PurchaseOrder, doc)]);

        links.Add(Link(b, false, 3));                                                                                 // taken off
        Assert.Empty(ProjectLinkRules.Effective(links));

        var other = Link(a, true, 4, ProjectLinkKind.Requisition);                                                    // the same id as another kind of document is a different document
        links.Add(other);
        var effective = ProjectLinkRules.Effective(links);
        Assert.Equal(a, Assert.Single(effective).Value);
        Assert.Equal((ProjectLinkKind.Requisition, doc), Assert.Single(effective).Key);
    }

    [Fact]
    public void CostCountsEachPoundOnce_AnOrderWithFinalizedInvoicesCountsAtTheInvoicedAmount()
    {
        var settled = Guid.CreateVersion7();
        var open = Guid.CreateVersion7();
        var cost = ProjectCostRules.Of(
            orders: [new CostPo(settled, 10_000m), new CostPo(open, 4_000m)],
            invoices: [new CostInvoice(settled, 6_000m), new CostInvoice(settled, 3_500m), new CostInvoice(null, 1_200m)],     // two invoices on the first order, and a spot invoice
            entries: [800m, -100m]);

        Assert.Equal(4_000m, cost.Committed);                       // only the order with nothing invoiced yet
        Assert.Equal(10_700m, cost.Invoiced);                       // 6,000 + 3,500 + 1,200: the first order is counted here, not at its own 10,000
        Assert.Equal(700m, cost.Other);
        Assert.Equal(15_400m, cost.ToDate);

        Assert.Equal(new ProjectCost(0, 0, 0), ProjectCostRules.Of([], [], []));
    }

    [Fact]
    public void BudgetKeepsContingencyBesideTheTotal_AndVarianceIsNegativeWhenOver()
    {
        var lines = new[]
        {
            new ProjectBudgetLine { PlannedUsd = 50_000m, ContingencyPct = 10m },
            new ProjectBudgetLine { PlannedUsd = 12_345.67m, ContingencyPct = 5m },
            new ProjectBudgetLine { PlannedUsd = 1_000m },
        };
        Assert.Equal(63_345.67m, ProjectCostRules.Budget(lines));
        Assert.Equal(5_617.28m, ProjectCostRules.Contingency(lines));                  // 5,000 + 617.2835 rounded
        Assert.Equal(48_345.67m, ProjectCostRules.Variance(63_345.67m, 15_000m));
        Assert.Equal(-2_000m, ProjectCostRules.Variance(10_000m, 12_000m));
    }

    [Fact]
    public void DaysAreCountedFromBothEnds_AndActualDaysRunToTodayUntilTheWorkEnds()
    {
        var start = new DateTime(2026, 11, 1);
        Assert.Equal(10, ProjectCostRules.PlannedDays(start, new DateTime(2026, 11, 10)));
        Assert.Equal(1, ProjectCostRules.PlannedDays(start, start));
        Assert.Null(ProjectCostRules.PlannedDays(start, new DateTime(2026, 10, 30)));
        Assert.Null(ProjectCostRules.PlannedDays(null, start));

        Assert.Null(ProjectCostRules.ActualDays(null, null, new DateTime(2026, 11, 5)));
        Assert.Equal(5, ProjectCostRules.ActualDays(start, null, new DateTime(2026, 11, 5)));                      // still going: to today
        Assert.Equal(12, ProjectCostRules.ActualDays(start, new DateTime(2026, 11, 12), new DateTime(2026, 12, 25)));  // finished: to the day it ended
    }
}

/// <summary>Projects on a real office and a real vessel: planned ashore, received onboard without any money, written to by both sides, and locked when closed.</summary>
public class ProjectSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!, _vessel = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _vessel = new ProjectService(_s.VesselFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApprovalRules.RemoveRange(db.ApprovalRules);      // the fixture seeds approval rules; the plain approval step needs none to apply
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private Task<Project> NewProjectAsync(string title = "Special survey and drydock 2027")
        => _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, title, "Intermediate drydock", null, "Capt. Rao", Today.AddDays(30), Today.AddDays(50), "Cochin Yard", IsUwild: false), _s.User);

    private static List<ProjectItemInput> TwoItems() =>
    [
        new(null, "Hull", "Blast and repaint the underwater hull"),
        new(null, "Machinery", "Overhaul the main sea-water pumps", ProjectItemSource.PmsJob, Guid.CreateVersion7(), "JOB-114"),
    ];

    [Fact]
    public async Task AProjectIsPlannedAshore_ReachesTheVesselWithItsSpecification_AndNeverWithItsMoney()
    {
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => NewProjectAsync());
        Assert.Contains("not switched on", off.Message);
        await SwitchOnAsync();

        var project = await NewProjectAsync();
        Assert.Matches($@"^MVSF-PR-{DateTime.UtcNow.Year}-H0001$", project.Code);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, TwoItems(), _s.User);
        var items = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 90_000m, 10m), new("Paint", 30_000m)], _s.User);
        await _shore.SetItemCostAsync(_s.CompanyId, project.Id, items[0].Id, 40_000m, 38_500m, _s.User);
        await _shore.AddCostEntryAsync(_s.CompanyId, project.Id, Today, "Surveyor's attendance", "INV-77", 1_200m, _s.User);
        var approved = await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "Budget agreed", _s.User);
        Assert.Equal(120_000m, Assert.Single(await _shore.ApprovalsAsync(_s.CompanyId, project.Id)).TotalUsd);       // what was approved is kept
        await _s.SettleUntilQuietAsync();

        // The vessel has the project, its two items and its status.
        var onboard = (await _vessel.GetAsync(_s.CompanyId, project.Id))!;
        Assert.Equal((project.Code, ProjectStatus.Approved, "Cochin Yard", 2), (onboard.Project.Code, onboard.Status, onboard.Project.Yard, onboard.Project.Items.Count));
        Assert.Equal(["Blast and repaint the underwater hull", "Overhaul the main sea-water pumps"], onboard.Project.Items.Select(i => i.Description));
        Assert.Equal((ProjectItemSource.PmsJob, "JOB-114"), (onboard.Project.Items[1].Source, onboard.Project.Items[1].SourceRef));
        Assert.Equal([approved.Id], onboard.Events.Select(e => e.Id));
        Assert.Single(await _vessel.ListAsync(_s.CompanyId, _s.VesselId));
        Assert.Empty(await _vessel.ListAsync(_s.CompanyId, _s.OtherVesselId));

        // No money at all: not one budget line, estimate, cost entry or approved total is held onboard.
        Assert.Equal((0, 0, 0, 0), (await OnVessel(db => db.ProjectBudgetLines.CountAsync()), await OnVessel(db => db.ProjectItemCosts.CountAsync()),
            await OnVessel(db => db.ProjectCostEntries.CountAsync()), await OnVessel(db => db.ProjectBudgetApprovals.CountAsync())));
        Assert.Empty(await _vessel.BudgetAsync(_s.CompanyId, project.Id));

        // And a vessel cannot make any: not through the service, and not by writing the tables directly.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 1m)], _s.User));
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.ProjectBudgetLines.Add(new ProjectBudgetLine { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, ProjectId = project.Id, Category = "Sneaked", PlannedUsd = 1m });
            Assert.Contains("handled by the office", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        var planned = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.NonDrydock, "Onboard project"), _s.User));
        Assert.Contains("office", planned.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveItemsAsync(_s.CompanyId, project.Id, TwoItems(), _s.User));

        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TheSameWorklistTaskOrPmsJobCannotBeOnTheSpecificationTwice_AndEachRuleOfThePlanIsEnforced()
    {
        await SwitchOnAsync();
        var project = await NewProjectAsync();
        var job = Guid.CreateVersion7();
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveItemsAsync(_s.CompanyId, project.Id,
            [new(null, "Machinery", "Overhaul pump", ProjectItemSource.PmsJob, job, "J1"), new(null, "Machinery", "Overhaul pump again", ProjectItemSource.PmsJob, job, "J1")], _s.User));
        Assert.Contains("twice", twice.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", " ")], _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.UpdateAsync(_s.CompanyId, project.Id,
            new ProjectEdit(ProjectType.Drydock, "Title", "", null, "", Today.AddDays(10), Today.AddDays(5), "", false), _s.User));                       // ends before it starts
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", -5m)], _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 5m, 120m)], _s.User));

        // Removing an item removes its estimate; steps must follow in order.
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, TwoItems(), _s.User);
        var items = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        await _shore.SetItemCostAsync(_s.CompanyId, project.Id, items[0].Id, 100m, null, _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(items[1].Id, items[1].Group, items[1].Description, items[1].Source, items[1].SourceId, items[1].SourceRef)], _s.User);
        Assert.Empty(await _shore.ItemCostsAsync(_s.CompanyId, project.Id));
        var early = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today, "", _s.User));
        Assert.Contains("planning", early.Message);
    }

    [Fact]
    public async Task BothSidesAddNotes_AndEndWithTheSameOrderedStory()
    {
        await SwitchOnAsync();
        var project = await NewProjectAsync();
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, TwoItems(), _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today.AddDays(-2), "Arrived at the yard", _s.User);
        await _s.SettleUntilQuietAsync();
        var item = (await _vessel.GetAsync(_s.CompanyId, project.Id))!.Project.Items[0].Id;

        await _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today.AddDays(-1), "Hull blasting started on the port side", 15, item), _s.User);
        await _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "Rain stopped work after noon", null), _s.User);
        await _shore.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "Superintendent attending Thursday", null), _s.User);
        await _s.SettleUntilQuietAsync();

        List<string> Story(ProjectDetail d) => d.Notes.Select(n => $"{n.Date:yyyy-MM-dd} {n.OwnerStage} {n.Text} {n.ProgressPct} {n.ItemId}").ToList();
        var atOffice = Story((await _shore.GetAsync(_s.CompanyId, project.Id))!);
        Assert.Equal(3, atOffice.Count);
        Assert.Equal(atOffice, Story((await _vessel.GetAsync(_s.CompanyId, project.Id))!));
        Assert.Equal(ProjectStatus.Ongoing, (await _vessel.GetAsync(_s.CompanyId, project.Id))!.Status);

        // Notes are history: they cannot be changed or removed, and must make sense.
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            var note = await db.ProjectNotes.FirstAsync();
            note.Text = "Rewritten";
            Assert.Contains("history", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today.AddDays(1), "Tomorrow"), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, " "), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "x", 140), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "x", null, Guid.NewGuid()), _s.User));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ADocumentIsLinkedByEitherSide_TheLatestLinkWins_AndOnlyTheVesselsOwnDocumentsCanBeLinked()
    {
        await SwitchOnAsync();
        var first = await NewProjectAsync("Drydock 2027");
        var second = await NewProjectAsync("Pump replacement");
        var requisition = await _s.VesselRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("yard stores"), _s.User);
        await _s.SettleUntilQuietAsync();

        var link = await _vessel.LinkAsync(_s.CompanyId, first.Id, ProjectLinkKind.Requisition, requisition.Id, _s.User);          // the vessel links its own requisition
        Assert.Equal((first.Id, true), (link.ProjectId, link.Added));
        Assert.Equal(link.Id, (await _vessel.LinkAsync(_s.CompanyId, first.Id, ProjectLinkKind.Requisition, requisition.Id, _s.User)).Id);   // saying it again changes nothing
        await _s.SettleUntilQuietAsync();
        Assert.Equal(first.Id, await _shore.ProjectOfAsync(_s.CompanyId, ProjectLinkKind.Requisition, requisition.Id));
        Assert.Equal([(ProjectLinkKind.Requisition, requisition.Id)], await _shore.LinksOfAsync(_s.CompanyId, first.Id));

        await _shore.LinkAsync(_s.CompanyId, second.Id, ProjectLinkKind.Requisition, requisition.Id, _s.User);                       // the office moves it
        await _s.SettleUntilQuietAsync();
        foreach (var side in new[] { _shore, _vessel })
        {
            Assert.Equal(second.Id, await side.ProjectOfAsync(_s.CompanyId, ProjectLinkKind.Requisition, requisition.Id));
            Assert.Empty(await side.LinksOfAsync(_s.CompanyId, first.Id));
            Assert.Equal([(ProjectLinkKind.Requisition, requisition.Id)], await side.LinksOfAsync(_s.CompanyId, second.Id));
        }
        var notLinked = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.UnlinkAsync(_s.CompanyId, first.Id, ProjectLinkKind.Requisition, requisition.Id, _s.User));
        Assert.Contains("not linked", notLinked.Message);

        await _vessel.UnlinkAsync(_s.CompanyId, second.Id, ProjectLinkKind.Requisition, requisition.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Null(await _shore.ProjectOfAsync(_s.CompanyId, ProjectLinkKind.Requisition, requisition.Id));

        // A document of another vessel, or one that does not exist, cannot be linked.
        var elsewhere = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("another ship", _s.OtherVesselId), _s.User);
        var wrongVessel = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.LinkAsync(_s.CompanyId, first.Id, ProjectLinkKind.Requisition, elsewhere.Id, _s.User));
        Assert.Contains("not found for this vessel", wrongVessel.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.LinkAsync(_s.CompanyId, first.Id, ProjectLinkKind.PurchaseOrder, Guid.NewGuid(), _s.User));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ClosingNeedsEveryItemSettledAndANote_AndLocksTheProjectOnBothSides()
    {
        await SwitchOnAsync();
        var project = await NewProjectAsync();
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, TwoItems(), _s.User);
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 5_000m)], _s.User);

        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Withdrawn, Today, "Scope changing", _s.User);
        Assert.Equal(ProjectStatus.Planning, (await _shore.GetAsync(_s.CompanyId, project.Id))!.Status);
        await _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 6_500m)], _s.User);                                       // a change after withdrawing...
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        Assert.Equal([5_000m, 6_500m], (await _shore.ApprovalsAsync(_s.CompanyId, project.Id)).Select(a => a.TotalUsd));              // ...never rewrites what was approved before

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today.AddDays(1), "", _s.User));    // not in the future
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today.AddDays(-10), "Arrived", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today.AddDays(-11), "", _s.User));   // before it started
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today.AddDays(-1), "Undocked", _s.User);

        var open = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Closed, Today, "", _s.User));
        Assert.Contains("still open", open.Message);
        Assert.Contains("reconciliation note", open.Message);
        var items = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(items[0].Id, items[0].Group, items[0].Description, Status: ProjectItemStatus.Done),
            new(items[1].Id, items[1].Group, items[1].Description, items[1].Source, items[1].SourceId, items[1].SourceRef, ProjectItemStatus.Dropped)], _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Closed, Today, "Finished 4% under budget", _s.User);
        await _s.SettleUntilQuietAsync();

        foreach (var side in new[] { _shore, _vessel })
        {
            var closed = (await side.GetAsync(_s.CompanyId, project.Id))!;
            Assert.Equal((ProjectStatus.Closed, Today.AddDays(-10), Today.AddDays(-1)), (closed.Status, closed.ActualStart, closed.ActualEnd));
            Assert.Equal([ProjectItemStatus.Done, ProjectItemStatus.Dropped], closed.Project.Items.Select(i => i.Status));
            var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => side.AddNoteAsync(_s.CompanyId, project.Id, new NewProjectNote(Today, "Too late"), _s.User));
            Assert.Contains("closed and locked", locked.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => side.LinkAsync(_s.CompanyId, project.Id, ProjectLinkKind.Requisition, Guid.NewGuid(), _s.User));
        }
        foreach (var attempt in new Func<Task>[]
        {
            () => _shore.UpdateAsync(_s.CompanyId, project.Id, new ProjectEdit(ProjectType.Drydock, "Renamed", "", null, "", null, null, "", false), _s.User),
            () => _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "One more job")], _s.User),
            () => _shore.SaveBudgetAsync(_s.CompanyId, project.Id, [new("Yard", 1m)], _s.User),
            () => _shore.AddCostEntryAsync(_s.CompanyId, project.Id, Today, "Late bill", "", 100m, _s.User),
            () => _shore.SetItemCostAsync(_s.CompanyId, project.Id, items[0].Id, 1m, 1m, _s.User),
            () => _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today, "", _s.User),
        })
            Assert.Contains("closed and locked", (await Assert.ThrowsAsync<InvalidOperationException>(attempt)).Message);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }
}
