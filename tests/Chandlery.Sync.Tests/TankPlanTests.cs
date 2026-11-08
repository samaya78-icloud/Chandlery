using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class TankLookTests
{
    [Fact]
    public void HealthComesFromTheLatestSubmittedInspection_AndEveryTypeAndHealthHasItsOwnColourAndName()
    {
        Assert.Equal(TankHealth.NotInspected, TankLook.HealthOf(false, 0, false));
        Assert.Equal(TankHealth.Clean, TankLook.HealthOf(true, 0, false));
        Assert.Equal(TankHealth.Exceptions, TankLook.HealthOf(true, 3, false));
        Assert.Equal(TankHealth.Bad, TankLook.HealthOf(true, 1, true));

        var types = Enum.GetValues<TankType>();
        Assert.Equal(types.Length, types.Select(TankLook.Colour).Distinct().Count());
        var healths = Enum.GetValues<TankHealth>();
        Assert.Equal(healths.Length, healths.Select(TankLook.Colour).Distinct().Count());
        Assert.Equal("Fresh water", TankLook.Label(TankType.FreshWater));
        Assert.Equal("Void space", TankLook.Label(TankType.VoidSpace));
    }
}

/// <summary>The tank plan data and copying a tank list between vessels, on a real office and a real vessel.</summary>
public class TankPlanTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private TankService _shoreTanks = null!, _vesselTanks = null!;
    private TankInspectionService _vessel = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreTanks = new TankService(_s.ShoreFactory);
        _vesselTanks = new TankService(_s.VesselFactory);
        _vessel = new TankInspectionService(_s.VesselFactory);
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        foreach (var vessel in new[] { _s.VesselId, _s.OtherVesselId })
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.TankInspection, Enabled = true });
        await db.SaveChangesAsync();
        await _s.SettleUntilQuietAsync();
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private static TankInput Tank(string code, string name, TankType type = TankType.Ballast, int x = 10, int y = 5) => new(code, name, type, x, y, 12, 8, SteelGradeDeck: "AH36", OriginalCoating: "Epoxy", CoatedAreaM2: 850.5m);

    private async Task InspectAsync(Tank tank, DateTime on, string coatingValue)
    {
        var template = (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).Single(t => t.Template.TankType == tank.Type).Template.Id;
        var started = await _vessel.StartAsync(_s.CompanyId, new NewInspection(tank.Id, template, on, "At sea"), _s.User);
        var detail = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        var answers = detail.Inspection.Results.Select(r => new InspectionAnswer(r.Id,
            r.CriterionName == "Condition of coating" ? coatingValue : r.Scale == ScaleKind.PercentBucket ? "76-100" : "Good", r.CriterionName == "Condition of coating" && coatingValue != "Good" ? "Rust and blistering" : "")).ToList();
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new InspectionEdit(on, "At sea", "", "", "Chief Officer", answers), _s.User);
        await _vessel.SubmitAsync(_s.CompanyId, started.Id, _s.User);
    }

    [Fact]
    public async Task ThePlanShowsEachTankWithItsLatestSubmittedInspection()
    {
        var clean = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("1P", "No. 1 port ballast"), _s.User);
        var worn = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("1S", "No. 1 starboard ballast", x: 30), _s.User);
        var never = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("2P", "No. 2 port ballast", x: 50), _s.User);
        await _s.SettleUntilQuietAsync();

        await InspectAsync(clean, Today.AddDays(-10), "Good");
        await InspectAsync(worn, Today.AddDays(-30), "Bad");                    // older: bad
        await InspectAsync(worn, Today.AddDays(-2), "Fair");                    // newer: only a fair rating, so the newest decides
        var draft = await _vessel.StartAsync(_s.CompanyId, new NewInspection(never.Id, (await _shoreTanks.ListTemplatesAsync(_s.CompanyId)).First(t => t.Template.TankType == TankType.Ballast).Template.Id, Today, "At sea"), _s.User);
        Assert.Equal(0, draft.SubmissionCount);                                  // a draft says nothing about the tank

        foreach (var service in new[] { _vesselTanks, _shoreTanks })
        {
            if (service == _shoreTanks) await _s.SettleUntilQuietAsync();
            var plan = (await service.PlanAsync(_s.CompanyId, _s.VesselId)).ToDictionary(r => r.Tank.Code);
            Assert.Equal(TankHealth.Clean, plan["1P"].Health);
            Assert.Equal(TankHealth.Exceptions, plan["1S"].Health);
            Assert.Equal((2, Today.AddDays(-2)), (plan["1S"].InspectionCount, plan["1S"].LastInspectedOn));
            Assert.Equal(TankHealth.NotInspected, plan["2P"].Health);
        }
    }

    [Fact]
    public async Task OutOfServiceTanksAreLeftOffThePlanUnlessAsked()
    {
        var tank = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("3P", "No. 3 port ballast"), _s.User);
        await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("3S", "No. 3 starboard ballast", x: 40), _s.User);
        await _shoreTanks.UpdateTankAsync(_s.CompanyId, tank.Id, Tank("3P", "No. 3 port ballast"), isActive: false);
        Assert.Equal(["3S"], (await _shoreTanks.PlanAsync(_s.CompanyId, _s.VesselId)).Select(r => r.Tank.Code));
        Assert.Equal(["3P", "3S"], (await _shoreTanks.PlanAsync(_s.CompanyId, _s.VesselId, includeInactive: true)).Select(r => r.Tank.Code));
    }

    [Fact]
    public async Task CopyingATankList_AddsWhatIsMissing_ChangesNothingElse_AndCanBeRepeated()
    {
        await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("1P", "No. 1 port ballast"), _s.User);
        await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("FW1", "Fresh water tank", TankType.FreshWater, 70, 20), _s.User);
        var retired = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("OLD", "Retired tank", x: 80), _s.User);
        await _shoreTanks.UpdateTankAsync(_s.CompanyId, retired.Id, Tank("OLD", "Retired tank", x: 80), isActive: false);        // not copied
        var theirs = await _shoreTanks.AddTankAsync(_s.CompanyId, _s.OtherVesselId, Tank("1P", "Their own name for it", x: 22, y: 9), _s.User);

        var first = await _shoreTanks.CopyTanksAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId], _s.User);
        Assert.Equal((1, 1, null), (first.Single().Added, first.Single().Skipped, first.Single().Problem));                  // FW1 added, 1P already there

        var onOther = (await _shoreTanks.ListTanksAsync(_s.CompanyId, _s.OtherVesselId, includeInactive: true)).ToDictionary(t => t.Code);
        Assert.Equal(["1P", "FW1"], onOther.Keys.Order());
        Assert.Equal(("Their own name for it", 22, 9), (onOther["1P"].Name, onOther["1P"].PlanX, onOther["1P"].PlanY));      // untouched
        Assert.Equal((TankType.FreshWater, 70, 20, "AH36", 850.5m), (onOther["FW1"].Type, onOther["FW1"].PlanX, onOther["FW1"].PlanY, onOther["FW1"].SteelGradeDeck, onOther["FW1"].CoatedAreaM2));
        Assert.Equal(theirs.Id, onOther["1P"].Id);

        var second = await _shoreTanks.CopyTanksAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId, _s.VesselId], _s.User);   // the source itself is ignored
        Assert.Equal((0, 2), (second.Single().Added, second.Single().Skipped));
        Assert.Equal(2, (await _shoreTanks.ListTanksAsync(_s.CompanyId, _s.OtherVesselId, includeInactive: true)).Count);
    }

    [Fact]
    public async Task OnlyTheOfficeCopiesTankLists_AndOnlyToVesselsWithTheModuleOn()
    {
        await _shoreTanks.AddTankAsync(_s.CompanyId, _s.VesselId, Tank("1P", "No. 1 port ballast"), _s.User);
        await _s.SettleUntilQuietAsync();
        var onboard = await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselTanks.CopyTanksAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId], _s.User));
        Assert.Contains("office", onboard.Message);

        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            var other = await db.VesselModuleSwitches.SingleAsync(x => x.VesselId == _s.OtherVesselId && x.Module == PolicyModules.TankInspection);
            other.Enabled = false;
            await db.SaveChangesAsync();
        }
        var off = await _shoreTanks.CopyTanksAsync(_s.CompanyId, _s.VesselId, [_s.OtherVesselId], _s.User);
        Assert.Equal((0, "Tank inspection is off for this vessel."), (off.Single().Added, off.Single().Problem));
        Assert.Empty(await _shoreTanks.ListTanksAsync(_s.CompanyId, _s.OtherVesselId, includeInactive: true));
    }
}
