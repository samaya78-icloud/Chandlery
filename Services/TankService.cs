using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record TankInput(string Code, string Name, TankType Type, int PlanX = 0, int PlanY = 0, int PlanWidth = 10, int PlanHeight = 8,
    string SteelGradeDeck = "", string SteelGradeBottom = "", string SteelGradeSide = "", string SteelGradeBulkhead = "", string OriginalCoating = "",
    decimal? CoatedAreaM2 = null, string CathodicProtection = "", decimal? ProtectedAreaM2 = null);

public record TankTemplateView(TankChecklistTemplate Template, List<TankCriterion> Criteria);

/// <summary>Tanks (the side that made one keeps it up to date) and the checklist library (the office's).</summary>
public class TankService(IDbContextFactory<ChandleryDbContext> factory)
{
    public const int PlanColumns = 100, PlanRows = 40;

    // ---- the module switch ----

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.TankInspection);

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.TankInspection);
    }

    /// <summary>The vessels tank inspection is on for (with their group, for "all vessels of a group") and the company's vessel groups.</summary>
    public async Task<(List<(Guid Id, string Name, Guid? GroupId)> Vessels, List<VesselGroup> Groups)> VesselChoicesAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.TankInspection)).Select(v => v.Id).ToHashSet();
        var vessels = (await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).OrderBy(v => v.Name).ToListAsync())
            .Where(v => enabled.Contains(v.Id)).Select(v => (v.Id, v.Name, v.GroupId)).ToList();
        var groups = await db.VesselGroups.AsNoTracking().Where(g => g.CompanyId == companyId).OrderBy(g => g.Level).ThenBy(g => g.Name).ToListAsync();
        return (vessels, groups);
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    // ---- tanks ----

    private static void Check(TankInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new InvalidOperationException("Give the tank a name.");
        if (string.IsNullOrWhiteSpace(input.Code)) throw new InvalidOperationException("Give the tank a code (for example 2P).");
        if (input.PlanWidth < 1 || input.PlanHeight < 1 || input.PlanX < 0 || input.PlanY < 0 || input.PlanX + input.PlanWidth > PlanColumns || input.PlanY + input.PlanHeight > PlanRows)
            throw new InvalidOperationException($"The tank must sit inside the plan ({PlanColumns} columns by {PlanRows} rows).");
        if (input.CoatedAreaM2 < 0 || input.ProtectedAreaM2 < 0) throw new InvalidOperationException("An area cannot be negative.");
    }

    private static void Fill(Tank t, TankInput i)
    {
        (t.Code, t.Name, t.Type) = (i.Code.Trim(), i.Name.Trim(), i.Type);
        (t.PlanX, t.PlanY, t.PlanWidth, t.PlanHeight) = (i.PlanX, i.PlanY, i.PlanWidth, i.PlanHeight);
        (t.SteelGradeDeck, t.SteelGradeBottom, t.SteelGradeSide, t.SteelGradeBulkhead) = (i.SteelGradeDeck.Trim(), i.SteelGradeBottom.Trim(), i.SteelGradeSide.Trim(), i.SteelGradeBulkhead.Trim());
        (t.OriginalCoating, t.CoatedAreaM2, t.CathodicProtection, t.ProtectedAreaM2) = (i.OriginalCoating.Trim(), i.CoatedAreaM2, i.CathodicProtection.Trim(), i.ProtectedAreaM2);
    }

    public async Task<Tank> AddTankAsync(Guid companyId, Guid vesselId, TankInput input, Actor actor)
    {
        Check(input);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Tank inspection is not switched on for this vessel.");
        var code = input.Code.Trim();
        if (await db.Tanks.AnyAsync(t => t.VesselId == vesselId && t.Code == code)) throw new InvalidOperationException($"This vessel already has a tank with the code {code}.");
        var tank = new Tank { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow };
        Fill(tank, input);
        db.Tanks.Add(tank);
        await db.SaveChangesAsync();
        return tank;
    }

    /// <summary>Changes a tank. Only the side that made it may; the other side's change is refused when saving.</summary>
    public async Task UpdateTankAsync(Guid companyId, Guid tankId, TankInput input, bool isActive = true)
    {
        Check(input);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var tank = await db.Tanks.FirstOrDefaultAsync(t => t.Id == tankId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Tank not found.");
        if (!await IsEnabledForVesselAsync(db, companyId, tank.VesselId)) throw new InvalidOperationException("Tank inspection is not switched on for this vessel.");
        var code = input.Code.Trim();
        if (await db.Tanks.AnyAsync(t => t.VesselId == tank.VesselId && t.Code == code && t.Id != tankId)) throw new InvalidOperationException($"This vessel already has a tank with the code {code}.");
        Fill(tank, input);
        tank.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    public async Task<Tank?> GetTankAsync(Guid companyId, Guid tankId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Tanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tankId && t.CompanyId == companyId);
    }

    public async Task<List<Tank>> ListTanksAsync(Guid companyId, Guid vesselId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.Tanks.AsNoTracking().Where(t => t.CompanyId == companyId && t.VesselId == vesselId);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        return await query.OrderBy(t => t.Code).ToListAsync();
    }

    /// <summary>The vessel's tanks, each with what its latest submitted inspection found (the newest by inspection date).</summary>
    public async Task<List<TankPlanRow>> PlanAsync(Guid companyId, Guid vesselId, bool includeInactive = false)
    {
        var tanks = await ListTanksAsync(companyId, vesselId, includeInactive);
        if (tanks.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var ids = tanks.Select(t => t.Id).ToList();
        var submitted = (await db.TankInspections.AsNoTracking().Where(i => ids.Contains(i.TankId) && i.SubmissionCount > 0)
            .Select(i => new { i.TankId, i.InspectedOn, i.CreatedAt, i.ExceptionCount, i.HasBadRating }).ToListAsync()).ToLookup(i => i.TankId);
        return tanks.Select(t =>
        {
            var mine = submitted[t.Id].ToList();
            var latest = mine.OrderByDescending(i => i.InspectedOn).ThenByDescending(i => i.CreatedAt).FirstOrDefault();
            return new TankPlanRow(t, mine.Count, latest?.InspectedOn, latest?.ExceptionCount ?? 0, latest?.HasBadRating ?? false);
        }).ToList();
    }

    /// <summary>
    /// Copies one vessel's tanks to others: a tank is matched by its code and added when the target has none with that code (active or not), with the same type, plan position and
    /// structural data. Nothing the target already has is changed, so it can be repeated; each target is copied all or nothing. Office only.
    /// </summary>
    public async Task<List<TankCopyResult>> CopyTanksAsync(Guid companyId, Guid sourceVesselId, IEnumerable<Guid> targetVesselIds, Actor actor)
    {
        await using var check = await factory.CreateDbContextAsync();
        check.CurrentCompanyId = companyId;
        if (check.Node.IsVessel) throw new InvalidOperationException("Tank lists are copied by the office.");
        var enabled = (await ModuleGate.EnabledVesselsAsync(check, companyId, PolicyModules.TankInspection)).ToDictionary(v => v.Id, v => v.Name);
        if (!enabled.ContainsKey(sourceVesselId)) throw new InvalidOperationException("Tank inspection is not switched on for the vessel to copy from.");
        var source = await check.Tanks.AsNoTracking().Where(t => t.VesselId == sourceVesselId && t.IsActive).OrderBy(t => t.Code).ToListAsync();
        var results = new List<TankCopyResult>();
        foreach (var target in targetVesselIds.Distinct().Where(t => t != sourceVesselId))
        {
            var name = enabled.GetValueOrDefault(target) ?? (await check.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == target))?.Name ?? target.ToString();
            if (!enabled.ContainsKey(target)) { results.Add(new TankCopyResult(name, 0, 0, "Tank inspection is off for this vessel.")); continue; }
            await using var db = await factory.CreateDbContextAsync();
            db.CurrentCompanyId = companyId;
            var have = (await db.Tanks.AsNoTracking().Where(t => t.VesselId == target).Select(t => t.Code).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0, skipped = 0;
            foreach (var s in source)
            {
                if (!have.Add(s.Code)) { skipped++; continue; }
                db.Tanks.Add(new Tank
                {
                    Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = target, Code = s.Code, Name = s.Name, Type = s.Type,
                    PlanX = s.PlanX, PlanY = s.PlanY, PlanWidth = s.PlanWidth, PlanHeight = s.PlanHeight,
                    SteelGradeDeck = s.SteelGradeDeck, SteelGradeBottom = s.SteelGradeBottom, SteelGradeSide = s.SteelGradeSide, SteelGradeBulkhead = s.SteelGradeBulkhead,
                    OriginalCoating = s.OriginalCoating, CoatedAreaM2 = s.CoatedAreaM2, CathodicProtection = s.CathodicProtection, ProtectedAreaM2 = s.ProtectedAreaM2,
                    CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
                });
                added++;
            }
            if (added > 0) await db.SaveChangesAsync();
            results.Add(new TankCopyResult(name, added, skipped));
        }
        return results;
    }

    // ---- the words a reviewer is shown highlighted (office) ----

    /// <summary>The company's review keywords as typed (blank means the standard list) and the list that is in effect.</summary>
    public async Task<(string Typed, List<string> Effective)> GetKeywordsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var typed = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.TankReviewKeywords).FirstOrDefaultAsync() ?? "";
        return (typed, TankKeywords.Effective(typed));
    }

    public async Task SaveKeywordsAsync(Guid companyId, string? text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId) ?? throw new InvalidOperationException("Company not found.");
        company.TankReviewKeywords = string.Join(", ", TankKeywords.Parse(text));
        await db.SaveChangesAsync();
    }

    // ---- the checklist library (office) ----

    private static void RequireOffice(ChandleryDbContext db)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException("Checklists are maintained by the office.");
    }

    public async Task<List<TankTemplateView>> ListTemplatesAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.TankChecklistTemplates.AsNoTracking().Where(t => t.CompanyId == companyId);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        var templates = await query.OrderBy(t => t.TankType).ThenBy(t => t.Name).ToListAsync();
        var ids = templates.Select(t => t.Id).ToList();
        var criteria = (await db.TankCriteria.AsNoTracking().Where(c => ids.Contains(c.TemplateId) && (includeInactive || c.IsActive)).ToListAsync()).ToLookup(c => c.TemplateId);
        return templates.Select(t => new TankTemplateView(t, criteria[t.Id].OrderBy(c => c.Group).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToList())).ToList();
    }

    public async Task<TankChecklistTemplate> SaveTemplateAsync(Guid companyId, Guid? id, string name, TankType type, string shipType = "", bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the checklist a name.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        TankChecklistTemplate t;
        if (id is { } existing)
        {
            t = await db.TankChecklistTemplates.FirstOrDefaultAsync(x => x.Id == existing && x.CompanyId == companyId) ?? throw new InvalidOperationException("Checklist not found.");
            t.Version++;
        }
        else t = db.TankChecklistTemplates.Add(new TankChecklistTemplate { Id = Guid.CreateVersion7(), CompanyId = companyId }).Entity;
        (t.Name, t.TankType, t.ShipType, t.IsActive) = (name.Trim(), type, (shipType ?? "").Trim(), isActive);
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Adds or changes a criterion; the checklist's version goes up, so inspections made earlier can be seen to predate the change.</summary>
    public async Task<TankCriterion> SaveCriterionAsync(Guid companyId, Guid templateId, Guid? id, CriterionGroup group, string name, ScaleKind scale, string? exceptionValues, int sortOrder, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the criterion a name.");
        var exceptions = exceptionValues is null ? TankScales.DefaultExceptionValues(scale) : string.Join(",", TankScales.ExceptionSet(exceptionValues));
        var bad = TankScales.ExceptionSet(exceptions).FirstOrDefault(v => !TankScales.Values(scale).Contains(v, StringComparer.OrdinalIgnoreCase));
        if (bad is not null) throw new InvalidOperationException($"\"{bad}\" is not an answer on this scale ({string.Join(", ", TankScales.Values(scale))}).");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var template = await db.TankChecklistTemplates.FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Checklist not found.");
        TankCriterion c;
        if (id is { } existing) c = await db.TankCriteria.FirstOrDefaultAsync(x => x.Id == existing && x.TemplateId == templateId) ?? throw new InvalidOperationException("Criterion not found.");
        else c = db.TankCriteria.Add(new TankCriterion { Id = Guid.CreateVersion7(), CompanyId = companyId, TemplateId = templateId }).Entity;
        (c.Group, c.Name, c.Scale, c.ExceptionValues, c.SortOrder, c.IsActive) = (group, name.Trim(), scale, exceptions, sortOrder, isActive);
        template.Version++;
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>Adds the standard checklist for each tank type the company does not have one for yet. Returns how many were made.</summary>
    public async Task<int> CreateStandardTemplatesAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var made = await TankDefaults.AddMissingAsync(db, companyId);
        await db.SaveChangesAsync();
        return made;
    }
}

