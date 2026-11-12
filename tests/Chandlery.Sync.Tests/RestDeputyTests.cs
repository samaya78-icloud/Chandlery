using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The authority list on a crew record, and the first thing it is used for: a deputy the office names signs hours of rest for the Master.</summary>
public class RestDeputyAuthorityTests
{
    [Fact]
    public void TheListIsKeptInOneFixedOrder_WithOnlyNamesThisBuildKnows_EachOnce()
    {
        Assert.Equal("", CrewAuthority.Normalise(null));
        Assert.Equal("", CrewAuthority.Normalise(["Nonsense"]));
        Assert.Equal(CrewAuthority.SignHoursAsDeputy, CrewAuthority.Normalise([CrewAuthority.SignHoursAsDeputy, "Nonsense", CrewAuthority.SignHoursAsDeputy]));
    }

    [Fact]
    public void ParseAndHas_ReadTheStoredText_AndSetAddsOrRemoves()
    {
        Assert.True(CrewAuthority.Has(" SignHoursAsDeputy , ", CrewAuthority.SignHoursAsDeputy));
        Assert.False(CrewAuthority.Has("", CrewAuthority.SignHoursAsDeputy));
        Assert.False(CrewAuthority.Has(null, CrewAuthority.SignHoursAsDeputy));
        Assert.Equal(CrewAuthority.SignHoursAsDeputy, CrewAuthority.Set("", CrewAuthority.SignHoursAsDeputy, true));
        Assert.Equal("", CrewAuthority.Set(CrewAuthority.SignHoursAsDeputy, CrewAuthority.SignHoursAsDeputy, false));
    }

    [Fact]
    public void ADeputyIsNotTheMaster_ButActsForTheMaster()
    {
        var deputy = new CrewMember { Authorities = CrewAuthority.SignHoursAsDeputy };
        Assert.True(deputy.ActsForMaster);
        Assert.False(deputy.IsMaster);
        Assert.True(new CrewMember { IsMaster = true }.ActsForMaster);
        Assert.False(new CrewMember { IsCrewAdmin = true, IsRiskReviewer = true }.ActsForMaster);
    }
}

