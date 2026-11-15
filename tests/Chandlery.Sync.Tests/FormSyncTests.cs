using System.Text.Json.Nodes;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Forms on a real office and a real vessel: the catalogue going down, an entry filled onboard and reviewed ashore, and what a vessel never holds.</summary>
public class FormSyncTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private FormService _shore = null!, _vessel = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new FormService(_s.ShoreFactory);
        _vessel = new FormService(_s.VesselFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.ShoreFactory.CreateDbContextAsync(); return await read(db); }
    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read) { await using var db = await _s.VesselFactory.CreateDbContextAsync(); return await read(db); }

    private async Task SwitchOnAsync(params Guid[] vessels)
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var vessel in vessels.Length == 0 ? [_s.VesselId] : vessels)
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = vessel, Module = PolicyModules.Forms, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private async Task<(FormType Type, FormVersion Version)> PublishedAsync(string code = "PM05", string? definition = null, Guid[]? vessels = null, bool officeOnly = false)
    {
        var type = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType(code, "Crankpin Bearings", "Technical", FormPeriodicity.Monthly, FormScheduleBasis.Calendar, Today.AddMonths(-3),
            Vessels: vessels, OfficeOnly: officeOnly), _s.User);
        var draft = await _shore.CreateDraftAsync(_s.CompanyId, type.Id, definition ?? FormSamples.Crankpin, "First version", _s.User);
        return (type, await _shore.PublishAsync(_s.CompanyId, draft.Id, _s.User));
    }

    private static JsonObject Fill(string note = "Within limits") => FormSamples.Values($$"""
        { "engineType": "7G60M", "draftFwd": 6.5, "draftAft": 9, "condition": "Bad", "pressure": 13,
          "measured": { "meas": { "b1.f": 0.55 } }, "note": "{{note}}" }
        """);

    [Fact]
    public async Task TheModuleIsOffUntilSwitchedOn_AndOnlyTheOfficeDesignsForms()
    {
        var (type, _) = await PublishedAsync();
        await _s.SettleUntilQuietAsync();
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User));
        Assert.Contains("not switched on", off.Message);

        await SwitchOnAsync();
        Assert.NotNull(await _shore.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User));

        foreach (var attempt in new Func<Task>[]
        {
            () => _vessel.CreateTypeAsync(_s.CompanyId, new NewFormType("X1", "Made onboard", "", FormPeriodicity.None), _s.User),
            () => _vessel.CreateDraftAsync(_s.CompanyId, type.Id, null, "", _s.User),
            () => _vessel.PublishAsync(_s.CompanyId, Guid.NewGuid(), _s.User),
        })
            Assert.Contains("done by the office", (await Assert.ThrowsAsync<InvalidOperationException>(attempt)).Message);
    }

    [Fact]
    public async Task ADraftNeverLeavesTheOffice_APublishedVersionReachesTheVessel_AndNeverChanges()
    {
        await SwitchOnAsync();
        var type = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("PM05", "Crankpin Bearings", "Technical", FormPeriodicity.Monthly, StartsOn: Today.AddMonths(-3)), _s.User);
        var draft = await _shore.CreateDraftAsync(_s.CompanyId, type.Id, FormSamples.Crankpin, "First", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.True(await OnVessel(db => db.FormTypes.AnyAsync(t => t.Id == type.Id)));              // the type is the catalogue
        Assert.False(await OnVessel(db => db.FormVersions.AnyAsync(v => v.Id == draft.Id)));         // the draft is not

        var published = await _shore.PublishAsync(_s.CompanyId, draft.Id, _s.User);
        await _s.SettleUntilQuietAsync();
        var held = await OnVessel(db => db.FormVersions.SingleAsync(v => v.Id == draft.Id));
        Assert.Equal((FormVersionStatus.Published, FormSamples.Crankpin, 1), (held.Status, held.Definition, held.Number));          // the same definition, word for word
        Assert.NotNull(published.PublishedAt);
    }

    [Fact]
    public async Task ADefinitionThatBreaksARuleCannotBePublished_AndSaysWhy()
    {
        var type = await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("BAD1", "Broken", "", FormPeriodicity.None), _s.User);
        var draft = await _shore.CreateDraftAsync(_s.CompanyId, type.Id, null, "", _s.User);
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.PublishAsync(_s.CompanyId, draft.Id, _s.User));
        Assert.Contains("no sections", empty.Message);

        await _shore.SaveDraftAsync(_s.CompanyId, draft.Id, FormSamples.Crankpin.Replace("draftAft - draftFwd", "draftAft - nothing"), "");
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.PublishAsync(_s.CompanyId, draft.Id, _s.User));
        Assert.Contains("no field or grid called nothing", missing.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveDraftAsync(_s.CompanyId, draft.Id, "{ not json", ""));
        Assert.Equal(FormVersionStatus.Draft, (await OnShore(db => db.FormVersions.SingleAsync(v => v.Id == draft.Id))).Status);
        Assert.Contains("already has a draft", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.CreateDraftAsync(_s.CompanyId, type.Id, null, "", _s.User))).Message);
    }

    [Fact]
    public async Task AnEntryIsFilledOnboardSubmittedReviewedAshoreReturnedCorrectedAndAccepted()
    {
        await SwitchOnAsync();
        var (type, version) = await PublishedAsync();
        await _s.SettleUntilQuietAsync();
        Assert.Equal(FormVersionStatus.Published, (await OnVessel(db => db.FormVersions.SingleAsync(v => v.Id == version.Id))).Status);

        // The vessel sees what is due and starts an entry, with no link needed to draw the form.
        var due = await _vessel.DueAsync(_s.CompanyId, _s.VesselId, DateOnly.FromDateTime(Today));
        Assert.Equal(type.Id, due.Single().Type.Id);
        Assert.Equal(FormDueStatus.Overdue, due.Single().Due.Status);                               // three months back and nothing filled
        var entry = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User);
        Assert.Equal((SyncStages.Vessel, 1, false), (entry.OwnerStage, entry.VersionNumber, entry.FilledAshore));
        Assert.Contains("-FRM-", entry.Code);

        var saved = await _vessel.SaveAsync(_s.CompanyId, entry.Id, FormSamples.Values("""{ "engineType": "7G60M", "draftFwd": 6.5, "draftAft": 9, "condition": "Bad", "measured": { "meas": { "b1.f": 0.55 } } }"""));
        Assert.True(saved.Ok);
        var incomplete = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, entry.Id, _s.User));
        Assert.Contains("Chief engineer is required", incomplete.Message);                          // the measurement showed the remarks, which are required

        await _vessel.SaveAsync(_s.CompanyId, entry.Id, Fill());
        var check = await _vessel.SubmitAsync(_s.CompanyId, entry.Id, _s.User);
        Assert.Equal(2, check.Exceptions.Count);                                                    // a Bad condition and a pressure over 12
        Assert.Equal(FormStatus.Submitted, (await _vessel.GetAsync(_s.CompanyId, entry.Id))!.Status);
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, entry.Id, Fill("changed")));
        Assert.Contains("cannot be changed until it is returned", locked.Message);
        await _s.SettleUntilQuietAsync();

        // The office has the very same entry, the same answers, the same calculation.
        var atShore = await OnShore(db => db.FormEntries.SingleAsync(e => e.Id == entry.Id));
        var atVessel = await OnVessel(db => db.FormEntries.SingleAsync(e => e.Id == entry.Id));
        Assert.Equal(atVessel.Values, atShore.Values);
        Assert.Equal((1, 2, version.Id), (atShore.SubmissionCount, atShore.ExceptionCount, atShore.VersionId));
        Assert.Equal(2.5m, JsonNode.Parse(atShore.Values)!["trim"]!.GetValue<decimal>());
        var def = FormSamples.Def(version.Definition);
        var recheck = FormValueRules.Check(def, JsonNode.Parse(atShore.Values) as JsonObject, DateOnly.FromDateTime(Today), true);
        Assert.True(recheck.Ok);
        Assert.Equal(atShore.Values, recheck.Clean.ToJsonString());                                  // what arrived passes as it is

        // Review: the vessel cannot do it; the office starts, returns with a reason, and the vessel sees it.
        Assert.Contains("done by the office", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Accepted, "", _s.User))).Message);
        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Started, "", _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Returned, " ", _s.User));   // a return needs a reason
        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Returned, "Pressure looks wrong, please recheck.", _s.User);
        await _s.SettleUntilQuietAsync();
        var returned = (await _vessel.GetAsync(_s.CompanyId, entry.Id))!;
        Assert.Equal(FormStatus.Returned, returned.Status);
        Assert.Equal("Pressure looks wrong, please recheck.", returned.Reviews.Last().Comment);

        // The vessel corrects and submits again; the earlier return no longer counts.
        await _vessel.SaveAsync(_s.CompanyId, entry.Id, FormSamples.Values("""{ "engineType": "7G60M", "draftFwd": 6.5, "draftAft": 9, "condition": "Good", "pressure": 11, "measured": { "meas": { "b1.f": 0.55 } }, "note": "Rechecked" }"""));
        Assert.Empty((await _vessel.SubmitAsync(_s.CompanyId, entry.Id, _s.User)).Exceptions);
        await _s.SettleUntilQuietAsync();
        Assert.Equal((FormStatus.Submitted, 2, 0), ((await _shore.GetAsync(_s.CompanyId, entry.Id))!.Status, (await OnShore(db => db.FormEntries.SingleAsync(e => e.Id == entry.Id))).SubmissionCount, (await OnShore(db => db.FormEntries.SingleAsync(e => e.Id == entry.Id))).ExceptionCount));

        await _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Accepted, "Thank you.", _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(FormStatus.Reviewed, (await _vessel.GetAsync(_s.CompanyId, entry.Id))!.Status);
        Assert.Contains("locked", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, entry.Id, Fill()))).Message);
        Assert.Contains("already been reviewed", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.ReviewAsync(_s.CompanyId, entry.Id, ReviewDecision.Accepted, "", _s.User))).Message);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());

        // The month is met, so the form is no longer overdue for this period, and a reviewed entry is corrected by a new one.
        var correction = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User, corrects: entry.Id);
        Assert.Equal(entry.Id, correction.CorrectsEntryId);
        Assert.Equal(JsonNode.Parse(atShore.Values)!["engineType"]!.ToString(), JsonNode.Parse(correction.Values)!["engineType"]!.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User, corrects: correction.Id));   // not reviewed yet
    }

    [Fact]
    public async Task AnEntryStaysCheckedAgainstTheVersionItWasFilledOn_WhenAnewVersionIsPublished()
    {
        await SwitchOnAsync();
        var (type, v1) = await PublishedAsync();
        await _s.SettleUntilQuietAsync();
        var oldEntry = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User);

        // Version 2 drops the required note and adds a field; version 1 is untouched.
        var v2Draft = await _shore.CopyAsync(_s.CompanyId, v1.Id, _s.User);
        Assert.Equal(2, v2Draft.Number);
        var changed = FormSamples.Crankpin.Replace("""{ "id": "note", "label": "Chief engineer", "type": "longtext", "required": true }""", """{ "id": "note", "label": "Chief engineer", "type": "longtext" }, { "id": "extra", "label": "Extra", "type": "text" }""");
        Assert.NotEqual(FormSamples.Crankpin, changed);
        await _shore.SaveDraftAsync(_s.CompanyId, v2Draft.Id, changed, "No required note");
        var v2 = await _shore.PublishAsync(_s.CompanyId, v2Draft.Id, _s.User);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.SaveDraftAsync(_s.CompanyId, v1.Id, changed, ""));        // a published version never changes
        await _s.SettleUntilQuietAsync();

        // The entry started before still needs the note; a new entry does not.
        await _vessel.SaveAsync(_s.CompanyId, oldEntry.Id, FormSamples.Values("""{ "engineType": "x", "measured": { "meas": { "b1.f": 0.55 } } }"""));
        Assert.Contains("Chief engineer is required", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SubmitAsync(_s.CompanyId, oldEntry.Id, _s.User))).Message);
        var newEntry = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today.AddDays(-1), _s.User);
        Assert.Equal(2, newEntry.VersionNumber);
        await _vessel.SaveAsync(_s.CompanyId, newEntry.Id, FormSamples.Values("""{ "engineType": "x", "measured": { "meas": { "b1.f": 0.55 } }, "extra": "ok" }"""));
        Assert.True((await _vessel.SubmitAsync(_s.CompanyId, newEntry.Id, _s.User)).Ok);

        // Retiring version 2 stops new entries on it (the form has no current version then), and the filled one stays readable.
        await _shore.RetireAsync(_s.CompanyId, v2.Id);
        await _shore.RetireAsync(_s.CompanyId, v1.Id);
        await _s.SettleUntilQuietAsync();
        Assert.Contains("no published version", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today.AddDays(-2), _s.User))).Message);
        Assert.NotNull((await _vessel.GetAsync(_s.CompanyId, newEntry.Id))!.Definition);
    }

    [Fact]
    public async Task AVesselHoldsOnlyTheFormsThatApplyToIt_AndNeverAnOfficeOnlyForm()
    {
        await SwitchOnAsync(_s.VesselId, _s.OtherVesselId);
        var (forThisVessel, _) = await PublishedAsync("PM05", vessels: [_s.VesselId]);
        var (forTheOther, otherVersion) = await PublishedAsync("PM06", vessels: [_s.OtherVesselId]);
        var (forEveryone, _) = await PublishedAsync("GEN1");
        var (shoreOnly, shoreVersion) = await PublishedAsync("SHORE1", officeOnly: true);
        await _s.SettleUntilQuietAsync();

        var types = await OnVessel(db => db.FormTypes.Select(t => t.Code).OrderBy(c => c).ToListAsync());
        Assert.Equal(["GEN1", "PM05"], types);
        Assert.False(await OnVessel(db => db.FormVersions.AnyAsync(v => v.Id == otherVersion.Id || v.Id == shoreVersion.Id)));
        Assert.Equal(["GEN1", "PM05"], (await _vessel.ApplicableAsync(_s.CompanyId, _s.VesselId)).Select(a => a.Type.Code).OrderBy(c => c));
        Assert.Contains("does not apply", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.StartAsync(_s.CompanyId, _s.VesselId, forTheOther.Id, Today, _s.User))).Message);

        // The office fills its own form for the vessel: it is held ashore only.
        var shoreEntry = await _shore.StartAsync(_s.CompanyId, _s.VesselId, shoreOnly.Id, Today, _s.User);
        var ashore = await _shore.StartAsync(_s.CompanyId, _s.VesselId, forEveryone.Id, Today, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Equal((SyncStages.Office, true, true), (shoreEntry.OwnerStage, shoreEntry.FilledAshore, shoreEntry.OfficeOnly));
        Assert.False(await OnVessel(db => db.FormEntries.AnyAsync(e => e.Id == shoreEntry.Id)));                  // an office-only form never leaves the office
        Assert.True(await OnVessel(db => db.FormEntries.AnyAsync(e => e.Id == ashore.Id)));                         // one the office fills for the vessel does arrive
        var refusedSave = (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.SaveAsync(_s.CompanyId, ashore.Id, Fill()))).Message;
        Assert.Contains("filled in by the office", refusedSave);
        Assert.Contains("Form not found", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, _s.VesselId, shoreOnly.Id, Today, _s.User))).Message);   // the vessel does not even hold it

        // Another vessel's entries never arrive.
        var otherEntry = await _shore.StartAsync(_s.CompanyId, _s.OtherVesselId, forTheOther.Id, Today, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.False(await OnVessel(db => db.FormEntries.AnyAsync(e => e.Id == otherEntry.Id)));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ChangingWhereAFormApplies_BringsItsPublishedVersionsIntoLine()
    {
        await SwitchOnAsync(_s.VesselId, _s.OtherVesselId);
        var (type, version) = await PublishedAsync("PM05", vessels: [_s.OtherVesselId]);
        await _s.SettleUntilQuietAsync();
        Assert.False(await OnVessel(db => db.FormTypes.AnyAsync(t => t.Id == type.Id)));

        await _shore.UpdateTypeAsync(_s.CompanyId, type.Id, new NewFormType("PM05", "Crankpin Bearings", "Technical", FormPeriodicity.Monthly, StartsOn: Today.AddMonths(-3), Vessels: [_s.OtherVesselId, _s.VesselId]), true, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.True(await OnVessel(db => db.FormTypes.AnyAsync(t => t.Id == type.Id)));
        Assert.True(await OnVessel(db => db.FormVersions.AnyAsync(v => v.Id == version.Id && v.Status == FormVersionStatus.Published)));
    }

    [Fact]
    public async Task TheCodeIsUnique_AndACalendarFormNeedsAStartDate()
    {
        await PublishedAsync("PM05");
        Assert.Contains("already a form", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("PM05", "Again", "", FormPeriodicity.None), _s.User))).Message);
        Assert.Contains("first period starts", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("PM07", "No start", "", FormPeriodicity.Weekly), _s.User))).Message);
        Assert.NotNull(await _shore.CreateTypeAsync(_s.CompanyId, new NewFormType("VY1", "Voyage report", "Operations", FormPeriodicity.Voyage), _s.User));       // a voyage form has no calendar
    }

    [Fact]
    public async Task TheListShowsTheStatusOfEveryEntry()
    {
        await SwitchOnAsync();
        var (type, _) = await PublishedAsync();
        await _s.SettleUntilQuietAsync();
        var draft = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today.AddDays(-5), _s.User);
        var sent = await _vessel.StartAsync(_s.CompanyId, _s.VesselId, type.Id, Today, _s.User);
        await _vessel.SaveAsync(_s.CompanyId, sent.Id, Fill());
        await _vessel.SubmitAsync(_s.CompanyId, sent.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        var rows = await _shore.ListAsync(_s.CompanyId, _s.VesselId, type.Id);
        Assert.Equal([FormStatus.Submitted, FormStatus.Draft], rows.Select(r => r.Status));              // newest form date first
        Assert.Equal([sent.Id, draft.Id], rows.Select(r => r.Entry.Id));
        Assert.Equal(FormStatus.Submitted, (await _shore.ListAsync(_s.CompanyId)).Single(r => r.Entry.Id == sent.Id).Status);

        // A submitted entry counts toward the period; the draft does not.
        var due = (await _vessel.DueAsync(_s.CompanyId, _s.VesselId, DateOnly.FromDateTime(Today))).Single();
        Assert.Equal((FormDueStatus.Done, 3), (due.Due.Status, due.Due.MissedPeriods));                  // this period is met; the three before it still have nothing
    }
}
