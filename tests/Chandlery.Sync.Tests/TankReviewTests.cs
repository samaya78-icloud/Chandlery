using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class TankKeywordTests
{
    [Fact]
    public void KeywordsAreFoundInAnyCase_AlsoInsideLongerWords_AndThePiecesAlwaysJoinBackToTheText()
    {
        var keywords = TankKeywords.Effective("");
        Assert.Equal(TankScales.ReviewKeywords, keywords);                                            // blank means the standard list

        var text = "Heavy RUST on the bottom, cracking near the anode, no leaks.";
        var pieces = TankKeywords.Highlight(text, keywords);
        Assert.Equal(text, string.Concat(pieces.Select(p => p.Text)));
        Assert.Equal(["RUST", "crack", "anode", "leak"], pieces.Where(p => p.Match).Select(p => p.Text));
        Assert.True(TankKeywords.Contains(text, keywords));
        Assert.False(TankKeywords.Contains("Coating looks fine", keywords));
        Assert.Equal([("", false)], TankKeywords.Highlight("", keywords).DefaultIfEmpty(("", false)));
    }

    [Fact]
    public void OverlappingKeywordsAreMerged_AndACompanyListReplacesTheStandardOne()
    {
        var pieces = TankKeywords.Highlight("A pitting hole", ["pit", "pitting", "ting"]);
        Assert.Equal(["pitting"], pieces.Where(p => p.Match).Select(p => p.Text));                    // three overlapping hits are one piece

        var own = TankKeywords.Effective(" sludge ; Scale,sludge\nx");
        Assert.Equal(["sludge", "Scale"], own);                                                       // trimmed, de-duplicated, too-short entries dropped
        Assert.True(TankKeywords.Contains("Heavy scale build up", own));
        Assert.False(TankKeywords.Contains("Heavy rust", own));
    }
}