/// <summary>The starter checklists a company begins with.</summary>
public static class TankDefaults
{
    /// <summary>Adds (without saving) a standard checklist for every tank type that has none. Returns how many.</summary>
    public static async Task<int> AddMissingAsync(ChandleryDbContext db, Guid companyId)
    {
        var have = (await db.TankChecklistTemplates.IgnoreQueryFilters().Where(t => t.CompanyId == companyId).Select(t => t.TankType).ToListAsync()).ToHashSet();
        var made = 0;
        foreach (var type in Enum.GetValues<TankType>().Where(t => !have.Contains(t)))
        {
            var template = new TankChecklistTemplate { Id = Guid.CreateVersion7(), CompanyId = companyId, Name = TankStandardTemplates.NameFor(type), TankType = type };
            db.TankChecklistTemplates.Add(template);
            var order = 0;
            foreach (var (group, name, scale) in TankStandardTemplates.CriteriaFor(type))
                db.TankCriteria.Add(new TankCriterion { Id = Guid.CreateVersion7(), CompanyId = companyId, TemplateId = template.Id, Group = group, Name = name, Scale = scale,
                    ExceptionValues = TankScales.DefaultExceptionValues(scale), SortOrder = order++ });
            made++;
        }
        return made;
    }

    public static async Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId)
    {
        if (await db.TankChecklistTemplates.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == companyId)) return;
        await AddMissingAsync(db, companyId);
    }
}
