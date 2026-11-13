using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The pure rules behind yard tenders, phases, the documents checklist and the fleet's superintendent workload.</summary>
public class ProjectPlanningRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 13);

    private static ProjectItem Item(string description, int order, ProjectItemStatus status = ProjectItemStatus.Open) => new() { Id = Guid.NewGuid(), Group = "Hull", Description = description, SortOrder = order, Status = status };

    private static (ProjectTender, string, List<ProjectTenderLine>) Quote(string yard, TenderStatus status, decimal other, params (ProjectItem Item, decimal Amount)[] prices)
    {
        var tender = new ProjectTender { Id = Guid.NewGuid(), YardId = Guid.NewGuid(), Status = status, OtherChargesUsd = other };
        return (tender, yard, prices.Select(p => new ProjectTenderLine { TenderId = tender.Id, ItemId = p.Item.Id, AmountUsd = p.Amount }).ToList());
    }

    [Fact]
    public void TheComparisonPicksTheCheapestPerItem_AndTheLowestTotalOnlyAmongYardsThatPricedEverything()
    {
        var (hull, pumps, paint) = (Item("Blast the hull", 0), Item("Overhaul pumps", 1), Item("Dropped job", 2, ProjectItemStatus.Dropped));
        var a = Quote("Alpha Yard", TenderStatus.Quoted, 1_000m, (hull, 10_000m), (pumps, 5_000m), (paint, 999m));      // the dropped item's price is ignored
        var b = Quote("Beta Yard", TenderStatus.Quoted, 500m, (hull, 9_000m));                                              // cheaper on the hull but left out the pumps
        var c = Quote("Gamma Yard", TenderStatus.Invited, 0m);                                                              // not quoted: not shown
        var d = Quote("Delta Yard", TenderStatus.Declined, 0m);

        var cmp = ProjectPlanningRules.Compare([hull, pumps, paint], [a, b, c, d]);

        Assert.Equal(["Alpha Yard", "Beta Yard"], cmp.Columns.Select(x => x.YardName));
        var (alpha, beta) = (cmp.Columns[0], cmp.Columns[1]);
        Assert.Equal((16_000m, 0, true), (alpha.Total, alpha.Unpriced, alpha.IsLowest));
        Assert.Equal((9_500m, 1, false), (beta.Total, beta.Unpriced, beta.IsLowest));                                       // lower in money, but it is not comparable
        Assert.Equal(["Blast the hull", "Overhaul pumps"], cmp.Rows.Select(r => r.Description));
        Assert.Equal(beta.TenderId, cmp.Rows[0].CheapestTenderId);
        Assert.Null(cmp.Rows[1].CheapestTenderId);                                                                          // only one yard priced the pumps: nothing to compare
    }

    [Fact]
    public void ADelayIsDaysPastThePlannedEnd_NeverNegative_AndTheChartRangeCoversEveryDate()
    {
        var phase = new ProjectPhase { PlannedStart = new DateTime(2026, 11, 1), PlannedEnd = new DateTime(2026, 11, 10) };
        Assert.Null(ProjectPlanningRules.DelayDays(phase, Today));                                                         // not started: nothing to compare
        phase.ActualStart = new DateTime(2026, 11, 2);
        Assert.Equal(3, ProjectPlanningRules.DelayDays(phase, Today));                                                     // still going, 3 days past the plan
        phase.ActualEnd = new DateTime(2026, 11, 14);
        Assert.Equal(4, ProjectPlanningRules.DelayDays(phase, Today));
        phase.ActualEnd = new DateTime(2026, 11, 8);
        Assert.Equal(0, ProjectPlanningRules.DelayDays(phase, Today));                                                     // early is not a delay

        Assert.Null(ProjectPlanningRules.Range([new ProjectPhase()], Today));
        Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 13)), ProjectPlanningRules.Range([new ProjectPhase { PlannedStart = new DateTime(2026, 11, 1), PlannedEnd = new DateTime(2026, 11, 10), ActualStart = new DateTime(2026, 11, 2) }], Today));
        Assert.NotNull(ProjectPlanningRules.CheckPhase(new DateTime(2026, 11, 5), new DateTime(2026, 11, 4)));
    }

    [Fact]
    public void TheSuperintendentLoadSplitsProjectDaysByMonth_AndGroupsUnassignedProjects()
    {
        static ProjectListRow Row(string superintendent, DateTime start, DateTime end)
        {
            var p = new Project { Id = Guid.NewGuid(), Code = "P", Title = "T", SuperintendentName = superintendent, CreatedAt = start };
            return new ProjectListRow(new ProjectRow(p, ProjectStatus.Complete, start, end, 0, 0, 0), "V", 0, 0, new ProjectCost(0, 0, 0), null, null);
        }
        var spans = ProjectFleetRules.Spans([Row("Anita", new DateTime(2026, 1, 25), new DateTime(2026, 2, 3)), Row("Anita", new DateTime(2026, 2, 1), new DateTime(2026, 2, 10)), Row("", new DateTime(2026, 2, 5), new DateTime(2026, 2, 6))], Today);

        var load = ProjectFleetRules.WorkloadBySuperintendent(spans, new DateTime(2026, 1, 1), 3);

        Assert.Equal(["Anita", "Not assigned"], load.Select(l => l.Name));
        Assert.Equal((2, 20), (load[0].Projects, load[0].Days));
        Assert.Equal([7, 13, 0], load[0].DaysByMonth);
        Assert.Equal((1, 2), (load[1].Projects, load[1].Days));
    }
}

