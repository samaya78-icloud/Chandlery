using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The whole tank inspection story on a real office and a real vessel: the ship inspects and submits while the office changes
/// the checklist, the office returns one inspection and accepts another, and both sides end with the same history.
/// </summary>
public class TankWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private TankService _shoreTanks = null!, _vesselTanks = null!;
    private TankInspectionService _shore = null!, _vessel = null!;
    private TankTrendService _shoreTrend = null!, _vesselTrend = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreTanks = new TankService(_s.ShoreFactory);
        _vesselTanks = new TankService(_s.VesselFactory);
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

    private async Task<Guid> StartAndAnswerAsync(Tank tank, Guid template, int daysAgo, params (string Criterion, string Value)[] except)
    {
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, template, Today.AddDays(-daysAgo), "At sea"), _s.User);
        await AnswerAsync(started.Id, daysAgo, except);
        return started.Id;
    }

    private async Task AnswerAsync(Guid id, int daysAgo, params (string Criterion, string Value)[] except)
    {
        var detail = (await _vessel.GetAsync(_s.CompanyId, id))!;
        var answers = detail.Inspection.Results.Select(r => new InspectionAnswer(r.Id,
            except.Any(e => e.Criterion == r.CriterionName) ? except.First(e => e.Criterion == r.CriterionName).Value : r.Scale == ScaleKind.PercentBucket ? "76-100" : "Good", "")).ToList();
        await _vessel.SaveAsync(_s.CompanyId, id, new InspectionEdit(Today.AddDays(-daysAgo), "At sea", "", "", "Chief Officer", answers), _s.User);
    }

    private static string History(TankTrendData d)
        => string.Join("|", d.Columns.Select(c => $"{c.Code}:{c.Status}:{c.ExceptionCount}")) + "#" + string.Join("|", d.Rows.Select(r => $"{r.Name}={string.Join(",", r.Cells.Select(c => c?.Value ?? "-"))}"));

    [Fact]
    public async Task TheVesselInspects_TheOfficeEditsTheChecklistReturnsOneAndAcceptsAnother_AndBothSidesEndWithTheSameStory()
    {
        // The office sets up the tanks; they reach the vessel.
        var ballast = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput("1P", "No. 1 ballast (port)", TankType.Ballast, 10, 5, 12, 8), _s.User);
        var cargo = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput("3C", "No. 3 cargo (centre)", TankType.Cargo, 30, 10, 16, 14), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, (await _vesselTanks.ListTanksAsync(_s.CompanyId, _s.VesselId)).Count);

        var templates = await _shoreTanks.ListTemplatesAsync(_s.CompanyId);
        var ballastTemplate = templates.Single(t => t.Template.TankType == TankType.Ballast).Template.Id;
        var cargoTemplate = templates.Single(t => t.Template.TankType == TankType.Cargo).Template.Id;
        var ballastCriteria = templates.Single(t => t.Template.Id == ballastTemplate).Criteria.Count;

        // The vessel inspects both tanks and submits one, leaving the other as a draft...
        var first = await StartAndAnswerAsync(ballast, ballastTemplate, 20, ("Condition of coating", "Fair"), ("Cracks", "Bad"));
        var cargoDraft = await StartAndAnswerAsync(cargo, cargoTemplate, 5);
        await _vessel.SubmitAsync(_s.CompanyId, first, _s.User);

        // ...while the office changes the ballast checklist. The inspection already made keeps the questions it was started with.
        await _shoreTanks.SaveCriterionAsync(_s.CompanyId, ballastTemplate, null, CriterionGroup.Reports, "Sediment build-up", ScaleKind.Rating, null, 99);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(ballastCriteria, (await _shore.GetAsync(_s.CompanyId, first))!.Inspection.Results.Count);

        // The office sees the Bad rating in the queue and sends it back.
        var queue = await _shore.ReviewQueueAsync(_s.CompanyId);
        Assert.Equal([first], queue.Select(q => q.Row.Inspection.Id));
        Assert.True(queue[0].Row.Inspection.HasBadRating);
        await _shore.ReviewAsync(_s.CompanyId, first, ReviewDecision.Returned, "Give the length of the crack", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(InspectionStatus.Returned, (await _vessel.GetAsync(_s.CompanyId, first))!.Status);

        // The vessel corrects and resubmits; a later inspection of the same tank carries the new question.
        await AnswerAsync(first, 20, ("Condition of coating", "Fair"), ("Cracks", "Fair"));
        await _vessel.SubmitAsync(_s.CompanyId, first, _s.User);
        var second = await StartAndAnswerAsync(ballast, ballastTemplate, 2, ("Condition of coating", "Good"), ("Sediment build-up", "Fair"));
        await _vessel.SubmitAsync(_s.CompanyId, second, _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, cargoDraft, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(ballastCriteria + 1, (await _shore.GetAsync(_s.CompanyId, second))!.Inspection.Results.Count);

        // The office accepts the first (it locks everywhere) and starts looking at the second.
        await _shore.ReviewAsync(_s.CompanyId, first, ReviewDecision.Accepted, "Noted", _s.User);
        await _shore.ReviewAsync(_s.CompanyId, second, ReviewDecision.Started, "", _s.User);
        await _s.SettleUntilQuietAsync();
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => AnswerAsync(first, 20, ("Cracks", "Good")));
        Assert.Contains("locked", locked.Message);
        Assert.Equal(new[] { second, cargoDraft }.Order(), (await _shore.ReviewQueueAsync(_s.CompanyId)).Select(q => q.Row.Inspection.Id).Order());

        // Both sides now tell the same story: statuses, every answer, and the exception counts.
        foreach (var tank in new[] { ballast, cargo })
            Assert.Equal(History((await _shoreTrend.TrendAsync(_s.CompanyId, tank.Id))!), History((await _vesselTrend.TrendAsync(_s.CompanyId, tank.Id))!));

        var trend = (await _shoreTrend.TrendAsync(_s.CompanyId, ballast.Id))!;
        Assert.Equal([InspectionStatus.Reviewed, InspectionStatus.InReview], trend.Columns.Select(c => c.Status));
        Assert.Equal(["Fair", "Good"], TankTrend.Series(trend, TankTrend.CoatingRow(trend)!).Select(p => p.Value));
        Assert.Equal([null, "Fair"], trend.Rows.Single(r => r.Name == "Sediment build-up").Cells.Select(c => c?.Value));   // the new question has an answer only from the later inspection

        // "Now" is the latest inspection of each tank: the coating is fine again by then, but the sediment is not.
        Assert.Equal(["Sediment build-up"], (await _shoreTrend.ExceptionsAsync(_s.CompanyId)).Select(r => r.Criterion));
        Assert.Equal(["Sediment build-up"], (await _vesselTrend.ExceptionsAsync(_s.CompanyId)).Select(r => r.Criterion));
        var everything = await _shoreTrend.ExceptionsAsync(_s.CompanyId, new ExceptionFilter(CurrentOnly: false));
        Assert.Equal(["Condition of coating", "Cracks", "Sediment build-up"], everything.Select(r => r.Criterion).Order());
        Assert.DoesNotContain(everything, r => r.Severity == ExceptionSeverity.Bad);                // the Bad crack was corrected on the resubmission
    }
}
