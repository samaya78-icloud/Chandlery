using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The working-arrangements table: checked against the rules as it is written, and signed by the Master onboard.</summary>
public class RestTableRulesTests
{
    private static readonly RestLimits Limits = RestLimits.Default;

    [Fact]
    public void AnOrdinaryDayShowsItsHoursOfWorkAndRest_WithNothingWrong()
    {
        var day = ArrangementRules.CheckDay("04:00-08:00,16:00-20:00", "08:00-10:00", Limits);
        Assert.Equal((600, 840), (day.WorkMinutes, day.RestMinutes));
        Assert.True(day.Ok);
    }

    [Fact]
    public void WatchkeepingAndOtherDutiesThatTouchOrOverlapAreAddedOnlyOnce()
    {
        var day = ArrangementRules.CheckDay("08:00-12:00", "10:00-14:00", Limits);
        Assert.Equal(360, day.WorkMinutes);
    }

    [Fact]
    public void ADayThatLeavesTooLittleRestIsReportedBeforeTheTableIsPosted()
    {
        var day = ArrangementRules.CheckDay("05:00-12:00,13:00-20:15", "", Limits);
        Assert.False(day.Ok);
        Assert.Equal(855, day.WorkMinutes);                                  // 14 h 15 of work
        Assert.Contains("Rest in 24 hours is 9h 45m; the minimum is 10h.", day.Problems);
        Assert.DoesNotContain(day.Problems, p => p.Contains("Jan"));           // no stand-in dates leak into the words
    }

    [Fact]
    public void RestBrokenIntoThreePeriodsIsReported()
    {
        var day = ArrangementRules.CheckDay("02:00-04:00,08:00-10:00,14:00-16:00,20:00-22:00", "", Limits);
        Assert.False(day.Ok);
    }

    [Fact]
    public void MistypedPeriodsAreAProblemOnTheRow_NotACrash()
    {
        Assert.False(ArrangementRules.CheckDay("nonsense", "", Limits).Ok);
        Assert.False(ArrangementRules.CheckDay("04:00-09:07", "", Limits).Ok);
    }

    [Fact]
    public void ARowWithNoWorkListedIsAllRest()
    {
        var row = ArrangementRules.Check(new ArrangementRow("Supernumerary", "", "", "", "", ""), Limits);
        Assert.Equal((0, 1440), (row.Sea.WorkMinutes, row.Sea.RestMinutes));
        Assert.True(row.Ok);
    }

    [Fact]
    public void AStricterRuleSetChangesWhatIsReported()
    {
        var strict = Limits with { MinRest24 = 900 };           // 15 h; the day below leaves 14 h
        Assert.True(ArrangementRules.CheckDay("08:00-12:00,13:00-19:00", "", Limits).Ok);
        Assert.False(ArrangementRules.CheckDay("08:00-12:00,13:00-19:00", "", strict).Ok);
    }
}

