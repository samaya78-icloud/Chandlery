using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Tanks, checklists and inspections with their reviews on a real office and a real vessel.</summary>
public class TankSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private TankService _shoreTanks = null!, _vesselTanks = null!;
    private TankInspectionService _shore = null!, _vessel = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreTanks = new TankService(_s.ShoreFactory);
        _vesselTanks = new TankService(_s.VesselFactory);
        _shore = new TankInspectionService(_s.ShoreFactory);
        _vessel = new TankInspectionService(_s.VesselFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync(params Guid[] vessels)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var vessel in vessels.Length == 0 ? [_s.VesselId] : vessels)
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.TankInspection, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private static TankInput Ballast(string code = "2P", string name = "No. 2 port ballast tank", int x = 10, int y = 5) => new(code, name, TankType.Ballast, x, y, 12, 8, SteelGradeDeck: "AH36", OriginalCoating: "Epoxy");

    private async Task<Guid> TemplateIdAsync(TankType type) => (await OnShore(db => db.TankChecklistTemplates.SingleAsync(t => t.TankType == type))).Id;

    /// <summary>The answers for every result of an inspection: Good, except for the named criteria.</summary>
    private static List<InspectionAnswer> Answers(TankInspection inspection, params (string Criterion, string Value)[] except)
        => inspection.Results.Select(r => new InspectionAnswer(r.Id, except.Any(e => e.Criterion == r.CriterionName) ? except.First(e => e.Criterion == r.CriterionName).Value
            : r.Scale == ScaleKind.PercentBucket ? "76-100" : "Good", "")).ToList();


    // ---- the module and the checklists ----

    [Fact]
    public async Task TheModuleIsOffUntilSwitchedOn_AndTheStandardChecklistsReachTheVesselReadOnly()
    {
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast(), _s.User));
        Assert.Contains("not switched on", off.Message);
        await SwitchOnAsync();
        Assert.NotNull(await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast(), _s.User));

        Assert.Equal(6, (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).Count);                                                     // one per tank type, created with the company
        var atVessel = await _vesselTanks.ListTemplatesAsync(_s.CompanyId);
        Assert.Equal(6, atVessel.Count);
        var cargo = atVessel.Single(t => t.Template.TankType == TankType.Cargo);
        Assert.Equal(30, cargo.Criteria.Count);
        Assert.Equal("Fair,Bad", cargo.Criteria.Single(c => c.Name == "Condition of coating").ExceptionValues);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselTanks.SaveTemplateAsync(_s.CompanyId, cargo.Template.Id, "Changed onboard", TankType.Cargo));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselTanks.SaveCriterionAsync(_s.CompanyId, cargo.Template.Id, null, CriterionGroup.Structure, "Made up onboard", ScaleKind.Rating, null, 99));

        // The office's change raises the version and reaches the vessel.
        var criterion = cargo.Criteria.Single(c => c.Name == "Cracks");
        await _shoreTanks.SaveCriterionAsync(_s.CompanyId, cargo.Template.Id, criterion.Id, CriterionGroup.Reports, "Cracks and fractures", ScaleKind.Rating, "Bad", criterion.SortOrder);
        await _s.SettleUntilQuietAsync();
        var updated = (await _vesselTanks.ListTemplatesAsync(_s.CompanyId)).Single(t => t.Template.TankType == TankType.Cargo);
        Assert.Equal((2, "Cracks and fractures", "Bad"), (updated.Template.Version, updated.Criteria.Single(c => c.Id == criterion.Id).Name, updated.Criteria.Single(c => c.Id == criterion.Id).ExceptionValues));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTanks.SaveCriterionAsync(_s.CompanyId, cargo.Template.Id, null, CriterionGroup.Structure, "Odd", ScaleKind.Rating, "Terrible", 99));   // not an answer on the scale
    }

    // ---- tanks ----

    [Fact]
    public async Task TanksAreKeptByTheSideThatMadeThem_AndAnotherVesselsTanksNeverArrive()
    {
        await SwitchOnAsync(_s.VesselId, _s.OtherVesselId);
        var officeTank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast(), _s.User);
        var otherTank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.OtherVesselId, Ballast(), _s.User);
        var vesselTank = await _vesselTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput("FW1", "Fresh water tank", TankType.FreshWater, 60, 20, 10, 8), _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.Equal(["2P", "FW1"], (await _vesselTanks.ListTanksAsync(_s.CompanyId, _s.VesselId)).Select(t => t.Code));
        Assert.Equal(["2P", "FW1"], (await _shoreTanks.ListTanksAsync(_s.CompanyId, _s.VesselId)).Select(t => t.Code));
        Assert.False(await OnVessel(db => db.Tanks.AnyAsync(t => t.Id == otherTank.Id)));                                                // another vessel's tank never arrives

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselTanks.UpdateTankAsync(_s.CompanyId, officeTank.Id, Ballast(name: "Renamed onboard")));
        Assert.Contains("belongs to the office", refused.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTanks.UpdateTankAsync(_s.CompanyId, vesselTank.Id, new TankInput("FW1", "Renamed ashore", TankType.FreshWater)));

        await _shoreTanks.UpdateTankAsync(_s.CompanyId, officeTank.Id, Ballast(name: "No. 2 port water ballast"));
        await _vesselTanks.UpdateTankAsync(_s.CompanyId, vesselTank.Id, new TankInput("FW1", "Fresh water tank (aft)", TankType.FreshWater, 62, 20, 10, 8));
        await _s.SettleUntilQuietAsync();
        Assert.Equal("No. 2 port water ballast", (await OnVessel(db => db.Tanks.SingleAsync(t => t.Id == officeTank.Id))).Name);
        Assert.Equal((62, "Fresh water tank (aft)"), (await OnShore(db => db.Tanks.SingleAsync(t => t.Id == vesselTank.Id))) is { } t ? (t.PlanX, t.Name) : default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast(), _s.User));                                  // the code is taken
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast("3P", "Off the plan", x: 95), _s.User));       // outside the plan
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    // ---- the inspection from start to locked ----

    [Fact]
    public async Task AnInspectionIsFilledInSubmittedReviewedReturnedCorrectedAndAccepted_AndIsThenLocked()
    {
        await SwitchOnAsync();
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast(), _s.User);
        await _s.SettleUntilQuietAsync();
        var template = await TemplateIdAsync(TankType.Ballast);

        // Starting: the checklist is copied into the inspection.
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, template, Today.AddDays(-1), "At sea", "Internal, six-monthly"), _s.User);
        Assert.Equal($"MVSF-TI-{DateTime.UtcNow.Year}-0001", started.Code);
        var draft = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        Assert.Equal((InspectionStatus.Draft, 23, "Ballast tank inspection"), (draft.Status, draft.Inspection.Results.Count, draft.Inspection.TemplateName));
        Assert.All(draft.Inspection.Results, r => Assert.Equal("", r.Value));

        // Not complete: it cannot be submitted.
        var answers = Answers(draft.Inspection, ("Condition of coating", "Fair"), ("Pitting", "Bad"), ("Cathodic condition", "51-75"));
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today.AddDays(-1), "At sea", "Internal, six-monthly", "Coating breaking down on the bottom", "Chief Officer", answers.Take(5).ToList()), _s.User);
        var incomplete = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User));
        Assert.Contains("still need an answer", incomplete.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today, "At sea", "", "", "", [new InspectionAnswer(draft.Inspection.Results[0].Id, "Excellent")]), _s.User));   // not a value of the scale

        await _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today.AddDays(-1), "At sea", "Internal, six-monthly", "Coating breaking down on the bottom", "Chief Officer", answers), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        var submitted = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        Assert.Equal((InspectionStatus.Submitted, 1, 2, true), (submitted.Status, submitted.Inspection.SubmissionCount, submitted.Inspection.ExceptionCount, submitted.Inspection.HasBadRating));

        // Submitted means locked for the vessel.
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today, "At sea", "", "changed", "", answers), _s.User));
        Assert.Contains("cannot be changed until it is returned", locked.Message);

        // It reaches the office with every result, and the same status.
        await _s.SettleUntilQuietAsync();
        var atOffice = (await _shore.GetAsync(_s.CompanyId, started.Id))!;
        Assert.Equal((InspectionStatus.Submitted, 23, "Coating breaking down on the bottom"), (atOffice.Status, atOffice.Inspection.Results.Count, atOffice.Inspection.GeneralComments));
        Assert.Equal(answers.Select(a => (a.ResultId, a.Value)), atOffice.Inspection.Results.OrderBy(r => r.SortOrder).Select(r => (r.Id, r.Value)).OrderBy(x => answers.FindIndex(a => a.ResultId == x.Id)));
        Assert.Equal(submitted.Inspection.Results.Where(r => r.Value is "Fair" or "Bad").Select(r => r.CriterionName).OrderBy(x => x), TankStatus.Exceptions(atOffice.Inspection.Results).Select(r => r.CriterionName).OrderBy(x => x));

        // The vessel cannot review; the office starts, returns with comments.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Accepted, "", _s.User));
        await _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Started, "", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Started, "", _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Returned, " ", _s.User));            // say what to correct
        await _s.SettleUntilQuietAsync();
        Assert.Equal(InspectionStatus.InReview, (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Status);

        await _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Returned, "Photograph the bottom plate and say how much coating has gone", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(InspectionStatus.Returned, (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Status);
        Assert.Equal(InspectionStatus.Returned, (await _shore.GetAsync(_s.CompanyId, started.Id))!.Status);

        // The vessel corrects it and submits again: the earlier review no longer counts.
        var corrected = Answers(draft.Inspection, ("Condition of coating", "Bad"), ("Pitting", "Bad"), ("Cathodic condition", "51-75"));
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today.AddDays(-1), "At sea", "Internal, six-monthly", "About 30% of the bottom coating has gone; pitting 2 mm", "Chief Officer", corrected), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        foreach (var side in new[] { _vessel, _shore })
        {
            var d = (await side.GetAsync(_s.CompanyId, started.Id))!;
            Assert.Equal((InspectionStatus.Submitted, 2, 2, "Bad"), (d.Status, d.Inspection.SubmissionCount, d.Inspection.ExceptionCount, d.Inspection.Results.Single(r => r.CriterionName == "Condition of coating").Value));
        }

        // Accepted: reviewed and locked on both sides.
        await _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Accepted, "Noted; raise the coating job", _s.User);
        await _s.SettleUntilQuietAsync();
        foreach (var side in new[] { _vessel, _shore })
            Assert.Equal((InspectionStatus.Reviewed, 3), ((await side.GetAsync(_s.CompanyId, started.Id))!.Status, (await side.GetAsync(_s.CompanyId, started.Id))!.Reviews.Count));
        var final = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today, "At sea", "", "rewritten", "", corrected), _s.User));
        Assert.Contains("locked", final.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Returned, "too late", _s.User));
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AChangedCopyOfAReviewedInspectionArrivingBySync_IsIgnored_AndReviewsCannotBeChanged()
    {
        await SwitchOnAsync();
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast(), _s.User);
        await _s.SettleUntilQuietAsync();
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, await TemplateIdAsync(TankType.Ballast), Today, "Lagos"), _s.User);
        var answers = Answers((await _vessel.GetAsync(_s.CompanyId, started.Id))!.Inspection);
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today, "Lagos", "", "All good", "", answers), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        var review = await _shore.ReviewAsync(_s.CompanyId, started.Id, ReviewDecision.Accepted, "", _s.User);
        await _s.SettleUntilQuietAsync();

        // A newer, altered copy from the vessel (a sender that was never told it is locked).
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            var held = await Chandlery.Services.Sync.Aggregates.LoadAsync(db, Chandlery.Services.Sync.Aggregates.Find(nameof(TankInspection))!, started.Id);
            var json = Chandlery.Services.Sync.Aggregates.ToJson(db, held!);
            json["GeneralComments"] = "rewritten after review";
            await _s.Agent.PublishAsync(Chandlery.Services.Sync.Aggregates.Document(Chandlery.Services.Sync.Aggregates.Find(nameof(TankInspection))!, started.Id, 9, json, SyncStages.Vessel, 99, null), Lane.Transaction);
        }
        await _s.SettleUntilQuietAsync();
        Assert.Equal("All good", (await _shore.GetAsync(_s.CompanyId, started.Id))!.Inspection.GeneralComments);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());

        // A review is history: no change, no removal, and the vessel cannot write one.
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            (await db.TankInspectionReviews.SingleAsync(r => r.Id == review.Id)).Comment = "changed";
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("history", refused.Message);
        }
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.TankInspectionReviews.Add(new TankInspectionReview { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, InspectionId = started.Id, ForSubmission = 1, Decision = ReviewDecision.Returned, At = DateTime.UtcNow });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("handled by the office", refused.Message);
        }
    }

    // ---- starting, checked ----

    [Fact]
    public async Task AnInspectionMustMatchItsTank_AndTheChecklistItIsMadeFromIsKeptAsItWas()
    {
        await SwitchOnAsync();
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Ballast(), _s.User);
        await _s.SettleUntilQuietAsync();
        var ballast = await TemplateIdAsync(TankType.Ballast);

        Assert.Contains("is for Cargo tanks", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, TemplateOf(TankType.Cargo), Today, "At sea"), _s.User))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, ballast, Today, " "), _s.User));                    // no place
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, ballast, Today.AddDays(2), "At sea"), _s.User));     // future date
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, new NewInspection(Guid.NewGuid(), ballast, Today, "At sea"), _s.User));        // no such tank

        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, ballast, Today, "At sea"), _s.User);
        var before = (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Inspection.Results.Select(r => (r.CriterionName, r.Group, r.Scale)).ToList();

        // The office renames a criterion and adds one: the inspection already made is untouched.
        var view = (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).Single(t => t.Template.TankType == TankType.Ballast);
        var coating = view.Criteria.Single(c => c.Name == "Condition of coating");
        await _shoreTanks.SaveCriterionAsync(_s.CompanyId, view.Template.Id, coating.Id, CriterionGroup.VariableData, "Coating condition (renamed)", ScaleKind.Rating, null, coating.SortOrder);
        await _shoreTanks.SaveCriterionAsync(_s.CompanyId, view.Template.Id, null, CriterionGroup.Structure, "A new criterion", ScaleKind.YesNoNa, "No", 99);
        await _s.SettleUntilQuietAsync();
        var after = (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Inspection;
        Assert.Equal(before, after.Results.Select(r => (r.CriterionName, r.Group, r.Scale)).ToList());
        Assert.Equal(1, after.TemplateVersion);

        // The next inspection uses the new version.
        var next = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, ballast, Today, "At sea"), _s.User);
        var results = (await _vessel.GetAsync(_s.CompanyId, next.Id))!.Inspection;
        Assert.Equal(3, results.TemplateVersion);
        Assert.Contains(results.Results, r => r.CriterionName == "Coating condition (renamed)");
        Assert.Equal("No", results.Results.Single(r => r.CriterionName == "A new criterion").ExceptionValues);
    }

    private Guid TemplateOf(TankType type) => _shoreTanks.ListTemplatesAsync(_s.CompanyId).Result.Single(t => t.Template.TankType == type).Template.Id;

    [Fact]
    public void ASubmittedInspectionWithABadRating_TravelsOnTheUrgentLane()
    {
        Assert.Equal(Lane.Urgent, Chandlery.Services.Sync.SyncPublisher.LaneFor(new TankInspection { HasBadRating = true, SubmissionCount = 1 }));
        Assert.Equal(Lane.Transaction, Chandlery.Services.Sync.SyncPublisher.LaneFor(new TankInspection { HasBadRating = true, SubmissionCount = 0 }));       // a draft is not urgent
        Assert.Equal(Lane.Transaction, Chandlery.Services.Sync.SyncPublisher.LaneFor(new TankInspection { HasBadRating = false, SubmissionCount = 1 }));
    }
}
