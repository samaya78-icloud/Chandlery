using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Certificates and surveys between the office and a vessel: the office sets them up, either side adds to the history, a scan travels as a record, and both sides end with the same picture.</summary>
public class CertificateTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private CertificateService _shore = null!, _vessel = null!;
    private DateTime _now = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Actor Office = new(null, "Superintendent");
    private static readonly Actor Crew = new(null, "Chief Engineer");

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new CertificateService(_s.ShoreFactory) { Clock = () => _now };
        _vessel = new CertificateService(_s.VesselFactory) { Clock = () => _now };
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.Pms, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private static async Task RefusedAsync(Func<Task> action) => await Assert.ThrowsAsync<InvalidOperationException>(action);

    private Task<ShipCertificate> AddLoadLineAsync(int dueInDays = 20)
        => _shore.AddAsync(_s.CompanyId, new NewCertificate(_s.VesselId, CertificateKind.Statutory, "International Load Line Certificate", "LL-123", "Flag", _now.AddYears(-4), _now.AddDays(dueInDays)), Office);

    private static string Line(CertificateRow r) => $"{r.Cert.Code}|{r.Cert.Title}|{r.Status.DueDate:yyyy-MM-dd}|{r.Status.State}|{r.EventCount}";

    [Fact]
    public async Task TheOfficeSetsACertificateUp_AndTheVesselReceivesIt()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync();
        Assert.Contains("-CRT-", cert.Code);
        await _s.SettleUntilQuietAsync();

        var onboard = Assert.Single(await _vessel.ListAsync(_s.CompanyId));
        Assert.Equal(cert.Id, onboard.Cert.Id);
        Assert.Equal(CertificateState.Due30, onboard.Status.State);
        Assert.Equal(SyncStages.Office, onboard.Cert.OwnerStage);
    }

    [Fact]
    public async Task TheVesselCannotSetUpOrChangeACertificate()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync();
        await _s.SettleUntilQuietAsync();

        await RefusedAsync(() => _vessel.AddAsync(_s.CompanyId, new NewCertificate(_s.VesselId, CertificateKind.Statutory, "Own certificate"), Crew));
        await RefusedAsync(() => _vessel.AddStandardSetAsync(_s.CompanyId, _s.VesselId, Crew));
        await RefusedAsync(() => _vessel.UpdateAsync(_s.CompanyId, cert.Id, new CertificateEdit("Renamed", "", "", null, null, 0, 0, null, null, "", true), Crew));
    }

    [Fact]
    public async Task TheVesselRecordsARenewal_AndTheOfficeSeesTheNewDate()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync(dueInDays: 20);
        await _s.SettleUntilQuietAsync();

        await _vessel.AddEventAsync(_s.CompanyId, cert.Id, new NewCertificateEvent(CertificateEventType.Issued, _now.AddDays(-1), _now.AddYears(5), "RO surveyor Tan", "Renewed at Singapore"), Crew);
        await _s.SettleUntilQuietAsync();

        var ashore = await _shore.GetAsync(_s.CompanyId, cert.Id);
        Assert.NotNull(ashore);
        Assert.Equal(CertificateState.Valid, ashore!.Status.State);
        Assert.Equal(_now.AddYears(5).Date, ashore.Status.DueDate);
        var e = Assert.Single(ashore.Events);
        Assert.Equal(("Chief Engineer", SyncStages.Vessel), (e.ByName, e.OwnerStage));
    }

    [Fact]
    public async Task TheOfficeExtendsACertificate_AndTheVesselSeesIt_WithTheAuthoritysReferenceRequired()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync(dueInDays: -2);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(CertificateState.Overdue, (await _vessel.GetAsync(_s.CompanyId, cert.Id))!.Status.State);

        await RefusedAsync(() => _shore.AddEventAsync(_s.CompanyId, cert.Id, new NewCertificateEvent(CertificateEventType.Extended, _now, _now.AddDays(30)), Office));
        await _shore.AddEventAsync(_s.CompanyId, cert.Id, new NewCertificateEvent(CertificateEventType.Extended, _now, _now.AddDays(30), "Flag letter 44/2026"), Office);
        await _s.SettleUntilQuietAsync();

        var onboard = (await _vessel.GetAsync(_s.CompanyId, cert.Id))!;
        Assert.Equal(CertificateState.Due30, onboard.Status.State);
        Assert.Equal(SyncStages.Office, Assert.Single(onboard.Events).OwnerStage);
    }

    [Fact]
    public async Task AScanTakenOnboardTravelsAsARecord_HeldOnTheVessel()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync();
        await _s.SettleUntilQuietAsync();

        var file = new CertificateFile { FileName = "load-line.pdf", ContentType = "application/pdf", SizeBytes = 1234, Sha256 = new string('a', 64), StoredKey = "k1", Caption = "Endorsed page" };
        await _vessel.AddFileAsync(_s.CompanyId, cert.Id, Crew, file);
        await _s.SettleUntilQuietAsync();

        var ashore = (await _shore.GetAsync(_s.CompanyId, cert.Id))!;
        var seen = Assert.Single(ashore.Files);
        Assert.Equal(("load-line.pdf", "Vessel"), (seen.FileName, seen.HeldBy));
        Assert.Equal(CertificateEventType.Document, Assert.Single(ashore.Events).Type);
    }

    [Fact]
    public async Task ACertificateKeepsAtMostTenFiles()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync();
        for (var i = 0; i < RecordFileRules.MaxPerRecord; i++)
            await _shore.AddFileAsync(_s.CompanyId, cert.Id, Office, new CertificateFile { FileName = $"scan{i}.pdf", ContentType = "application/pdf", SizeBytes = 10, Sha256 = new string('b', 64), StoredKey = $"k{i}" });
        await RefusedAsync(() => _shore.AddFileAsync(_s.CompanyId, cert.Id, Office, new CertificateFile { FileName = "one-more.pdf", ContentType = "application/pdf", SizeBytes = 10, Sha256 = new string('c', 64), StoredKey = "kx" }));
    }

    [Fact]
    public async Task HistoryIsNeverRewritten()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync();
        var e = await _shore.AddEventAsync(_s.CompanyId, cert.Id, new NewCertificateEvent(CertificateEventType.Note, _now, Note: "Stamp checked"), Office);

        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var row = await db.CertificateEvents.FirstAsync(x => x.Id == e.Id);
        row.Note = "Changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AnEventCannotBeInTheFuture_OrRenewToADateBeforeItself()
    {
        await SwitchOnAsync();
        var cert = await AddLoadLineAsync();
        await RefusedAsync(() => _shore.AddEventAsync(_s.CompanyId, cert.Id, new NewCertificateEvent(CertificateEventType.Note, _now.AddDays(2), Note: "later"), Office));
        await RefusedAsync(() => _shore.AddEventAsync(_s.CompanyId, cert.Id, new NewCertificateEvent(CertificateEventType.Issued, _now, _now.AddDays(-5)), Office));
    }

    [Fact]
    public async Task DatesFromABrowserFormHaveNoTimeZone_AndAreStoredAnyway()
    {
        await SwitchOnAsync();
        var unspecified = (DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified);
        var cert = await _shore.AddAsync(_s.CompanyId, new NewCertificate(_s.VesselId, CertificateKind.Statutory, "ISSC", IssuedOn: unspecified(_now.AddYears(-1)), DueDate: unspecified(_now.AddDays(200))), Office);
        await _shore.AddEventAsync(_s.CompanyId, cert.Id, new NewCertificateEvent(CertificateEventType.Issued, unspecified(_now), unspecified(_now.AddDays(25)), "RO"), Office);
        var detail = (await _shore.GetAsync(_s.CompanyId, cert.Id))!;
        Assert.Equal(CertificateState.Due30, detail.Status.State);
    }

    [Fact]
    public async Task TheStandardSetIsAddedOnce()
    {
        await SwitchOnAsync();
        var first = await _shore.AddStandardSetAsync(_s.CompanyId, _s.VesselId, Office);
        Assert.Equal(CertificateStarter.Standard.Count, first);
        Assert.Equal(0, await _shore.AddStandardSetAsync(_s.CompanyId, _s.VesselId, Office));
        await _s.SettleUntilQuietAsync();
        Assert.Equal(first, (await _vessel.ListAsync(_s.CompanyId)).Count);
    }

    [Fact]
    public async Task NothingWorksWhilePlannedMaintenanceIsOff()
    {
        await RefusedAsync(() => AddLoadLineAsync());
        Assert.Null(await _shore.CountsAsync(_s.CompanyId));
    }

    [Fact]
    public async Task BothSidesEndWithTheSamePicture_AndTheCountsFollowTheDates()
    {
        await SwitchOnAsync();
        var a = await AddLoadLineAsync(dueInDays: -3);
        var b = await _shore.AddAsync(_s.CompanyId, new NewCertificate(_s.VesselId, CertificateKind.Survey, "Class annual survey", DueDate: _now.AddDays(45), WindowBeforeDays: 90, WindowAfterDays: 90), Office);
        await _shore.AddEventAsync(_s.CompanyId, a.Id, new NewCertificateEvent(CertificateEventType.Note, _now, Note: "Surveyor attends Monday"), Office);
        await _s.SettleUntilQuietAsync();
        await _vessel.AddEventAsync(_s.CompanyId, b.Id, new NewCertificateEvent(CertificateEventType.SurveyDone, _now, _now.AddYears(1), "Surveyor Lim"), Crew);
        await _s.SettleUntilQuietAsync();

        var ashore = (await _shore.ListAsync(_s.CompanyId)).Select(Line).Order().ToList();
        var onboard = (await _vessel.ListAsync(_s.CompanyId)).Select(Line).Order().ToList();
        Assert.Equal(ashore, onboard);

        var counts = (await _shore.CountsAsync(_s.CompanyId))!;
        Assert.Equal((2, 1, 1), (counts.Total, counts.Overdue, counts.NeedingAttention));   // the load line is overdue; the survey, done, is a year away
    }
}