public class RestTableTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private WorkArrangementService _shore = null!, _vessel = null!;

    private static readonly ArrangementRow Master = new("Master", "", "08:00-12:00,13:00-17:00", "", "08:00-12:00,13:00-17:00", "");
    private static readonly ArrangementRow Deckhand = new("Able Seaman", "00:00-04:00,12:00-16:00", "", "08:00-12:00", "13:00-17:00", "");
    private static readonly ArrangementRow TooMuch = new("Bosun", "05:00-12:00,13:00-20:15", "", "", "", "");

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new WorkArrangementService(_s.ShoreFactory);
        _vessel = new WorkArrangementService(_s.VesselFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RestHours, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static ArrangementInput Input(params ArrangementRow[] rows)
        => new(DateTime.UtcNow.Date.AddDays(-1), "Flag state regulations", "10 h in 24, 77 h in 7 days", "", rows.ToList());

    [Fact]
    public async Task OnlyTheMasterOfTheVesselSignsTheTable_AndOnlyTheNewestVersion_OnceEach()
    {
        var master = await SignOnAsync("T1001", "Master", master: true);
        var seaman = await SignOnAsync("T1002", "Able Seaman");
        var v1 = await _shore.PublishVersionAsync(_s.CompanyId, _s.VesselId, Input(Master, Deckhand), _s.User);
        await _s.SettleUntilQuietAsync();

        // Not the seafarer, and not someone who is not aboard (the office user is no crew member).
        var notMaster = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(seaman), v1.Id));
        Assert.Contains("Only the Master", notMaster.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignAsync(_s.CompanyId, _s.VesselId, _s.User, v1.Id));

        var signed = await _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(master), v1.Id);
        Assert.Equal((1, master.FullName, SyncStages.Vessel), (signed.VersionNo, signed.SignedByName, signed.OwnerStage));
        Assert.Equal(signed.Id, (await _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(master), v1.Id)).Id);          // again: nothing new
        Assert.Single(await _vessel.SignaturesAsync(_s.CompanyId, _s.VesselId));

        // A new version has to be signed again, and the old one can no longer be.
        var v2 = await _shore.PublishVersionAsync(_s.CompanyId, _s.VesselId, Input(Master), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Null((await _vessel.DocumentAsync(_s.CompanyId, _s.VesselId, 2))!.Signature);
        Assert.NotNull((await _vessel.DocumentAsync(_s.CompanyId, _s.VesselId, 1))!.Signature);
        var old = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(master), v1.Id));
        Assert.Contains("replaced by version 2", old.Message);
        await _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(master), v2.Id);
    }

    [Fact]
    public async Task TheMastersSignatureTravelsToTheOffice_WhichSeesTheTableAsSignedAndCannotSignItself()
    {
        var master = await SignOnAsync("T2001", "Master", master: true);
        var v1 = await _shore.PublishVersionAsync(_s.CompanyId, _s.VesselId, Input(Master), _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Null((await _shore.DocumentAsync(_s.CompanyId, _s.VesselId))!.Signature);

        await _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(master), v1.Id);
        await _s.SettleUntilQuietAsync();

        var doc = (await _shore.DocumentAsync(_s.CompanyId, _s.VesselId))!;
        Assert.True(doc.Signed);
        Assert.Equal(master.FullName, doc.Signature!.SignedByName);
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());

        // The signature is history: nobody edits it.
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        (await db.WorkArrangementSignOffs.FirstAsync()).SignedByName = "someone else";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ThePrintedTableCarriesTheHeaderTheTotalsAndWhatIsWrong_AndMakesAPdf()
    {
        await _shore.PublishVersionAsync(_s.CompanyId, _s.VesselId, Input(Master, TooMuch), _s.User);
        var doc = (await _shore.DocumentAsync(_s.CompanyId, _s.VesselId))!;
        Assert.Equal(1, doc.Table.VersionNo);
        Assert.Equal(["Master", "Bosun"], doc.Rows.Select(r => r.Row.Position));
        Assert.True(doc.Rows[0].Ok);
        Assert.False(doc.Rows[1].Sea.Ok);
        Assert.True(doc.Problems > 0);
        Assert.Equal(855, doc.Rows[1].Sea.WorkMinutes);
        Assert.False(doc.Signed);

        var pdf = ArrangementPdf.Build(doc);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Null(await _shore.DocumentAsync(_s.CompanyId, Guid.NewGuid()));
        Assert.Null(await _shore.DocumentAsync(_s.CompanyId, _s.VesselId, 9));
    }

    [Fact]
    public async Task AVesselWithTheStricterRuleSetShowsTheRowsAsBreaching()
    {
        await _shore.PublishVersionAsync(_s.CompanyId, _s.VesselId, Input(Deckhand), _s.User);
        Assert.True((await _shore.DocumentAsync(_s.CompanyId, _s.VesselId))!.Rows[0].Ok);
        await new RestHoursService(_s.ShoreFactory).SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Strict", IsDefault = true, MinRest24Minutes = 1000 });
        Assert.False((await _shore.DocumentAsync(_s.CompanyId, _s.VesselId))!.Rows[0].Ok);
    }

    [Fact]
    public async Task APositionCannotAppearTwice()
    {
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.PublishVersionAsync(_s.CompanyId, _s.VesselId, Input(Master, Master with { Comments = "again" }), _s.User));
        Assert.Contains("twice", twice.Message);
    }
}