/// <summary>Yard tenders, phases, the documents checklist and risk links on a real office and a real vessel: office only, locked when the project is.</summary>
public class ProjectPlanningTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ProjectService _shore = null!;
    private ProjectPlanningService _plan = null!, _vesselPlan = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ProjectService(_s.ShoreFactory);
        _plan = new ProjectPlanningService(_s.ShoreFactory);
        _vesselPlan = new ProjectPlanningService(_s.VesselFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.ApprovalRules.RemoveRange(db.ApprovalRules);                                                                     // the plain approval step needs no matrix
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Projects, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<(Project Project, List<ProjectItem> Items)> PlannedAsync()
    {
        var project = await _shore.CreateAsync(_s.CompanyId, new NewProject(_s.VesselId, ProjectType.Drydock, "Drydock 2027", PlannedStart: Today.AddDays(5)), _s.User);
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, [new(null, "Hull", "Blast and repaint the underwater hull"), new(null, "Machinery", "Overhaul the sea-water pumps")], _s.User);
        return (project, (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items.OrderBy(i => i.SortOrder).ToList());
    }

    private async Task<int> OnVessel(Func<ChandleryDbContext, Task<int>> count)
    {
        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        return await count(db);
    }

    [Fact]
    public async Task YardsAreInvited_QuotesComparedAndOneAwarded_WhichSetsTheYardAndTheQuotedCosts_AndTheVesselSeesOnlyTheYardName()
    {
        var (project, items) = await PlannedAsync();
        var alpha = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Alpha Yard", "Cochin"), _s.User);
        var beta = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Beta Yard", "Colombo"), _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "alpha yard"), _s.User));      // names are unique

        var tenderA = await _plan.InviteAsync(_s.CompanyId, project.Id, alpha.Id, _s.User);
        var tenderB = await _plan.InviteAsync(_s.CompanyId, project.Id, beta.Id, _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.InviteAsync(_s.CompanyId, project.Id, alpha.Id, _s.User));                        // only once

        Assert.Contains("at least one price", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.RecordQuoteAsync(_s.CompanyId, tenderA.Id, new TenderQuoteInput("", null, 0, "", new Dictionary<Guid, decimal?>()), _s.User))).Message);
        await _plan.RecordQuoteAsync(_s.CompanyId, tenderA.Id, new TenderQuoteInput("Q-1", Today.AddDays(30), 1_000m, "", new Dictionary<Guid, decimal?> { [items[0].Id] = 10_000m, [items[1].Id] = 5_000m }), _s.User);
        await _plan.RecordQuoteAsync(_s.CompanyId, tenderB.Id, new TenderQuoteInput("", null, 500m, "", new Dictionary<Guid, decimal?> { [items[0].Id] = 9_000m, [items[1].Id] = 6_500m }), _s.User);

        var cmp = await _plan.CompareAsync(_s.CompanyId, project.Id);
        Assert.Equal((16_000m, 16_000m), (cmp.Columns[0].Total, cmp.Columns[1].Total));
        Assert.Equal([tenderB.Id, tenderA.Id], cmp.Rows.Select(r => r.CheapestTenderId));                                                              // Beta is cheaper on the hull, Alpha on the pumps

        var awarded = await _plan.AwardAsync(_s.CompanyId, tenderA.Id, _s.User);
        Assert.Equal(TenderStatus.Awarded, awarded.Status);
        var tenders = (await _plan.TendersAsync(_s.CompanyId, project.Id)).ToDictionary(t => t.YardName, t => t.Tender.Status);
        Assert.Equal((TenderStatus.Awarded, TenderStatus.NotAwarded), (tenders["Alpha Yard"], tenders["Beta Yard"]));
        Assert.Equal("Alpha Yard", (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Yard);
        Assert.Equal([10_000m, 5_000m], (await _shore.ItemCostsAsync(_s.CompanyId, project.Id)).OrderBy(c => c.QuotedUsd).Reverse().Select(c => c.QuotedUsd!.Value));

        await _plan.AwardAsync(_s.CompanyId, tenderB.Id, _s.User);                                                                                       // changing the award swaps everything over
        Assert.Equal("Beta Yard", (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Yard);
        Assert.Equal(TenderStatus.NotAwarded, (await _plan.TendersAsync(_s.CompanyId, project.Id)).Single(t => t.YardName == "Alpha Yard").Tender.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.RemoveTenderAsync(_s.CompanyId, tenderA.Id, _s.User));                           // a yard that quoted stays on record

        // The vessel learns who the yard is (it is part of the project) and nothing else: no yard, tender or price is held onboard.
        await _s.SettleUntilQuietAsync();
        Assert.Equal("Beta Yard", (await new ProjectService(_s.VesselFactory).GetAsync(_s.CompanyId, project.Id))!.Project.Yard);
        Assert.Equal((0, 0, 0), (await OnVessel(db => db.ProjectYards.CountAsync()), await OnVessel(db => db.ProjectTenders.CountAsync()), await OnVessel(db => db.ProjectTenderLines.CountAsync())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselPlan.InviteAsync(_s.CompanyId, project.Id, alpha.Id, _s.User));
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.ProjectYards.Add(new ProjectYard { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "Sneaked" });
            Assert.Contains("handled by the office", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        }
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TendersNeedAManager_ARefusingYardCanBeRecorded_AndTheyStopWhenTheWorkIsComplete()
    {
        var (project, _) = await PlannedAsync();
        var yard = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Gamma Yard"), _s.User);
        var other = await _plan.SaveYardAsync(_s.CompanyId, new YardInput(null, "Delta Yard"), _s.User);

        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var clerk = new AppUser { Id = Guid.CreateVersion7(), Email = $"clerk{Guid.NewGuid():N}@test.example.com", PasswordHash = "x", Role = UserRole.CompanyUser, DisplayName = "Clerk", CompanyId = _s.CompanyId, CreatedAt = DateTime.UtcNow };
            db.Users.Add(clerk);
            await db.SaveChangesAsync();
            Assert.Contains("project manager", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.InviteAsync(_s.CompanyId, project.Id, yard.Id, new Actor(clerk.Id, "Clerk")))).Message);
        }

        var tender = await _plan.InviteAsync(_s.CompanyId, project.Id, yard.Id, _s.User);
        await _plan.DeclineAsync(_s.CompanyId, tender.Id, "No dock free that month", _s.User);
        var view = Assert.Single(await _plan.TendersAsync(_s.CompanyId, project.Id));
        Assert.Equal((TenderStatus.Declined, "No dock free that month"), (view.Tender.Status, view.Tender.Remarks));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AwardAsync(_s.CompanyId, tender.Id, _s.User));                                  // a yard that declined cannot win
        await _plan.RemoveTenderAsync(_s.CompanyId, tender.Id, _s.User);
        Assert.Empty(await _plan.TendersAsync(_s.CompanyId, project.Id));

        await _plan.InviteAsync(_s.CompanyId, project.Id, other.Id, _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today, "", _s.User);
        Assert.Contains("already complete", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.InviteAsync(_s.CompanyId, project.Id, yard.Id, _s.User))).Message);
    }

    [Fact]
    public async Task PhasesAndTheDocumentsChecklistAreFilledFromTheStandardLists_AndTheMissingReportFollowsThem_UntilTheProjectCloses()
    {
        var (project, _) = await PlannedAsync();

        Assert.Equal(5, await _plan.AddStandardPhasesAsync(_s.CompanyId, project.Id, _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.AddStandardPhasesAsync(_s.CompanyId, project.Id, _s.User));
        var phases = (await _plan.PhasesAsync(_s.CompanyId, project.Id)).Select(p => new PhaseInput(null, p.Name, null, null, null, null)).ToList();
        phases[1] = phases[1] with { PlannedStart = Today.AddDays(2), PlannedEnd = Today.AddDays(1) };
        Assert.Contains("cannot end before it starts", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.SavePhasesAsync(_s.CompanyId, project.Id, phases, _s.User))).Message);
        phases[1] = phases[1] with { PlannedEnd = Today.AddDays(6) };
        await _plan.SavePhasesAsync(_s.CompanyId, project.Id, phases, _s.User);
        Assert.Equal(Today.AddDays(6), (await _plan.PhasesAsync(_s.CompanyId, project.Id))[1].PlannedEnd);

        Assert.Equal(9, await _plan.AddStandardDocumentsAsync(_s.CompanyId, project.Id, _s.User));
        Assert.Equal(0, await _plan.AddStandardDocumentsAsync(_s.CompanyId, project.Id, _s.User));                                                      // nothing is added twice
        var report = Assert.Single(await _plan.MissingDocumentsAsync(_s.CompanyId));
        Assert.Equal((project.Id, 9, 0), (report.ProjectId, report.Missing.Count, report.Overdue));

        var docs = (await _plan.DocumentsAsync(_s.CompanyId, project.Id)).Select(d => new DocumentInput(null, d.Name, d.Status, d.DueDate, d.ReceivedOn, d.Reference, d.Note)).ToList();
        docs[0] = docs[0] with { Status = DocumentCheckStatus.Received };                                                                               // received, no date given: today
        docs[1] = docs[1] with { Status = DocumentCheckStatus.NotNeeded };
        docs[2] = docs[2] with { DueDate = Today.AddDays(-2) };
        await _plan.SaveDocumentsAsync(_s.CompanyId, project.Id, docs, _s.User);
        Assert.Equal(Today, (await _plan.DocumentsAsync(_s.CompanyId, project.Id))[0].ReceivedOn);
        report = Assert.Single(await _plan.MissingDocumentsAsync(_s.CompanyId));
        Assert.Equal((7, 1), (report.Missing.Count, report.Overdue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.SaveDocumentsAsync(_s.CompanyId, project.Id, [new DocumentInput(null, " ", DocumentCheckStatus.Required, null, null)], _s.User));

        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Approved, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Started, Today, "", _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Completed, Today, "", _s.User);
        var everything = (await _shore.GetAsync(_s.CompanyId, project.Id))!.Project.Items;
        await _shore.SaveItemsAsync(_s.CompanyId, project.Id, everything.Select(i => new ProjectItemInput(i.Id, i.Group, i.Description, i.Source, i.SourceId, i.SourceRef, ProjectItemStatus.Dropped)).ToList(), _s.User);
        await _shore.RecordAsync(_s.CompanyId, project.Id, ProjectEventKind.Closed, Today, "Reconciled", _s.User);
        Assert.Empty(await _plan.MissingDocumentsAsync(_s.CompanyId));                                                                                  // a closed project is no longer chased
        Assert.Contains("closed and locked", (await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.SaveDocumentsAsync(_s.CompanyId, project.Id, docs, _s.User))).Message);

        await _s.SettleUntilQuietAsync();
        Assert.Equal((0, 0), (await OnVessel(db => db.ProjectPhases.CountAsync()), await OnVessel(db => db.ProjectDocumentChecks.CountAsync())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselPlan.AddStandardDocumentsAsync(_s.CompanyId, project.Id, _s.User));
    }

    [Fact]
    public async Task ARiskAssessmentIsLinkedByItsReference_AndTheLinkFollowsItToItsLatestVersion()
    {
        var (project, _) = await PlannedAsync();
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.RiskAssessments.Add(new RiskAssessment { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Code = "SF-RA-2026-0001", Version = 1, JobDescription = "Hot work in the dock", SubmissionCount = 1, HighestResidualBand = "Medium", CreatedAt = DateTime.UtcNow });
            db.RiskAssessments.Add(new RiskAssessment { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Code = "SF-RA-2026-0002", Version = 1, JobDescription = "A draft nobody submitted", SubmissionCount = 0, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var candidate = Assert.Single(await _plan.RiskCandidatesAsync(_s.CompanyId, project.Id));                                                       // drafts are not offered
        Assert.Equal("SF-RA-2026-0001", candidate.Code);
        await _plan.LinkRiskAsync(_s.CompanyId, project.Id, candidate.Code, _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.LinkRiskAsync(_s.CompanyId, project.Id, candidate.Code, _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _plan.LinkRiskAsync(_s.CompanyId, project.Id, "NO-SUCH", _s.User));
        Assert.Empty(await _plan.RiskCandidatesAsync(_s.CompanyId, project.Id));

        var first = Assert.Single(await _plan.RiskLinksAsync(_s.CompanyId, project.Id));
        Assert.Equal((1, "Hot work in the dock", "Medium"), (first.Version, first.Title, first.ResidualBand));

        Guid v2;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            v2 = Guid.CreateVersion7();
            db.RiskAssessments.Add(new RiskAssessment { Id = v2, CompanyId = _s.CompanyId, VesselId = _s.VesselId, Code = "SF-RA-2026-0001", Version = 2, JobDescription = "Hot work in the dock (revised)", SubmissionCount = 1, HighestResidualBand = "Low", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var latest = Assert.Single(await _plan.RiskLinksAsync(_s.CompanyId, project.Id));
        Assert.Equal((v2, 2, "Low"), (latest.AssessmentId, latest.Version, latest.ResidualBand));

        await _plan.UnlinkRiskAsync(_s.CompanyId, latest.Link.Id, _s.User);
        Assert.Empty(await _plan.RiskLinksAsync(_s.CompanyId, project.Id));
        Assert.Equal(0, await OnVessel(db => db.ProjectRiskLinks.CountAsync()));
    }
}
