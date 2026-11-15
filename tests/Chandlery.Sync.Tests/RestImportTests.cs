using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Hours of rest history brought in from a CSV: the plan (pure), then the Master importing onboard with the office seeing the same days.</summary>
public class RestImportTests : IAsyncLifetime
{
    private static readonly DateTime Today = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Ravi = Guid.NewGuid(), Anil = Guid.NewGuid(), Dup1 = Guid.NewGuid(), Dup2 = Guid.NewGuid();
    private static readonly RestImportCrew[] Crew = [new(Ravi, "Ravi Singh"), new(Anil, "Anil Kumar"), new(Dup1, "Same Name"), new(Dup2, "same name")];

    private static IReadOnlyDictionary<(Guid, DateTime), string> None => new Dictionary<(Guid, DateTime), string>();

    private static RestImportPlan Plan(string csv, IReadOnlyDictionary<(Guid, DateTime), string>? existing = null) => RestImport.Plan(csv, Crew, existing ?? None, Today);

    private const string Header = "Seafarer,Date,Work periods\n";

    // ---- the plan ----

    [Fact]
    public void NewSameAndCorrectedDaysAreTold_AndNothingIsWritten()
    {
        var existing = new Dictionary<(Guid, DateTime), string>
        {
            [(Ravi, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc))] = "00:00-04:00,16:00-20:00",
            [(Ravi, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc))] = "08:00-12:00",
        };
        var plan = Plan(Header + "Ravi Singh,2026-10-01,00:00-04:00\nRavi Singh,2026-10-02,\"16:00-20:00, 00:00-04:00\"\nRavi Singh,2026-10-03,08:00-13:00\nanil kumar,2026-10-01,", existing);
        Assert.Null(plan.FileProblem);
        Assert.Equal([RestImportStatus.New, RestImportStatus.Same, RestImportStatus.Correction, RestImportStatus.New], plan.Rows.Select(r => r.Status));
        Assert.Equal("00:00-04:00,16:00-20:00", plan.Rows[1].Spans);                                               // spans are put in order before they are compared
        Assert.Equal("", plan.Rows[3].Spans);                                                                     // a day without work is a day of rest, and valid
        Assert.Equal(Anil, plan.Rows[3].CrewId);                                                                  // names match without regard to case
        Assert.Equal(3, plan.ToRecord.Count());
    }

    [Fact]
    public void RowsThatCannotBeImportedSayWhy_AndTheRestStillGo()
    {
        var plan = Plan(Header
            + "Nobody Here,2026-10-01,00:00-04:00\n"
            + "Ravi Singh,not a date,00:00-04:00\n"
            + "Ravi Singh,2026-12-25,00:00-04:00\n"                                                              // has not happened yet
            + "Ravi Singh,2026-10-05,00:00-99:00\n"                                                              // not a time
            + "Ravi Singh,2026-10-06,00:00-12:00,13:00-06:00\n"                                                  // overlapping, and an extra cell
            + "Same Name,2026-10-07,00:00-04:00\n"                                                               // two seafarers with the name
            + "Ravi Singh,2026-10-08,00:00-04:00\n"
            + "Ravi Singh,2026-10-08,00:00-05:00\n");                                                            // the same day twice
        Assert.Equal(
            [RestImportStatus.Problem, RestImportStatus.Problem, RestImportStatus.Problem, RestImportStatus.Problem, RestImportStatus.Problem, RestImportStatus.Problem, RestImportStatus.New, RestImportStatus.Problem],
            plan.Rows.Select(r => r.Status));
        Assert.Contains("No seafarer of that name", plan.Rows[0].Problem);
        Assert.Contains("not understood", plan.Rows[1].Problem);
        Assert.Contains("not happened yet", plan.Rows[2].Problem);
        Assert.Contains("Two seafarers", plan.Rows[5].Problem);
        Assert.Contains("twice", plan.Rows[7].Problem);
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9], plan.Rows.Select(r => r.Line));                                  // line numbers are the file's, counting the header
        Assert.Single(plan.ToRecord);
    }

    [Fact]
    public void TheFileNeedsItsColumns_AndHeadersMayBeNamedAsTheExportNamesThem()
    {
        Assert.Equal("The file is empty.", Plan("").FileProblem);
        Assert.Contains("must name the columns", Plan("Who,When,What\nRavi Singh,2026-10-01,00:00-04:00").FileProblem);
        var named = Plan("name,DATE,Work Spans,Rank\nRavi Singh,01 Oct 2026,00:00-04:00,Third Officer");
        Assert.Null(named.FileProblem);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), named.Rows.Single().Date);
        Assert.Equal(RestImportStatus.New, named.Rows.Single().Status);
    }

    [Fact]
    public void TheDailyExportCanBeReadBack()
    {
        var rows = new[]
        {
            new RestDailyRow("MV Test", "Ravi Singh", "Third Officer", new DateTime(2026, 10, 1), "00:00-04:00,16:00-20:00", 480, 960, 600, 4000, RestDayState.Ok, ""),
            new RestDailyRow("MV Test", "Anil Kumar", "Chief Officer", new DateTime(2026, 10, 1), "", 0, 1440, 1440, 4000, RestDayState.Ok, ""),
        };
        var plan = Plan(RestReports.DailyCsv(rows));
        Assert.Null(plan.FileProblem);
        Assert.Equal([RestImportStatus.New, RestImportStatus.New], plan.Rows.Select(r => r.Status));
        Assert.Equal(["00:00-04:00,16:00-20:00", ""], plan.Rows.Select(r => r.Spans));
    }

    [Fact]
    public void ALargeFileIsRefused()
    {
        var csv = Header + string.Join("\n", Enumerable.Range(0, RestImport.MaxRows + 1).Select(i => $"Ravi Singh,2026-01-01,"));
        Assert.Contains("more than", Plan(csv).FileProblem);
    }

    // ---- onboard ----

    private readonly TwoSites _s = new();
    private RestHoursService _shoreHours = null!, _vesselHours = null!;
    private RestImportService _import = null!;
    private CrewMember _master = null!, _admin = null!, _hand = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreHours = new RestHoursService(_s.ShoreFactory);
        _vesselHours = new RestHoursService(_s.VesselFactory);
        _import = new RestImportService(_s.VesselFactory, _vesselHours);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task OnboardSetupAsync()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RestHours, Enabled = true });
            await db.SaveChangesAsync();
        }
        _master = await SignOnAsync("H1001", "Master", master: true);
        _admin = await SignOnAsync("H1002", "Chief Officer", admin: true);
        _hand = await SignOnAsync("H1003", "Able Seaman");
    }

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool admin = false, bool master = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, admin, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    [Fact]
    public async Task TheMasterImportsHistory_OnceOnly_AndTheOfficeSeesTheSameDays()
    {
        await OnboardSetupAsync();
        var csv = Header
            + $"{_hand.FullName},2026-10-01,00:00-04:00\n"
            + $"{_hand.FullName},2026-10-02,\"08:00-12:00,13:00-17:00\"\n"
            + $"{_admin.FullName},2026-10-01,\n"
            + "Nobody Aboard,2026-10-01,00:00-04:00\n";

        var preview = await _import.PreviewAsync(_s.CompanyId, _s.VesselId, As(_master), csv);
        Assert.Equal((3, 1), (preview.Count(RestImportStatus.New), preview.Count(RestImportStatus.Problem)));
        Assert.Empty(await RestEntriesAsync(_s.VesselFactory));                                                   // a preview writes nothing

        var (plan, recorded) = await _import.ApplyAsync(_s.CompanyId, _s.VesselId, As(_master), csv);
        Assert.Equal(3, recorded);
        Assert.Equal(1, plan.Count(RestImportStatus.Problem));
        await _s.SettleUntilQuietAsync();
        var onboard = await RestEntriesAsync(_s.VesselFactory);
        Assert.Equal(onboard, await RestEntriesAsync(_s.ShoreFactory));                                           // the office holds the same days
        Assert.Equal(3, onboard.Count);
        Assert.All(onboard, line => Assert.EndsWith("|" + RestImportService.Reason, line));                       // each day says where it came from

        var again = await _import.ApplyAsync(_s.CompanyId, _s.VesselId, As(_master), csv);
        Assert.Equal(0, again.Recorded);                                                                          // the same file twice changes nothing
        Assert.Equal(3, again.Plan.Count(RestImportStatus.Same));                                                 // including the day without work
        Assert.Equal(3, (await RestEntriesAsync(_s.VesselFactory)).Count);
    }

    [Fact]
    public async Task ADifferentDayGetsACorrectionBesideTheFirst_AndOnlyTheMasterOrACrewAdminMayImport()
    {
        await OnboardSetupAsync();
        await _import.ApplyAsync(_s.CompanyId, _s.VesselId, As(_admin), Header + $"{_hand.FullName},2026-10-01,00:00-04:00\n");
        var (plan, recorded) = await _import.ApplyAsync(_s.CompanyId, _s.VesselId, As(_admin), Header + $"{_hand.FullName},2026-10-01,00:00-06:00\n");
        Assert.Equal(1, recorded);
        Assert.Equal(RestImportStatus.Correction, plan.Rows.Single().Status);
        var entries = await RestEntriesAsync(_s.VesselFactory);
        Assert.Equal(2, entries.Count);                                                                           // nothing was overwritten
        Assert.Contains(entries, e => e.Contains("00:00-06:00") && e.Contains(RestImportService.Reason));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _import.PreviewAsync(_s.CompanyId, _s.VesselId, As(_hand), Header + $"{_hand.FullName},2026-10-02,00:00-04:00\n"));
        Assert.Contains("Master or a crew admin", refused.Message);
        var office = await Assert.ThrowsAsync<InvalidOperationException>(() => new RestImportService(_s.ShoreFactory, _shoreHours).PreviewAsync(_s.CompanyId, _s.VesselId, _s.User, Header));
        Assert.Contains("onboard", office.Message);
    }

    private static async Task<List<string>> RestEntriesAsync(Microsoft.EntityFrameworkCore.IDbContextFactory<Chandlery.Data.ChandleryDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.RestEntries.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        return rows.OrderBy(e => e.CrewName).ThenBy(e => e.Date).ThenBy(e => e.EnteredAt).Select(e => $"{e.CrewName}|{e.Date:yyyy-MM-dd}|{e.WorkSpans}|{e.Reason}").ToList();
    }
}