public class RestDeputyTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RestHoursService _shoreRest = null!, _vesselRest = null!;
    private RestReviewService _shore = null!, _vessel = null!;
    private const string Normal = "08:00-12:00,13:00-17:00";
    private const string TooLong = "05:00-12:00,13:00-20:15";           // 14 h 15 of work: 15 minutes short of the 24-hour rest, and no other rule broken

    private static readonly DateTime First = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    private static DateTime Day(int n) => First.AddDays(n - 1);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreRest = new RestHoursService(_s.ShoreFactory);
        _vesselRest = new RestHoursService(_s.VesselFactory);
        _shore = new RestReviewService(_s.ShoreFactory, _shoreRest, new WorklistService(_s.ShoreFactory));
        _vessel = new RestReviewService(_s.VesselFactory, _vesselRest, new WorklistService(_s.VesselFactory));
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RestHours, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false, bool deputy = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, master,
            Authorities: deputy ? CrewAuthority.SignHoursAsDeputy : ""), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private async Task RecordAsync(CrewMember by, CrewMember crew, int from, int to, string spans = Normal)
    {
        for (var d = from; d <= to; d++) await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(by), crew.Id, Day(d), spans);
    }

    private Task<RestSignOff> SignAsync(CrewMember by, CrewMember crew, int from, int to, RestRole role) => _vessel.SignAsync(_s.CompanyId, _s.VesselId, As(by), crew.Id, Day(from), Day(to), role);

    private async Task<RestPeriod> PeriodAsync(CrewMember crew, int from, bool office = false)
        => (await (office ? _shore : _vessel).MonthAsync(_s.CompanyId, _s.VesselId, crew.Id, First.Year, First.Month)).Periods.Single(p => p.From == Day(from));

    // ---- who may be a deputy ----

    [Fact]
    public async Task TheOfficeNamesTheDeputy_TheVesselReceivesIt_AndSigningOffTakesItAway()
    {
        var deputy = await SignOnAsync("D1001", "Chief Officer", deputy: true);
        var plain = await SignOnAsync("D1002", "Second Officer");

        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.CurrentCompanyId = _s.CompanyId;
            Assert.True((await db.CrewMembers.SingleAsync(c => c.Id == deputy.Id)).Has(CrewAuthority.SignHoursAsDeputy));
            Assert.False((await db.CrewMembers.SingleAsync(c => c.Id == plain.Id)).ActsForMaster);
        }

        await _s.ShoreCrew.SignOffAsync(_s.CompanyId, deputy.Id, _s.User);
        Assert.Equal("", (await _s.ShoreCrew.GetAsync(_s.CompanyId, deputy.Id))!.Authorities);
    }

    // ---- signing as deputy ----

    [Fact]
    public async Task ADeputySignsAsMasterForSomeoneElse_AndTheSignatureSaysSo_OnBothSides()
    {
        var crew = await SignOnAsync("D2001", "Third Officer");
        var master = await SignOnAsync("D2002", "Master", master: true);
        var deputy = await SignOnAsync("D2003", "Chief Officer", deputy: true);
        var plain = await SignOnAsync("D2004", "Second Officer");
        await RecordAsync(crew, crew, 1, 7);
        await SignAsync(crew, crew, 1, 7, RestRole.Seafarer);

        Assert.Contains("deputy the office has named", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(plain, crew, 1, 7, RestRole.Master))).Message);
        var sign = await SignAsync(deputy, crew, 1, 7, RestRole.Master);
        Assert.True(sign.AsDeputy);
        Assert.Equal($"{deputy.FullName} (deputy for the Master)", sign.SignerLabel);
        Assert.True((await PeriodAsync(crew, 1)).Complete);

        await _s.SettleUntilQuietAsync();
        var seen = await PeriodAsync(crew, 1, office: true);
        Assert.True(seen.Complete);
        Assert.True(seen.Master!.Sign.AsDeputy);
        Assert.False(seen.Seafarer!.Sign.AsDeputy);

        // The Master's own signature is not marked.
        await RecordAsync(crew, crew, 8, 14);
        await SignAsync(crew, crew, 8, 14, RestRole.Seafarer);
        Assert.False((await SignAsync(master, crew, 8, 14, RestRole.Master)).AsDeputy);
    }

    [Fact]
    public async Task ADeputyCannotSignTheirOwnHoursAsMaster_ButTheMasterOrAnotherDeputyCan()
    {
        var master = await SignOnAsync("D3001", "Master", master: true);
        var deputy = await SignOnAsync("D3002", "Chief Officer", deputy: true);
        var other = await SignOnAsync("D3003", "Chief Engineer", deputy: true);
        await RecordAsync(deputy, deputy, 1, 7);
        await SignAsync(deputy, deputy, 1, 7, RestRole.Seafarer);

        Assert.Contains("own hours", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(deputy, deputy, 1, 7, RestRole.Master))).Message);
        Assert.True((await SignAsync(other, deputy, 1, 7, RestRole.Master)).AsDeputy);

        await RecordAsync(deputy, deputy, 8, 14);
        await SignAsync(deputy, deputy, 8, 14, RestRole.Seafarer);
        Assert.False((await SignAsync(master, deputy, 8, 14, RestRole.Master)).AsDeputy);
    }

    [Fact]
    public async Task ADeputysSignatureLocksTheWeek_AgainstTheCrewAndAgainstTheDeputy_UntilTheMasterChangesADay()
    {
        var crew = await SignOnAsync("D4001", "Third Officer");
        var master = await SignOnAsync("D4002", "Master", master: true);
        var deputy = await SignOnAsync("D4003", "Chief Officer", deputy: true);
        await RecordAsync(crew, crew, 1, 7);
        await SignAsync(crew, crew, 1, 7, RestRole.Seafarer);
        await SignAsync(deputy, crew, 1, 7, RestRole.Master);

        var byCrew = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(3), "08:00-12:00,13:00-18:00", "Forgot an hour"));
        Assert.Contains($"for the Master by {deputy.FullName}", byCrew.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(deputy), crew.Id, Day(3), "08:00-12:00,13:00-18:00", "Changed by the deputy"));

        await _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(master), crew.Id, Day(3), "08:00-12:00,13:00-18:00", "Seafarer worked an extra hour");
        Assert.True((await PeriodAsync(crew, 1)).OutOfDate);
    }

    // ---- breaches ----

    [Fact]
    public async Task ADeputyAcknowledgesSomeoneElsesBreach_ButCannotDeclareAnException_OrAcknowledgeTheirOwn()
    {
        var crew = await SignOnAsync("D5001", "Third Officer");
        var master = await SignOnAsync("D5002", "Master", master: true);
        var deputy = await SignOnAsync("D5003", "Chief Officer", deputy: true);
        foreach (var person in new[] { crew, deputy })
        {
            await RecordAsync(person, person, 1, 9);
            await RecordAsync(person, person, 10, 10, TooLong);
            await RecordAsync(person, person, 11, 14);
            await _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(person), person.Id, Day(10), RestRule.Rest24, "Cargo operation ran over");
        }

        Assert.Contains("deputy", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(deputy), deputy.Id, Day(10), RestRule.Rest24))).Message);
        Assert.Contains("Only the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.DeclareExceptionAsync(_s.CompanyId, _s.VesselId, As(deputy), crew.Id, Day(10), RestRule.Rest24, RestExceptionKind.Emergency, "Berthing in a squall"))).Message);
        Assert.Contains("Only the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ConfirmCompensatoryAsync(_s.CompanyId, _s.VesselId, As(deputy), crew.Id, Day(10), RestRule.Rest24, "Given the next day"))).Message);

        await _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(deputy), crew.Id, Day(10), RestRule.Rest24);
        await _vessel.AcknowledgeAsync(_s.CompanyId, _s.VesselId, As(master), deputy.Id, Day(10), RestRule.Rest24);           // the Master deals with the deputy's own
    }

    [Fact]
    public async Task ADeputyWithBreachesToDealWith_SeesThemOnTheHomePage_ButNeverTheirOwn()
    {
        var crew = await SignOnAsync("D6001", "Third Officer");
        var deputy = await SignOnAsync("D6002", "Chief Officer", deputy: true);
        await RecordAsync(crew, crew, 1, 9);
        await RecordAsync(crew, crew, 10, 10, TooLong);
        await RecordAsync(crew, crew, 11, 14);
        await _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(crew), crew.Id, Day(10), RestRule.Rest24, "Cargo operation ran over");
        await RecordAsync(deputy, deputy, 1, 9);
        await RecordAsync(deputy, deputy, 10, 10, TooLong);
        await RecordAsync(deputy, deputy, 11, 14);
        await _vessel.ExplainAsync(_s.CompanyId, _s.VesselId, As(deputy), deputy.Id, Day(10), RestRule.Rest24, "Same job");

        var mine = await _vessel.AttentionAsync(_s.CompanyId, As(deputy));
        Assert.NotNull(mine);
        Assert.True(mine!.IsDeputy);
        Assert.False(mine.IsMaster);
        Assert.Equal(1, mine.ToAcknowledge);                    // the other officer's breach, not their own
    }
}
