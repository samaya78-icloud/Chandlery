using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Photos and statements kept with an incident report and a permit: the record travels, the bytes stay on the device that took them.</summary>
public class RecordFileTests : IAsyncLifetime
{
    private sealed class MemoryFiles : IFileStore
    {
        public readonly Dictionary<string, byte[]> Stored = new();
        public Task<string> SaveAsync(Guid companyId, Stream content, CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var key = $"{companyId}/{Guid.NewGuid():N}";
            Stored[key] = copy.ToArray();
            return Task.FromResult(key);
        }
        public Stream? OpenRead(string key) => Stored.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null;
        public void Delete(string key) => Stored.Remove(key);
    }

    private static readonly byte[] Photo = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5];

    private readonly TwoSites _s = new();
    private readonly MemoryFiles _shoreStore = new(), _vesselStore = new();
    private IncidentService _shoreIncidents = null!, _vesselIncidents = null!;
    private PermitService _shorePermits = null!, _vesselPermits = null!;
    private RecordFileService _shore = null!, _vessel = null!;
    private CrewMember _reporter = null!, _master = null!, _cook = null!, _chief = null!, _hand = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        var shoreWorklist = new WorklistService(_s.ShoreFactory);
        var vesselWorklist = new WorklistService(_s.VesselFactory);
        _shoreIncidents = new IncidentService(_s.ShoreFactory, shoreWorklist);
        _vesselIncidents = new IncidentService(_s.VesselFactory, vesselWorklist);
        _shorePermits = new PermitService(_s.ShoreFactory, shoreWorklist);
        _vesselPermits = new PermitService(_s.VesselFactory, vesselWorklist);
        _shore = new RecordFileService(_shoreStore, _shoreIncidents, _shorePermits, _s.ShoreFactory);
        _vessel = new RecordFileService(_vesselStore, _vesselIncidents, _vesselPermits, _s.VesselFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Incidents, PolicyModules.Worklist, PolicyModules.PermitToWork })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        _reporter = await SignOnAsync("F1001", "Third Officer");
        _master = await SignOnAsync("F1002", "Master", master: true);
        _cook = await SignOnAsync("F1003", "Cook");
        _chief = await SignOnAsync("F1004", "Chief Officer");
        _hand = await SignOnAsync("F1005", "Able Seaman");
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank, bool master = false)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false, master), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static PendingAttachment Upload(string name = "wrench.png", byte[]? data = null) => new() { FileName = name, Data = data ?? Photo };

    private async Task<Incident> SubmittedIncidentAsync()
    {
        var draft = await _vesselIncidents.SaveDraftAsync(_s.CompanyId, _s.VesselId, As(_reporter), null,
            new IncidentInput(IncidentKind.NearMiss, DateTime.UtcNow.AddHours(-3), "Engine room", IncidentShipStatus.AtSea, "Calm", "A wrench fell.", "Area cleared.", null, "Purifier", "", false, false, 0, 4, 3, []));
        var submitted = await _vesselIncidents.SubmitAsync(_s.CompanyId, draft.Id, As(_reporter));
        await _s.SettleUntilQuietAsync();
        return submitted;
    }

    [Fact]
    public async Task APhotoKeptWithAReport_TravelsAsARecord_WhileTheBytesStayOnTheVessel()
    {
        var report = await SubmittedIncidentAsync();
        var added = await _vessel.AddToIncidentAsync(_s.CompanyId, report.Id, As(_reporter), Upload(), " The wrench on the grating ");
        Assert.Equal("The wrench on the grating", added.Caption);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Photo)).ToLowerInvariant(), added.Sha256);
        Assert.Equal((Photo.LongLength, "image/png", "Vessel"), (added.SizeBytes, added.ContentType, added.HeldBy));
        await _s.SettleUntilQuietAsync();

        var atOffice = (await _shoreIncidents.GetAsync(_s.CompanyId, report.Id, _s.User))!.Incident.Files.Single();
        Assert.Equal((added.Id, "wrench.png", added.Sha256, "Vessel"), (atOffice.Id, atOffice.FileName, atOffice.Sha256, atOffice.HeldBy));

        var onboard = (await _vessel.OpenAsync(_s.CompanyId, "incident", report.Id, added.Id, As(_cook)))!.Value;
        Assert.NotNull(onboard.Content);
        using (var copy = new MemoryStream()) { await onboard.Content!.CopyToAsync(copy); Assert.Equal(Photo, copy.ToArray()); }

        var ashore = (await _shore.OpenAsync(_s.CompanyId, "incident", report.Id, added.Id, _s.User))!.Value;
        Assert.Equal("wrench.png", ashore.File.FileName);
        Assert.Null(ashore.Content);                                                                              // the file has not come ashore: the screen says where it is
        Assert.Empty(_shoreStore.Stored);
        Assert.Null(await _vessel.OpenAsync(_s.CompanyId, "incident", report.Id, Guid.NewGuid(), As(_reporter)));
        Assert.Null(await _vessel.OpenAsync(_s.CompanyId, "elsewhere", report.Id, added.Id, As(_reporter)));
    }

    [Fact]
    public async Task OnlyThoseWhoMayWriteTheReportKeepFilesWithIt_AndARefusedFileLeavesNothingBehind()
    {
        var report = await SubmittedIncidentAsync();
        Assert.Contains("reporter or the Master", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddToIncidentAsync(_s.CompanyId, report.Id, As(_cook), Upload()))).Message);
        Assert.Contains("onboard", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddToIncidentAsync(_s.CompanyId, report.Id, _s.User, Upload()))).Message);
        Assert.Contains("only images", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddToIncidentAsync(_s.CompanyId, report.Id, As(_reporter), Upload("run.exe")))).Message);
        Assert.Contains("empty", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddToIncidentAsync(_s.CompanyId, report.Id, As(_reporter), Upload(data: [])))).Message);
        Assert.Contains("300", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddToIncidentAsync(_s.CompanyId, report.Id, As(_reporter), Upload(), new string('x', 301)))).Message);
        Assert.Empty(_vesselStore.Stored);                                                                         // none of those left bytes in the store

        for (var i = 0; i < RecordFileRules.MaxPerRecord; i++) await _vessel.AddToIncidentAsync(_s.CompanyId, report.Id, As(_master), Upload($"p{i}.png"));
        Assert.Contains("up to 10", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddToIncidentAsync(_s.CompanyId, report.Id, As(_master), Upload("one-more.png")))).Message);
        Assert.Equal(RecordFileRules.MaxPerRecord, _vesselStore.Stored.Count);
    }

    [Fact]
    public async Task APhotoKeptWithAPermit_TravelsToo_ButNotOnAClosedPermit()
    {
        var aloft = (await _shorePermits.TypesAsync(_s.CompanyId)).Single(t => t.Name == "Working aloft");
        var permit = await _vesselPermits.RequestAsync(_s.CompanyId, _s.VesselId, As(_chief),
            new PermitInput(aloft.Id, "Foremast", "Paint the mast", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddHours(4), [_hand.Id], _cook.Id));
        await _s.SettleUntilQuietAsync();

        var added = await _vessel.AddToPermitAsync(_s.CompanyId, permit.Id, As(_chief), Upload("harness.png"), "Harness checked");
        await _s.SettleUntilQuietAsync();
        var atOffice = (await _shorePermits.GetAsync(_s.CompanyId, permit.Id, _s.User))!.Permit.Files.Single();
        Assert.Equal((added.Id, "harness.png", "Vessel"), (atOffice.Id, atOffice.FileName, atOffice.HeldBy));
        Assert.Null((await _shore.OpenAsync(_s.CompanyId, "permit", permit.Id, added.Id, _s.User))!.Value.Content);
        Assert.NotNull((await _vessel.OpenAsync(_s.CompanyId, "permit", permit.Id, added.Id, As(_hand)))!.Value.Content);

        Assert.Contains("person in charge", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddToPermitAsync(_s.CompanyId, permit.Id, As(_hand), Upload()))).Message);
        Assert.Contains("onboard", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.AddToPermitAsync(_s.CompanyId, permit.Id, _s.User, Upload()))).Message);

        await _vesselPermits.WithdrawAsync(_s.CompanyId, permit.Id, As(_chief), "Raised in error");
        Assert.Contains("closed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.AddToPermitAsync(_s.CompanyId, permit.Id, As(_chief), Upload()))).Message);
    }
}
