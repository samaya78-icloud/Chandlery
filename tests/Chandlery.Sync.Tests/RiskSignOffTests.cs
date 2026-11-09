using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Who may sign, the queue of what waits, and sharing a signed assessment, on a real office and a real vessel.</summary>
public class RiskSignOffTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RiskAssessmentService _shore = null!, _vessel = null!;
    private RiskLibraryService _library = null!;
    private Actor _crewReviewer = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new RiskAssessmentService(_s.ShoreFactory);
        _vessel = new RiskAssessmentService(_s.VesselFactory);
        _library = new RiskLibraryService(_s.ShoreFactory);
        _crewReviewer = await RiskTestSupport.MakeReviewersAsync(_s);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

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

    /// <summary>An assessment that passes the checks, written by the given side and submitted.</summary>
    private async Task<Guid> SubmittedAsync(RiskAssessmentService side, Guid vesselId, Actor writer, int residual = 2)
    {
        var category = (await _library.ListCategoriesAsync(_s.CompanyId)).First();
        var started = await side.StartAsync(_s.CompanyId, new NewAssessment(vesselId, "Crane lift", Today, category.Id), writer);
        await side.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Crane lift", category.Id, Today, "", [new RaTeamInput("Bosun")],
            [new RaActivityInput(null, "Lifting", [new RaHazardInput(null, "Dropped load", "", 5, 5, "Tag lines", "", residual, residual, "", "Chief Officer", Today.AddDays(30))])]), writer);
        await side.SubmitAsync(_s.CompanyId, started.Id, writer);
        await _s.SettleUntilQuietAsync();
        return started.Id;
    }

    [Fact]
    public async Task OnlyAReviewerMaySignOff_OnboardACrewMemberTheOfficeFlagged_AshoreAnAdminOrAFlaggedUser()
    {
        await SwitchOnAsync();
        var id = await SubmittedAsync(_vessel, _s.VesselId, _crewReviewer);

        // A stranger ashore, and crew who are not reviewers onboard, are refused; so is a person the system does not know.
        var stranger = new Actor(Guid.NewGuid(), "Stranger");
        Assert.Contains("risk reviewer", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "", stranger))).Message);
        var plain = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, "RAPLAIN", "Able Seaman", "AB", "", null, "", null, null, "", "", _s.VesselId, null, true, false), _s.User);
        await _s.SettleUntilQuietAsync();
        var plainActor = new Actor(plain.UserId, plain.FullName);
        Assert.Contains("risk reviewer of this vessel", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "", plainActor))).Message);
        Assert.Contains("risk reviewer", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "", _s.User))).Message);   // an office user means nothing onboard
        Assert.Contains("risk reviewer", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignOffAsync(_s.CompanyId, id, RaDecision.Started, "", new Actor(null, "Nobody")))).Message);

        Assert.False((await _vessel.MaySignOffAsync(_s.CompanyId, id, plainActor)).May);
        Assert.True((await _vessel.MaySignOffAsync(_s.CompanyId, id, _crewReviewer)).May);
        Assert.True((await _shore.MaySignOffAsync(_s.CompanyId, id, _s.User)).May);                       // the flagged office user

        // The flag is the office's to give: a user is named and un-named, and the change takes effect at once.
        await _library.SetRiskReviewerAsync(_s.CompanyId, _s.User.UserId!.Value, false);
        UserRole role;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync()) role = (await db.Users.AsNoTracking().FirstAsync(u => u.Id == _s.User.UserId)).Role;
        Assert.Equal(role == UserRole.CompanyAdmin, (await _shore.MaySignOffAsync(_s.CompanyId, id, _s.User)).May);   // an admin is always one
        await _library.SetRiskReviewerAsync(_s.CompanyId, _s.User.UserId!.Value, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.SetRiskReviewerAsync(_s.CompanyId, Guid.NewGuid(), true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RiskLibraryService(_s.VesselFactory).SetRiskReviewerAsync(_s.CompanyId, _s.User.UserId!.Value, true));

        await _vessel.SignOffAsync(_s.CompanyId, id, RaDecision.Signed, "", _crewReviewer, "Master");
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task WhenTheCompanyAsksForAnotherPerson_TheWriterCannotSignTheirOwnWork_UnlessTheyAreTheOnlyReviewer()
    {
        await SwitchOnAsync();
        await _library.SetSignOffByAnotherPersonAsync(_s.CompanyId, true);
        await _s.SettleUntilQuietAsync();
        Assert.True(await _library.GetSignOffByAnotherPersonAsync(_s.CompanyId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RiskLibraryService(_s.VesselFactory).SetSignOffByAnotherPersonAsync(_s.CompanyId, false));   // the office's setting

        // Onboard the writer is the only reviewer, so the rule does not stop them.
        var alone = await SubmittedAsync(_vessel, _s.VesselId, _crewReviewer);
        Assert.True((await _vessel.MaySignOffAsync(_s.CompanyId, alone, _crewReviewer)).May);
        await _vessel.SignOffAsync(_s.CompanyId, alone, RaDecision.Signed, "", _crewReviewer, "Master");

        // With a second reviewer onboard, the writer may start a review or return it, but not sign it.
        var second = await RiskTestSupport.MakeReviewersAsync(_s, "RAREV2", "Chief Engineer Two");
        var written = await SubmittedAsync(_vessel, _s.VesselId, _crewReviewer);
        var right = await _vessel.MaySignOffAsync(_s.CompanyId, written, _crewReviewer);
        Assert.False(right.May);
        Assert.Contains("someone other than the writer", right.Why);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignOffAsync(_s.CompanyId, written, RaDecision.Signed, "", _crewReviewer));
        await _vessel.SignOffAsync(_s.CompanyId, written, RaDecision.Started, "", _crewReviewer);
        await _vessel.SignOffAsync(_s.CompanyId, written, RaDecision.Signed, "", second, "Chief Engineer");

        // Off again: the writer may sign.
        await _library.SetSignOffByAnotherPersonAsync(_s.CompanyId, false);
        await _s.SettleUntilQuietAsync();
        var again = await SubmittedAsync(_vessel, _s.VesselId, _crewReviewer);
        Assert.True((await _vessel.MaySignOffAsync(_s.CompanyId, again, _crewReviewer)).May);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TheQueueHoldsWhatWaits_TheHighestRiskFirst_AndEmptiesAsItIsSigned()
    {
        await SwitchOnAsync(_s.VesselId, _s.OtherVesselId);
        var low = await SubmittedAsync(_shore, _s.VesselId, _s.User, residual: 2);
        await Task.Delay(30);
        var high = await SubmittedAsync(_shore, _s.OtherVesselId, _s.User, residual: 4);               // 16 left: High
        var draft = (await _shore.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Not yet", Today), _s.User)).Id;

        var queue = await _shore.SignOffQueueAsync(_s.CompanyId);
        Assert.Equal([high, low], queue.Select(r => r.Assessment.Id));                                  // the draft is not waiting
        Assert.Equal(["High", "Low"], queue.Select(r => r.Assessment.HighestResidualBand));
        Assert.All(queue, r => Assert.NotEqual("", r.VesselName));
        Assert.Equal([low], (await _shore.SignOffQueueAsync(_s.CompanyId, _s.VesselId)).Select(r => r.Assessment.Id));
        Assert.Equal([low], (await _vessel.SignOffQueueAsync(_s.CompanyId)).Select(r => r.Assessment.Id));   // the vessel sees its own

        await _shore.SignOffAsync(_s.CompanyId, high, RaDecision.Started, "", _s.User);
        Assert.Equal(2, (await _shore.SignOffQueueAsync(_s.CompanyId)).Count);                         // a started review stays in the queue
        await _shore.SignOffAsync(_s.CompanyId, high, RaDecision.Signed, "", _s.User);
        await _shore.SignOffAsync(_s.CompanyId, low, RaDecision.Returned, "Say who holds the tag line", _s.User);
        Assert.Empty(await _shore.SignOffQueueAsync(_s.CompanyId));                                      // signed and returned both leave it
        Assert.NotEqual(Guid.Empty, draft);
    }

    [Fact]
    public async Task ASignedAssessmentIsSharedWithOtherVessels_AsItsOwnSignedCopy_ThatTheVesselCanGiveNewVersions()
    {
        await SwitchOnAsync(_s.VesselId, _s.OtherVesselId);
        var source = await SubmittedAsync(_shore, _s.OtherVesselId, _s.User);                         // written ashore for the other vessel

        // Only a signed assessment, in force, can be shared; only the office shares; the vessel must have the module on.
        Assert.Contains("signed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ShareAsync(_s.CompanyId, source, [_s.VesselId], _s.User))).Message);
        await _shore.SignOffAsync(_s.CompanyId, source, RaDecision.Signed, "", _s.User, "Superintendent");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ShareAsync(_s.CompanyId, source, [], _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ShareAsync(_s.CompanyId, source, [_s.OtherVesselId], _s.User));          // it is its own vessel's already
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ShareAsync(_s.CompanyId, source, [Guid.NewGuid()], _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ShareAsync(_s.CompanyId, source, [_s.VesselId], _crewReviewer));

        var targets = await _shore.ShareTargetsAsync(_s.CompanyId, source);
        Assert.Equal([_s.VesselId], targets.Select(t => t.VesselId));
        Assert.False(targets[0].AlreadyHasCopy);

        var copies = await _shore.ShareAsync(_s.CompanyId, source, [_s.VesselId], _s.User);
        var copy = Assert.Single(copies);
        await _s.SettleUntilQuietAsync();
        Assert.True((await _shore.ShareTargetsAsync(_s.CompanyId, source)).Single().AlreadyHasCopy);
        Assert.Empty(await _shore.ShareAsync(_s.CompanyId, source, [_s.VesselId], _s.User));            // a second time changes nothing

        // The vessel receives a signed assessment of its own, with its own reference and the original signing.
        var original = (await _shore.GetAsync(_s.CompanyId, source))!;
        var atVessel = (await _vessel.GetAsync(_s.CompanyId, copy.Id))!;
        Assert.Equal((RaStatus.Signed, 1, original.Assessment.Code, 1), (atVessel.Status, atVessel.Assessment.Version, atVessel.Assessment.SharedFromCode, atVessel.Assessment.SharedFromVersion));
        Assert.NotEqual(original.Assessment.Code, atVessel.Assessment.Code);
        Assert.StartsWith("MVSF-RA-", atVessel.Assessment.Code);
        Assert.Equal((1, 1, 1), (atVessel.Assessment.Activities.Count, atVessel.Assessment.Hazards.Count, atVessel.Assessment.Team.Count));
        Assert.Equal(original.SignedAt, atVessel.SignedAt);                                              // the review date counts from the original signing
        Assert.Equal(original.NextReview, atVessel.NextReview);
        Assert.Equal("Superintendent", atVessel.SignOffs.Single().ReviewerRank);

        // It is locked like any signed assessment, and the vessel can repeat it as its own interim version.
        Assert.Contains("locked", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, copy.Id,
            new AssessmentEdit("Changed", copy.CategoryId, Today, "", [], []), _crewReviewer))).Message);
        var interim = await _vessel.NewVersionAsync(_s.CompanyId, copy.Id, RaKind.Interim, _crewReviewer);
        Assert.Equal((2, RaKind.Interim, copy.Code), (interim.Version, interim.Kind, interim.Code));
        await _s.SettleUntilQuietAsync();                                                                // the office now knows the vessel is writing it

        // A newer version of the source replaces it: it can no longer be shared, but its newer version can.
        var next = await _shore.NewVersionAsync(_s.CompanyId, source, RaKind.Annual, _s.User);
        await _shore.SubmitAsync(_s.CompanyId, next.Id, _s.User);
        await _shore.SignOffAsync(_s.CompanyId, next.Id, RaDecision.Signed, "", _s.User);
        Assert.Contains("newer version", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ShareAsync(_s.CompanyId, source, [_s.VesselId], _s.User))).Message);

        // While the vessel is still writing its own interim version, a newer share has to wait; once that is signed it becomes the next version of the vessel's own chain.
        Assert.Contains("working on a newer version", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ShareAsync(_s.CompanyId, next.Id, [_s.VesselId], _s.User))).Message);
        await _vessel.SubmitAsync(_s.CompanyId, interim.Id, _crewReviewer);
        await _vessel.SignOffAsync(_s.CompanyId, interim.Id, RaDecision.Signed, "", _crewReviewer, "Master");
        await _s.SettleUntilQuietAsync();
        Assert.False((await _shore.ShareTargetsAsync(_s.CompanyId, next.Id)).Single().AlreadyHasCopy);       // the vessel holds only the earlier version
        var third = Assert.Single(await _shore.ShareAsync(_s.CompanyId, next.Id, [_s.VesselId], _s.User));
        await _s.SettleUntilQuietAsync();
        Assert.Equal((copy.Code, 3, interim.Id, 2), (third.Code, third.Version, third.PreviousVersionId, third.SharedFromVersion));
        Assert.True((await _shore.ShareTargetsAsync(_s.CompanyId, next.Id)).Single().AlreadyHasCopy);
        Assert.Empty(await _shore.ShareAsync(_s.CompanyId, next.Id, [_s.VesselId], _s.User));

        foreach (var side in new[] { _shore, _vessel })
            Assert.Equal([(1, RaStatus.Signed), (2, RaStatus.Signed), (3, RaStatus.Signed)], (await side.VersionsAsync(_s.CompanyId, copy.Code)).Select(v => (v.Assessment.Version, v.Status)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TwoVersionsWithTheSameNumberCanMeet_BecauseTheOfficeAndAVesselMayEachStartTheNextWhileApart()
    {
        await SwitchOnAsync();
        var id = await SubmittedAsync(_shore, _s.VesselId, _s.User);
        var a = (await _shore.GetAsync(_s.CompanyId, id))!.Assessment;
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        foreach (var n in new[] { "office", "vessel" })
            db.RiskAssessments.Add(new RiskAssessment { Id = Guid.CreateVersion7(), CompanyId = a.CompanyId, VesselId = a.VesselId, Code = a.Code, Version = 2, PreviousVersionId = a.Id, CreatedByName = n, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();                                                                    // no unique-index failure: both are kept and shown
        Assert.Equal(2, await db.RiskAssessments.CountAsync(x => x.Code == a.Code && x.Version == 2));
    }

    [Fact]
    public async Task ASharedAssessmentOnlyGoesToVesselsWithTheModuleOn()
    {
        await SwitchOnAsync();                                                                          // only the first vessel
        var source = await SubmittedAsync(_vessel, _s.VesselId, _crewReviewer);
        await _vessel.SignOffAsync(_s.CompanyId, source, RaDecision.Signed, "", _crewReviewer, "Master");
        await _s.SettleUntilQuietAsync();
        Assert.Empty(await _shore.ShareTargetsAsync(_s.CompanyId, source));                           // no other vessel has it on
        Assert.Contains("not switched on", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ShareAsync(_s.CompanyId, source, [_s.OtherVesselId], _s.User))).Message);
    }
}
