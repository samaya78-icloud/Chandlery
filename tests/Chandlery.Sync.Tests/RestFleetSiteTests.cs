using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>The office reading what a vessel recorded: the same numbers as onboard, the trend, the exports and the printed record.</summary>
public class RestFleetSiteTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RestHoursService _shoreRest = null!, _vesselRest = null!;
    private RestReviewService _shoreReview = null!, _vesselReview = null!;
    private RestFleetService _shore = null!, _vessel = null!;
    private const string Normal = "08:00-12:00,13:00-17:00";
    private const string TooLong = "05:00-12:00,13:00-20:15";           // 14 h 15 of work: 9 h 45 of rest on the day, 15 minutes short, and no other rule broken

    // The month before this one, so the tests read the same whatever day they run on.
    private static readonly DateTime First = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    private static DateTime Day(int n) => First.AddDays(n - 1);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreRest = new RestHoursService(_s.ShoreFactory);
        _vesselRest = new RestHoursService(_s.VesselFactory);
        _shoreReview = new RestReviewService(_s.ShoreFactory, _shoreRest, new WorklistService(_s.ShoreFactory));
        _vesselReview = new RestReviewService(_s.VesselFactory, _vesselRest, new WorklistService(_s.VesselFactory));
        _shore = new RestFleetService(_s.ShoreFactory);
        _vessel = new RestFleetService(_s.VesselFactory);
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

    private async Task RecordAsync(CrewMember crew, int from, int to, string spans = Normal)
    {
        for (var d = from; d <= to; d++) await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(d), spans);
    }

    private async Task<RestVesselMonth> MonthAsync(RestFleetService site)
        => Assert.Single(await site.FleetAsync(_s.CompanyId, First.Year, First.Month));

    /// <summary>Days 1 to 14 for the seafarer, ordinary except the 10th, which breaks the 24-hour rule by 15 minutes.</summary>
    private async Task<(CrewMember Crew, CrewMember Master)> BreachAsync()
    {
        var crew = await SignOnAsync("F5001", "Third Officer");
        var master = await SignOnAsync("F5002", "Master", master: true);
        await RecordAsync(crew, 1, 9);
        await RecordAsync(crew, 10, 10, TooLong);
        await RecordAsync(crew, 11, 14);
        await _s.SettleUntilQuietAsync();
        return (crew, master);
    }

    [Fact]
    public async Task TheOfficeSeesTheSameMonthTheVesselDoes_AndFollowsABreachToItsResolution()
    {
        var (crew, master) = await BreachAsync();

        var onboard = await MonthAsync(_vessel);
        var ashore = await MonthAsync(_shore);
        var member = ashore.Crew.Single(c => c.CrewId == crew.Id);
        Assert.Equal((onboard.Breaches, onboard.Unresolved, onboard.Missing), (ashore.Breaches, ashore.Unresolved, ashore.Missing));
        Assert.Equal((1, 1), (member.Breaches.Count, member.Unresolved));
        Assert.Equal(14, member.Recorded);
        Assert.Equal("danger", ashore.Tone);

        await _vesselReview.ExplainAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, "Cargo operation ran over at the berth");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(1, (await MonthAsync(_shore)).Unresolved);                              // explained, but the Master has not dealt with it

        await _vesselReview.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(10), RestRule.Rest24);
        await _s.SettleUntilQuietAsync();
        var resolved = await MonthAsync(_shore);
        Assert.Equal((1, 0), (resolved.Breaches, resolved.Unresolved));

        // Both seafarers' first fortnight is over and unsigned: two periods for the seafarer, and the Master's own as well.
        Assert.True(resolved.ToSign >= 2);
    }

    [Fact]
    public async Task ASignaturePutOutOfDateByACorrectionShowsAtTheOffice_AndTheSeafarersTrendHoldsTheMonth()
    {
        var (crew, master) = await BreachAsync();
        await _vesselReview.SignAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(1), Day(7), RestRole.Seafarer);
        await _s.SettleUntilQuietAsync();
        var before = (await MonthAsync(_shore)).Crew.Single(c => c.CrewId == crew.Id);
        Assert.Equal(0, before.OutOfDate);

        await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(3), "09:00-12:00,13:00-17:00", reason: "Corrected");
        await _s.SettleUntilQuietAsync();
        var after = (await MonthAsync(_shore)).Crew.Single(c => c.CrewId == crew.Id);
        Assert.Equal(1, after.OutOfDate);
        Assert.Contains(after.Periods, p => p.From == Day(1) && p.Seafarer is { Current: false });

        var trend = await _shore.TrendAsync(_s.CompanyId, _s.VesselId, crew.Id, months: 3);
        Assert.Equal(3, trend.Count);
        Assert.Equal(trend.OrderBy(m => m.First).Select(m => m.First), trend.Select(m => m.First));           // oldest first
        var month = trend.Single(m => m.First == First);
        Assert.Equal((1, 14), (month.Breaches.Count, month.Recorded));
    }

    [Fact]
    public async Task TheReportsHoldWhatWasRecorded_AndTheTilesCountTheLastTwoMonths()
    {
        var (crew, _) = await BreachAsync();

        var daily = await _shore.DailyAsync(_s.CompanyId, First.Year, First.Month);
        var recorded = daily.Where(r => r.Name == crew.FullName && r.Date <= Day(14)).ToList();
        Assert.Equal(14, recorded.Count);
        Assert.Equal(TooLong, recorded.Single(r => r.Date == Day(10)).Spans);
        Assert.Equal(RestDayState.Breach, recorded.Single(r => r.Date == Day(10)).State);
        var csv = RestReports.DailyCsv(daily);
        Assert.Contains($"\"{TooLong}\"", csv);
        Assert.Contains("\"Breach\"", csv);

        var counts = await _shore.CountsAsync(_s.CompanyId);
        Assert.NotNull(counts);
        Assert.Equal(1, counts!.Vessels);
        Assert.Equal(1, counts.Unresolved);
        Assert.True(counts.Any);
    }

    [Fact]
    public async Task ASeafarersMonthPrintsAsAPdf_ForTheOfficeAndForTheVessel_WithTheHeaderAndTheSignatures()
    {
        var (crew, _) = await BreachAsync();
        await _vesselReview.SignAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(1), Day(7), RestRole.Seafarer);
        await _s.SettleUntilQuietAsync();

        foreach (var (rest, review) in new[] { (_shoreRest, _shoreReview), (_vesselRest, _vesselReview) })
        {
            var head = await rest.HeaderAsync(_s.CompanyId, _s.VesselId);
            var sheet = await rest.MonthAsync(_s.CompanyId, _s.VesselId, crew.Id, First.Year, First.Month);
            var month = await review.MonthAsync(_s.CompanyId, _s.VesselId, crew.Id, First.Year, First.Month);
            Assert.False(string.IsNullOrWhiteSpace(head.Vessel));
            Assert.Equal(crew.FullName, sheet.CrewName);
            Assert.Single(month.Breaches);

            var pdf = RestSheetPdf.Build(new RestSheetDocument(head, sheet, month));
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
            Assert.True(pdf.Length > 2000);
        }
    }

    [Fact]
    public async Task AVesselWithNoSeafarerRecordsShowsNothingToChase()
    {
        var month = await MonthAsync(_shore);
        Assert.Empty(month.Crew);
        Assert.Equal((0, 0, 0), (month.Breaches, month.Unresolved, month.Missing));
        Assert.Equal("success", month.Tone);
        Assert.False((await _shore.CountsAsync(_s.CompanyId))!.Any);
    }
}
