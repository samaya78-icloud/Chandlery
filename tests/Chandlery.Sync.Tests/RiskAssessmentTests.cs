using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class RiskRulesTests
{
    [Fact]
    public void AFactorFallsInTheHighestBandItReaches_AndZeroHasNone()
    {
        var m = RiskMatrix.Default;
        Assert.Equal(["", "Low", "Low", "Medium", "Medium", "Medium High", "High", "Extreme", "Extreme"], new[] { 0, 1, 4, 5, 9, 10, 15, 20, 25 }.Select(m.BandOf));
        Assert.Equal(20, RiskMatrix.Factor(4, 5));
        Assert.True(m.IsSerious("High") && m.IsSerious("Extreme"));
        Assert.False(m.IsSerious("Medium High") || m.IsSerious("Low"));
        Assert.True(RiskMatrix.IsLevel(5) && !RiskMatrix.IsLevel(0) && !RiskMatrix.IsLevel(6));
    }

    [Fact]
    public void ACompanyCanHaveItsOwnBands()
    {
        var own = new RiskMatrix([("Acceptable", 1), ("Tolerable", 8), ("Intolerable", 16)]);
        Assert.Equal(["Acceptable", "Tolerable", "Intolerable"], new[] { 7, 8, 16 }.Select(own.BandOf));
        Assert.True(own.IsSerious("Tolerable"));                                                      // the top two bands of whatever the company defined
    }

    private static RaSignOff Sign(int forSubmission, RaDecision decision, int minutes) => new() { Id = Guid.CreateVersion7(), ForSubmission = forSubmission, Decision = decision, At = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(minutes) };

    [Fact]
    public void TheStatusIsWorkedOutFromTheSubmissionsAndTheLatestSignOffOfTheCurrentOne()
    {
        Assert.Equal(RaStatus.Draft, RaStatusRules.Of(0, []));
        Assert.Equal(RaStatus.Submitted, RaStatusRules.Of(1, []));
        Assert.Equal(RaStatus.InReview, RaStatusRules.Of(1, [Sign(1, RaDecision.Started, 0)]));
        Assert.Equal(RaStatus.Returned, RaStatusRules.Of(1, [Sign(1, RaDecision.Started, 0), Sign(1, RaDecision.Returned, 5)]));
        Assert.Equal(RaStatus.Submitted, RaStatusRules.Of(2, [Sign(1, RaDecision.Returned, 5)]));       // a decision on an earlier submission no longer counts
        Assert.Equal(RaStatus.Signed, RaStatusRules.Of(2, [Sign(1, RaDecision.Returned, 5), Sign(2, RaDecision.Signed, 9)]));
        Assert.True(RaStatusRules.IsEditable(RaStatus.Draft) && RaStatusRules.IsEditable(RaStatus.Returned));
        Assert.False(RaStatusRules.IsEditable(RaStatus.Submitted) || RaStatusRules.IsEditable(RaStatus.InReview) || RaStatusRules.IsEditable(RaStatus.Signed));
    }

    [Fact]
    public void TheNextReviewIsThePeriodAfterTheSigningDay_AndTheVersionInForceIsTheHighestSigned()
    {
        var signed = new[] { Sign(1, RaDecision.Signed, 0) };
        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), RaStatusRules.SignedAt(1, signed));
        Assert.Null(RaStatusRules.SignedAt(1, [Sign(1, RaDecision.Returned, 0)]));
        Assert.Equal(new DateTime(2027, 2, 1), RaStatusRules.NextReview(new DateTime(2026, 3, 1, 10, 0, 0), 11));
        Assert.Equal(new DateTime(2026, 2, 28), RaStatusRules.NextReview(new DateTime(2026, 1, 31), 1));    // the end of a short month, not an overflow into March

        (int, bool)[] chain = [(1, true), (2, true), (3, false)];
        Assert.Equal(2, RaStatusRules.InForce(chain));
        Assert.True(RaStatusRules.IsSuperseded(1, chain));
        Assert.False(RaStatusRules.IsSuperseded(2, chain));                                              // version 3 is only a draft; version 2 stays in force
        Assert.False(RaStatusRules.IsSuperseded(3, chain));
        Assert.Null(RaStatusRules.InForce([(1, false)]));
    }

    private static RaHazard Hazard(Guid activity, int cb, int pb, int ca, int pa, string controls = "Permit to work", string owner = "Chief Officer", DateTime? due = null, string reason = "")
        => new() { Id = Guid.CreateVersion7(), ActivityId = activity, Hazard = "Fire", ConsequenceBefore = cb, ProbabilityBefore = pb, ConsequenceAfter = ca, ProbabilityAfter = pa,
            ExistingControls = controls, ResponsibleName = owner, DueDate = due, ResidualReason = reason };

    private static RiskAssessment Assessment(params Func<Guid, RaHazard>[] hazards)
    {
        var a = new RiskAssessment { JobDescription = "Hot work in the engine room", CategoryId = Guid.NewGuid() };
        a.Team.Add(new RaTeamMember { Name = "Chief Engineer", Rank = "C/E" });
        var activity = new RaActivity { Id = Guid.CreateVersion7(), Title = "Welding", SortOrder = 0 };
        a.Activities.Add(activity);
        foreach (var h in hazards) a.Hazards.Add(h(activity.Id));
        return a;
    }

    [Fact]
    public void ScoresAreKeptWithTheHazard_AndAnAssessmentNeedsItsPartsBeforeItCanBeSubmitted()
    {
        var h = Hazard(Guid.NewGuid(), 4, 4, 2, 2);
        RaChecks.Score(h, RiskMatrix.Default);
        Assert.Equal((16, "High", 4, "Low"), (h.FactorBefore, h.BandBefore, h.FactorAfter, h.BandAfter));

        Assert.Empty(RaChecks.SubmissionProblems(Assessment(a => Hazard(a, 4, 4, 2, 2)), RiskMatrix.Default));

        var empty = new RiskAssessment();
        Assert.Equal(4, RaChecks.SubmissionProblems(empty, RiskMatrix.Default).Count);                   // description, category, team, activity
        var noHazards = Assessment();
        Assert.Contains(RaChecks.SubmissionProblems(noHazards, RiskMatrix.Default), p => p.Contains("add at least one hazard"));
        Assert.Contains(RaChecks.SubmissionProblems(Assessment(a => Hazard(a, 4, 4, 0, 2)), RiskMatrix.Default), p => p.Contains("score the risk"));
    }

    [Fact]
    public void RiskThatIsNotLowerAfterControlsNeedsAReason_AndAHighOrExtremeResidualNeedsAControlAnOwnerAndADate()
    {
        var m = RiskMatrix.Default;
        Assert.Contains(RaChecks.SubmissionProblems(Assessment(a => Hazard(a, 3, 3, 3, 3)), m), p => p.Contains("not lower than before"));
        Assert.Empty(RaChecks.SubmissionProblems(Assessment(a => Hazard(a, 2, 2, 2, 2, reason: "No further control is practicable")), m));

        var serious = RaChecks.SubmissionProblems(Assessment(a => Hazard(a, 5, 5, 4, 4, controls: "", owner: "", due: null)), m);        // 16 after: High
        Assert.Equal(3, serious.Count);
        Assert.Empty(RaChecks.SubmissionProblems(Assessment(a => Hazard(a, 5, 5, 4, 4, due: new DateTime(2026, 12, 1))), m));
        Assert.Empty(RaChecks.SubmissionProblems(Assessment(a => Hazard(a, 5, 5, 3, 3, controls: "", owner: "")), m));                   // 9 after: Medium, no extra demands

        var top = RaChecks.HighestResidual([new RaHazard { FactorAfter = 6, BandAfter = "Medium" }, new RaHazard { FactorAfter = 16, BandAfter = "High" }]);
        Assert.Equal((16, "High"), top);
        Assert.Equal((0, ""), RaChecks.HighestResidual([]));
    }
}

