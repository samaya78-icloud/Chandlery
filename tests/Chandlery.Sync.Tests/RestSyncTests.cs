using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Seafarers' hours of rest on a real office and a real vessel: recorded onboard, read ashore, the same answer on both sides.</summary>
public class RestSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RestHoursService _shore = null!, _vessel = null!;
    private WorkArrangementService _shoreTable = null!, _vesselTable = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime Ago(int days) => DateTime.SpecifyKind(Today.AddDays(-days), DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new RestHoursService(_s.ShoreFactory);
        _vessel = new RestHoursService(_s.VesselFactory);
        _shoreTable = new WorkArrangementService(_s.ShoreFactory);
        _vesselTable = new WorkArrangementService(_s.VesselFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync(Guid? vessel = null)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel ?? _s.VesselId, Module = PolicyModules.RestHours, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<CrewMember> SignOnAsync(string code, Guid? vessel = null, DateTime? born = null)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", "Third Officer", "Singaporean", born, "Passport", "E1234567X", null, "", "", vessel ?? _s.VesselId, null, true, false), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static string Normal => "08:00-12:00,13:00-17:00";

    private async Task RecordWeekAsync(RestHoursService service, Guid crewId, int days = 9, string spans = "08:00-12:00,13:00-17:00")
    {
        for (var i = days; i >= 1; i--) await service.RecordDayAsync(_s.CompanyId, _s.VesselId, crewId, Ago(i), spans, _s.User);
    }

    // ---- the module, and recording ----

    [Fact]
    public async Task TheModuleIsOffUntilSwitchedOn_AndOnlyASeafarerSignedOnToTheVesselCanBeRecorded()
    {
        var crew = await SignOnAsync("R1001");
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(1), Normal, _s.User));
        Assert.Contains("not switched on", off.Message);

        await SwitchOnAsync();
        Assert.NotNull(await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(1), Normal, _s.User));

        var elsewhere = await SignOnAsync("R1002", _s.OtherVesselId);
        var notAboard = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordDayAsync(_s.CompanyId, _s.VesselId, elsewhere.Id, Ago(1), Normal, _s.User));
        Assert.Contains("not signed on", notAboard.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, elsewhere.Id, Ago(1), Normal, _s.User));      // the vessel does not even hold another vessel's crew
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Today.AddDays(3), Normal, _s.User));         // a day that has not started
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(2), "22:00-02:00", _s.User));              // over midnight: two entries
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(2), "08:07-12:00", _s.User));              // not on the quarter hour
    }

    [Fact]
    public async Task AnEntryReachesTheOffice_AndBothSidesWorkOutTheSameAnswer()
    {
        await SwitchOnAsync();
        var crew = await SignOnAsync("R2001");
        await RecordWeekAsync(_vessel, crew.Id, 9);
        await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(0), "06:00-20:15", _s.User);                    // a long day, 9 h 45 of rest
        await _s.SettleUntilQuietAsync();

        var onShore = await OnShore(db => db.RestEntries.CountAsync());
        Assert.Equal(10, onShore);
        Assert.All(await OnShore(db => db.RestEntries.ToListAsync()), e => Assert.Equal(SyncStages.Vessel, e.OwnerStage));      // the vessel owns its hours
        Assert.Equal("Name of R2001", (await OnShore(db => db.RestEntries.FirstAsync())).CrewName);

        var atVessel = await _vessel.CheckAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(2), Ago(0));
        var atOffice = await _shore.CheckAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(2), Ago(0));
        Assert.Equal(3, atOffice.Days.Count);
        Assert.Equal(atVessel.Days.Select(d => (d.Date, d.State, d.WorkMinutes, d.Worst24, d.Worst7)), atOffice.Days.Select(d => (d.Date, d.State, d.WorkMinutes, d.Worst24, d.Worst7)));
        Assert.Equal(atVessel.Days.SelectMany(d => d.Findings).Select(f => (f.Rule, f.Actual)), atOffice.Days.SelectMany(d => d.Findings).Select(f => (f.Rule, f.Actual)));
        var today = atOffice.Days.Last();
        Assert.Equal(RestDayState.Breach, today.State);
        Assert.Contains(today.Findings, f => f.Rule == RestRule.Rest24 && f.Actual == 585);
        Assert.Equal(RestDayState.Ok, atOffice.Days[0].State);
    }

    [Fact]
    public async Task ACorrectionIsANewRevisionWithAReason_TheLatestCountsAndTheHistoryStays()
    {
        await SwitchOnAsync();
        var crew = await SignOnAsync("R3001");
        var first = await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(1), Normal, _s.User);

        var same = await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(1), "13:00-17:00,08:00-12:00", _s.User);                  // the same day, written differently: nothing new
        Assert.Equal(first.Id, same.Id);

        var noReason = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(1), "08:00-18:00", _s.User));
        Assert.Contains("reason", noReason.Message);
        var revised = await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(1), "08:00-18:00", _s.User, reason: "Worked through lunch");
        await _s.SettleUntilQuietAsync();

        var history = await _shore.HistoryAsync(_s.CompanyId, crew.Id, Ago(5), Ago(0));
        Assert.Equal([Normal, "08:00-18:00"], history.Select(h => h.WorkSpans));
        Assert.Equal("Worked through lunch", history[1].Reason);
        var latest = RestHoursService.Latest(history);
        Assert.Equal(revised.Id, latest[Ago(1)].Id);

        // Once sent, a record is history on both sides: it cannot be changed or removed.
        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        var held = await db.RestEntries.FirstAsync(e => e.Id == first.Id);
        held.WorkSpans = "00:00-24:00";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("history", ex.Message);
        await using var db2 = await _s.VesselFactory.CreateDbContextAsync();
        db2.RestEntries.Remove(await db2.RestEntries.FirstAsync(e => e.Id == first.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task AnOfficerMayEnterHoursForSomeoneElse_AndItIsMarked()
    {
        await SwitchOnAsync();
        var seafarer = await SignOnAsync("R4001");
        var officer = await SignOnAsync("R4002");
        var entry = await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, seafarer.Id, Ago(1), Normal, _s.User, enteredByCrewId: officer.Id);
        Assert.True(entry.OnBehalf);
        Assert.False((await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, officer.Id, Ago(1), Normal, _s.User, enteredByCrewId: officer.Id)).OnBehalf);
    }

    [Fact]
    public async Task AYoungSeafarersNightWorkIsCaughtFromTheirDateOfBirth()
    {
        await SwitchOnAsync();
        var youngster = await SignOnAsync("R5001", born: Today.AddYears(-17));
        var adult = await SignOnAsync("R5002", born: Today.AddYears(-30));
        foreach (var crew in new[] { youngster, adult })
        {
            await RecordWeekAsync(_vessel, crew.Id, 8, "07:00-12:00,13:00-17:00");
            await _vessel.RecordDayAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(0), "07:00-12:00,22:00-24:00", _s.User);
        }
        Assert.Contains((await _vessel.CheckAsync(_s.CompanyId, _s.VesselId, youngster.Id, Ago(0), Ago(0))).Days[0].Findings, f => f.Rule == RestRule.MinorNight && f.Actual == 120);
        Assert.DoesNotContain((await _vessel.CheckAsync(_s.CompanyId, _s.VesselId, adult.Id, Ago(0), Ago(0))).Days[0].Findings, f => f.Rule == RestRule.MinorNight);
    }

    [Fact]
    public async Task TheVesselsDayStatusIsKeptAsHistory_TheLatestSettingForADateCounts()
    {
        await SwitchOnAsync();
        var first = await _vessel.SetDayStatusAsync(_s.CompanyId, _s.VesselId, Ago(1), VesselDayKind.AtSea, "ignored", _s.User);
        Assert.Equal("", first.PortName);                                                         // a port belongs to "in port" only
        await _vessel.SetDayStatusAsync(_s.CompanyId, _s.VesselId, Ago(1), VesselDayKind.InPort, "Singapore", _s.User);
        await _s.SettleUntilQuietAsync();

        var atOffice = await _shore.DayStatusAsync(_s.CompanyId, _s.VesselId, Ago(1));
        Assert.Equal((VesselDayKind.InPort, "Singapore"), (atOffice!.Kind, atOffice.PortName));
        Assert.Equal(2, await OnShore(db => db.VesselDayStatuses.CountAsync()));
        Assert.Null(await _shore.DayStatusAsync(_s.CompanyId, _s.VesselId, Ago(4)));
    }

    // ---- the rule sets ----

    [Fact]
    public async Task TheCompanyHasADefaultRuleSetThatReachesTheVessel_AndAVesselCanHaveItsOwn()
    {
        await SwitchOnAsync();
        var sets = await _shore.ListRuleSetsAsync(_s.CompanyId);
        var standard = Assert.Single(sets);
        Assert.Equal((RestDefaults.DefaultName, true, 600, 4620, false), (standard.Name, standard.IsDefault, standard.MinRest24Minutes, standard.MinRest7Minutes, standard.ReductionAllowed));
        await _s.SettleUntilQuietAsync();
        Assert.Single(await _vessel.ListRuleSetsAsync(_s.CompanyId));

        // A set for another vessel never reaches this one; a set for this vessel overrides the default there.
        await _shore.SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Other vessel", VesselId = _s.OtherVesselId, ReductionAllowed = true });
        await _shore.SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "This vessel (flag state agreement)", VesselId = _s.VesselId, ReductionAllowed = true, MinRest7Minutes = 4200 });
        await _s.SettleUntilQuietAsync();
        var onVessel = await _vessel.ListRuleSetsAsync(_s.CompanyId);
        Assert.Equal(["ILO 180 / STCW default", "This vessel (flag state agreement)"], onVessel.Select(r => r.Name).Order().ToList());
        Assert.DoesNotContain(onVessel, r => r.Name == "Other vessel");

        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        var (chosen, limits) = await RestHoursService.RulesForAsync(db, _s.CompanyId, _s.VesselId);
        Assert.Equal("This vessel (flag state agreement)", chosen!.Name);
        Assert.Equal((4200, true), (limits.MinRest7, limits.ReductionAllowed));

        // A vessel receives rule sets; it does not write them.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Made onboard" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Bad", MinRest24Minutes = 0 }));
    }

    [Fact]
    public async Task OnlyOneRuleSetIsTheCompanyDefault()
    {
        var standard = (await _shore.ListRuleSetsAsync(_s.CompanyId)).Single();
        var second = await _shore.SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Stricter", IsDefault = true, MinRest24Minutes = 660 });
        var sets = await _shore.ListRuleSetsAsync(_s.CompanyId);
        Assert.Equal([second.Id], sets.Where(r => r.IsDefault).Select(r => r.Id));
        Assert.Contains(sets, r => r.Id == standard.Id && !r.IsDefault);
    }

    // ---- the table of working arrangements ----

    private static ArrangementInput Table(int daysAgo, params ArrangementRow[] rows)
        => new(Ago(daysAgo), "Singapore Merchant Shipping (Maritime Labour Convention) Regulations", "Minimum hours of rest: 10 in 24 hours, 77 in 7 days", "", rows.ToList());

    private static readonly ArrangementRow ChiefOfficer = new("Chief Officer", "04:00-08:00,16:00-20:00", "", "06:00-12:00", "13:00-17:00", "");
    private static readonly ArrangementRow Cook = new("Cook", "", "05:00-13:00", "", "05:00-13:00", "Plus meals for port staff");

    [Fact]
    public async Task TheOfficePublishesTheTable_TheVesselReceivesItAndCannotChangeIt_EachChangeIsANewVersion()
    {
        await SwitchOnAsync();
        var v1 = await _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, Table(30, ChiefOfficer, Cook), _s.User);
        Assert.Equal((1, SyncStages.Office), (v1.VersionNo, v1.OwnerStage));
        await _s.SettleUntilQuietAsync();

        var received = await _vesselTable.InForceAsync(_s.CompanyId, _s.VesselId, Ago(0));
        Assert.Equal(v1.Id, received!.Id);
        var rows = WorkArrangementService.RowsOf(received);
        Assert.Equal(["Chief Officer", "Cook"], rows.Select(r => r.Position));
        Assert.Equal((480, 600), (rows[0].SeaWorkMinutes, rows[0].PortWorkMinutes));
        Assert.Equal(480, rows[1].SeaWorkMinutes);

        // The vessel's copy is read-only.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, Table(0, Cook), _s.User));
        Assert.Contains("handled by the office", refused.Message);

        // A change is a new version; the old one stays, and the version in force depends on the date.
        var v2 = await _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, Table(0, ChiefOfficer), _s.User);
        Assert.Equal(2, v2.VersionNo);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(v2.Id, (await _vesselTable.InForceAsync(_s.CompanyId, _s.VesselId, Ago(0)))!.Id);
        Assert.Equal(v1.Id, (await _vesselTable.InForceAsync(_s.CompanyId, _s.VesselId, Ago(10)))!.Id);
        Assert.Null(await _vesselTable.InForceAsync(_s.CompanyId, _s.VesselId, Ago(60)));
        Assert.Equal([2, 1], (await _vesselTable.VersionsAsync(_s.CompanyId, _s.VesselId)).Select(t => t.VersionNo));

        // Versions are history even for the office.
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        (await db.WorkArrangementTables.FirstAsync(t => t.Id == v1.Id)).LawReference = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ATableIsCheckedBeforeItIsPublished()
    {
        await SwitchOnAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, Table(0), _s.User));                                  // no rows
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, Table(0, new ArrangementRow("", "", "", "", "", "")), _s.User));   // no position
        var overlap = await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId,
            Table(0, new ArrangementRow("Bosun", "04:00-09:00,08:00-12:00", "", "", "", "")), _s.User));
        Assert.Contains("Bosun", overlap.Message);
        Assert.Contains("overlap", overlap.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, Table(0, new ArrangementRow("Bosun", "nonsense", "", "", "", "")), _s.User));
    }

    [Fact]
    public async Task AVesselThatStartsFromNothingReceivesTheTableTheOfficeAlreadyHolds()
    {
        await SwitchOnAsync();
        var table = new WorkArrangementTable
        {
            Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, VersionNo = 1, EffectiveFrom = Ago(30), LawReference = "Flag state regulations",
            RowsJson = System.Text.Json.JsonSerializer.Serialize(new[] { ChiefOfficer }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            CreatedByName = "Office", CreatedAt = DateTime.UtcNow, OwnerStage = SyncStages.Office,
        };
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.ApplyingSync = true;                                                                         // existed before this vessel connected: nothing announced
            db.WorkArrangementTables.Add(table);
            await db.SaveChangesAsync();
        }
        Assert.Null(await _vesselTable.InForceAsync(_s.CompanyId, _s.VesselId, Ago(0)));

        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();
        var received = await _vesselTable.InForceAsync(_s.CompanyId, _s.VesselId, Ago(0));
        Assert.Equal(table.Id, received!.Id);
        Assert.Equal("Chief Officer", Assert.Single(WorkArrangementService.RowsOf(received)).Position);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }
}
