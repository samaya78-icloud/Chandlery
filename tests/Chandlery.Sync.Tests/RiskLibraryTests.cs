using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

public class RiskToneTests
{
    [Fact]
    public void BandsGoFromGreenToRed_HoweverManyTheCompanyHas()
    {
        var m = RiskMatrix.Default;
        Assert.Equal(["success", "neutral", "warning", "danger", "danger"], new[] { "Low", "Medium", "Medium High", "High", "Extreme" }.Select(m.ToneOf));
        Assert.Equal("neutral", m.ToneOf("Unknown band"));

        var three = new RiskMatrix([("Acceptable", 1), ("Tolerable", 8), ("Intolerable", 16)]);
        Assert.Equal(["success", "warning", "danger"], new[] { "Acceptable", "Tolerable", "Intolerable" }.Select(three.ToneOf));
        Assert.Equal("success", new RiskMatrix([("Only", 1)]).ToneOf("Only"));
    }
}

/// <summary>The office's risk library on a real office and a real vessel: what can be changed, by whom, and what reaches the ship.</summary>
public class RiskLibraryTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private RiskLibraryService _shoreLibrary = null!, _vesselLibrary = null!;
    private RiskAssessmentService _vessel = null!;
    private static DateTime Today => DateTime.UtcNow.Date;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shoreLibrary = new RiskLibraryService(_s.ShoreFactory);
        _vesselLibrary = new RiskLibraryService(_s.VesselFactory);
        _vessel = new RiskAssessmentService(_s.VesselFactory);
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task SwitchOnAsync()
    {
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = PolicyModules.RiskAssessment, Enabled = true });
            await db.SaveChangesAsync();
        }
        await _s.SettleUntilQuietAsync();
    }

    private static RaTemplateHazardInput Hazard(Guid? id, string name, int cb = 4, int pb = 4, int ca = 2, int pa = 2) => new(id, name, "Burns", "Fire watch", "", cb, pb, ca, pa);

    [Fact]
    public async Task CategoriesAreCheckedAndKeptByTheOffice_AndReachTheVessel()
    {
        Assert.Equal(8, (await _shoreLibrary.ListCategoriesAsync(_s.CompanyId)).Count);
        Assert.Equal("The risk assessment library is maintained by the office.", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselLibrary.SaveCategoryAsync(_s.CompanyId, null, "Diving", "DIV", 9))).Message);

        var dive = await _shoreLibrary.SaveCategoryAsync(_s.CompanyId, null, " Diving ", " div ", 9);
        Assert.Equal(("Diving", "DIV"), (dive.Name, dive.Code));                                      // trimmed, code in capitals
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveCategoryAsync(_s.CompanyId, null, "diving", "DV2", 10));   // the name is taken, whatever the case
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveCategoryAsync(_s.CompanyId, null, "Other", "div", 10));    // the code is taken
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveCategoryAsync(_s.CompanyId, null, "Other", "D", 10));      // too short
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveCategoryAsync(_s.CompanyId, null, "Other", "D-V", 10));    // not letters or digits
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveCategoryAsync(_s.CompanyId, null, " ", "OTH", 10));

        await _shoreLibrary.SaveCategoryAsync(_s.CompanyId, dive.Id, "Diving operations", "DIV", 9, isActive: false);   // the same row keeps its own code
        await _s.SettleUntilQuietAsync();
        var atVessel = await _vesselLibrary.ListCategoriesAsync(_s.CompanyId);
        Assert.Equal(9, atVessel.Count);
        Assert.Equal(("Diving operations", false), (atVessel.Single(c => c.Code == "DIV").Name, atVessel.Single(c => c.Code == "DIV").IsActive));
        Assert.Equal(8, (await _vesselLibrary.ListCategoriesAsync(_s.CompanyId, includeInactive: false)).Count);
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task TheMatrixWordingAndBandsAreChecked_ReachTheVessel_AndNeverRescoreAnAssessmentAlreadyMade()
    {
        await SwitchOnAsync();
        var matrix = await _vesselLibrary.GetMatrixAsync(_s.CompanyId);
        Assert.Equal(("Insignificant", "Almost certain", 5), (matrix.Consequence[0], matrix.Probability[4], matrix.Bands.Count));

        // An assessment is scored under the standard bands: 4 x 4 = 16 is High.
        var category = (await _shoreLibrary.ListCategoriesAsync(_s.CompanyId)).First();
        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "Hold cleaning", Today, category.Id), _s.User);
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Hold cleaning", category.Id, Today, "", [new RaTeamInput("Bosun")],
            [new RaActivityInput(null, "Washing", [new RaHazardInput(null, "Slips", "", 4, 4, "Mats", "", 2, 2)])]), _s.User);
        Assert.Equal("High", (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Assessment.Hazards.Single().BandBefore);

        // Checked: five words each, none empty; two to eight bands, the first at 1, different starts and names, none above 25.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveLabelsAsync(_s.CompanyId, ["a", "b", "c", "d"], ["a", "b", "c", "d", "e"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveLabelsAsync(_s.CompanyId, ["a", "b", " ", "d", "e"], ["a", "b", "c", "d", "e"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselLibrary.SaveLabelsAsync(_s.CompanyId, ["a", "b", "c", "d", "e"], ["a", "b", "c", "d", "e"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveBandsAsync(_s.CompanyId, [new("Only", 1)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveBandsAsync(_s.CompanyId, [new("Low", 2), new("High", 10)]));        // does not start at 1
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveBandsAsync(_s.CompanyId, [new("Low", 1), new("High", 1)]));        // same start
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveBandsAsync(_s.CompanyId, [new("Low", 1), new("low", 10)]));        // same name
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveBandsAsync(_s.CompanyId, [new("Low", 1), new("High", 26)]));       // above the highest factor

        // The office changes the wording and gives the company three bands (fewer than before); the vessel receives both.
        await _shoreLibrary.SaveLabelsAsync(_s.CompanyId, ["Trivial", "Small", "Serious", "Severe", "Fatal"], ["Never", "Seldom", "Sometimes", "Often", "Always"]);
        await _shoreLibrary.SaveBandsAsync(_s.CompanyId, [new("Acceptable", 1), new("Tolerable", 8), new("Intolerable", 16)]);
        await _s.SettleUntilQuietAsync();

        var changed = await _vesselLibrary.GetMatrixAsync(_s.CompanyId);
        Assert.Equal(("Trivial", "Always"), (changed.Consequence[0], changed.Probability[4]));
        Assert.Equal([("Acceptable", 1), ("Tolerable", 8), ("Intolerable", 16)], changed.Bands.Select(b => (b.Name, b.MinFactor)));
        Assert.Equal(["Acceptable", "Tolerable", "Intolerable"], new[] { 4, 8, 16 }.Select(changed.Matrix.BandOf));

        // The assessment already scored keeps its band; saving it again scores under the new bands.
        Assert.Equal("High", (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Assessment.Hazards.Single().BandBefore);
        var detail = (await _vessel.GetAsync(_s.CompanyId, started.Id))!;
        await _vessel.SaveAsync(_s.CompanyId, started.Id, new AssessmentEdit("Hold cleaning", category.Id, Today, "", [new RaTeamInput("Bosun")],
            [new RaActivityInput(detail.Assessment.Activities.Single().Id, "Washing", [new RaHazardInput(detail.Assessment.Hazards.Single().Id, "Slips", "", 4, 4, "Mats", "", 2, 2)])]), _s.User);
        Assert.Equal(("Intolerable", "Acceptable"), ((await _vessel.GetAsync(_s.CompanyId, started.Id))!.Assessment.Hazards.Single() is { } h ? (h.BandBefore, h.BandAfter) : default));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ATemplateIsBuiltChangedAndReachesTheVessel_EachChangeRaisesTheVersion_AndAssessmentsAlreadyMadeAreNotAltered()
    {
        await SwitchOnAsync();
        var category = (await _shoreLibrary.ListCategoriesAsync(_s.CompanyId)).Single(c => c.Code == "HOT");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vesselLibrary.SaveTemplateAsync(_s.CompanyId, null, "Onboard template", category.Id, "", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveTemplateAsync(_s.CompanyId, null, " ", category.Id, "", ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveTemplateAsync(_s.CompanyId, null, "Hot work", Guid.NewGuid(), "", ""));   // no such category

        var template = await _shoreLibrary.SaveTemplateAsync(_s.CompanyId, null, "Hot work on deck", category.Id, "Welding outside the workshop", "Stop work and raise the alarm");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveTemplateAsync(_s.CompanyId, null, "hot work on deck", category.Id, "", ""));   // the name is taken

        await _shoreLibrary.SaveTemplateContentAsync(_s.CompanyId, template.Id,
        [
            new RaTemplateActivityInput(null, "Preparing the area", [Hazard(null, "Flammable material nearby")]),
            new RaTemplateActivityInput(null, "Welding", [Hazard(null, "Fire"), Hazard(null, "Fumes", 3, 3, 2, 1)]),
        ]);
        await _s.SettleUntilQuietAsync();
        var view = (await _shoreLibrary.GetTemplateAsync(_s.CompanyId, template.Id))!;
        Assert.Equal((2, "Preparing the area|Welding", 3), (view.Template.Version, string.Join("|", view.Activities.Select(a => a.Title)), view.Hazards.Count));   // saved once: version 2
        var atVessel = (await _vesselLibrary.GetTemplateAsync(_s.CompanyId, template.Id))!;
        Assert.Equal((2, 2, 3), (atVessel.Template.Version, atVessel.Activities.Count, atVessel.Hazards.Count));

        // A vessel starts an assessment from it: it keeps its own copy, scored under the matrix.
        var started = await _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "", Today, TemplateId: template.Id), _s.User);
        var copy = (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Assessment;
        Assert.Equal((2, 3, "Welding outside the workshop", 2), (copy.TemplateVersion, copy.Hazards.Count, copy.JobDescription, copy.Activities.Count));
        Assert.Equal("High", copy.Hazards.Single(h => h.Hazard == "Fire").BandBefore);

        // The office rewrites the template: a hazard keeps its row, another is removed, a new one is added, and an activity goes.
        var fire = view.Hazards.Single(h => h.Hazard == "Fire");
        var welding = view.Activities.Single(a => a.Title == "Welding");
        await _shoreLibrary.SaveTemplateContentAsync(_s.CompanyId, template.Id,
            [new RaTemplateActivityInput(welding.Id, "Welding and cutting", [Hazard(fire.Id, "Fire and sparks", 5, 4, 2, 1), Hazard(null, "Hot slag")])]);
        await _s.SettleUntilQuietAsync();

        var updated = (await _vesselLibrary.GetTemplateAsync(_s.CompanyId, template.Id))!;
        Assert.Equal(3, updated.Template.Version);
        Assert.Equal(["Welding and cutting"], updated.Activities.Select(a => a.Title));
        Assert.Equal(["Fire and sparks", "Hot slag"], updated.Hazards.Select(h => h.Hazard));
        Assert.Equal(fire.Id, updated.Hazards[0].Id);                                                 // the same row, changed

        // The assessment made earlier still reads as it did.
        var after = (await _vessel.GetAsync(_s.CompanyId, started.Id))!.Assessment;
        Assert.Equal((2, 3, 2), (after.TemplateVersion, after.Hazards.Count, after.Activities.Count));
        Assert.Contains(after.Hazards, h => h.Hazard == "Fumes");

        // Details change the version too; a template switched off cannot be started from.
        await _shoreLibrary.SaveTemplateAsync(_s.CompanyId, template.Id, "Hot work on deck", category.Id, "Welding and cutting outside the workshop", "", isActive: false);
        await _s.SettleUntilQuietAsync();
        Assert.Equal(4, (await _vesselLibrary.GetTemplateAsync(_s.CompanyId, template.Id))!.Template.Version);
        Assert.Contains("no longer in use", (await Assert.ThrowsAsync<InvalidOperationException>(() => _vessel.StartAsync(_s.CompanyId, new NewAssessment(_s.VesselId, "", Today, TemplateId: template.Id), _s.User))).Message);

        var rows = await _shoreLibrary.ListTemplatesAsync(_s.CompanyId);
        var row = Assert.Single(rows);
        Assert.Equal(("Hot work", 1, 2), (row.CategoryName, row.Activities, row.Hazards));
        Assert.Empty(await _s.VesselSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task ATemplateHasLimits_AndScoresMustBeOnTheScale()
    {
        var template = await _shoreLibrary.SaveTemplateAsync(_s.CompanyId, null, "Limits", null, "", "");
        var tooMany = Enumerable.Range(0, RiskAssessmentService.MaxActivities + 1).Select(i => new RaTemplateActivityInput(null, $"A{i}", [])).ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveTemplateContentAsync(_s.CompanyId, template.Id, tooMany));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveTemplateContentAsync(_s.CompanyId, template.Id, [new(null, " ", [])]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveTemplateContentAsync(_s.CompanyId, template.Id, [new(null, "A", [Hazard(null, "")])]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _shoreLibrary.SaveTemplateContentAsync(_s.CompanyId, template.Id, [new(null, "A", [Hazard(null, "Gas", cb: 6)])]));
        await _shoreLibrary.SaveTemplateContentAsync(_s.CompanyId, template.Id, [new(null, "A", [Hazard(null, "Gas", 0, 0, 0, 0)])]);          // a template may leave the scores to whoever uses it
        Assert.Equal(2, (await _shoreLibrary.GetTemplateAsync(_s.CompanyId, template.Id))!.Template.Version);
        Assert.Null(await _shoreLibrary.GetTemplateAsync(_s.CompanyId, Guid.NewGuid()));
    }
}
