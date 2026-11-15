using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class FormExportTests
{
    private static readonly string Answers = """
        { "engineType": "7G60M, port", "draftFwd": 6.5, "draftAft": 9, "trim": 2.5, "condition": "Bad", "pressure": -1,
          "measured": { "meas": { "b1.f": 0.55 }, "orig": { "b1.f": 0.5, "b2.a": 0.4 } }, "note": "=HYPERLINK(\"x\")" }
        """;

    [Fact]
    public void ThereIsOneColumnPerFieldAndPerGridCell_InTheOrderOfTheForm()
    {
        var columns = FormExport.Columns(FormSamples.Def());
        Assert.Equal(7 + 2 * 4 + 1, columns.Count);
        Assert.Equal(["engineType", "date", "draftFwd", "draftAft", "trim", "condition", "pressure"], columns.Take(7).Select(c => c.Key));
        Assert.Equal("Measured / Original / B1 F", columns[7].Heading);
        Assert.Equal(columns.Count, columns.Select(c => c.Heading).Distinct().Count());                    // no two columns are called the same
        Assert.Equal("measured.orig.b1.f", columns[7].Key);
        Assert.Equal("note", columns[^1].Key);
    }

    [Fact]
    public void ARowOfAFreeGridIsAColumnOnlyWhereSomeEntryHasIt()
    {
        var def = FormSamples.Def("""
            { "schema": 1, "title": "T", "sections": [ { "id": "g", "title": "Readings", "layout": "grid",
              "columns": [ { "id": "v", "label": "Value", "type": "number" } ], "rows": { "mode": "free" } } ] }
            """);
        Assert.Empty(FormExport.Columns(def));
        var seen = new[] { FormSamples.Values("""{ "g": { "r2": { "v": 1 }, "r1": { "v": 2 } } }"""), FormSamples.Values("""{ "g": { "r3": { "v": 3 } } }""") };
        Assert.Equal(["g.r1.v", "g.r2.v", "g.r3.v"], FormExport.Columns(def, seen).Select(c => c.Key));
    }

    [Fact]
    public void AnAnswerIsFoundByItsKey_EvenWhenTheCellIdHasADot()
    {
        var values = FormSamples.Values(Answers);
        Assert.Equal(0.4m, FormExport.Find(values, "measured.orig.b2.a")!.GetValue<decimal>());
        Assert.Equal("Bad", FormExport.Find(values, "condition")!.ToString());
        Assert.Null(FormExport.Find(values, "measured.orig.b2.f"));
        Assert.Null(FormExport.Find(values, "measured.nothing.b1.f"));
        Assert.Null(FormExport.Find(values, "nothing.at.all"));
    }

    [Fact]
    public void AnswersAreWrittenAsPeopleReadThem()
    {
        Assert.Equal("", FormExport.Text(null, FormFieldTypes.Text));
        Assert.Equal("6.5", FormExport.Text(JsonValue.Create(6.50m), FormFieldTypes.Number));
        Assert.Equal("Yes", FormExport.Text(JsonValue.Create(true), FormFieldTypes.Tick));
        Assert.Equal("No", FormExport.Text(JsonValue.Create(false), FormFieldTypes.Tick));
        Assert.Equal("A; B", FormExport.Text(new JsonArray("A", "B"), FormFieldTypes.MultiChoice));
        Assert.Equal("2 picture(s)", FormExport.Text(new JsonArray(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()), FormFieldTypes.Photo));
        Assert.Equal("A. Jones 2026-11-15T09:30", FormExport.Text(new JsonObject { ["name"] = "A. Jones", ["at"] = "2026-11-15T09:30" }, FormFieldTypes.Signature));
    }

    [Fact]
    public void TextASpreadsheetCouldRunAsAFormulaIsKeptAsText_ButANegativeNumberIsLeftAlone()
    {
        Assert.Equal("'=1+1", FormExport.Safe("=1+1"));
        Assert.Equal("'+cmd", FormExport.Safe("+cmd"));
        Assert.Equal("'@sum", FormExport.Safe("@sum"));
        Assert.Equal("'-cmd", FormExport.Safe("-cmd"));
        Assert.Equal("-12.5", FormExport.Safe("-12.5"));
        Assert.Equal("plain", FormExport.Safe("plain"));
        Assert.Equal("", FormExport.Safe(""));
    }

    [Fact]
    public void ACsvQuotesWhatNeedsIt_AndStartsWithTheFixedColumns()
    {
        var columns = FormExport.Columns(FormSamples.Def());
        var entry = new FormEntry { Code = "SF-FRM-2026-0001", FormDate = new DateTime(2026, 11, 3), Values = FormSamples.Values(Answers).ToJsonString(), VersionNumber = 2, ExceptionCount = 2 };
        var csv = FormExport.Csv(columns, [FormExport.Row(columns, entry, "MV, Sea Falcon", FormStatus.Reviewed)]);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Reference,Vessel,Date,Status,Version,Exceptions,Engine type,", lines[0]);
        Assert.StartsWith("SF-FRM-2026-0001,\"MV, Sea Falcon\",2026-11-03,Reviewed,2,2,\"7G60M, port\",", lines[1]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);                  // doubled quotes, and not a formula
        Assert.Contains(",-1,", lines[1]);                                           // a negative number stays a number
    }

    [Fact]
    public void AnExcelFileHasNumbersAsNumbers_AndTextAsText()
    {
        var columns = FormExport.Columns(FormSamples.Def());
        var entry = new FormEntry { Code = "X-1", FormDate = new DateTime(2026, 11, 3), Values = FormSamples.Values(Answers).ToJsonString(), VersionNumber = 1 };
        var bytes = FormExport.Xlsx("PM05", columns, [FormExport.Row(columns, entry, "MV Sea Falcon", FormStatus.Submitted)]);
        using var book = new XLWorkbook(new MemoryStream(bytes));
        var sheet = book.Worksheet(1);
        Assert.Equal("PM05", sheet.Name);
        Assert.Equal("Reference", sheet.Cell(1, 1).GetString());
        Assert.Equal("X-1", sheet.Cell(2, 1).GetString());
        var pressure = 7 + columns.FindIndex(c => c.Key == "pressure");
        Assert.Equal(XLDataType.Number, sheet.Cell(2, pressure).DataType);
        Assert.Equal(-1, sheet.Cell(2, pressure).GetDouble());
        var note = 6 + columns.Count;
        Assert.Equal(XLDataType.Text, sheet.Cell(2, note).DataType);
        Assert.False(sheet.Cell(2, note).HasFormula);
    }

    [Fact]
    public void TheYearViewCountsPerMonth_AndShowsTheLeastSettledState()
    {
        var months = FormYearView.Months(
        [
            (new DateTime(2026, 3, 1), FormStatus.Reviewed), (new DateTime(2026, 3, 9), FormStatus.Returned),
            (new DateTime(2026, 4, 2), FormStatus.Reviewed), (new DateTime(2026, 5, 2), FormStatus.Submitted), (new DateTime(2026, 5, 3), FormStatus.InReview),
            (new DateTime(2025, 3, 1), FormStatus.Draft),
        ], 2026);
        Assert.Equal(12, months.Length);
        Assert.Equal(new FormYearCell(0, null), months[0]);
        Assert.Equal(new FormYearCell(2, FormStatus.Returned), months[2]);
        Assert.Equal(new FormYearCell(1, FormStatus.Reviewed), months[3]);
        Assert.Equal(new FormYearCell(2, FormStatus.Submitted), months[4]);
    }
}

