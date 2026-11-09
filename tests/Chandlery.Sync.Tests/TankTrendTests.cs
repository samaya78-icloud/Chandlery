using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

public class TankTrendRulesTests
{
    private static TankInspection Inspection(string code, int daysAgo, params (CriterionGroup Group, string Name, ScaleKind Scale, string Value, string Exceptions)[] answers)
    {
        var i = new TankInspection { Id = Guid.CreateVersion7(), Code = code, InspectedOn = DateTime.UtcNow.Date.AddDays(-daysAgo), CreatedAt = DateTime.UtcNow.AddDays(-daysAgo), SubmissionCount = 1 };
        var order = 0;
        foreach (var a in answers) i.Results.Add(new TankInspectionResult { Id = Guid.CreateVersion7(), Group = a.Group, CriterionName = a.Name, Scale = a.Scale, Value = a.Value, ExceptionValues = a.Exceptions, SortOrder = order++ });
        return i;
    }

    private static (CriterionGroup, string, ScaleKind, string, string) Rating(string name, string value, CriterionGroup group = CriterionGroup.Structure)
        => (group, name, ScaleKind.Rating, value, "Fair,Bad");

    [Fact]
    public void AnswersAreColouredByHowGoodTheyAre_AndNotInspectedOrNotApplicableSayNothing()
    {
        Assert.Equal([CellTone.Good, CellTone.Fair, CellTone.Bad, CellTone.None, CellTone.None], new[] { "Good", "Fair", "Bad", "NI", "NA" }.Select(v => TankTrend.Tone(ScaleKind.Rating, v, "Fair,Bad")));
        Assert.Equal([CellTone.Good, CellTone.Fair, CellTone.Fair, CellTone.Bad], new[] { "76-100", "51-75", "26-50", "0-25" }.Select(v => TankTrend.Tone(ScaleKind.PercentBucket, v, "")));
        Assert.Equal(CellTone.Good, TankTrend.Tone(ScaleKind.YesNoNa, "No", ""));                     // "No" is only a concern when the criterion says so
        Assert.Equal(CellTone.Fair, TankTrend.Tone(ScaleKind.YesNoNa, "No", "No"));
        Assert.Equal(CellTone.None, TankTrend.Tone(ScaleKind.Rating, "", ""));
        Assert.Equal([ExceptionSeverity.Bad, ExceptionSeverity.Fair, ExceptionSeverity.Other], [TankTrend.SeverityOf(ScaleKind.Rating, "Bad"), TankTrend.SeverityOf(ScaleKind.Rating, "Fair"), TankTrend.SeverityOf(ScaleKind.PercentBucket, "0-25")]);
        Assert.Equal([(1, "0-25"), (2, "26-50"), (3, "51-75"), (4, "76-100")], TankTrend.Levels(ScaleKind.PercentBucket));
        Assert.Equal([(1, "Bad"), (2, "Fair"), (3, "Good")], TankTrend.Levels(ScaleKind.Rating));
    }