/// <summary>Risk assessments on a real office and a real vessel: the library, writing, sign-off, locking and versions.</summary>
public class RiskSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RiskAssessmentService _shore = null!, _vessel = null!;
    private Actor _reviewer = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new RiskAssessmentService(_s.ShoreFactory);
        _vessel = new RiskAssessmentService(_s.VesselFactory);
        _reviewer = await RiskTestSupport.MakeReviewersAsync(_s);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync(params Guid[] vessels)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var vessel in vessels.Length == 0 ? [_s.VesselId] : vessels)
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.RiskAssessment, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<Guid> CategoryAsync(string code) => (await OnShore(db => db.RaCategories.SingleAsync(c => c.Code == code))).Id;

    private async Task<RaTemplate> AddTemplateAsync()
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var template = new RaTemplate { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Name = "Hot work", CategoryId = await CategoryAsync("HOT"), JobDescription = "Hot work outside the workshop", Contingency = "Stop work, raise the alarm" };
        var activity = new RaTemplateActivity { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, TemplateId = template.Id, Title = "Welding on deck", SortOrder = 0 };
        db.RaTemplates.Add(template);
        db.RaTemplateActivities.Add(activity);
        db.RaTemplateHazards.Add(new RaTemplateHazard { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, TemplateId = template.Id, ActivityId = activity.Id, Hazard = "Fire", Effect = "Burns, damage",
            ExistingControls = "Fire watch", ConsequenceBefore = 4, ProbabilityBefore = 4, ConsequenceAfter = 2, ProbabilityAfter = 2, SortOrder = 0 });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
        return template;
    }

    private static AssessmentEdit Edit(RiskAssessment a, Guid category, string description = "Hot work on the main deck", int afterC = 2)
        => new(description, category, Today, "Stop work", [new RaTeamInput("Chief Officer", "C/O"), new RaTeamInput("Bosun", "Bosun")],
            a.Activities.OrderBy(x => x.SortOrder).Select(act => new RaActivityInput(act.Id, act.Title,
                a.Hazards.Where(h => h.ActivityId == act.Id).OrderBy(h => h.SortOrder).Select(h => new RaHazardInput(h.Id, h.Hazard, h.Effect, h.ConsequenceBefore, h.ProbabilityBefore,
                    h.ExistingControls, h.AdditionalControls, afterC, afterC)).ToList())).ToList());

    /// <summary>Starts from the template onboard, fills in the rest and submits.</summary>
    private async Task<Guid> SubmittedOnboardAsync(RaTemplate template)
    {
        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "", Today.AddDays(-1), TemplateId: template.Id), _s.User);
        var detail = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        await _vessel.SaveAsync(_s.CompanyId, started.Id, Edit(detail.Assessment, template.CategoryId!.Value), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        return started.Id;
    }

    [Fact]
    public async Task TheModuleIsOffUntilSwitchedOn_AndTheLibraryReachesTheVesselReadOnly()
    {
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Test", Today), _s.User));
        Assert.Contains("not switched on", off.Message);
        await SwitchOnAsync();

        Assert.Equal(8, await OnVessel(db => db.RaCategories.CountAsync()));                          // the standard categories, created with the company
        Assert.Equal(10, await OnVessel(db => db.RaMatrixLabels.CountAsync()));
        Assert.Equal(["Low", "Medium", "Medium High", "High", "Extreme"], await OnVessel(db => db.RaMatrixBands.OrderBy(b => b.MinFactor).Select(b => b.Name).ToListAsync()));

        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.RaCategories.Add(new RaCategory { CompanyId = _s.CompanyId, Name = "Made onboard", Code = "XXX" });
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("master data", refused.Message);
        }

        var template = await AddTemplateAsync();
        Assert.Equal(1, await OnVessel(db => db.RaTemplateHazards.CountAsync(h => h.TemplateId == template.Id)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AnAssessmentIsWrittenOnboardFromATemplate_SubmittedReturnedCorrectedAndSigned_AndIsThenLocked()
    {
        await SwitchOnAsync();
        var template = await AddTemplateAsync();

        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "", Today.AddDays(-1), TemplateId: template.Id), _s.User);
        Assert.Equal($"MVSF-RA-{DateTime.UtcNow.Year}-0001", started.Code);
        var draft = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        Assert.Equal((RaStatus.Draft, "Hot work outside the workshop", "Hot work", 1, 1), (draft.Status, draft.Assessment.JobDescription, draft.Assessment.CategoryName, draft.Assessment.Activities.Count, draft.Assessment.Hazards.Count));
        Assert.Equal((16, "High", 4, "Low"), (draft.Assessment.Hazards[0].FactorBefore, draft.Assessment.Hazards[0].BandBefore, draft.Assessment.Hazards[0].FactorAfter, draft.Assessment.Hazards[0].BandAfter));

        // No team yet: it cannot be submitted.
        var incomplete = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User));
        Assert.Contains("team", incomplete.Message);

        await _vessel.SaveAsync(_s.CompanyId, started.Id, Edit(draft.Assessment, template.CategoryId!.Value), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        var atOffice = (await _shore.GetAsync(_s.CompanyId, started.Id))!;
        Assert.Equal((RaStatus.Submitted, 2, 1, 1), (atOffice.Status, atOffice.Assessment.Team.Count, atOffice.Assessment.Activities.Count, atOffice.Assessment.Hazards.Count));
        Assert.Equal((4, "Low"), (atOffice.Assessment.HighestResidualFactor, atOffice.Assessment.HighestResidualBand));

        // Submitted means locked for the vessel; the office cannot edit what the vessel wrote.
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, started.Id, Edit(atOffice.Assessment, template.CategoryId!.Value, "Changed"), _s.User));
        Assert.Contains("submitted", locked.Message);
        var notYours = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveAsync(_s.CompanyId, started.Id, Edit(atOffice.Assessment, template.CategoryId!.Value, "Changed"), _s.User));
        Assert.Contains("submitted", notYours.Message);                                                      // not editable by anyone while it waits for sign-off

        // The office starts the review, then returns it; it comes back to the vessel for correction.
        await _shore.SignOffAsync(_s.CompanyId, started.Id, RaDecision.Started, "", _s.User);
        var silent = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SignOffAsync(_s.CompanyId, started.Id, RaDecision.Returned, " ", _s.User));
        Assert.Contains("what needs correcting", silent.Message);
        await _shore.SignOffAsync(_s.CompanyId, started.Id, RaDecision.Returned, "Name the fire watch", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(RaStatus.Returned, (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Status);

        var corrected = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        await _vessel.SaveAsync(_s.CompanyId, started.Id, Edit(corrected.Assessment, template.CategoryId!.Value, "Hot work on the main deck, fire watch posted"), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        var q = await _s.ShoreSyncStore.GetQuarantineAsync(); var qv = await _s.VesselSyncStore.GetQuarantineAsync(); Assert.True(q.Count == 0 && qv.Count == 0, string.Join(" / ", q.Concat(qv).Select(x => x.ToString())));
        Assert.Equal(RaStatus.Submitted, (await _shore.GetAsync(_s.CompanyId, started.Id))!.Status);                       // the earlier return does not count against the new submission

        await _shore.SignOffAsync(_s.CompanyId, started.Id, RaDecision.Signed, "Approved", _s.User, "Superintendent");
        await _s.SettleUntilQuietAsync();
        foreach (var side in new[] { _shore, _vessel })
        {
            var signed = (await side.GetAsync(_s.CompanyId, started.Id))!;
            Assert.Equal((RaStatus.Signed, false, "Hot work on the main deck, fire watch posted"), (signed.Status, signed.Superseded, signed.Assessment.JobDescription));
            Assert.Equal(signed.SignedAt!.Value.Date.AddMonths(11), signed.NextReview);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => side.SaveAsync(_s.CompanyId, started.Id, Edit(signed.Assessment, template.CategoryId!.Value, "Changed after signing"), _s.User));
            Assert.Contains("locked", refused.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => side.SignOffAsync(_s.CompanyId, started.Id, RaDecision.Returned, "Too late", _s.User));
        }
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ASignedAssessmentGetsNewVersions_TheOldOneIsSupersededOnlyWhenTheNewOneIsSigned_AndBothSidesAgree()
    {
        await SwitchOnAsync();
        var template = await AddTemplateAsync();
        var v1 = await SubmittedOnboardAsync(template);
        var notSigned = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.NewVersionAsync(_s.CompanyId, v1, RaKind.Interim, _s.User));
        Assert.Contains("signed", notSigned.Message);
        await _shore.SignOffAsync(_s.CompanyId, v1, RaDecision.Signed, "", _s.User);
        await _s.SettleUntilQuietAsync();

        var draft = await _vessel.NewVersionAsync(_s.CompanyId, v1, RaKind.Interim, _s.User);
        var first = (await _vessel.GetAsync(_s.CompanyId, v1))!.Assessment;
        Assert.Equal((first.Code, 2, v1, RaKind.Interim), (draft.Code, draft.Version, draft.PreviousVersionId, draft.Kind));
        Assert.Equal((2, 1, 1), (draft.Team.Count, draft.Activities.Count, draft.Hazards.Count));
        Assert.All(draft.Hazards, h => Assert.Contains(h.ActivityId, draft.Activities.Select(a => a.Id)));            // the copy points at its own activities, not the old ones
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.NewVersionAsync(_s.CompanyId, v1, RaKind.Annual, _s.User));        // one being written already
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.NewVersionAsync(_s.CompanyId, v1, RaKind.Initial, _s.User));

        Assert.False((await _vessel.GetAsync(_s.CompanyId, v1))!.Superseded);                                         // a draft successor does not replace the signed one

        await _vessel.SubmitAsync(_s.CompanyId, draft.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        await _shore.SignOffAsync(_s.CompanyId, draft.Id, RaDecision.Signed, "", _s.User);
        await _s.SettleUntilQuietAsync();

        foreach (var side in new[] { _shore, _vessel })
        {
            Assert.True((await side.GetAsync(_s.CompanyId, v1))!.Superseded);
            Assert.False((await side.GetAsync(_s.CompanyId, draft.Id))!.Superseded);
            Assert.Equal([(1, RaStatus.Signed), (2, RaStatus.Signed)], (await side.VersionsAsync(_s.CompanyId, first.Code)).Select(v => (v.Assessment.Version, v.Status)));
            Assert.Equal(2, (await side.ListAsync(_s.CompanyId)).Count);
        }
        var old = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.NewVersionAsync(_s.CompanyId, v1, RaKind.Revision, _s.User));
        Assert.Contains("newer version", old.Message);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AnAssessmentWrittenAshoreReachesTheVessel_WhichSignsItOnboard_AndAnotherVesselNeverSeesIt()
    {
        await SwitchOnAsync(_s.VesselId, _s.OtherVesselId);
        var template = await AddTemplateAsync();
        var started = await _shore.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "", Today, TemplateId: template.Id), _s.User);
        Assert.Equal($"MVSF-RA-{DateTime.UtcNow.Year}-H0001", started.Code);                                          // ashore: the H lane
        var draft = (await _shore.GetAsync(_s.CompanyId, started.Id))!;
        await _shore.SaveAsync(_s.CompanyId, started.Id, Edit(draft.Assessment, template.CategoryId!.Value), _s.User);
        await _shore.SubmitAsync(_s.CompanyId, started.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        var onboard = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        Assert.Equal((RaStatus.Submitted, 1), (onboard.Status, onboard.Assessment.Hazards.Count));
        Assert.False(await OnVessel(db => db.RiskAssessments.AnyAsync(a => a.VesselId == _s.OtherVesselId)));
        var notYours = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, started.Id, Edit(onboard.Assessment, template.CategoryId!.Value, "Changed onboard"), _s.User));
        Assert.Contains("submitted", notYours.Message);

        await _vessel.SignOffAsync(_s.CompanyId, started.Id, RaDecision.Signed, "Signed by the Master", _reviewer, "Master");     // the Master signs onboard
        await _s.SettleUntilQuietAsync();
        var atOffice = (await _shore.GetAsync(_s.CompanyId, started.Id))!;
        Assert.Equal((RaStatus.Signed, "Master"), (atOffice.Status, atOffice.SignOffs.Single(s => s.Decision == RaDecision.Signed).ReviewerRank));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task AnAssessmentHasLimits_AndScoresMustBeOnTheScale()
    {
        await SwitchOnAsync();
        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Cargo hold entry", Today, CategoryId: await CategoryAsync("OPS")), _s.User);
        var tooMany = Enumerable.Range(0, RiskAssessmentService.MaxActivities + 1).Select(i => new RaActivityInput(null, $"Activity {i}", [])).ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("x", null, Today, "", [], tooMany), _s.User));
        var badScore = new RaActivityInput(null, "Entry", [new RaHazardInput(null, "Gas", "", 6, 1, "", "", 1, 1)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("x", null, Today, "", [], [badScore]), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "x", Today.AddDays(2)), _s.User));   // not in the future
    }
}
