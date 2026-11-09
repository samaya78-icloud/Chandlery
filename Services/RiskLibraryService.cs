using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record RaMatrixView(List<string> Consequence, List<string> Probability, List<(Guid Id, string Name, int MinFactor)> Bands, RiskMatrix Matrix);

public record RaBandInput(string Name, int MinFactor);

public record RaTemplateHazardInput(Guid? Id, string Hazard, string Effect, string ExistingControls, string AdditionalControls, int ConsequenceBefore, int ProbabilityBefore,
    int ConsequenceAfter, int ProbabilityAfter, string Contingency = "");

public record RaTemplateActivityInput(Guid? Id, string Title, IReadOnlyList<RaTemplateHazardInput> Hazards);

public record RaTemplateView(RaTemplate Template, List<RaTemplateActivity> Activities, List<RaTemplateHazard> Hazards);

public record RaTemplateRow(RaTemplate Template, string CategoryName, int Activities, int Hazards);

/// <summary>
/// The risk assessment library, kept by the office: categories, the risk matrix (its wording and bands) and the templates assessments start from. All of it is master
/// data, so vessels receive it read-only. Changing a template raises its version; assessments already made keep their own copy.
/// </summary>
public partial class RiskLibraryService(IDbContextFactory<ChandleryDbContext> factory)
{
    private static void RequireOffice(ChandleryDbContext db)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException("The risk assessment library is maintained by the office.");
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.RiskAssessment);
    }

    // ---- categories ----

    public async Task<List<RaCategory>> ListCategoriesAsync(Guid companyId, bool includeInactive = true)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.RaCategories.AsNoTracking().Where(c => c.CompanyId == companyId);
        if (!includeInactive) query = query.Where(c => c.IsActive);
        return await query.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
    }

    /// <summary>
    /// Adds or changes a category. The short code (2 to 6 letters or digits) goes into lists, so it must be unique. Renaming a category does not change assessments
    /// already made: they keep the name they were made with.
    /// </summary>
    public async Task<RaCategory> SaveCategoryAsync(Guid companyId, Guid? id, string name, string code, int sortOrder, bool isActive = true)
    {
        name = (name ?? "").Trim();
        code = (code ?? "").Trim().ToUpperInvariant();
        if (name == "") throw new InvalidOperationException("Give the category a name.");
        if (code.Length is < 2 or > 6 || !code.All(char.IsLetterOrDigit)) throw new InvalidOperationException("The short code is 2 to 6 letters or digits.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var others = await db.RaCategories.Where(c => c.CompanyId == companyId && c.Id != id).Select(c => new { c.Name, c.Code }).ToListAsync();
        if (others.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"There is already a category called {name}.");
        if (others.Any(c => c.Code == code)) throw new InvalidOperationException($"The short code {code} is taken.");

        RaCategory category;
        if (id is { } existing) category = await db.RaCategories.FirstOrDefaultAsync(c => c.Id == existing && c.CompanyId == companyId) ?? throw new InvalidOperationException("Category not found.");
        else category = db.RaCategories.Add(new RaCategory { Id = Guid.CreateVersion7(), CompanyId = companyId }).Entity;
        (category.Name, category.Code, category.SortOrder, category.IsActive) = (name, code, sortOrder, isActive);
        await db.SaveChangesAsync();
        return category;
    }

    // ---- the matrix ----

    public async Task<RaMatrixView> GetMatrixAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var labels = await db.RaMatrixLabels.AsNoTracking().Where(l => l.CompanyId == companyId).ToListAsync();
        string[] Words(RaAxis axis, IReadOnlyList<string> fallback) => Enumerable.Range(1, RiskMatrix.Levels).Select(i => labels.FirstOrDefault(l => l.Axis == axis && l.Level == i)?.Label ?? fallback[i - 1]).ToArray();
        var bands = await db.RaMatrixBands.AsNoTracking().Where(b => b.CompanyId == companyId).OrderBy(b => b.MinFactor).ToListAsync();
        return new RaMatrixView([.. Words(RaAxis.Consequence, RaStandard.Consequence)], [.. Words(RaAxis.Probability, RaStandard.Probability)],
            bands.Select(b => (b.Id, b.Name, b.MinFactor)).ToList(), await RiskAssessmentService.MatrixAsync(db, companyId));
    }

    /// <summary>Changes the words for the five levels of each axis. Existing assessments are not touched: they keep their scores.</summary>
    public async Task SaveLabelsAsync(Guid companyId, IReadOnlyList<string> consequence, IReadOnlyList<string> probability)
    {
        if (consequence.Count != RiskMatrix.Levels || probability.Count != RiskMatrix.Levels) throw new InvalidOperationException($"Give a word for each of the {RiskMatrix.Levels} levels.");
        if (consequence.Concat(probability).Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Every level needs a word.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var held = await db.RaMatrixLabels.Where(l => l.CompanyId == companyId).ToListAsync();
        foreach (var (axis, words) in new[] { (RaAxis.Consequence, consequence), (RaAxis.Probability, probability) })
            for (var i = 0; i < RiskMatrix.Levels; i++)
            {
                var label = held.FirstOrDefault(l => l.Axis == axis && l.Level == i + 1);
                if (label is null) db.RaMatrixLabels.Add(new RaMatrixLabel { Id = Guid.CreateVersion7(), CompanyId = companyId, Axis = axis, Level = i + 1, Label = words[i].Trim() });
                else label.Label = words[i].Trim();
            }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Replaces the bands. The first must start at factor 1 so every score has a band, each must start higher than the one before, and none may start above the highest
    /// possible factor (25). Assessments already made keep the band they were scored with.
    /// </summary>
    public async Task SaveBandsAsync(Guid companyId, IReadOnlyList<RaBandInput> bands)
    {
        if (bands.Count is < 2 or > 8) throw new InvalidOperationException("Use between 2 and 8 bands.");
        var ordered = bands.OrderBy(b => b.MinFactor).ToList();
        if (ordered.Any(b => string.IsNullOrWhiteSpace(b.Name))) throw new InvalidOperationException("Give every band a name.");
        if (ordered.Select(b => b.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Count) throw new InvalidOperationException("Band names must be different.");
        if (ordered[0].MinFactor != 1) throw new InvalidOperationException("The lowest band must start at 1, so that every score has a band.");
        if (ordered.Select(b => b.MinFactor).Distinct().Count() != ordered.Count) throw new InvalidOperationException("Each band must start at a different factor.");
        if (ordered[^1].MinFactor > RiskMatrix.Levels * RiskMatrix.Levels) throw new InvalidOperationException($"No band can start above {RiskMatrix.Levels * RiskMatrix.Levels}, the highest possible factor.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var held = await db.RaMatrixBands.Where(b => b.CompanyId == companyId).OrderBy(b => b.MinFactor).ToListAsync();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i < held.Count) (held[i].Name, held[i].MinFactor, held[i].SortOrder) = (ordered[i].Name.Trim(), ordered[i].MinFactor, i);
            else db.RaMatrixBands.Add(new RaMatrixBand { Id = Guid.CreateVersion7(), CompanyId = companyId, Name = ordered[i].Name.Trim(), MinFactor = ordered[i].MinFactor, SortOrder = i });
        }
        for (var i = ordered.Count; i < held.Count; i++) db.RaMatrixBands.Remove(held[i]);
        await db.SaveChangesAsync();
    }

    // ---- templates ----

    public async Task<List<RaTemplateRow>> ListTemplatesAsync(Guid companyId, bool includeInactive = true)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.RaTemplates.AsNoTracking().Where(t => t.CompanyId == companyId);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        var templates = await query.OrderBy(t => t.Name).ToListAsync();
        var ids = templates.Select(t => t.Id).ToList();
        var activities = (await db.RaTemplateActivities.AsNoTracking().Where(a => ids.Contains(a.TemplateId)).Select(a => a.TemplateId).ToListAsync()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var hazards = (await db.RaTemplateHazards.AsNoTracking().Where(h => ids.Contains(h.TemplateId)).Select(h => h.TemplateId).ToListAsync()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var categories = (await db.RaCategories.AsNoTracking().Where(c => c.CompanyId == companyId).ToListAsync()).ToDictionary(c => c.Id, c => c.Name);
        return templates.Select(t => new RaTemplateRow(t, t.CategoryId is { } c && categories.TryGetValue(c, out var n) ? n : "", activities.GetValueOrDefault(t.Id), hazards.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<RaTemplateView?> GetTemplateAsync(Guid companyId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var template = await db.RaTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId);
        if (template is null) return null;
        return new RaTemplateView(template,
            await db.RaTemplateActivities.AsNoTracking().Where(a => a.TemplateId == id).OrderBy(a => a.SortOrder).ThenBy(a => a.Title).ToListAsync(),
            await db.RaTemplateHazards.AsNoTracking().Where(h => h.TemplateId == id).OrderBy(h => h.SortOrder).ToListAsync());
    }

    /// <summary>Adds a template, or changes the details of one (its version goes up). A template is "in use" when assessments can be started from it.</summary>
    public async Task<RaTemplate> SaveTemplateAsync(Guid companyId, Guid? id, string name, Guid? categoryId, string jobDescription, string contingency, bool isActive = true)
    {
        name = (name ?? "").Trim();
        if (name == "") throw new InvalidOperationException("Give the template a name.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        if (categoryId is { } cid && !await db.RaCategories.AnyAsync(c => c.Id == cid && c.CompanyId == companyId)) throw new InvalidOperationException("Category not found.");
        if (await db.RaTemplates.AnyAsync(t => t.CompanyId == companyId && t.Id != id && t.Name.ToLower() == name.ToLower())) throw new InvalidOperationException($"There is already a template called {name}.");

        RaTemplate template;
        if (id is { } existing)
        {
            template = await db.RaTemplates.FirstOrDefaultAsync(t => t.Id == existing && t.CompanyId == companyId) ?? throw new InvalidOperationException("Template not found.");
            template.Version++;
        }
        else template = db.RaTemplates.Add(new RaTemplate { Id = Guid.CreateVersion7(), CompanyId = companyId }).Entity;
        (template.Name, template.CategoryId, template.JobDescription, template.Contingency, template.IsActive) = (name, categoryId, (jobDescription ?? "").Trim(), (contingency ?? "").Trim(), isActive);
        await db.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// Replaces the activities and hazards of a template with those given (an id keeps the row; a missing one is removed) and raises the version once. Scores may be left
    /// at 0 in a template (the person using it scores them), but when given they must be 1 to 5.
    /// </summary>
    public async Task SaveTemplateContentAsync(Guid companyId, Guid templateId, IReadOnlyList<RaTemplateActivityInput> activities)
    {
        if (activities.Count > RiskAssessmentService.MaxActivities) throw new InvalidOperationException($"A template can hold at most {RiskAssessmentService.MaxActivities} work activities.");
        if (activities.Any(a => string.IsNullOrWhiteSpace(a.Title))) throw new InvalidOperationException("Give every work activity a title.");
        if (activities.Any(a => a.Hazards.Count > RiskAssessmentService.MaxHazardsPerActivity)) throw new InvalidOperationException($"A work activity can hold at most {RiskAssessmentService.MaxHazardsPerActivity} hazards.");
        foreach (var h in activities.SelectMany(a => a.Hazards))
        {
            if (string.IsNullOrWhiteSpace(h.Hazard)) throw new InvalidOperationException("Give every hazard a name.");
            foreach (var s in new[] { h.ConsequenceBefore, h.ProbabilityBefore, h.ConsequenceAfter, h.ProbabilityAfter })
                if (s != 0 && !RiskMatrix.IsLevel(s)) throw new InvalidOperationException($"Scores run from 1 to {RiskMatrix.Levels}.");
        }

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var template = await db.RaTemplates.FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Template not found.");
        var heldActivities = await db.RaTemplateActivities.Where(a => a.TemplateId == templateId).ToListAsync();
        var heldHazards = await db.RaTemplateHazards.Where(h => h.TemplateId == templateId).ToListAsync();
        var keepActivities = new HashSet<Guid>();
        var keepHazards = new HashSet<Guid>();

        var order = 0;
        foreach (var input in activities)
        {
            var activity = input.Id is { } aid ? heldActivities.FirstOrDefault(a => a.Id == aid) : null;
            if (activity is null) { activity = new RaTemplateActivity { Id = Guid.CreateVersion7(), CompanyId = companyId, TemplateId = templateId }; db.RaTemplateActivities.Add(activity); }
            (activity.Title, activity.SortOrder) = (input.Title.Trim(), order++);
            keepActivities.Add(activity.Id);

            var h = 0;
            foreach (var hi in input.Hazards)
            {
                var hazard = hi.Id is { } hid ? heldHazards.FirstOrDefault(x => x.Id == hid) : null;
                if (hazard is null) { hazard = new RaTemplateHazard { Id = Guid.CreateVersion7(), CompanyId = companyId, TemplateId = templateId }; db.RaTemplateHazards.Add(hazard); }
                (hazard.ActivityId, hazard.Hazard, hazard.Effect, hazard.SortOrder) = (activity.Id, hi.Hazard.Trim(), (hi.Effect ?? "").Trim(), h++);
                (hazard.ExistingControls, hazard.AdditionalControls, hazard.Contingency) = ((hi.ExistingControls ?? "").Trim(), (hi.AdditionalControls ?? "").Trim(), (hi.Contingency ?? "").Trim());
                (hazard.ConsequenceBefore, hazard.ProbabilityBefore, hazard.ConsequenceAfter, hazard.ProbabilityAfter) = (hi.ConsequenceBefore, hi.ProbabilityBefore, hi.ConsequenceAfter, hi.ProbabilityAfter);
                keepHazards.Add(hazard.Id);
            }
        }
        db.RaTemplateHazards.RemoveRange(heldHazards.Where(x => !keepHazards.Contains(x.Id)));
        db.RaTemplateActivities.RemoveRange(heldActivities.Where(x => !keepActivities.Contains(x.Id)));
        template.Version++;
        await db.SaveChangesAsync();
    }
}
