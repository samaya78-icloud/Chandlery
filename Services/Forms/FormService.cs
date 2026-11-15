using System.Text.Json.Nodes;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record NewFormType(string Code, string Title, string Department, FormPeriodicity Periodicity, FormScheduleBasis Basis = FormScheduleBasis.Calendar,
    DateTime? StartsOn = null, IReadOnlyCollection<Guid>? Groups = null, IReadOnlyCollection<Guid>? Vessels = null, bool OfficeOnly = false);

public record FormEntryDetail(FormEntry Entry, FormVersion Version, FormDef? Definition, List<FormReview> Reviews, FormStatus Status);

public record FormEntryRow(FormEntry Entry, FormStatus Status);

public record FormDueRow(FormType Type, FormVersion Version, FormDue Due);

/// <summary>
/// Forms: the office designs a form type and publishes versions of it, a vessel starts an entry on the current version, fills it in, submits it, and the office
/// reviews it (accept, or return for correction). A published version never changes; an entry is checked against the version it was filled on. Everything the
/// answers have to satisfy is decided by the pure rules (<see cref="FormValueRules"/>), so the two sides can never disagree.
/// </summary>
public class FormService(IDbContextFactory<ChandleryDbContext> factory)
{
    private static DateTime Day(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Forms);

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Forms);
    }

    private static string Stage(ChandleryDbContext db) => db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office;

    private static void OfficeOnly(ChandleryDbContext db, string what)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException($"{what} is done by the office.");
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    /// <summary>The groups (with their path) and the vessels a form can be pointed at.</summary>
    public async Task<(List<(Guid Id, string Path)> Groups, List<(Guid Id, string Name)> Vessels)> ScopeOptionsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var groups = VesselGroup.BuildTree(await db.VesselGroups.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync()).Select(g => (g.Group.Id, g.Path)).ToList();
        var vessels = (await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).OrderBy(v => v.Name).Select(v => new { v.Id, v.Name }).ToListAsync()).Select(v => (v.Id, v.Name)).ToList();
        return (groups, vessels);
    }

    public async Task<(FormType Type, FormVersion Version)?> GetVersionAsync(Guid companyId, Guid versionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var version = await db.FormVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.CompanyId == companyId);
        if (version is null) return null;
        return (await db.FormTypes.AsNoTracking().FirstAsync(t => t.Id == version.FormTypeId), version);
    }

    // ---- form types (the office) ----

    public async Task<FormType> CreateTypeAsync(Guid companyId, NewFormType input, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Designing forms");
        var code = (input.Code ?? "").Trim();
        if (code == "" || code.Length > 40) throw new InvalidOperationException("Give the form a code of up to 40 characters.");
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("Give the form a title.");
        if (await db.FormTypes.AnyAsync(t => t.CompanyId == companyId && t.Code == code)) throw new InvalidOperationException($"There is already a form with the code {code}.");
        if (input.Basis == FormScheduleBasis.Calendar && FormSchedule.PeriodOf(input.Periodicity) is not null && input.StartsOn is null)
            throw new InvalidOperationException("A form due on the calendar needs the date its first period starts.");

        var type = new FormType
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, Code = code, Title = input.Title.Trim(), Department = (input.Department ?? "").Trim(), Periodicity = input.Periodicity,
            Basis = input.Basis, StartsOn = input.StartsOn is { } start ? Day(start) : null, AppliesToGroups = FormApplicability.Join(input.Groups ?? []),
            AppliesToVessels = FormApplicability.Join(input.Vessels ?? []), OfficeOnly = input.OfficeOnly, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        db.FormTypes.Add(type);
        await db.SaveChangesAsync();
        return type;
    }

    /// <summary>Changes where a form applies and how it is scheduled. Published versions carry a copy of the applicability, so those are brought into line in the same save.</summary>
    public async Task UpdateTypeAsync(Guid companyId, Guid typeId, NewFormType input, bool isActive, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Designing forms");
        var type = await db.FormTypes.FirstOrDefaultAsync(t => t.Id == typeId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Form not found.");
        var code = (input.Code ?? "").Trim();
        if (code == "" || code.Length > 40) throw new InvalidOperationException("Give the form a code of up to 40 characters.");
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("Give the form a title.");
        if (code != type.Code && await db.FormTypes.AnyAsync(t => t.CompanyId == companyId && t.Code == code)) throw new InvalidOperationException($"There is already a form with the code {code}.");
        if (input.Basis == FormScheduleBasis.Calendar && FormSchedule.PeriodOf(input.Periodicity) is not null && input.StartsOn is null)
            throw new InvalidOperationException("A form due on the calendar needs the date its first period starts.");

        (type.Code, type.Title, type.Department, type.Periodicity, type.Basis, type.StartsOn) = (code, input.Title.Trim(), (input.Department ?? "").Trim(), input.Periodicity, input.Basis, input.StartsOn is { } s ? Day(s) : (DateTime?)null);
        (type.AppliesToGroups, type.AppliesToVessels, type.OfficeOnly, type.IsActive) = (FormApplicability.Join(input.Groups ?? []), FormApplicability.Join(input.Vessels ?? []), input.OfficeOnly, isActive);
        foreach (var version in await db.FormVersions.Where(v => v.FormTypeId == typeId && v.Status != FormVersionStatus.Draft).ToListAsync())
            (version.AppliesToGroups, version.AppliesToVessels, version.OfficeOnly) = (type.AppliesToGroups, type.AppliesToVessels, type.OfficeOnly);
        await db.SaveChangesAsync();
    }

    // ---- versions (the office) ----

    private static async Task<FormVersion> NewDraftAsync(ChandleryDbContext db, FormType type, string definition, string note, Actor actor)
    {
        if (await db.FormVersions.AnyAsync(v => v.FormTypeId == type.Id && v.Status == FormVersionStatus.Draft)) throw new InvalidOperationException("This form already has a draft; publish or discard it first.");
        var next = (await db.FormVersions.Where(v => v.FormTypeId == type.Id).MaxAsync(v => (int?)v.Number) ?? 0) + 1;
        var version = new FormVersion
        {
            Id = Guid.CreateVersion7(), CompanyId = type.CompanyId, FormTypeId = type.Id, Number = next, Status = FormVersionStatus.Draft, Definition = definition, Note = note.Trim(),
            AppliesToGroups = type.AppliesToGroups, AppliesToVessels = type.AppliesToVessels, OfficeOnly = type.OfficeOnly, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        db.FormVersions.Add(version);
        return version;
    }

    /// <summary>A new draft, empty or taken from the definition given. A form has one draft at a time.</summary>
    public async Task<FormVersion> CreateDraftAsync(Guid companyId, Guid typeId, string? definition, string note, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Designing forms");
        var type = await db.FormTypes.FirstOrDefaultAsync(t => t.Id == typeId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Form not found.");
        var version = await NewDraftAsync(db, type, string.IsNullOrWhiteSpace(definition) ? new FormDef { Title = type.Title }.ToJson() : definition, note ?? "", actor);
        await db.SaveChangesAsync();
        return version;
    }

    /// <summary>A new draft that starts as a copy of another version (the way to change a published form).</summary>
    public async Task<FormVersion> CopyAsync(Guid companyId, Guid versionId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Designing forms");
        var source = await db.FormVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.CompanyId == companyId) ?? throw new InvalidOperationException("Version not found.");
        var type = await db.FormTypes.FirstAsync(t => t.Id == source.FormTypeId);
        var version = await NewDraftAsync(db, type, source.Definition, $"Copied from version {source.Number}", actor);
        await db.SaveChangesAsync();
        return version;
    }

    /// <summary>Saves the definition of a draft, whatever state it is in (it is only checked when published, so work in progress can be kept). Refused for a published version.</summary>
    public async Task SaveDraftAsync(Guid companyId, Guid versionId, string definition, string note)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Designing forms");
        var version = await db.FormVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.CompanyId == companyId) ?? throw new InvalidOperationException("Version not found.");
        if (version.Status != FormVersionStatus.Draft) throw new InvalidOperationException("A published version never changes. Copy it to make a new draft.");
        if (FormDef.TryRead(definition, out var error) is null) throw new InvalidOperationException($"That is not a form definition: {error}");
        (version.Definition, version.Note) = (definition, (note ?? "").Trim());
        await db.SaveChangesAsync();
    }

    public async Task DiscardDraftAsync(Guid companyId, Guid versionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Designing forms");
        var version = await db.FormVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.CompanyId == companyId) ?? throw new InvalidOperationException("Version not found.");
        if (version.Status != FormVersionStatus.Draft) throw new InvalidOperationException("Only a draft can be discarded; a published version is retired instead.");
        db.FormVersions.Remove(version);
        await db.SaveChangesAsync();
    }

    /// <summary>Checks the draft and publishes it: from then on it never changes and the vessels it applies to receive it. Refused, with every problem, when the definition breaks a rule.</summary>
    public async Task<FormVersion> PublishAsync(Guid companyId, Guid versionId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Publishing a form");
        var version = await db.FormVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.CompanyId == companyId) ?? throw new InvalidOperationException("Version not found.");
        if (version.Status != FormVersionStatus.Draft) throw new InvalidOperationException("This version is already published.");
        var problems = FormDefinitionRules.Validate(version.Definition);
        if (problems.Count > 0)
            throw new InvalidOperationException($"The form cannot be published: {string.Join(" ", problems.Take(3).Select(p => p.ToString()))}{(problems.Count > 3 ? $" (and {problems.Count - 3} more)" : "")}");
        var type = await db.FormTypes.AsNoTracking().FirstAsync(t => t.Id == version.FormTypeId);
        (version.Status, version.PublishedAt, version.AppliesToGroups, version.AppliesToVessels, version.OfficeOnly) = (FormVersionStatus.Published, DateTime.UtcNow, type.AppliesToGroups, type.AppliesToVessels, type.OfficeOnly);
        await db.SaveChangesAsync();
        return version;
    }

    /// <summary>A retired version takes no new entries; entries already filled on it stay readable and valid.</summary>
    public async Task RetireAsync(Guid companyId, Guid versionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Retiring a form version");
        var version = await db.FormVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.CompanyId == companyId) ?? throw new InvalidOperationException("Version not found.");
        if (version.Status != FormVersionStatus.Published) throw new InvalidOperationException("Only a published version can be retired.");
        version.Status = FormVersionStatus.Retired;
        await db.SaveChangesAsync();
    }

    // ---- what a vessel may fill ----

    private static async Task<HashSet<Guid>> GroupChainAsync(ChandleryDbContext db, Guid? groupId)
    {
        var parents = await db.VesselGroups.AsNoTracking().Select(g => new { g.Id, g.ParentId }).ToDictionaryAsync(g => g.Id, g => g.ParentId);
        return MasterData.ChainOf(parents, groupId);
    }

    /// <summary>The form types that apply to a vessel, each with the version a new entry would be filled on (the highest published one). Forms with no published version are left out.</summary>
    public async Task<List<(FormType Type, FormVersion Version)>> ApplicableAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ApplicableAsync(db, companyId, vesselId);
    }

    private static async Task<List<(FormType Type, FormVersion Version)>> ApplicableAsync(ChandleryDbContext db, Guid companyId, Guid vesselId)
    {
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == vesselId && v.CompanyId == companyId).Select(v => new { v.GroupId }).FirstOrDefaultAsync();
        if (vessel is null) return [];
        var chain = await GroupChainAsync(db, vessel.GroupId);
        var types = (await db.FormTypes.AsNoTracking().Where(t => t.CompanyId == companyId && t.IsActive && !t.OfficeOnly).OrderBy(t => t.Code).ToListAsync())
            .Where(t => FormApplicability.AppliesTo(t.AppliesToGroups, t.AppliesToVessels, vesselId, chain)).ToList();
        var ids = types.Select(t => t.Id).ToList();
        var published = (await db.FormVersions.AsNoTracking().Where(v => ids.Contains(v.FormTypeId) && v.Status == FormVersionStatus.Published).ToListAsync())
            .GroupBy(v => v.FormTypeId).ToDictionary(g => g.Key, g => g.OrderBy(v => v.Number).Last());
        return types.Where(t => published.ContainsKey(t.Id)).Select(t => (t, published[t.Id])).ToList();
    }

    /// <summary>What is due for a vessel, form by form, worked out by <see cref="FormSchedule"/> from the entries that have been submitted.</summary>
    public async Task<List<FormDueRow>> DueAsync(Guid companyId, Guid vesselId, DateOnly today)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var applicable = await ApplicableAsync(db, companyId, vesselId);
        var typeIds = applicable.Select(a => a.Type.Id).ToList();
        var dates = (await db.FormEntries.AsNoTracking().Where(e => e.VesselId == vesselId && typeIds.Contains(e.FormTypeId) && e.SubmissionCount > 0).Select(e => new { e.FormTypeId, e.FormDate }).ToListAsync())
            .GroupBy(e => e.FormTypeId).ToDictionary(g => g.Key, g => g.Select(e => DateOnly.FromDateTime(e.FormDate)).ToList());
        return applicable.Select(a => new FormDueRow(a.Type, a.Version,
            FormSchedule.Compute(a.Type.Periodicity, a.Type.Basis, a.Type.StartsOn is { } s ? DateOnly.FromDateTime(s) : null, dates.GetValueOrDefault(a.Type.Id) ?? [], today))).ToList();
    }

    // ---- entries ----

    private static async Task<FormStatus> StatusOfAsync(ChandleryDbContext db, FormEntry entry)
        => FormStatusRules.Of(entry.SubmissionCount, await db.FormReviews.AsNoTracking().Where(r => r.EntryId == entry.Id).ToListAsync());

    /// <summary>
    /// Starts an entry on the current published version of a form, with its prefilled answers. The vessel starts its own; the office may start one for a vessel
    /// (flagged as filled ashore) and, for a form that is the office's own, one that never leaves the office. <paramref name="corrects"/> names a reviewed entry this one corrects.
    /// </summary>
    public async Task<FormEntry> StartAsync(Guid companyId, Guid vesselId, Guid typeId, DateTime formDate, Actor actor, Guid? corrects = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (formDate.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("A form cannot be dated in the future.");
        var vessel = await db.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId) ?? throw new InvalidOperationException("Vessel not found.");
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Forms are not switched on for this vessel.");
        var type = await db.FormTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Form not found.");
        if (!type.IsActive) throw new InvalidOperationException("This form is no longer in use.");
        if (type.OfficeOnly && db.Node.IsVessel) throw new InvalidOperationException("This form is filled by the office.");
        if (!type.OfficeOnly && !FormApplicability.AppliesTo(type.AppliesToGroups, type.AppliesToVessels, vesselId, await GroupChainAsync(db, vessel.GroupId)))
            throw new InvalidOperationException($"{type.Code} does not apply to {vessel.Name}.");
        var version = (await db.FormVersions.AsNoTracking().Where(v => v.FormTypeId == typeId && v.Status == FormVersionStatus.Published).ToListAsync()).OrderBy(v => v.Number).LastOrDefault()
            ?? throw new InvalidOperationException($"{type.Code} has no published version yet.");
        var def = FormDef.TryRead(version.Definition, out var error) ?? throw new InvalidOperationException($"The form definition cannot be read: {error}");

        JsonObject values;
        if (corrects is { } correctedId)
        {
            var corrected = await db.FormEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == correctedId && e.VesselId == vesselId && e.FormTypeId == typeId) ?? throw new InvalidOperationException("The entry to correct was not found.");
            if (await StatusOfAsync(db, corrected) != FormStatus.Reviewed) throw new InvalidOperationException("Only a reviewed entry is corrected by a new one; an earlier entry is still open.");
            values = JsonNode.Parse(corrected.Values) as JsonObject ?? [];
        }
        else
        {
            var previous = await db.FormEntries.AsNoTracking().Where(e => e.VesselId == vesselId && e.FormTypeId == typeId && e.SubmissionCount > 0).OrderByDescending(e => e.FormDate).ThenByDescending(e => e.CreatedAt).FirstOrDefaultAsync();
            values = FormValueRules.Initial(def, new FormPrefillContext(vessel.Name, vessel.ImoNumber ?? "", vessel.ShortCode, DateOnly.FromDateTime(DateTime.UtcNow),
                previous is null ? null : JsonNode.Parse(previous.Values) as JsonObject));
        }
        var check = FormValueRules.Check(def, values, DateOnly.FromDateTime(DateTime.UtcNow), forSubmit: false);

        var entry = new FormEntry
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, FormTypeId = typeId, VersionId = version.Id, VersionNumber = version.Number,
            FormCode = type.Code, FormTitle = type.Title, Code = await DocumentNumbers.NextAsync(db, companyId, vesselId, DocType.Form), FormDate = Day(formDate),
            Values = check.Clean.ToJsonString(), CorrectsEntryId = corrects, FilledAshore = !db.Node.IsVessel, OfficeOnly = type.OfficeOnly, CreatedByName = actor.Name,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, OwnerStage = Stage(db),
        };
        db.FormEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private static async Task<(FormEntry Entry, FormDef Def)> LoadEditableAsync(ChandleryDbContext db, Guid companyId, Guid entryId)
    {
        var entry = await db.FormEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == companyId) ?? throw new InvalidOperationException("Entry not found.");
        if (entry.OwnerStage != Stage(db)) throw new InvalidOperationException($"This entry is being filled in {(entry.OwnerStage == SyncStages.Vessel ? "onboard" : "by the office")}.");
        var status = await StatusOfAsync(db, entry);
        if (!FormStatusRules.IsEditable(status))
            throw new InvalidOperationException(status == FormStatus.Reviewed
                ? "This entry has been reviewed and is locked. Start a new entry to correct it."
                : $"This entry has been submitted ({status.ToString().ToLowerInvariant()}) and cannot be changed until it is returned.");
        var version = await db.FormVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == entry.VersionId) ?? throw new InvalidOperationException("The form version this entry was filled on is not here.");
        var def = FormDef.TryRead(version.Definition, out var error) ?? throw new InvalidOperationException($"The form definition cannot be read: {error}");
        return (entry, def);
    }

    /// <summary>
    /// Saves the answers typed so far, checked against the version the entry is on. What cannot be kept (a wrong type, a value out of range, an answer in a hidden field) is left
    /// out of what is stored and reported in the result, so the screen can show it; a draft needs no required answers yet.
    /// </summary>
    public async Task<FormCheck> SaveAsync(Guid companyId, Guid entryId, JsonObject values, DateTime? formDate = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (entry, def) = await LoadEditableAsync(db, companyId, entryId);
        if (formDate is { } date)
        {
            if (date.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("A form cannot be dated in the future.");
            entry.FormDate = Day(date);
        }
        var check = FormValueRules.Check(def, values, DateOnly.FromDateTime(DateTime.UtcNow), forSubmit: false);
        (entry.Values, entry.UpdatedAt) = (check.Clean.ToJsonString(), DateTime.UtcNow);
        await db.SaveChangesAsync();
        return check;
    }

    /// <summary>Submits the entry for review. Every required answer must be there and every answer valid; refused otherwise, with what is missing.</summary>
    public async Task<FormCheck> SubmitAsync(Guid companyId, Guid entryId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (entry, def) = await LoadEditableAsync(db, companyId, entryId);
        var values = JsonNode.Parse(entry.Values) as JsonObject ?? [];
        var check = FormValueRules.Check(def, values, DateOnly.FromDateTime(DateTime.UtcNow), forSubmit: true);
        if (check.Problems.Count > 0)
            throw new InvalidOperationException($"{check.Problems.Count} answer(s) need attention: {string.Join(" ", check.Problems.Take(3).Select(p => p.ToString()))}{(check.Problems.Count > 3 ? " …" : "")}");
        entry.Values = check.Clean.ToJsonString();
        entry.SubmissionCount++;
        (entry.SubmittedAt, entry.UpdatedAt, entry.ExceptionCount) = (DateTime.UtcNow, DateTime.UtcNow, check.Exceptions.Count);
        await db.SaveChangesAsync();
        return check;
    }

    /// <summary>
    /// The office's response to the current submission: start reviewing, accept (the entry is then locked) or return with comments (the vessel corrects it and submits again).
    /// A review is a new record; the entry itself is never edited by the reviewer.
    /// </summary>
    public async Task<FormReview> ReviewAsync(Guid companyId, Guid entryId, ReviewDecision decision, string comment, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        OfficeOnly(db, "Reviewing a form");
        var entry = await db.FormEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == companyId) ?? throw new InvalidOperationException("Entry not found.");
        var status = await StatusOfAsync(db, entry);
        if (status is FormStatus.Draft or FormStatus.Returned) throw new InvalidOperationException("This entry has not been submitted (or is back with the vessel).");
        if (status == FormStatus.Reviewed) throw new InvalidOperationException("This entry has already been reviewed.");
        if (decision == ReviewDecision.Started && status == FormStatus.InReview) throw new InvalidOperationException("The review has already been started.");
        if (decision == ReviewDecision.Returned && string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException("Say what needs correcting.");

        var review = new FormReview
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = entry.VesselId, EntryId = entryId, ForSubmission = entry.SubmissionCount, Decision = decision,
            Comment = (comment ?? "").Trim(), ReviewerName = actor.Name, AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.FormReviews.Add(review);
        await db.SaveChangesAsync();
        return review;
    }

    // ---- reading ----

    public async Task<FormEntryDetail?> GetAsync(Guid companyId, Guid entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var entry = await db.FormEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == companyId);
        if (entry is null) return null;
        var version = await db.FormVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == entry.VersionId);
        if (version is null) return null;
        var reviews = await db.FormReviews.AsNoTracking().Where(r => r.EntryId == entryId).OrderBy(r => r.At).ToListAsync();
        return new FormEntryDetail(entry, version, FormDef.TryRead(version.Definition, out _), reviews, FormStatusRules.Of(entry.SubmissionCount, reviews));
    }

    public async Task<List<FormEntryRow>> ListAsync(Guid companyId, Guid? vesselId = null, Guid? typeId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.FormEntries.AsNoTracking().Where(e => e.CompanyId == companyId);
        if (vesselId is { } vessel) query = query.Where(e => e.VesselId == vessel);
        if (typeId is { } type) query = query.Where(e => e.FormTypeId == type);
        var entries = await query.OrderByDescending(e => e.FormDate).ThenByDescending(e => e.CreatedAt).Take(500).ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        var reviews = (await db.FormReviews.AsNoTracking().Where(r => ids.Contains(r.EntryId)).ToListAsync()).ToLookup(r => r.EntryId);
        return entries.Select(e => new FormEntryRow(e, FormStatusRules.Of(e.SubmissionCount, reviews[e.Id]))).ToList();
    }

    public async Task<List<FormType>> TypesAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.FormTypes.AsNoTracking().Where(t => t.CompanyId == companyId).OrderBy(t => t.Code).ToListAsync();
    }

    public async Task<List<FormVersion>> VersionsAsync(Guid companyId, Guid typeId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.FormVersions.AsNoTracking().Where(v => v.CompanyId == companyId && v.FormTypeId == typeId).OrderByDescending(v => v.Number).ToListAsync();
    }
}
