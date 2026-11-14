using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>The permit follow-ups: jobs that need a permit, the drill a permit relies on, and the list of closed permits past retention.</summary>
public class PermitFollowUpTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private PermitService _shore = null!, _vessel = null!;
    private List<PermitType> _types = null!;
    private PermitType Enclosed => _types.Single(t => t.Name == "Enclosed space entry");
    private PermitType Hot => _types.Single(t => t.Name == "Hot work");

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new PermitService(_s.ShoreFactory, new WorklistService(_s.ShoreFactory));
        _vessel = new PermitService(_s.VesselFactory, new WorklistService(_s.VesselFactory));
        _types = await _shore.TypesAsync(_s.CompanyId);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<JobTemplate> LibraryJobAsync(string title)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var job = new JobTemplate { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, Code = "T-" + title[..3].ToUpperInvariant(), Title = title, Instructions = "Check it." };
        db.JobTemplates.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    // ---- jobs that need a permit ----

    [Fact]
    public async Task AJobRuleIsTheOfficesToSet_ReachesTheVessel_AndFollowsTheTypeBeingSwitchedOff()
    {
        var job = await LibraryJobAsync("Clean the cargo tank");
        Assert.Null(await _vessel.RequiredForJobAsync(_s.CompanyId, job.Id));
        Assert.Null(await _vessel.RequiredForJobAsync(_s.CompanyId, null));                                       // a job that came from no library job needs nothing

        await _shore.SaveJobRuleAsync(_s.CompanyId, job.Id, Enclosed.Id);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(Enclosed.Id, (await _vessel.RequiredForJobAsync(_s.CompanyId, job.Id))!.Id);

        await _shore.SaveJobRuleAsync(_s.CompanyId, job.Id, Hot.Id);                                              // one kind to a job: setting it again changes it
        await _s.SettleUntilQuietAsync();
        Assert.Equal(Hot.Id, (await _vessel.RequiredForJobAsync(_s.CompanyId, job.Id))!.Id);
        Assert.Single(await _shore.JobRulesAsync(_s.CompanyId));

        var onboard = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveJobRuleAsync(_s.CompanyId, job.Id, Enclosed.Id));
        Assert.Contains("office", onboard.Message);
        var unknownJob = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveJobRuleAsync(_s.CompanyId, Guid.NewGuid(), Hot.Id));
        Assert.Contains("maintenance library", unknownJob.Message);

        Hot.IsActive = false;
        await _shore.SaveTypeAsync(_s.CompanyId, Hot);
        await _s.SettleUntilQuietAsync();
        Assert.Null(await _vessel.RequiredForJobAsync(_s.CompanyId, job.Id));                                     // a kind switched off is no longer asked for
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveJobRuleAsync(_s.CompanyId, job.Id, Hot.Id));
        Assert.Contains("in use", off.Message);

        var rule = (await _shore.JobRulesAsync(_s.CompanyId)).Single();
        await _shore.RemoveJobRuleAsync(_s.CompanyId, rule.Rule.Id);
        await _s.SettleUntilQuietAsync();
        Assert.Empty(await _shore.JobRulesAsync(_s.CompanyId));
    }

    // ---- the drill a permit relies on ----

    [Fact]
    public async Task ThePermitTypeCanNameADrill_AndTheVesselSeesWhenItWasLastHeld()
    {
        var today = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        Guid drillTypeId;
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
            drillTypeId = (await db.DrillTypes.AsNoTracking().FirstAsync(t => t.CompanyId == _s.CompanyId && t.Name == "Enclosed space entry and rescue")).Id;

        Assert.Equal(drillTypeId, Enclosed.RescueDrillTypeId);                                                    // the default enclosed space type names its rescue drill, as a starting point
        Assert.Null(Hot.RescueDrillTypeId);
        await _s.SettleUntilQuietAsync();
        Assert.Null(await _vessel.RescueDrillAsync(_s.CompanyId, _s.VesselId, Hot.Id, today));                    // a type that names no drill shows none

        var never = (await _vessel.RescueDrillAsync(_s.CompanyId, _s.VesselId, Enclosed.Id, today))!;
        Assert.Equal("Enclosed space entry and rescue", never.DrillName);
        Assert.Null(never.LastHeld);
        Assert.True(never.Overdue);                                                                               // never held counts as due

        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            db.CurrentCompanyId = _s.CompanyId;
            db.Drills.Add(new Drill { Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.VesselId, Code = "DR-TEST-1", DrillTypeId = drillTypeId, TypeName = "Enclosed space entry and rescue", StartedAt = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), CreatedAt = today });
            await db.SaveChangesAsync();
        }
        var recent = (await _vessel.RescueDrillAsync(_s.CompanyId, _s.VesselId, Enclosed.Id, today))!;
        Assert.Equal(new DateTime(2026, 10, 20), recent.LastHeld!.Value.Date);
        Assert.False(recent.Overdue);                                                                             // held three weeks ago, every two months
        Assert.True((await _vessel.RescueDrillAsync(_s.CompanyId, _s.VesselId, Enclosed.Id, today.AddMonths(3)))!.Overdue);

        var other = await Assert.ThrowsAsync<InvalidOperationException>(() => { Enclosed.RescueDrillTypeId = Guid.NewGuid(); return _shore.SaveTypeAsync(_s.CompanyId, Enclosed); });
        Assert.Contains("drill type", other.Message);
        Enclosed.RescueDrillTypeId = null;
        await _shore.SaveTypeAsync(_s.CompanyId, Enclosed);
        await _s.SettleUntilQuietAsync();
        Assert.Null(await _vessel.RescueDrillAsync(_s.CompanyId, _s.VesselId, Enclosed.Id, today));
    }

    // ---- retention ----

    private static PermitRow Closed(string code, PermitState state, DateTime closedAt, bool withEvent = true)
    {
        var permit = new Permit { Id = Guid.NewGuid(), Code = code, TypeName = "Hot work" };
        if (withEvent) permit.Events.Add(new PermitEvent { Id = Guid.NewGuid(), PermitId = permit.Id, Kind = PermitEventKind.Completed, At = closedAt });
        permit.Events.Add(new PermitEvent { Id = Guid.NewGuid(), PermitId = permit.Id, Kind = PermitEventKind.Started, At = closedAt.AddHours(-4) });
        return new PermitRow(permit, "MV Test", state, false, "", closedAt, 0);
    }

    [Fact]
    public void ClosedPermitsOlderThanTheRetentionPeriodAreListedOldestFirst_AndOpenOnesNever()
    {
        var today = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Closed("P-NEW", PermitState.Completed, today.AddMonths(-35)),
            Closed("P-OLD", PermitState.Completed, today.AddMonths(-40)),
            Closed("P-OLDEST", PermitState.Withdrawn, today.AddMonths(-60)),
            Closed("P-OPEN", PermitState.Active, today.AddMonths(-50)),                                           // still open: never listed however old
            Closed("P-EXPIRED", PermitState.Expired, today.AddMonths(-50)),                                       // expired and not closed is for the board, not the archive
        };
        var past = PermitReports.PastRetention(rows, today, 36);
        Assert.Equal(["P-OLDEST", "P-OLD"], past.Select(x => x.Row.Permit.Code));
        Assert.Equal(["P-OLDEST", "P-OLD", "P-NEW"], PermitReports.PastRetention(rows, today, 24).Select(x => x.Row.Permit.Code));
        Assert.Empty(PermitReports.PastRetention(rows, today, 120));

        var csv = PermitReports.RetentionCsv(rows, today, 36);
        Assert.Contains("P-OLDEST", csv);
        Assert.DoesNotContain("P-NEW", csv);
        Assert.DoesNotContain("P-OPEN", csv);
    }

    [Fact]
    public void TheClosingDayIsTheLastEvent_AndAPermitWithNoEventsIsNotListed()
    {
        var today = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        var exactly = Closed("P-EDGE", PermitState.Completed, today.AddMonths(-36));                              // closed exactly 36 months ago is not yet past it
        var none = new PermitRow(new Permit { Id = Guid.NewGuid(), Code = "P-NONE", TypeName = "Hot work" }, "MV Test", PermitState.Withdrawn, false, "", today.AddYears(-9), 0);
        Assert.Empty(PermitReports.PastRetention([exactly, none], today, 36));
        Assert.Single(PermitReports.PastRetention([exactly, none], today.AddDays(1), 36));
    }
}
