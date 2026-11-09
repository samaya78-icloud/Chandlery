using Chandlery.Components.Shared;
using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

public class RiskReviewRulesTests
{
    private static readonly DateTime Today = new(2026, 11, 9);

    [Fact]
    public void AReviewIsOverdueOncePast_DueSoonWithinThirtyDays_AndOtherwiseOnSchedule()
    {
        Assert.Equal(RaReviewState.None, RaReview.StateOf(null, Today));
        Assert.Equal(RaReviewState.Overdue, RaReview.StateOf(Today.AddDays(-1), Today));
        Assert.Equal(RaReviewState.DueSoon, RaReview.StateOf(Today, Today));                         // the day itself is not yet late
        Assert.Equal(RaReviewState.DueSoon, RaReview.StateOf(Today.AddDays(RaReview.SoonDays), Today));
        Assert.Equal(RaReviewState.Ok, RaReview.StateOf(Today.AddDays(RaReview.SoonDays + 1), Today));
        Assert.Equal(["Review overdue", "Review due soon", "Review on schedule", ""], new[] { RaReviewState.Overdue, RaReviewState.DueSoon, RaReviewState.Ok, RaReviewState.None }.Select(RaReview.Label));
    }

    [Fact]
    public void EveryStatusHasItsOwnNameAndTone_AndATaskLinkOpensTheAssessment()
    {
        Assert.Equal([("Draft", "neutral"), ("Submitted", "info"), ("In review", "warning"), ("Returned", "danger"), ("Signed", "success")],
            Enum.GetValues<RaStatus>().Select(s => StatusChip.Describe(s)).ToArray());
        Assert.Equal(("Review overdue", "danger"), StatusChip.Describe(RaReviewState.Overdue));
        var id = Guid.NewGuid();
        Assert.Equal($"/risk/{id}", WorklistLinkTypes.Href(WorklistLinkTypes.RiskAssessment, id, onVessel: true));
    }
}

/// <summary>What the assessment screens read: the full list of what is left before submitting, and the list with review dates.</summary>
public class RiskEditorTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RiskAssessmentService _shore = null!, _vessel = null!;
    private RiskLibraryService _library = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new RiskAssessmentService(_s.ShoreFactory);
        _vessel = new RiskAssessmentService(_s.VesselFactory);
        _library = new RiskLibraryService(_s.ShoreFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RiskAssessment, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    [Fact]
    public async Task TheCheckListsEverythingLeftToPutRight_AtOnce_AndIsEmptyWhenReady()
    {
        var category = (await _library.ListCategoriesAsync(_s.CompanyId)).First();
        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Tank entry", Today), _s.User);

        var blank = await _vessel.CheckAsync(_s.CompanyId, started.Id);
        Assert.Equal(3, blank.Count);                                                                  // no category, no team, no activity
        Assert.Contains(blank, p => p.Contains("category"));

        // A serious residual risk with nothing behind it, and a risk that is not lower than before with no reason.
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Tank entry", category.Id, Today, "", [new RaTeamInput("Chief Officer", "C/O")],
        [
            new RaActivityInput(null, "Entering", [new RaHazardInput(null, "No oxygen", "", 5, 5, "", "", 5, 4), new RaHazardInput(null, "Slips", "", 3, 3, "Mats", "", 3, 3)]),
            new RaActivityInput(null, "Empty one", []),
        ]), _s.User);
        var problems = await _vessel.CheckAsync(_s.CompanyId, started.Id);
        Assert.Equal(5, problems.Count);                                                               // control, owner and date for the first; a reason for the second; the empty activity
        Assert.Contains(problems, p => p.Contains("Empty one") && p.Contains("at least one hazard"));
        Assert.Contains(problems, p => p.Contains("Slips") && p.Contains("not lower than before"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User));
        Assert.Contains("5 thing(s)", refused.Message);

        await _vessel.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Tank entry", category.Id, Today, "", [new RaTeamInput("Chief Officer", "C/O")],
            [new RaActivityInput(null, "Entering", [new RaHazardInput(null, "Slips", "", 3, 3, "Mats", "", 2, 2)])]), _s.User);
        Assert.Empty(await _vessel.CheckAsync(_s.CompanyId, started.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.CheckAsync(_s.CompanyId, Guid.NewGuid()));
    }

    [Fact]
    public async Task TheListShowsTheNextReviewOfASignedAssessment_AndOnlyTheOneInForceIsLive()
    {
        var category = (await _library.ListCategoriesAsync(_s.CompanyId)).First();
        var first = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Mooring", Today), _s.User);
        await _vessel.SaveAsync(_s.CompanyId, first.Id, new AssessmentEdit("Mooring", category.Id, Today, "", [new RaTeamInput("Bosun")],
            [new RaActivityInput(null, "Heaving", [new RaHazardInput(null, "Snap-back", "", 4, 3, "Zone marked", "", 2, 2)])]), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, first.Id, _s.User);
        await _vessel.SignOffAsync(_s.CompanyId, first.Id, RaDecision.Signed, "", _s.User, "Master");

        var row = Assert.Single(await _vessel.ListAsync(_s.CompanyId));
        Assert.Equal((RaStatus.Signed, false, "Low", 4), (row.Status, row.Superseded, row.Assessment.HighestResidualBand, row.Assessment.HighestResidualFactor));
        Assert.Equal(Today.AddMonths(11), row.NextReview!.Value.Date);
        Assert.Equal(RaReviewState.Ok, RaReview.StateOf(row.NextReview, Today));
        Assert.Equal(RaReviewState.Overdue, RaReview.StateOf(row.NextReview, Today.AddMonths(12)));     // a year on, it would be overdue

        var second = await _vessel.NewVersionAsync(_s.CompanyId, first.Id, RaKind.Annual, _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, second.Id, _s.User);
        await _vessel.SignOffAsync(_s.CompanyId, second.Id, RaDecision.Signed, "", _s.User, "Master");
        var rows = (await _vessel.ListAsync(_s.CompanyId)).OrderBy(r => r.Assessment.Version).ToList();
        Assert.Equal([true, false], rows.Select(r => r.Superseded));                                    // the page hides replaced versions unless asked
        Assert.Single(await _vessel.ListAsync(_s.CompanyId, _s.VesselId), r => !r.Superseded);
        Assert.Empty(await _vessel.ListAsync(_s.CompanyId, _s.OtherVesselId));
    }
}
