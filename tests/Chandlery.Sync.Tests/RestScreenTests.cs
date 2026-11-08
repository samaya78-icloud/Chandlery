using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>What the rest-hours screens rely on: who may record, the month as recorded, the day pre-filled from the working table, and the live check of a day being filled in.</summary>
public class RestScreenTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RestHoursService _shore = null!, _vessel = null!;
    private WorkArrangementService _shoreTable = null!;
    private static DateTime Today => DateTime.UtcNow.Date;
    private static DateTime Ago(int days) => DateTime.SpecifyKind(Today.AddDays(-days), DateTimeKind.Utc);
    private const string Normal = "08:00-12:00,13:00-17:00";

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new RestHoursService(_s.ShoreFactory);
        _vessel = new RestHoursService(_s.VesselFactory);
        _shoreTable = new WorkArrangementService(_s.ShoreFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RestHours, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank = "Third Officer", bool master = false, bool admin = false, Guid? vessel = null)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", vessel ?? _s.VesselId, null, true, admin, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    // ---- who may record ----

    [Fact]
    public async Task ASeafarerRecordsTheirOwnHours_TheMasterAndCrewAdminsMayRecordForOthers_NobodyElseMay()
    {
        var seafarer = await SignOnAsync("S1001");
        var other = await SignOnAsync("S1002");
        var master = await SignOnAsync("S1003", "Master", master: true);
        var admin = await SignOnAsync("S1004", "Chief Officer", admin: true);

        var own = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(seafarer), seafarer.Id, Ago(1), Normal);
        Assert.False(own.OnBehalf);
        Assert.Equal(seafarer.FullName, own.EnteredByName);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(seafarer), other.Id, Ago(1), Normal));
        Assert.Contains("Master or a crew admin", refused.Message);

        var byMaster = await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(master), other.Id, Ago(1), Normal);
        Assert.True(byMaster.OnBehalf);
        Assert.Equal(master.Id, byMaster.EnteredByCrewId);
        Assert.True((await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(admin), other.Id, Ago(2), Normal)).OnBehalf);

        // The office, and anyone who is not a seafarer aboard, cannot record: hours are a vessel record.
        var office = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, _s.VesselId, _s.User, seafarer.Id, Ago(3), Normal));
        Assert.Contains("onboard", office.Message);
        var wrongVessel = await SignOnAsync("S1005", vessel: _s.OtherVesselId);
        var notAboard = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.RecordAsync(_s.CompanyId, _s.VesselId, As(wrongVessel), seafarer.Id, Ago(3), Normal));
        Assert.Contains("not signed on to this vessel", notAboard.Message);

        // Setting the vessel's day status is open to any seafarer aboard, and to nobody else.
        Assert.Equal(VesselDayKind.AtSea, (await _vessel.SetStatusAsync(_s.CompanyId, _s.VesselId, As(seafarer), Ago(1), VesselDayKind.AtSea, "")).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SetStatusAsync(_s.CompanyId, _s.VesselId, _s.User, Ago(1), VesselDayKind.AtSea, ""));
    }

    // ---- the month ----

    [Fact]
    public async Task TheMonthShowsEveryDate_WithWhatIsRecorded_TheCheck_AndTheVesselsStatus()
    {
        var crew = await SignOnAsync("S2001");
        var master = await SignOnAsync("S2002", "Master", master: true);
        // The month before this one, so the test reads the same whatever day it runs on: the first twelve days are recorded, newest first.
        var first = new DateTime(Today.Year, Today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var recorded = Enumerable.Range(0, 12).Select(i => first.AddDays(11 - i)).ToList();
        foreach (var d in recorded) await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, d, Normal);
        var last = recorded[0];
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, last, "08:00-12:00,13:00-18:00", "Extra hour");          // a correction
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, recorded[^1], "09:00-12:00,13:00-17:00", "Entered by the Master");
        await _vessel.SetStatusAsync(_s.CompanyId, _s.VesselId, As(crew), last, VesselDayKind.InPort, "Singapore");
        await _s.SettleUntilQuietAsync();

        // The office reads the same month the vessel recorded.
        foreach (var service in new[] { _vessel, _shore })
        {
            var month = await service.MonthAsync(_s.CompanyId, _s.VesselId, crew.Id, first.Year, first.Month);
            Assert.Equal(DateTime.DaysInMonth(first.Year, first.Month), month.Days.Count);
            Assert.Equal((crew.FullName, "Third Officer"), (month.CrewName, month.Rank));
            Assert.Equal(recorded.Count, month.Recorded);
            Assert.Equal(0, month.Breaches);

            var lastDay = month.Days.Single(d => d.Date == last);
            Assert.Equal("08:00-12:00,13:00-18:00", lastDay.Spans);
            Assert.Equal(2, lastDay.Revisions);
            Assert.Equal((VesselDayKind.InPort, "Singapore"), (lastDay.Status!.Kind, lastDay.Status.PortName));
            var firstRecorded = month.Days.Single(d => d.Date == recorded[^1]);
            Assert.True(firstRecorded.OnBehalf);
            Assert.Equal(master.FullName, firstRecorded.LastBy);
            Assert.All(month.Days.Where(d => d.Spans is null), d => Assert.Equal(RestDayState.NotRecorded, d.Result.State));
        }
    }

    // ---- the day started from the working table ----

    [Fact]
    public async Task TheDayIsStartedFromTheWorkingTable_AtSeaOrInPort_ForTheSeafarersRank()
    {
        var crew = await SignOnAsync("S3001", "Third Officer");
        Assert.Null(await _vessel.SuggestAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(0)));                      // no table yet

        var rows = new List<ArrangementRow>
        {
            new("Third officer", "00:00-04:00,12:00-16:00", "", "08:00-12:00", "13:00-17:00", ""),               // written differently from the rank: case does not matter
            new("Cook", "", "05:00-13:00", "", "05:00-13:00", ""),
        };
        await _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, new ArrangementInput(Ago(30), "Flag state regulations", "", "", rows), _s.User);
        await _s.SettleUntilQuietAsync();

        var atSea = await _vessel.SuggestAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(0));
        Assert.Equal("00:00-04:00,12:00-16:00", atSea!.Spans);                                                  // no status set: at sea
        Assert.Contains("at sea", atSea.Basis);

        await _vessel.SetStatusAsync(_s.CompanyId, _s.VesselId, As(crew), Ago(2), VesselDayKind.InPort, "Rotterdam");
        var inPort = await _vessel.SuggestAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(0));                      // in port since two days ago, until told otherwise
        Assert.Equal("08:00-12:00,13:00-17:00", inPort!.Spans);
        Assert.Contains("in port", inPort.Basis);

        await _vessel.SetStatusAsync(_s.CompanyId, _s.VesselId, As(crew), Ago(1), VesselDayKind.AtSea, "");
        Assert.Contains("at sea", (await _vessel.SuggestAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(0)))!.Basis);
        Assert.Contains("in port", (await _vessel.SuggestAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(2)))!.Basis);

        var noRow = await SignOnAsync("S3002", "Bosun");
        Assert.Null(await _vessel.SuggestAsync(_s.CompanyId, _s.VesselId, noRow.Id, Ago(0)));
    }

    // ---- the live check ----

    [Fact]
    public async Task ADayBeingFilledInIsCheckedAgainstTheDaysAroundIt_BeforeItIsSaved()
    {
        var crew = await SignOnAsync("S4001");
        for (var i = 9; i >= 1; i--) await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Ago(i), Normal);

        var window = await _vessel.WindowAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(0));
        Assert.Empty(window.Check(Ago(0), null));                                                            // nothing recorded for today, nothing on the grid

        var ordinary = window.Check(Ago(0), Normal);
        Assert.Equal(RestDayState.Ok, Assert.Single(ordinary).State);

        var tooLong = window.Check(Ago(0), "06:00-20:15");                                                   // 14 h 15 of work: 9 h 45 of rest
        var breach = Assert.Single(tooLong);
        Assert.Equal(RestDayState.Breach, breach.State);
        Assert.Contains(breach.Findings, f => f.Rule == RestRule.Rest24 && f.Actual == 585);

        // Changing yesterday also changes how today stands: both are shown once today is recorded.
        await _vessel.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Ago(0), Normal);
        var withToday = await _vessel.WindowAsync(_s.CompanyId, _s.VesselId, crew.Id, Ago(1));
        var results = withToday.Check(Ago(1), "08:00-24:00");
        Assert.Equal([Ago(1), Ago(0)], results.Select(r => r.Date));
        Assert.Equal(RestDayState.Breach, results[0].State);                                                // 8 hours of rest

        // A seafarer under 18 is checked from the date of birth the crew record holds.
        var young = await SignOnAsync("S4002");
        await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(young.Id, "S4002", "Name of S4002", "Third Officer", "Singaporean", Today.AddYears(-16), "Passport", null, null, "", "", _s.VesselId, null, true, false), _s.User);
        await _s.SettleUntilQuietAsync();
        var youngWindow = await _vessel.WindowAsync(_s.CompanyId, _s.VesselId, young.Id, Ago(0));
        Assert.Contains(Assert.Single(youngWindow.Check(Ago(0), "07:00-12:00,21:00-23:00")).Findings, f => f.Rule == RestRule.MinorNight);
    }
}
