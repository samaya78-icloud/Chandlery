using System.Text.Json.Nodes;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The whole of forms, once, on a real office and a real vessel: a form is published, the vessel fills it with no link to shore, submits, the office reviews, raises a task
/// from an exception and returns it, the vessel corrects and resubmits, the office accepts, and a reviewed entry is corrected by a new one. The due list, the year view and
/// the fleet list agree throughout, and the vessel never holds a form that does not apply to it.
/// </summary>
public class FormWalkthroughTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private FormService _shore = null!, _vessel = null!;
    private FormReports _reports = null!, _vesselReports = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new FormService(_s.ShoreFactory);
        _vessel = new FormService(_s.VesselFactory);
        _reports = new FormReports(_s.ShoreFactory, _shore, new WorklistService(_s.ShoreFactory));
        _vesselReports = new FormReports(_s.VesselFactory, _vessel, new WorklistService(_s.VesselFactory));
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync(Guid vessel, params string[] modules)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in modules) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private static JsonObject Answers(string condition, decimal pressure, string note) => FormSamples.Values($$"""
        { "engineType": "7G60M", "draftFwd": 6.5, "draftAft": 9, "condition": "{{condition}}", "pressure": {{pressure}},
          "measured": { "meas": { "b1.f": 0.55 } }, "note": "{{note}}" }
        """);

    [Fact]
    public async Task FromPublishingToAnAcceptedAndCorrectedEntry_EverythingAgrees()
    {
        await SwitchOnAsync(_s.VesselId, PolicyModules.Forms, PolicyModules.Worklist);
        await SwitchOnAsync(_s.OtherVesselId, PolicyModules.Forms);
        var today = DateOnly.FromDateTime(Today);

        // 1. The office publishes a monthly form for this vessel only, and another for the other vessel, and one of its own.
        var type = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("PM05", "Crankpin Bearings", "Technical", FormPeriodicity.Monthly, FormScheduleBasis.Calendar, Today.AddMonths(-2), Vessels: [_s.VesselId]), _s.User);
        await _shore.PublishAsync(_s.CompanyId, (await _shore.CreateDraftAsync(_s.CompanyId, type.Id, FormSamples.Crankpin, "First", _s.User)).Id, _s.User);
        var elsewhere = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("PM09", "Not for this vessel", "Technical", FormPeriodicity.Monthly, FormScheduleBasis.Calendar, Today.AddMonths(-2), Vessels: [_s.OtherVesselId]), _s.User);
        await _shore.PublishAsync(_s.CompanyId, (await _shore.CreateDraftAsync(_s.CompanyId, elsewhere.Id, FormSamples.Crankpin, "", _s.User)).Id, _s.User);
        var own = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("TP01", "Office's own", "Technical", FormPeriodicity.ThirdParty, OfficeOnly: true), _s.User);
        await _shore.PublishAsync(_s.CompanyId, (await _shore.CreateDraftAsync(_s.CompanyId, own.Id, FormSamples.Crankpin, "", _s.User)).Id, _s.User);
        await _s.SettleUntilQuietAsync();

        // The vessel holds only what applies to it, and nothing of the other two.
        Assert.Equal([type.Id], (await OnVessel(db => db.FormVersions.Select(v => v.FormTypeId).ToListAsync())).Distinct());
        Assert.DoesNotContain(await OnVessel(db => db.FormTypes.Select(t => t.Code).ToListAsync()), code => code is "TP01");
        Assert.Equal(["PM05"], (await _vessel.DueAsync(_s.CompanyId, _s.VesselId, today)).Select(d => d.Type.Code));

        // 2. The vessel sees it overdue, starts it, and cannot submit what is incomplete.
        Assert.Equal(FormDueStatus.Overdue, (await _vessel.DueAsync(_s.CompanyId, _s.VesselId, today)).Single().Due.Status);
        Assert.Equal(1, (await _vessel.AttentionAsync(_s.CompanyId, today)));
        var entry = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User);
        await _vessel.SaveAsync(_s.CompanyId, entry.Id, FormSamples.Values("""{ "engineType": "7G60M", "measured": { "meas": { "b1.f": 0.55 } } }"""));
        Assert.Contains("Chief engineer is required", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, entry.Id, _s.User))).Message);

        // 3. Filled and submitted: two exceptions. The office has the same entry, and it is waiting.
        await _vessel.SaveAsync(_s.CompanyId, entry.Id, Answers("Bad", 13, "Bearing two is scored"));
        var check = await _vessel.SubmitAsync(_s.CompanyId, entry.Id, _s.User);
        Assert.Equal(2, check.Exceptions.Count);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(1, await _shore.AwaitingReviewAsync(_s.CompanyId));
        Assert.Equal([entry.Id], (await _reports.SearchAsync(_s.CompanyId, new FormFilter(AwaitingReview: true))).Select(r => r.Entry.Id));
        Assert.Equal((await OnVessel(db => db.FormEntries.SingleAsync(e => e.Id == entry.Id))).Values, (await _shore.GetAsync(_s.CompanyId, entry.Id))!.Entry.Values);

        // 4. The office starts the review, raises a task from an exception (it appears on the vessel too) and returns the form.
        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Started, "", _s.User);
        var task = await _reports.AddActionAsync(_s.CompanyId, entry.Id, new NewFormAction("Check bearing two", "Pressure 13 bar."), _s.User);
        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Returned, "Pressure looks wrong, recheck.", _s.User);
        Assert.Equal(0, await _shore.AwaitingReviewAsync(_s.CompanyId));
        await _s.SettleUntilQuietAsync();
        Assert.Equal([task.Id], (await _vesselReports.ActionsAsync(_s.CompanyId, entry.Id)).Select(t => t.Id));
        var back = (await _vessel.GetAsync(_s.CompanyId, entry.Id))!;
        Assert.Equal(FormStatus.Returned, back.Status);
        Assert.Equal("Pressure looks wrong, recheck.", back.Reviews.Last().Comment);

        // 5. The vessel corrects it and submits again: no exceptions now; the office accepts and it is locked everywhere.
        await _vessel.SaveAsync(_s.CompanyId, entry.Id, Answers("Good", 10, "Bearing two is scored"));
        Assert.Empty((await _vessel.SubmitAsync(_s.CompanyId, entry.Id, _s.User)).Exceptions);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(2, (await _shore.GetAsync(_s.CompanyId, entry.Id))!.Entry.SubmissionCount);
        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Accepted, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(FormStatus.Reviewed, (await _vessel.GetAsync(_s.CompanyId, entry.Id))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, entry.Id, Answers("Bad", 20, "tampered")));
        Assert.Equal(0, (await _shore.GetAsync(_s.CompanyId, entry.Id))!.Entry.ExceptionCount);

        // 6. A reviewed entry is corrected by a new one, which starts from its answers; the old one stays as it was.
        var correction = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User, corrects: entry.Id);
        Assert.Equal(entry.Id, correction.CorrectsEntryId);
        Assert.Contains("Bearing two is scored", correction.Values);
        await _vessel.SaveAsync(_s.CompanyId, correction.Id, Answers("Good", 10, "Bearing two is polished, not scored"));
        await _vessel.SubmitAsync(_s.CompanyId, correction.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        await _shore.ReviewAsync(_s.CompanyId, correction.Id, ReviewDecision.Accepted, "", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Contains("Bearing two is scored", (await _shore.GetAsync(_s.CompanyId, entry.Id))!.Entry.Values);

        // 7. The due list, the fleet list and the year view all say the same thing, on both sides.
        var vesselDue = (await _vessel.DueAsync(_s.CompanyId, _s.VesselId, today)).Single();
        var shoreDue = (await _shore.DueAsync(_s.CompanyId, _s.VesselId, today)).Single();
        var fleet = (await _reports.FleetDueAsync(_s.CompanyId, today)).Single(r => r.VesselId == _s.VesselId && r.Due.Type.Code == "PM05");
        Assert.Equal(vesselDue.Due, shoreDue.Due);
        Assert.Equal(shoreDue.Due, fleet.Due.Due);
        Assert.NotEqual(FormDueStatus.Overdue, vesselDue.Due.Status);                                            // this month is met
        Assert.Equal(2, vesselDue.Due.MissedPeriods);                                                              // the two before it were not
        var year = (await _reports.YearAsync(_s.CompanyId, _s.VesselId, Today.Year)).Single(r => r.Type.Code == "PM05");
        Assert.Equal(new FormYearCell(2, FormStatus.Reviewed), year.Months[Today.Month - 1]);
        Assert.Equal(0, (await _shore.CountsAsync(_s.CompanyId, today))!.ToReview);

        // 8. What goes out of the building: the list of entries, the file and the print.
        var (_, columns, rows) = (await _reports.ExportAsync(_s.CompanyId, type.Id))!.Value;
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "Bearing two is polished, not scored", "Bearing two is scored" }, rows.Select(r => r.Cells[columns.FindIndex(c => c.Key == "note")]).Order());
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(FormPdf.Build((await _shore.GetAsync(_s.CompanyId, entry.Id))!, "MV Sea Falcon", "Sea Co", await _reports.ActionsAsync(_s.CompanyId, entry.Id)), 0, 4));
        Assert.True(FormExport.Xlsx("PM05", columns, rows).Length > 1000);
    }
}
