using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The whole of hours of rest in one story, between a real office database and a real vessel database: the office sets the rules and the table, the vessel
/// records a month with a deliberate breach and an emergency, signs, corrects a day that was already signed, and follows the breach into the worklist. At the
/// end both sides must hold the same totals, the same breaches and the same signatures, and the vessel must hold nothing of another vessel's crew.
/// </summary>
public class RestWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RestHoursService _shoreRest = null!, _vesselRest = null!;
    private RestReviewService _shoreReview = null!, _vesselReview = null!;
    private RestFleetService _shoreFleet = null!, _vesselFleet = null!;
    private WorkArrangementService _shoreTable = null!, _vesselTable = null!;
    private WorklistService _shoreWorklist = null!, _vesselWorklist = null!;

    private const string Normal = "08:00-12:00,13:00-17:00";
    private const string TooLong = "05:00-12:00,13:00-20:15";           // 14 h 15 of work: 9 h 45 of rest on the day, 15 minutes short, and no other rule broken

    // The month before this one, so every period of it is over and the story reads the same whatever day it runs on.
    private static readonly DateTime First = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    private static DateTime Day(int n) => First.AddDays(n - 1);

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreRest = new RestHoursService(_s.ShoreFactory);
        _vesselRest = new RestHoursService(_s.VesselFactory);
        _shoreWorklist = new WorklistService(_s.ShoreFactory);
        _vesselWorklist = new WorklistService(_s.VesselFactory);
        _shoreReview = new RestReviewService(_s.ShoreFactory, _shoreRest, _shoreWorklist);
        _vesselReview = new RestReviewService(_s.VesselFactory, _vesselRest, _vesselWorklist);
        _shoreFleet = new RestFleetService(_s.ShoreFactory);
        _vesselFleet = new RestFleetService(_s.VesselFactory);
        _shoreTable = new WorkArrangementService(_s.ShoreFactory);
        _vesselTable = new WorkArrangementService(_s.VesselFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync(Guid vesselId, params string[] modules)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in modules) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<CrewMember> SignOnAsync(string code, string rank, Guid vesselId, bool master = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", vesselId, null, true, false, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private Task RecordAsync(CrewMember by, CrewMember crew, int day, string spans, string reason = "")
        => _vesselRest.RecordAsync(_s.CompanyId, _s.VesselId, As(by), crew.Id, Day(day), spans, reason);

    private Task<RestSignOff> SignAsync(CrewMember by, CrewMember crew, int from, int to, RestRole role)
        => _vesselReview.SignAsync(_s.CompanyId, _s.VesselId, As(by), crew.Id, Day(from), Day(to), role);

    /// <summary>One seafarer's month as a line that can be compared between the two sides: the totals, and the state of every signing period.</summary>
    private static string Line(RestCrewMonth c)
        => $"{c.Name}|rec {c.Recorded}|miss {c.Missing}|breach {c.Breaches.Count}|open {c.Unresolved}|rest owed {c.CompensatoryOutstanding}/{c.OwedMinutes}|to sign {c.ToSign}|stale {c.OutOfDate}|24h {c.Worst24}|7d {c.Worst7}|"
           + string.Join(",", c.Periods.Select(p => $"{p.From:dd}:{State(p.Seafarer)}:{State(p.Master)}"));

    private static string State(SignView? s) => s is null ? "none" : s.Current ? "ok" : "old";

    [Fact]
    public async Task TheOfficeSetsTheRulesAndTheTable_TheVesselRecordsAMonthWithABreachAndAnEmergency_AndBothSidesEndWithTheSameTotalsBreachesAndSignatures()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.RestHours, PolicyModules.Worklist);

        // ---- 1. The office sets the rules and writes the table ----
        await _shoreRest.SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Singapore MLC", FlagState = "Singapore", IsDefault = true, RetentionMonths = 60 });
        var table = await _shoreTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, new ArrangementInput(First.AddDays(-30), "Singapore Merchant Shipping (Maritime Labour Convention) Regulations", "Minimum hours of rest: 10 in 24 hours, 77 in 7 days", "",
            [new ArrangementRow("Master", "", Normal, "", Normal, ""), new ArrangementRow("Third Officer", "00:00-04:00,12:00-16:00", "", "08:00-12:00", "13:00-17:00", "")]), _s.User);
        var written = (await _shoreTable.DocumentAsync(_s.CompanyId, _s.VesselId))!;
        Assert.Equal(0, written.Problems);                                                      // the table itself can be worked within the rules
        var master = await SignOnAsync("W6001", "Master", _s.VesselId, master: true);
        var officer = await SignOnAsync("W6002", "Third Officer", _s.VesselId);

        // The vessel received the rules, the table and the crew, and cannot change what the office owns.
        Assert.Equal(("Singapore MLC", "Singapore"), (await _vesselRest.ListRuleSetsAsync(_s.CompanyId)).Where(r => r.IsDefault).Select(r => (r.Name, r.FlagState)).Single());
        Assert.Equal(table.Id, (await _vesselTable.InForceAsync(_s.CompanyId, _s.VesselId, Day(1)))!.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselRest.SaveRuleSetAsync(_s.CompanyId, new RestRuleSet { Name = "Our own", IsDefault = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselTable.PublishVersionAsync(_s.CompanyId, _s.VesselId, new ArrangementInput(Day(1), "", "", "", [new ArrangementRow("Cook", "", "", "", "", "")]), _s.User));

        // The Master signs the table onboard; the office sees it signed.
        await _vesselTable.SignAsync(_s.CompanyId, _s.VesselId, As(master), table.Id);
        await _s.SettleUntilQuietAsync();
        Assert.True((await _shoreTable.DocumentAsync(_s.CompanyId, _s.VesselId))!.Signed);

        // ---- 2. The vessel records a fortnight ----
        await _vesselRest.SetStatusAsync(_s.CompanyId, _s.VesselId, As(master), Day(1), VesselDayKind.AtSea, "");
        await _vesselRest.SetStatusAsync(_s.CompanyId, _s.VesselId, As(master), Day(6), VesselDayKind.InPort, "Singapore");
        var atSea = (await _vesselRest.SuggestAsync(_s.CompanyId, _s.VesselId, officer.Id, Day(1)))!;
        Assert.Equal("00:00-04:00,12:00-16:00", atSea.Spans);
        Assert.Contains("at sea", atSea.Basis);
        Assert.Contains("in port", (await _vesselRest.SuggestAsync(_s.CompanyId, _s.VesselId, officer.Id, Day(7)))!.Basis);

        await RecordAsync(officer, officer, 1, atSea.Spans);                                    // the first day starts from the table
        foreach (var d in Enumerable.Range(2, 13).Where(d => d is not 10 and not 13)) await RecordAsync(officer, officer, d, Normal);
        await RecordAsync(officer, officer, 10, TooLong);                                       // a long day of cargo work
        await RecordAsync(officer, officer, 13, TooLong);                                       // and another one, which nobody will explain
        foreach (var d in Enumerable.Range(1, 14)) await RecordAsync(master, master, d, Normal);

        // ---- 3. Signatures on the first week ----
        await SignAsync(officer, officer, 1, 7, RestRole.Seafarer);
        await SignAsync(master, officer, 1, 7, RestRole.Master);
        await SignAsync(master, master, 1, 7, RestRole.Seafarer);
        await SignAsync(master, master, 1, 7, RestRole.Master);

        // ---- 4. A breach, an explanation, and an emergency the Master declares ----
        Assert.Equal([Day(10), Day(13)], (await _vesselReview.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(14))).Select(b => b.Finding.Date));
        await _vesselReview.ExplainAsync(_s.CompanyId, _s.VesselId, As(officer), officer.Id, Day(10), RestRule.Rest24, "Cargo operation ran over; the gangway was needed for the fire drill inspection");
        var emergency = await _vesselReview.DeclareExceptionAsync(_s.CompanyId, _s.VesselId, As(master), officer.Id, Day(10), RestRule.Rest24, RestExceptionKind.Emergency, "Pilot and tug delayed in heavy weather; everyone was needed on deck");
        var task = emergency.Task!;
        Assert.Null(emergency.TaskProblem);

        // ---- 5. The second week: the seafarer signs, the Master cannot until the unexplained breach is dealt with ----
        await SignAsync(officer, officer, 8, 14, RestRole.Seafarer);
        Assert.Contains("still need an explanation", (await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(master, officer, 8, 14, RestRole.Master))).Message);

        // ---- 6. A signed day is corrected, by the Master only, which takes both signatures on that week back ----
        Assert.Contains("signed by the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => RecordAsync(officer, officer, 3, "09:00-12:00,13:00-17:00", "Started an hour late"))).Message);
        await RecordAsync(master, officer, 3, "09:00-12:00,13:00-17:00", "Started an hour late");

        await _s.SettleUntilQuietAsync();

        // ---- 7. Both sides end with the same story ----
        var onboard = Assert.Single(await _vesselFleet.FleetAsync(_s.CompanyId, First.Year, First.Month, _s.VesselId));
        var ashore = Assert.Single(await _shoreFleet.FleetAsync(_s.CompanyId, First.Year, First.Month, _s.VesselId));
        Assert.Equal(onboard.Crew.Select(Line), ashore.Crew.Select(Line));

        var daysInMonth = DateTime.DaysInMonth(First.Year, First.Month);
        var o = ashore.Crew.Single(c => c.CrewId == officer.Id);
        Assert.Equal((14, daysInMonth - 14, 2, 1), (o.Recorded, o.Missing, o.Breaches.Count, o.Unresolved));
        Assert.Equal((1, 15), (o.CompensatoryOutstanding, o.OwedMinutes));                                    // the emergency owes the rest the day fell short by
        Assert.Equal(BreachStatus.Excepted, o.Breaches.Single(b => b.Finding.Date == Day(10)).Status);
        Assert.Equal(BreachStatus.Unexplained, o.Breaches.Single(b => b.Finding.Date == Day(13)).Status);
        Assert.Equal(585, o.Worst24);                                                                         // 9 h 45
        var week1 = o.Periods.Single(p => p.From == Day(1));
        var week2 = o.Periods.Single(p => p.From == Day(8));
        Assert.True(week1.OutOfDate);                                                                         // both signatures were given for a day that has since changed
        Assert.False(week1.SeafarerSigned || week1.MasterSigned);
        Assert.True(week2.SeafarerSigned);
        Assert.False(week2.MasterSigned);
        var m = ashore.Crew.Single(c => c.CrewId == master.Id);
        Assert.True(m.Periods.Single(p => p.From == Day(1)).Complete);                                        // the Master's own first week is untouched
        Assert.False(m.Periods.Single(p => p.From == Day(8)).Complete);

        // The same breaches, with the same words, on both sides.
        var breachesBoard = await _vesselReview.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(31));
        var breachesShore = await _shoreReview.BreachesAsync(_s.CompanyId, _s.VesselId, Day(1), Day(31));
        Assert.Equal(breachesBoard.Select(b => (b.Key, b.Status, b.Notes.Select(n => (n.Kind, n.Text)).ToList())), breachesShore.Select(b => (b.Key, b.Status, b.Notes.Select(n => (n.Kind, n.Text)).ToList())));

        // The worklist task the emergency raised reached the office, with its link back to the note.
        var tasks = await _shoreWorklist.ListAsync(_s.CompanyId, _s.VesselId);
        Assert.Equal(task.Id, Assert.Single(tasks).Id);
        var link = Assert.Single(WorklistService.LinksOf((await _shoreWorklist.GetAsync(_s.CompanyId, task.Id))!.Entries));
        Assert.Equal((WorklistLinkTypes.RestBreach, emergency.Note.Id), (link.Type, link.Target));

        // Every export gives the same file from either side.
        var vesselName = (await _vesselRest.HeaderAsync(_s.CompanyId, _s.VesselId)).Vessel;
        Assert.Equal(RestReports.FleetCsv([onboard]), RestReports.FleetCsv([ashore]));
        Assert.Equal(RestReports.BreachesCsv([onboard]), RestReports.BreachesCsv([ashore]));
        Assert.Equal(RestReports.UnsignedCsv([onboard]), RestReports.UnsignedCsv([ashore]));
        Assert.Equal(RestReports.DailyCsv((await _vesselFleet.DailyAsync(_s.CompanyId, First.Year, First.Month)).Where(r => r.Vessel == vesselName)),
            RestReports.DailyCsv((await _shoreFleet.DailyAsync(_s.CompanyId, First.Year, First.Month)).Where(r => r.Vessel == vesselName)));

        // The printed record carries the flag state the office set, and is the same record from both sides.
        Assert.Equal("Singapore", (await _shoreRest.HeaderAsync(_s.CompanyId, _s.VesselId)).FlagState);
        Assert.Equal("Singapore", (await _vesselRest.HeaderAsync(_s.CompanyId, _s.VesselId)).FlagState);
        foreach (var (rest, review) in new[] { (_shoreRest, _shoreReview), (_vesselRest, _vesselReview) })
        {
            var pdf = RestSheetPdf.Build(new RestSheetDocument(await rest.HeaderAsync(_s.CompanyId, _s.VesselId),
                await rest.MonthAsync(_s.CompanyId, _s.VesselId, officer.Id, First.Year, First.Month), await review.MonthAsync(_s.CompanyId, _s.VesselId, officer.Id, First.Year, First.Month)));
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        }

        // ---- 8. The Master confirms the rest was given; the office sees it close ----
        await _vesselReview.ConfirmCompensatoryAsync(_s.CompanyId, _s.VesselId, As(master), officer.Id, Day(10), RestRule.Rest24, "An extra hour of rest on the 11th");
        await _s.SettleUntilQuietAsync();
        Assert.Equal(0, Assert.Single(await _shoreFleet.FleetAsync(_s.CompanyId, First.Year, First.Month, _s.VesselId)).CompensatoryOutstanding);
    }

    [Fact]
    public async Task TheVesselHoldsOnlyItsOwnCrewsHours_NotEvenAfterAFullBaseline()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.RestHours);
        await SwitchOnAsync(_s.OtherVesselId, PolicyModules.RestHours);
        var ours = await SignOnAsync("W7001", "Third Officer", _s.VesselId);
        await RecordAsync(ours, ours, 1, Normal);

        // Another vessel of the same company, with a crew member, a table and hours that the office holds.
        var theirs = await SignOnAsync("W7002", "Third Officer", _s.OtherVesselId);
        await _shoreTable.PublishVersionAsync(_s.CompanyId, _s.OtherVesselId, new ArrangementInput(First.AddDays(-30), "Other law", "", "", [new ArrangementRow("Third Officer", "", Normal, "", Normal, "")]), _s.User);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.RestEntries.Add(new RestEntry
            {
                Id = Guid.CreateVersion7(), CompanyId = _s.CompanyId, VesselId = _s.OtherVesselId, CrewMemberId = theirs.Id, CrewName = theirs.FullName, Rank = theirs.Rank,
                Date = Day(1), WorkSpans = Normal, EnteredByName = theirs.FullName, EnteredAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
        await _s.ShorePublisher.BaselineAsync(_s.VesselNode.SyncNodeId!);
        await _s.SettleUntilQuietAsync();

        await using var vessel = await _s.VesselFactory.CreateDbContextAsync();
        Assert.True(await vessel.RestEntries.IgnoreQueryFilters().AnyAsync(e => e.CrewMemberId == ours.Id));
        Assert.All(await vessel.RestEntries.IgnoreQueryFilters().ToListAsync(), e => Assert.Equal(_s.VesselId, e.VesselId));
        Assert.Empty(await vessel.WorkArrangementTables.IgnoreQueryFilters().Where(t => t.VesselId == _s.OtherVesselId).ToListAsync());
        Assert.Empty(await vessel.CrewMembers.IgnoreQueryFilters().Where(c => c.VesselId == _s.OtherVesselId).ToListAsync());
        Assert.Empty(await vessel.RestSignOffs.IgnoreQueryFilters().Where(s => s.VesselId == _s.OtherVesselId).ToListAsync());

        // The office does have both, and keeps them apart.
        var fleet = await _shoreFleet.FleetAsync(_s.CompanyId, First.Year, First.Month);
        Assert.Equal(2, fleet.Count);
        Assert.All(fleet, v => Assert.All(v.Crew, c => Assert.Equal(v.VesselId, c.VesselId)));
        Assert.Single(await _vesselFleet.FleetAsync(_s.CompanyId, First.Year, First.Month));
    }
}