    [Fact]
    public void TheMatrixHoldsTheNewestInspectionsOldestFirst_AMissingCriterionIsAGap_AndARenamedOneStartsANewRow()
    {
        var coating = (CriterionGroup.VariableData, "Condition of coating", ScaleKind.Rating, "x", "Fair,Bad");
        var old = Inspection("A", 90, coating with { Item4 = "Good" }, Rating("Bottom plate", "Good"));
        var mid = Inspection("B", 60, coating with { Item4 = "Fair" }, Rating("Bottom plate", "Fair"), Rating("Cracks", "Good", CriterionGroup.Reports));
        var newest = Inspection("C", 30, coating with { Item4 = "Bad" }, Rating("Bottom plates", "Bad"), Rating("Cracks", "Bad", CriterionGroup.Reports));
        var draft = Inspection("D", 1, coating with { Item4 = "Good" }); draft.SubmissionCount = 0;
        var tank = new Tank { Id = Guid.NewGuid(), Code = "1P" };

        var data = TankTrend.Build(tank, [(newest, InspectionStatus.Submitted), (draft, InspectionStatus.Draft), (old, InspectionStatus.Reviewed), (mid, InspectionStatus.Returned)], take: 0);
        Assert.Equal(["A", "B", "C"], data.Columns.Select(c => c.Code));                              // oldest on the left; the draft is not history
        Assert.Equal(3, data.TotalInspections);
        Assert.Equal(["Condition of coating", "Bottom plate", "Bottom plates", "Cracks"], data.Rows.Select(r => r.Name));   // group order first, then the position on the checklist

        var plate = data.Rows.Single(r => r.Name == "Bottom plate");
        Assert.Equal(["Good", "Fair", null], plate.Cells.Select(c => c?.Value));                      // the renamed criterion left a gap in its old row...
        Assert.Equal([null, null, "Bad"], data.Rows.Single(r => r.Name == "Bottom plates").Cells.Select(c => c?.Value));   // ...and a new row of its own
        Assert.Equal([null, "Good", "Bad"], data.Rows.Single(r => r.Name == "Cracks").Cells.Select(c => c?.Value));
        Assert.True(data.Rows.Single(r => r.Name == "Cracks").Cells[2]!.IsException);
        Assert.False(plate.Cells[0]!.IsException);

        var newestTwo = TankTrend.Build(tank, [(old, InspectionStatus.Reviewed), (mid, InspectionStatus.Reviewed), (newest, InspectionStatus.Submitted)], take: 2);
        Assert.Equal(["B", "C"], newestTwo.Columns.Select(c => c.Code));
        Assert.Equal(3, newestTwo.TotalInspections);
    }

    [Fact]
    public void TheCoatingChartHasOnePointPerScoredAnswer_AndNotInspectedLeavesAGap()
    {
        var coating = (CriterionGroup.VariableData, "Condition of coating", ScaleKind.Rating, "x", "Fair,Bad");
        var inspections = new[] { ("A", 90, "Good"), ("B", 60, "NI"), ("C", 30, "Fair"), ("D", 10, "Bad") }
            .Select(x => (Inspection(x.Item1, x.Item2, coating with { Item4 = x.Item3 }), InspectionStatus.Reviewed)).ToList();
        var data = TankTrend.Build(new Tank { Id = Guid.NewGuid() }, inspections, take: 0);

        var row = TankTrend.CoatingRow(data)!;
        var points = TankTrend.Series(data, row);
        Assert.Equal(["A", "C", "D"], points.Select(p => p.Code));
        Assert.Equal([3, 2, 1], points.Select(p => p.Score));
        Assert.Equal([CellTone.Good, CellTone.Fair, CellTone.Bad], points.Select(p => p.Tone));

        var none = TankTrend.Build(new Tank { Id = Guid.NewGuid() }, [(Inspection("E", 5, Rating("Bottom plate", "Good")), InspectionStatus.Submitted)], take: 0);
        Assert.Null(TankTrend.CoatingRow(none));                                                      // a tank whose checklist has no coating criterion simply has no coating chart
    }

    [Fact]
    public void ExceptionsAreListedMostSeriousFirst_ThenByVesselAndTank()
    {
        var vesselA = Guid.NewGuid(); var vesselB = Guid.NewGuid();
        var t1 = new Tank { Id = Guid.NewGuid(), VesselId = vesselA, Code = "2P", Name = "Two port", Type = TankType.Ballast };
        var t2 = new Tank { Id = Guid.NewGuid(), VesselId = vesselB, Code = "1S", Name = "One starboard", Type = TankType.Cargo };
        var i1 = Inspection("I1", 5, Rating("Cracks", "Fair"), Rating("Buckling", "Good")); i1.TankId = t1.Id; i1.VesselId = vesselA;
        var i2 = Inspection("I2", 5, Rating("Cracks", "Bad"), (CriterionGroup.VariableData, "Cathodic condition", ScaleKind.PercentBucket, "0-25", "0-25")); i2.TankId = t2.Id; i2.VesselId = vesselB;
        var tanks = new Dictionary<Guid, Tank> { [t1.Id] = t1, [t2.Id] = t2 };
        var names = new Dictionary<Guid, string> { [vesselA] = "MV Alpha", [vesselB] = "MV Bravo" };

        var all = TankTrend.Exceptions([i1, i2], tanks, names, _ => InspectionStatus.Submitted);
        Assert.Equal([("I2", ExceptionSeverity.Bad), ("I1", ExceptionSeverity.Fair), ("I2", ExceptionSeverity.Other)], all.Select(r => (r.InspectionCode, r.Severity)));
        Assert.Equal(["MV Bravo", "MV Alpha", "MV Bravo"], all.Select(r => r.VesselName));
        Assert.Equal(["I2"], TankTrend.Exceptions([i1, i2], tanks, names, _ => InspectionStatus.Submitted, [ExceptionSeverity.Bad]).Select(r => r.InspectionCode));
    }
}