/// <summary>The office side of forms on a real office and vessel: finding entries, raising actions, files, the year and the counts.</summary>
public class FormReviewTests : IAsyncLifetime
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

    private async Task SwitchOnAsync(params string[] modules)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in modules) db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<FormType> PublishedAsync(string code = "PM05")
    {
        var type = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType(code, "Crankpin Bearings", "Technical", FormPeriodicity.Monthly, FormScheduleBasis.Calendar, Today.AddMonths(-3)), _s.User);
        var draft = await _shore.CreateDraftAsync(_s.CompanyId, type.Id, FormSamples.Crankpin, "First", _s.User);
        await _shore.PublishAsync(_s.CompanyId, draft.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        return type;
    }

    private async Task<FormEntry> SubmittedAsync(FormType type, DateTime date, string note = "Within limits")
    {
        var entry = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, date, _s.User);
        await _vessel.SaveAsync(_s.CompanyId, entry.Id, FormSamples.Values($$"""
            { "engineType": "7G60M", "draftFwd": 6.5, "draftAft": 9, "condition": "Bad", "pressure": 13, "measured": { "meas": { "b1.f": 0.55 } }, "note": "{{note}}" }
            """));
        await _vessel.SubmitAsync(_s.CompanyId, entry.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        return entry;
    }

    [Fact]
    public async Task TheOfficeFindsEntriesByVesselFormStateMonthAndAWordInTheAnswers_AndNeverSeesADraft()
    {
        await SwitchOnAsync(PolicyModules.Forms);
        var type = await PublishedAsync();
        var march = await SubmittedAsync(type, Today.AddDays(-40), "Bearing two is scored");
        var later = await SubmittedAsync(type, Today, "Nothing to report");
        var draft = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today.AddDays(-1), _s.User);
        await _s.SettleUntilQuietAsync();

        var all = await _reports.SearchAsync(_s.CompanyId, new FormFilter());
        Assert.Equal(new[] { march.Id, later.Id }.Order(), all.Select(r => r.Entry.Id).Order());                      // the draft is not shown
        Assert.DoesNotContain(all, r => r.Entry.Id == draft.Id);
        Assert.All(all, r => Assert.NotEqual("", r.Vessel));
        Assert.Equal(later.Id, all[0].Entry.Id);                                                                       // newest first
        Assert.Equal([march.Id], (await _reports.SearchAsync(_s.CompanyId, new FormFilter(Keyword: "SCORED"))).Select(r => r.Entry.Id));   // in the answers, whatever the case
        Assert.Equal([later.Id], (await _reports.SearchAsync(_s.CompanyId, new FormFilter(Keyword: later.Code))).Select(r => r.Entry.Id));
        Assert.Empty(await _reports.SearchAsync(_s.CompanyId, new FormFilter(Keyword: "no such words")));
        Assert.Empty(await _reports.SearchAsync(_s.CompanyId, new FormFilter(Keyword: "100%")));                      // a percent sign is a character, not a wildcard
        Assert.Equal(2, (await _reports.SearchAsync(_s.CompanyId, new FormFilter(TypeId: type.Id, Department: "Technical"))).Count);
        Assert.Empty(await _reports.SearchAsync(_s.CompanyId, new FormFilter(Department: "Deck")));
        Assert.Empty(await _reports.SearchAsync(_s.CompanyId, new FormFilter(VesselId: Guid.NewGuid())));
        Assert.Equal([later.Id], (await _reports.SearchAsync(_s.CompanyId, new FormFilter(Year: Today.Year, Month: Today.Month))).Select(r => r.Entry.Id));
        var before = Today.AddDays(-40);
        Assert.Equal([march.Id], (await _reports.SearchAsync(_s.CompanyId, new FormFilter(Year: before.Year, Month: before.Month))).Select(r => r.Entry.Id));

        // Waiting for review, then not once accepted.
        Assert.Equal(2, (await _reports.SearchAsync(_s.CompanyId, new FormFilter(AwaitingReview: true))).Count);
        await _shore.ReviewAsync(_s.CompanyId, march.Id, ReviewDecision.Accepted, "", _s.User);
        Assert.Equal([later.Id], (await _reports.SearchAsync(_s.CompanyId, new FormFilter(AwaitingReview: true))).Select(r => r.Entry.Id));
        Assert.Equal([march.Id], (await _reports.SearchAsync(_s.CompanyId, new FormFilter(Status: FormStatus.Reviewed))).Select(r => r.Entry.Id));
    }

    [Fact]
    public async Task TheWaitingCountFallsAsTheOfficeDecides()
    {
        await SwitchOnAsync(PolicyModules.Forms);
        var type = await PublishedAsync();
        Assert.Equal(0, await _shore.AwaitingReviewAsync(_s.CompanyId));
        var a = await SubmittedAsync(type, Today.AddDays(-2));
        var b = await SubmittedAsync(type, Today.AddDays(-1));
        Assert.Equal(2, await _shore.AwaitingReviewAsync(_s.CompanyId));
        Assert.Equal(2, (await _shore.CountsAsync(_s.CompanyId, DateOnly.FromDateTime(Today)))!.ToReview);

        await _shore.ReviewAsync(_s.CompanyId, a.Id, ReviewDecision.Started, "", _s.User);
        Assert.Equal(2, await _shore.AwaitingReviewAsync(_s.CompanyId));                                              // a started review is still waiting
        await _shore.ReviewAsync(_s.CompanyId, a.Id, ReviewDecision.Accepted, "", _s.User);
        await _shore.ReviewAsync(_s.CompanyId, b.Id, ReviewDecision.Returned, "Please recheck the pressure.", _s.User);
        Assert.Equal(0, await _shore.AwaitingReviewAsync(_s.CompanyId));                                              // returned is with the vessel
    }

    [Fact]
    public async Task AnActionRaisedFromAFormIsAWorklistTaskOnTheVessel_LinkedBack_AndOnlyTheOfficeRaisesIt()
    {
        await SwitchOnAsync(PolicyModules.Forms, PolicyModules.Worklist);
        var type = await PublishedAsync();
        var draft = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Contains("not been submitted", (await Assert.ThrowsAsync<InvalidOperationException>(() => _reports.AddActionAsync(_s.CompanyId, draft.Id, new NewFormAction("Check bearing"), _s.User))).Message);

        var entry = await SubmittedAsync(type, Today.AddDays(-1));
        Assert.Contains("raised by the office", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselReports.AddActionAsync(_s.CompanyId, entry.Id, new NewFormAction("Check bearing"), _s.User))).Message);
        Assert.Contains("title", (await Assert.ThrowsAsync<InvalidOperationException>(() => _reports.AddActionAsync(_s.CompanyId, entry.Id, new NewFormAction("  "), _s.User))).Message);
        Assert.Empty(await _reports.ActionsAsync(_s.CompanyId, entry.Id));

        var task = await _reports.AddActionAsync(_s.CompanyId, entry.Id, new NewFormAction("Check bearing two", "Pressure was 13 bar.", Target: Today.AddDays(7)), _s.User);
        Assert.Equal(_s.VesselId, task.VesselId);
        Assert.Contains("Pressure was 13 bar.", task.Description);
        Assert.Contains(entry.Code, task.Description);
        Assert.Equal([task.Id], (await _reports.ActionsAsync(_s.CompanyId, entry.Id)).Select(t => t.Id));
        Assert.Empty(await _reports.ActionsAsync(_s.CompanyId, draft.Id));
        Assert.Equal($"/forms/entry/{entry.Id}", WorklistLinkTypes.Href(WorklistLinkTypes.Form, entry.Id, false));

        await _s.SettleUntilQuietAsync();                                                                              // the vessel sees the task and its link
        Assert.Equal([task.Id], (await _vesselReports.ActionsAsync(_s.CompanyId, entry.Id)).Select(t => t.Id));
    }

    [Fact]
    public async Task NoTaskIsMadeWhereTheWorklistIsOff()
    {
        await SwitchOnAsync(PolicyModules.Forms);
        var type = await PublishedAsync();
        var entry = await SubmittedAsync(type, Today);
        Assert.Contains("worklist is not switched on", (await Assert.ThrowsAsync<InvalidOperationException>(() => _reports.AddActionAsync(_s.CompanyId, entry.Id, new NewFormAction("Check bearing"), _s.User))).Message);
        Assert.Empty(await _reports.ActionsAsync(_s.CompanyId, entry.Id));
    }

    [Fact]
    public async Task TheExportHasOneColumnPerFieldAndOneRowPerSubmittedEntry_AcrossVersions()
    {
        await SwitchOnAsync(PolicyModules.Forms);
        var type = await PublishedAsync();
        await SubmittedAsync(type, Today.AddDays(-2), "Bearing two is scored");
        await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today.AddDays(-1), _s.User);                       // a draft is not exported
        await _s.SettleUntilQuietAsync();

        // A second version adds a field; the old entry keeps its answers and the table covers both versions.
        var draftV2 = await _shore.CreateDraftAsync(_s.CompanyId, type.Id,
            FormSamples.Crankpin.Replace("""{ "id": "engineType", "label": "Engine type", "type": "text", "required": true },""", """{ "id": "engineType", "label": "Engine type", "type": "text", "required": true }, { "id": "port", "label": "Port", "type": "text" },"""), "Second", _s.User);
        await _shore.PublishAsync(_s.CompanyId, draftV2.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.DoesNotContain((await _reports.ExportAsync(_s.CompanyId, type.Id))!.Value.Columns, c => c.Key == "port");   // no entry was filled on it yet
        await SubmittedAsync(type, Today, "Nothing to report");

        var (exported, columns, rows) = (await _reports.ExportAsync(_s.CompanyId, type.Id))!.Value;
        Assert.Equal(type.Id, exported.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal([1, 2], rows.Select(r => r.Version));                                                              // oldest first
        Assert.Contains(columns, c => c.Key == "note" && c.Heading == "Chief engineer");
        Assert.Contains(columns, c => c.Key == "measured.meas.b1.f");
        Assert.Contains(columns, c => c.Key == "port");
        Assert.Equal("", rows[0].Cells[columns.FindIndex(c => c.Key == "port")]);                                        // asked later, not answered then
        Assert.Equal(columns.Count, rows[0].Cells.Count);
        Assert.Equal("Bearing two is scored", rows[0].Cells[columns.FindIndex(c => c.Key == "note")]);
        Assert.Equal("13", rows[0].Cells[columns.FindIndex(c => c.Key == "pressure")]);
        Assert.Equal((1, "Submitted", 2), (rows[0].Version, rows[0].Status, rows[0].Exceptions));
        Assert.Contains("Bearing two is scored", FormExport.Csv(columns, rows));

        Assert.Empty((await _reports.ExportAsync(_s.CompanyId, type.Id, Guid.NewGuid()))!.Value.Rows);
        Assert.Empty((await _reports.ExportAsync(_s.CompanyId, type.Id, year: Today.Year - 3))!.Value.Rows);
        Assert.Null(await _reports.ExportAsync(_s.CompanyId, Guid.NewGuid()));
    }

    [Fact]
    public async Task TheFleetDueListPutsWhatIsOverdueFirst_ShowsFormsStartedByHand_AndTheOfficesOwnForms()
    {
        await SwitchOnAsync(PolicyModules.Forms);
        var monthly = await PublishedAsync("PM05");
        var voyage = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("VT01", "Change of command", "Deck", FormPeriodicity.Voyage), _s.User);
        await _shore.PublishAsync(_s.CompanyId, (await _shore.CreateDraftAsync(_s.CompanyId, voyage.Id, FormSamples.Crankpin, "", _s.User)).Id, _s.User);
        var office = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("TP01", "Class survey", "Technical", FormPeriodicity.ThirdParty, OfficeOnly: true), _s.User);
        await _shore.PublishAsync(_s.CompanyId, (await _shore.CreateDraftAsync(_s.CompanyId, office.Id, FormSamples.Crankpin, "", _s.User)).Id, _s.User);
        await _s.SettleUntilQuietAsync();
        var today = DateOnly.FromDateTime(Today);

        var fleet = await _reports.FleetDueAsync(_s.CompanyId, today);
        Assert.Equal(new[] { "PM05", "TP01", "VT01" }, fleet.Select(r => r.Due.Type.Code).Order());
        Assert.Equal("PM05", fleet[0].Due.Type.Code);                                                       // overdue first
        Assert.Equal(FormDueStatus.Overdue, fleet[0].Due.Due.Status);
        Assert.All(fleet.Where(r => r.Due.Type.Code != "PM05"), r => Assert.Equal(FormDueStatus.NotScheduled, r.Due.Due.Status));
        Assert.All(fleet, r => Assert.Equal(_s.VesselId, r.VesselId));

        // The vessel never sees the office's own form; the plain due list leaves it out too.
        Assert.DoesNotContain(await _vessel.DueAsync(_s.CompanyId, _s.VesselId, today), r => r.Type.Code == "TP01");
        Assert.DoesNotContain(await _shore.DueAsync(_s.CompanyId, _s.VesselId, today), r => r.Type.Code == "TP01");
        Assert.Contains(await _shore.DueAsync(_s.CompanyId, _s.VesselId, today, includeOfficeOnly: true), r => r.Type.Code == "TP01");

        // The office starts a form for the vessel by hand; it is marked as filled ashore, and a voyage form needs no schedule.
        var byHand = await _shore.StartAsync(_s.CompanyId, _s.VesselId, voyage.Id, Today, _s.User);
        Assert.True(byHand.FilledAshore);
        var own = await _shore.StartAsync(_s.CompanyId, _s.VesselId, office.Id, Today, _s.User);
        Assert.True(own.OfficeOnly);
        await _s.SettleUntilQuietAsync();
        Assert.False(await OnVessel(db => db.FormEntries.AnyAsync(e => e.Id == own.Id)));                  // it never leaves the office
        Assert.True(await OnVessel(db => db.FormEntries.AnyAsync(e => e.Id == byHand.Id)));

        // A submitted monthly form for this month moves it down the list.
        await SubmittedAsync(monthly, Today);
        var after = await _reports.FleetDueAsync(_s.CompanyId, today);
        Assert.NotEqual(FormDueStatus.Overdue, after.Single(r => r.Due.Type.Code == "PM05").Due.Due.Status);
    }

    [Fact]
    public async Task AConvertedLegacyFormIsPublishedLikeAnyOther_AndItsAnswersGoIntoAnEntryByTheSameIds()
    {
        await SwitchOnAsync(PolicyModules.Forms);
        var conversion = FormHtmlConverter.Convert(FormConverterTests.EngineReport);
        var type = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("PM05", "Crankpin Bearings", "Technical", FormPeriodicity.Monthly, FormScheduleBasis.Calendar, Today.AddMonths(-3)), _s.User);
        var draft = await _shore.CreateDraftAsync(_s.CompanyId, type.Id, conversion.Definition.ToJson(), "Converted from PM05.html", _s.User);
        await _shore.PublishAsync(_s.CompanyId, draft.Id, _s.User);                                                  // the converted draft passes the same publishing rules
        await _s.SettleUntilQuietAsync();

        var (entry, check) = await _reports.ImportAsync(_s.CompanyId, _s.VesselId, type.Id, new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc), (JsonObject)conversion.Values.DeepClone(), _s.User);
        Assert.Empty(check.Problems);
        Assert.True(entry.FilledAshore);
        var saved = (await _shore.GetAsync(_s.CompanyId, entry.Id))!;
        var values = JsonNode.Parse(saved.Entry.Values)!;
        Assert.Equal("7G60M-CGIE-HPSCR", values["engineType"]!.ToString());
        Assert.Equal(0.55m, values["tblMeasured"]!["measured"]!["bearing1.f"]!.GetValue<decimal>());
        Assert.Equal(FormStatus.Draft, saved.Status);
    }

    [Fact]
    public async Task TheYearViewShowsWhatWasFilledEachMonthForTheFormsThatApply()
    {
        await SwitchOnAsync(PolicyModules.Forms);
        var type = await PublishedAsync();
        var first = new DateTime(Today.Year, Today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = await SubmittedAsync(type, first);
        var year = await _reports.YearAsync(_s.CompanyId, _s.VesselId, Today.Year);
        var row = year.Single();
        Assert.Equal(type.Id, row.Type.Id);
        Assert.Equal(new FormYearCell(1, FormStatus.Submitted), row.Months[Today.Month - 1]);
        Assert.Equal(1, row.Months.Sum(m => m.Count));

        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Accepted, "", _s.User);
        Assert.Equal(FormStatus.Reviewed, (await _reports.YearAsync(_s.CompanyId, _s.VesselId, Today.Year)).Single().Months[Today.Month - 1].Worst);
        Assert.Equal(0, (await _reports.YearAsync(_s.CompanyId, _s.VesselId, Today.Year - 1)).Single().Months.Sum(m => m.Count));
    }

    [Fact]
    public async Task AFormEntryPrintsAsAPdf_WithItsReviewAndActions()
    {
        await SwitchOnAsync(PolicyModules.Forms, PolicyModules.Worklist);
        var type = await PublishedAsync();
        var entry = await SubmittedAsync(type, Today);
        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Returned, "Please recheck the pressure.", _s.User);
        var task = await _reports.AddActionAsync(_s.CompanyId, entry.Id, new NewFormAction("Check bearing two"), _s.User);
        var detail = (await _shore.GetAsync(_s.CompanyId, entry.Id))!;

        var pdf = FormPdf.Build(detail, "MV Sea Falcon", "Sea Co", await _reports.ActionsAsync(_s.CompanyId, entry.Id));
        Assert.True(pdf.Length > 1500);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal($"form-{entry.Code}.pdf", FormPdf.FileName(detail.Entry));
        Assert.NotEqual(Guid.Empty, task.Id);
    }
}