/// <summary>The office side of a tank inspection: the queue, the decisions and what they do to the vessel, on a real office and a real vessel.</summary>
public class TankReviewTests : IAsyncLifetime
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
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.TankInspection, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<Guid> SubmitAsync(string tankCode, params (string Criterion, string Value)[] except)
    {
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, new TankInput(tankCode, $"Tank {tankCode}", TankType.Ballast, 10, 5, 12, 8), _s.User);
        await _s.SettleUntilQuietAsync();
        var template = (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).Single(t => t.Template.TankType == TankType.Ballast).Template.Id;
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, template, Today, "At sea"), _s.User);
        await AnswerAsync(started.Id, except);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        return started.Id;
    }

    private async Task AnswerAsync(Guid inspectionId, params (string Criterion, string Value)[] except)
    {
        var detail = (await _vessel.GetAsync(_s.CompanyId, inspectionId))!;
        var answers = detail.Inspection.Results.Select(r => new InspectionAnswer(r.Id,
            except.Any(e => e.Criterion == r.CriterionName) ? except.First(e => e.Criterion == r.CriterionName).Value : r.Scale == ScaleKind.PercentBucket ? "76-100" : "Good", "")).ToList();
        await _vessel.SaveAsync(_s.CompanyId, inspectionId, new InspectionEdit(detail.Inspection.InspectedOn, "At sea", "", "", "Chief Officer", answers), _s.User);
    }

    [Fact]
    public async Task TheQueueHoldsWhatIsSubmittedOrBeingReviewed_ABadRatingFirst_AndWaitingCountsFollow()
    {
        var clean = await SubmitAsync("1P");
        await Task.Delay(30);
        var bad = await SubmitAsync("2P", ("Cracks", "Bad"));

        var queue = await _shore.ReviewQueueAsync(_s.CompanyId);
        Assert.Equal([bad, clean], queue.Select(q => q.Row.Inspection.Id));                         // the Bad rating is first although it came later
        Assert.All(queue, q => Assert.Equal("MV Sea Falcon", q.VesselName));
        Assert.Equal(2, (await _shore.ReviewWaitingAsync(_s.CompanyId)).Count);
        Assert.Equal(1, (await _shore.ReviewWaitingAsync(_s.CompanyId)).WithBadRating);
        Assert.Single(await _shore.ReviewQueueAsync(_s.CompanyId, _s.VesselId), q => q.Row.Inspection.Id == bad);
        Assert.Empty(await _shore.ReviewQueueAsync(_s.CompanyId, _s.OtherVesselId));

        // Starting the review keeps it in the queue and lets the vessel see it is being looked at.
        await _shore.ReviewAsync(_s.CompanyId, clean, ReviewDecision.Started, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(InspectionStatus.InReview, (await _vessel.GetAsync(_s.CompanyId, clean))!.Status);
        Assert.Equal(2, (await _shore.ReviewQueueAsync(_s.CompanyId)).Count);

        // Accepting leaves the queue and locks it on both sides.
        await _shore.ReviewAsync(_s.CompanyId, clean, ReviewDecision.Accepted, "Fine", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal([bad], (await _shore.ReviewQueueAsync(_s.CompanyId)).Select(q => q.Row.Inspection.Id));
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => AnswerAsync(clean, ("Cracks", "Bad")));
        Assert.Contains("locked", locked.Message);

        // Returning sends it back to the vessel: it leaves the queue until it is submitted again.
        await _shore.ReviewAsync(_s.CompanyId, bad, ReviewDecision.Returned, "Describe the crack and its length", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Empty(await _shore.ReviewQueueAsync(_s.CompanyId));
        Assert.Equal(InspectionStatus.Returned, (await _vessel.GetAsync(_s.CompanyId, bad))!.Status);
        await AnswerAsync(bad, ("Cracks", "Fair"));
        await _vessel.SubmitAsync(_s.CompanyId, bad, _s.User);
        await _s.SettleUntilQuietAsync();
        var again = Assert.Single(await _shore.ReviewQueueAsync(_s.CompanyId));
        Assert.Equal((bad, InspectionStatus.Submitted, false), (again.Row.Inspection.Id, again.Row.Status, again.Row.Inspection.HasBadRating));
    }

    [Fact]
    public async Task OnlyTheOfficeReviews_AReturnNeedsAComment_AndAnInspectionNotSubmittedCannotBeReviewed()
    {
        var id = await SubmitAsync("1P");
        var onboard = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, id, ReviewDecision.Accepted, "", _s.User));
        Assert.Contains("office", onboard.Message);
        var silent = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, id, ReviewDecision.Returned, "  ", _s.User));
        Assert.Contains("what needs correcting", silent.Message);

        await _shore.ReviewAsync(_s.CompanyId, id, ReviewDecision.Started, "", _s.User);
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, id, ReviewDecision.Started, "", _s.User));
        Assert.Contains("already been started", twice.Message);
        await _shore.ReviewAsync(_s.CompanyId, id, ReviewDecision.Accepted, "", _s.User);
        var done = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, id, ReviewDecision.Returned, "Too late", _s.User));
        Assert.Contains("already been reviewed", done.Message);
    }

    [Fact]
    public async Task TheReviewKeywordsAreSetByTheOfficeAlone_SavedCleaned_AndReachTheVesselReadOnly()
    {
        var standard = await _shoreTanks.GetKeywordsAsync(_s.CompanyId);
        Assert.Equal("", standard.Typed);
        Assert.Equal(TankScales.ReviewKeywords, standard.Effective);
        await _shoreTanks.SaveKeywordsAsync(_s.CompanyId, "sludge; Scale ,sludge\nx");
        var saved = await _shoreTanks.GetKeywordsAsync(_s.CompanyId);
        Assert.Equal("sludge, Scale", saved.Typed);
        Assert.Equal(["sludge", "Scale"], saved.Effective);

        await _shoreTanks.SaveKeywordsAsync(_s.CompanyId, "");                                          // empty goes back to the standard list
        Assert.Equal(TankScales.ReviewKeywords, (await _shoreTanks.GetKeywordsAsync(_s.CompanyId)).Effective);

        await _shoreTanks.SaveKeywordsAsync(_s.CompanyId, "sludge");
        await _s.SettleUntilQuietAsync();
        Assert.Equal("sludge", (await _vesselTanks.GetKeywordsAsync(_s.CompanyId)).Typed);              // it arrives with the company record, read-only
        var onboard = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselTanks.SaveKeywordsAsync(_s.CompanyId, "rust"));
        Assert.Contains("office", onboard.Message);
    }
}