/// <summary>Trends and exceptions read from real inspections on a real office and a real vessel: both sides must tell the same story.</summary>
public class TankTrendTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private TankService _shoreTanks = null!;
    private TankInspectionService _shore = null!, _vessel = null!;
    private TankTrendService _shoreTrend = null!, _vesselTrend = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreTanks = new TankService(_s.ShoreFactory);
        _shore = new TankInspectionService(_s.ShoreFactory);
        _vessel = new TankInspectionService(_s.VesselFactory);
        _shoreTrend = new TankTrendService(_s.ShoreFactory, _shore);
        _vesselTrend = new TankTrendService(_s.VesselFactory, _vessel);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.TankInspection, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Tank> AddTankAsync(string code, TankType type = TankType.Ballast)
    {
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput(code, $"Tank {code}", type, 10, 5, 12, 8), _s.User);
        await _s.SettleUntilQuietAsync();
        return tank;
    }

    private async Task<Guid> InspectAsync(Tank tank, int daysAgo, bool submit, params (string Criterion, string Value)[] answers)
    {
        var template = (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).Single(t => t.Template.TankType == tank.Type).Template.Id;
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, template, Today.AddDays(-daysAgo), "At sea"), _s.User);
        var detail = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        var edit = detail.Inspection.Results.Select(r => new InspectionAnswer(r.Id,
            answers.Any(a => a.Criterion == r.CriterionName) ? answers.First(a => a.Criterion == r.CriterionName).Value : r.Scale == ScaleKind.PercentBucket ? "76-100" : "Good", "")).ToList();
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(Today.AddDays(-daysAgo), "At sea", "", "", "Chief Officer", edit), _s.User);
        if (submit) await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        return started.Id;
    }

    private static string Describe(TankTrendData data)
        => string.Join("|", data.Columns.Select(c => $"{c.Code}:{c.Status}:{c.ExceptionCount}")) + "#" + string.Join("|", data.Rows.Select(r => $"{r.Name}={string.Join(",", r.Cells.Select(c => c?.Value ?? "-"))}"));

    [Fact]
    public async Task TheOfficeAndTheVesselDrawTheSameTrend_AndTheCoatingChartFollowsTheCondition()
    {
        var tank = await AddTankAsync("2P");
        await InspectAsync(tank, 30, true, ("Condition of coating", "Good"), ("Cracks", "Fair"));
        await InspectAsync(tank, 20, true, ("Condition of coating", "Fair"));
        var last = await InspectAsync(tank, 10, true, ("Condition of coating", "Bad"));
        await InspectAsync(tank, 1, false, ("Condition of coating", "Good"));                         // a draft is not history
        await _shore.ReviewAsync(_s.CompanyId, last, ReviewDecision.Started, "", _s.User);
        await _s.SettleUntilQuietAsync();

        var shore = (await _shoreTrend.TrendAsync(_s.CompanyId, tank.Id))!;
        var vessel = (await _vesselTrend.TrendAsync(_s.CompanyId, tank.Id))!;
        Assert.Equal(Describe(shore), Describe(vessel));
        Assert.Equal(3, shore.Columns.Count);
        Assert.Equal([InspectionStatus.Submitted, InspectionStatus.Submitted, InspectionStatus.InReview], shore.Columns.Select(c => c.Status));
        Assert.Equal([1, 1, 1], shore.Columns.Select(c => c.ExceptionCount));                        // Fair cracks; Fair coating; Bad coating... one each

        var points = TankTrend.Series(shore, TankTrend.CoatingRow(shore)!);
        Assert.Equal(["Good", "Fair", "Bad"], points.Select(p => p.Value));
        Assert.Equal([3, 2, 1], points.Select(p => p.Score));

        Assert.Equal(2, (await _shoreTrend.TrendAsync(_s.CompanyId, tank.Id, take: 2))!.Columns.Count);
        Assert.Null(await _shoreTrend.TrendAsync(_s.CompanyId, Guid.NewGuid()));
    }

    [Fact]
    public async Task TheExceptionSummaryShowsWhatIsWrongNow_OrEverythingFound_AndAnswersTheFilters()
    {
        var tank = await AddTankAsync("2P");
        await InspectAsync(tank, 30, true, ("Cracks", "Fair"));
        await InspectAsync(tank, 20, true, ("Condition of coating", "Fair"));
        await InspectAsync(tank, 10, true, ("Condition of coating", "Bad"));

        var now = await _shoreTrend.ExceptionsAsync(_s.CompanyId);
        var current = Assert.Single(now);                                                             // only the latest inspection of the tank counts as "now"
        Assert.Equal(("Condition of coating", "Bad", ExceptionSeverity.Bad, "2P"), (current.Criterion, current.Value, current.Severity, current.TankCode));
        Assert.Equal("MV Sea Falcon", current.VesselName);

        var everything = await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter(CurrentOnly: false));
        Assert.Equal([ExceptionSeverity.Bad, ExceptionSeverity.Fair, ExceptionSeverity.Fair], everything.Select(r => r.Severity));
        Assert.Equal(1, (await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter(Severities: [ExceptionSeverity.Bad], CurrentOnly: false))).Count);
        Assert.Equal(2, (await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter(CurrentOnly: false, Since: Today.AddDays(-25)))).Count);
        Assert.Equal(3, (await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter([_s.VesselId], [TankType.Ballast], CurrentOnly: false))).Count);
        Assert.Empty(await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter(Types: [TankType.Cargo], CurrentOnly: false)));
        Assert.Empty(await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter(VesselIds: [_s.OtherVesselId], CurrentOnly: false)));   // a vessel the module is off for is not in the picture

        Assert.Equal(1, (await _vesselTrend.ExceptionsAsync(_s.CompanyId)).Count);                    // the vessel sees its own the same way

        // A tank taken out of service no longer has "current" exceptions, though its history stays.
        await _shoreTanks.UpdateTankAsync(_s.CompanyId, tank.Id, new TankInput("2P", "Tank 2P", TankType.Ballast, 10, 5, 12, 8), isActive: false);
        Assert.Empty(await _shoreTrend.ExceptionsAsync(_s.CompanyId));
        Assert.Equal(3, (await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter(CurrentOnly: false))).Count);
    }

    [Fact]
    public async Task TheCountsForTilesAndMenuFollowTheWork_AndAreNullWhenTheModuleIsOff()
    {
        var tank = await AddTankAsync("2P");
        var never = await AddTankAsync("9S");
        await InspectAsync(tank, 20, true, ("Cracks", "Fair"));
        await InspectAsync(tank, 10, true, ("Condition of coating", "Bad"));
        await InspectAsync(never, 1, false);                                                          // a draft on a tank that has never been submitted

        var counts = (await _shoreTrend.CountsAsync(_s.CompanyId))!;
        Assert.Equal(new TankCounts(WaitingReview: 2, WaitingBad: 1, WithVessel: 1, CurrentExceptions: 1, TanksWithBad: 1, NeverInspected: 1), counts);

        var older = (await _shore.ListAsync(_s.CompanyId, tankId: tank.Id)).Single(r => r.Inspection.ExceptionCount == 1 && !r.Inspection.HasBadRating).Inspection.Id;
        await _shore.ReviewAsync(_s.CompanyId, older, ReviewDecision.Accepted, "Noted", _s.User);
        Assert.Equal(1, (await _shoreTrend.CountsAsync(_s.CompanyId))!.WaitingReview);

        Assert.Null(await new TankTrendService(_s.ShoreFactory, _shore).CountsAsync(Guid.NewGuid()));   // another company has the module on for no vessel
    }
}
