using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record NewMachineryItem(Guid VesselId, string Name, string Code = "", Guid? ParentId = null, string Maker = "", string Model = "", string Serial = "",
    Guid? MachineryClassId = null, bool IsCritical = false, bool HasHourMeter = false);

/// <summary>A job to put on a vessel's plan: from a template (which supplies the definition), or a vessel's own (which supplies it here).</summary>
public record NewPlannedJob(Guid MachineryItemId, Guid? TemplateId = null, string Code = "", string Title = "", string Instructions = "", PmsJobType Type = PmsJobType.Check,
    int? CalendarInterval = null, PmsIntervalUnit CalendarUnit = PmsIntervalUnit.Months, int? HoursInterval = null, PmsAnchor Anchor = PmsAnchor.FromCompletion,
    DateTime? FixedAnchorDate = null, int? GraceDays = null, bool IsCritical = false, Guid? DepartmentId = null, int? EstimatedMinutes = null, bool RequiresVerification = false,
    DateTime? StartDate = null, DateTime? BaselineDoneDate = null, decimal? BaselineDoneHours = null);

public record NewCompletion(Guid PlannedJobId, DateTime DoneDate, decimal? HoursAtDone = null, string PerformedBy = "", int? TimeSpentMinutes = null, string Comments = "", bool IsImported = false);

public record TemplateInput(string Code, string Title, string Instructions = "", PmsJobType Type = PmsJobType.Check, int? CalendarInterval = null,
    PmsIntervalUnit CalendarUnit = PmsIntervalUnit.Months, int? HoursInterval = null, PmsAnchor Anchor = PmsAnchor.FromCompletion, int? GraceDays = null,
    bool IsCritical = false, Guid? DepartmentId = null, int? EstimatedMinutes = null, bool RequiresVerification = false, Guid? MachineryClassId = null, bool IsActive = true);

/// <summary>One job the library would add: this template on this machinery item.</summary>
public record LibrarySuggestion(MachineryItem Item, JobTemplate Template, bool AlreadyPlanned, bool NeedsHourMeter);

/// <summary>A deferral request with the decision that currently counts (the latest), if any.</summary>
public record DeferralView(JobDeferralRequest Request, JobDeferralDecision? Decision, List<JobDeferralDecision> AllDecisions);

public record PendingDeferral(JobDeferralRequest Request, PlannedJob Job, string Vessel, string Machinery, bool MasterMayApprove);

/// <summary>What the Master may approve onboard without the office. Fixed for now; a policy setting (spec 07 section 9) will replace the constants.</summary>
public static class PmsDeferralRules
{
    /// <summary>The longest a Master may put a job off beyond its due date.</summary>
    public const int MasterMaxDays = 14;

    public static bool MasterMayApprove(bool critical, DateTime currentDue, DateTime approvedDate) => !critical && (approvedDate.Date - currentDue.Date).Days <= MasterMaxDays;
}

/// <summary>The definition of a job as its owner edits it.</summary>
public record JobEdit(string Title, string Instructions, PmsJobType Type, int? CalendarInterval, PmsIntervalUnit CalendarUnit, int? HoursInterval, PmsAnchor Anchor,
    int? GraceDays, bool IsCritical, Guid? DepartmentId, int? EstimatedMinutes, bool RequiresVerification, bool IsActive = true);

public record MachineryEdit(string Name, string Code, string Maker, string Model, string Serial, Guid? MachineryClassId, bool IsCritical, bool HasHourMeter, bool IsActive = true);

/// <summary>What a vessel proposes to change. Only the values that are set are changed.</summary>
public record PlanChangeProposal(
    int? CalendarInterval = null, PmsIntervalUnit? CalendarUnit = null, int? HoursInterval = null, int? GraceDays = null,
    string? Name = null, bool? IsCritical = null, bool? HasHourMeter = null, Guid? MachineryClassId = null);

public record NewPlanChange(PlanChangeKind Kind, Guid? PlannedJobId, Guid? MachineryItemId, PlanChangeProposal Proposed, string Reason);

public record PlanChangeView(PlanChangeRequest Request, PlanChangeDecision? Decision, string Vessel, string Target);

public record TemplateChange(string Field, string From, string To);

public record TemplateUpdate(PlannedJob Job, JobTemplate Template, string Vessel, string Machinery, List<TemplateChange> Changes, bool CanApplyHere);

/// <summary>What would change on a planned job if it took on its template's current version. Pure.</summary>
public static class PmsTemplateDiff
{
    public static List<TemplateChange> Of(PlannedJob job, JobTemplate t)
    {
        var changes = new List<TemplateChange>();
        void Add<T>(string field, T from, T to, Func<T, string>? show = null)
        {
            if (EqualityComparer<T>.Default.Equals(from, to)) return;
            show ??= v => v?.ToString() ?? "none";
            changes.Add(new TemplateChange(field, show(from), show(to)));
        }
        Add("Title", job.Title, t.Title);
        Add("Checklist", job.Instructions, t.Instructions, v => v.Replace("\n", " / ").Trim() is { Length: > 0 } x ? (x.Length > 80 ? x[..80] + "…" : x) : "none");
        Add("Type", job.Type, t.Type);
        Add("Calendar interval", Interval(job.CalendarInterval, job.CalendarUnit), Interval(t.CalendarInterval, t.CalendarUnit));
        Add("Running hours interval", job.HoursInterval, t.HoursInterval, v => v is { } h ? $"{h:N0} h" : "none");
        Add("Counted", job.Anchor, t.Anchor, v => v == PmsAnchor.FixedCalendar ? "on fixed dates" : "from the last completion");
        Add("Grace", job.GraceDays, t.GraceDays, v => v is { } g ? $"{g} days" : "default");
        Add("Critical", job.IsCritical, t.IsCritical, v => v ? "yes" : "no");
        Add("Department", job.DepartmentId, t.DepartmentId, v => v is null ? "none" : "changed");
        Add("Estimated time", job.EstimatedMinutes, t.EstimatedMinutes, v => v is { } m ? $"{m} min" : "none");
        Add("Second sign-off", job.RequiresVerification, t.RequiresVerification, v => v ? "required" : "not required");
        return changes;
    }

    private static string Interval(int? n, PmsIntervalUnit unit) => n is > 0 ? $"{n} {unit.ToString().ToLowerInvariant()}" : "none";
}

public record PmsJobDetail(PlannedJob Job, MachineryItem Item, List<JobCompletion> Completions, HashSet<Guid> VerifiedCompletionIds);

/// <summary>
/// A vessel's maintenance plan and what happens on it. Definitions (machinery, jobs) are changed only by the side that made them; readings,
/// completions and verifications are only ever added. Each job's schedule is worked out by <see cref="PmsScheduler"/> from those records.
/// </summary>
public class PmsService(IDbContextFactory<ChandleryDbContext> factory)
{
    // ---- the module switch ----

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Pms);

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Pms);
    }

    /// <summary>Is planned maintenance on for at least one vessel of the company (for showing it in the menu)?</summary>
    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId)
        => (await EnabledVesselsAsync(companyId)).Count > 0;

    private static async Task RequireEnabledAsync(ChandleryDbContext db, Guid companyId, Guid vesselId)
    {
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Planned maintenance is not switched on for this vessel.");
    }

    // ---- the library (office) ----

    public async Task<MachineryClass> SaveMachineryClassAsync(Guid companyId, Guid? id, string name, Guid? parentId, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the machinery class a name.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        if (parentId is { } parent && !await db.MachineryClasses.AnyAsync(c => c.Id == parent && c.CompanyId == companyId)) throw new InvalidOperationException("The parent class does not exist.");
        if (id is { } own && parentId == own) throw new InvalidOperationException("A class cannot be its own parent.");

        var item = id is { } existing ? await db.MachineryClasses.FirstOrDefaultAsync(c => c.Id == existing && c.CompanyId == companyId) ?? throw new InvalidOperationException("Class not found.")
            : db.MachineryClasses.Add(new MachineryClass { Id = Guid.CreateVersion7(), CompanyId = companyId }).Entity;
        item.Name = name.Trim();
        item.ParentId = parentId;
        item.IsActive = isActive;
        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>Adds a template, or changes one (its version goes up, so vessels can be offered the change).</summary>
    public async Task<JobTemplate> SaveJobTemplateAsync(Guid companyId, Guid? id, TemplateInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Code) || string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("A template needs a code and a title.");
        RequireInterval(input.CalendarInterval, input.HoursInterval);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var code = input.Code.Trim();
        if (await db.JobTemplates.AnyAsync(t => t.CompanyId == companyId && t.Code == code && t.Id != id)) throw new InvalidOperationException($"A template with the code {code} already exists.");
        if (input.MachineryClassId is { } cls && !await db.MachineryClasses.AnyAsync(c => c.Id == cls && c.CompanyId == companyId)) throw new InvalidOperationException("That machinery class does not exist.");
        if (input.DepartmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept && d.CompanyId == companyId)) throw new InvalidOperationException("That department does not exist.");

        JobTemplate t;
        if (id is { } existing)
        {
            t = await db.JobTemplates.FirstOrDefaultAsync(x => x.Id == existing && x.CompanyId == companyId) ?? throw new InvalidOperationException("Template not found.");
            t.Version++;
        }
        else t = db.JobTemplates.Add(new JobTemplate { Id = Guid.CreateVersion7(), CompanyId = companyId }).Entity;
        (t.Code, t.Title, t.Instructions, t.Type, t.CalendarInterval, t.CalendarUnit, t.HoursInterval, t.Anchor) =
            (code, input.Title.Trim(), input.Instructions.Trim(), input.Type, input.CalendarInterval, input.CalendarUnit, input.HoursInterval, input.Anchor);
        (t.GraceDays, t.IsCritical, t.DepartmentId, t.EstimatedMinutes, t.RequiresVerification, t.MachineryClassId, t.IsActive) =
            (input.GraceDays, input.IsCritical, input.DepartmentId, input.EstimatedMinutes, input.RequiresVerification, input.MachineryClassId, input.IsActive);
        await db.SaveChangesAsync();
        return t;
    }

    // ---- the plan (either side; each owns what it makes) ----

    public async Task<MachineryItem> AddMachineryItemAsync(Guid companyId, NewMachineryItem input, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new InvalidOperationException("Give the machinery a name.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        await RequireEnabledAsync(db, companyId, input.VesselId);
        if (input.ParentId is { } parent && !await db.MachineryItems.AnyAsync(m => m.Id == parent && m.VesselId == input.VesselId)) throw new InvalidOperationException("The parent machinery is not on this vessel.");
        if (input.MachineryClassId is { } cls && !await db.MachineryClasses.AnyAsync(c => c.Id == cls)) throw new InvalidOperationException("That machinery class does not exist.");

        var item = new MachineryItem
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = input.VesselId, ParentId = input.ParentId, Code = input.Code.Trim(), Name = input.Name.Trim(),
            Maker = input.Maker.Trim(), Model = input.Model.Trim(), Serial = input.Serial.Trim(), MachineryClassId = input.MachineryClassId, IsCritical = input.IsCritical,
            HasHourMeter = input.HasHourMeter, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        db.MachineryItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    public async Task<PlannedJob> AddJobAsync(Guid companyId, NewPlannedJob input, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.MachineryItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == input.MachineryItemId && m.CompanyId == companyId) ?? throw new InvalidOperationException("That machinery is not on the plan.");
        await RequireEnabledAsync(db, companyId, item.VesselId);
        var job = await StageJobAsync(db, companyId, item, input, actor);
        await db.SaveChangesAsync();
        return job;
    }

    /// <summary>Checks a job and adds it to the context with its schedule worked out, without saving (so several can be saved together).</summary>
    internal static async Task<PlannedJob> StageJobAsync(ChandleryDbContext db, Guid companyId, MachineryItem item, NewPlannedJob input, Actor actor, JobTemplate? known = null)
    {
        var job = new PlannedJob
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = item.VesselId, MachineryItemId = item.Id, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
            StartDate = Day(input.StartDate ?? DateTime.UtcNow), BaselineDoneDate = input.BaselineDoneDate is { } baseline ? Day(baseline) : null, BaselineDoneHours = input.BaselineDoneHours,
        };
        if (input.TemplateId is { } templateId)
        {
            var t = known ?? await db.JobTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == templateId && x.CompanyId == companyId) ?? throw new InvalidOperationException("That template does not exist.");
            if (!t.IsActive) throw new InvalidOperationException("That template is no longer in use.");
            (job.TemplateId, job.TemplateVersion, job.Code, job.Title, job.Instructions, job.Type) = (t.Id, t.Version, t.Code, t.Title, t.Instructions, t.Type);
            (job.CalendarInterval, job.CalendarUnit, job.HoursInterval, job.Anchor, job.GraceDays) = (t.CalendarInterval, t.CalendarUnit, t.HoursInterval, t.Anchor, t.GraceDays);
            (job.IsCritical, job.DepartmentId, job.EstimatedMinutes, job.RequiresVerification) = (t.IsCritical, t.DepartmentId, t.EstimatedMinutes, t.RequiresVerification);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(input.Code) || string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("A job needs a code and a title.");
            if (input.DepartmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept && d.CompanyId == companyId)) throw new InvalidOperationException("That department does not exist.");
            (job.Code, job.Title, job.Instructions, job.Type) = (input.Code.Trim(), input.Title.Trim(), input.Instructions.Trim(), input.Type);
            (job.CalendarInterval, job.CalendarUnit, job.HoursInterval, job.Anchor, job.GraceDays) = (input.CalendarInterval, input.CalendarUnit, input.HoursInterval, input.Anchor, input.GraceDays);
            (job.IsCritical, job.DepartmentId, job.EstimatedMinutes, job.RequiresVerification) = (input.IsCritical, input.DepartmentId, input.EstimatedMinutes, input.RequiresVerification);
        }
        job.FixedAnchorDate = job.Anchor == PmsAnchor.FixedCalendar ? Day(input.FixedAnchorDate ?? job.StartDate) : null;
        RequireInterval(job.CalendarInterval, job.HoursInterval);
        if (job.HoursInterval is > 0 && !item.HasHourMeter) throw new InvalidOperationException($"{job.Code}: running hours need an hour meter on {item.Name}.");

        var readings = await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == item.Id).ToListAsync();
        PmsProjection.Apply(job, PmsScheduler.Compute(PmsProjection.InputOf(job), [], readings.Select(PmsProjection.InputOf)));
        db.PlannedJobs.Add(job);
        return job;
    }

    // ---- applying the library to a vessel ----

    public async Task<List<MachineryClass>> ListClassesAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.MachineryClasses.AsNoTracking().Where(c => c.CompanyId == companyId);
        if (!includeInactive) query = query.Where(c => c.IsActive);
        return await query.OrderBy(c => c.Name).ToListAsync();
    }

    /// <summary>Maps a machinery item to a library class. Only the side that made the item may change it.</summary>
    public async Task SetMachineryClassAsync(Guid companyId, Guid machineryItemId, Guid? classId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.MachineryItems.FirstOrDefaultAsync(m => m.Id == machineryItemId && m.CompanyId == companyId) ?? throw new InvalidOperationException("That machinery is not on the plan.");
        if (classId is { } cls && !await db.MachineryClasses.AnyAsync(c => c.Id == cls && c.CompanyId == companyId)) throw new InvalidOperationException("That machinery class does not exist.");
        item.MachineryClassId = classId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The jobs the library would add to a vessel: for every machinery item mapped to a class, each active template of that class or of any class above it.
    /// A template already planned on the item (the same template) is shown as already there.
    /// </summary>
    public async Task<List<LibrarySuggestion>> PreviewLibraryAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var classes = await db.MachineryClasses.AsNoTracking().Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Id);
        var templates = await db.JobTemplates.AsNoTracking().Where(t => t.CompanyId == companyId && t.IsActive && t.MachineryClassId != null).ToListAsync();
        var items = await db.MachineryItems.AsNoTracking().Where(m => m.CompanyId == companyId && m.VesselId == vesselId && m.IsActive && m.MachineryClassId != null).OrderBy(m => m.Name).ToListAsync();
        var planned = (await db.PlannedJobs.AsNoTracking().Where(j => j.VesselId == vesselId && j.TemplateId != null).Select(j => new { j.MachineryItemId, j.TemplateId }).ToListAsync())
            .Select(j => (j.MachineryItemId, j.TemplateId!.Value)).ToHashSet();

        var result = new List<LibrarySuggestion>();
        foreach (var item in items)
        {
            var chain = new HashSet<Guid>();
            for (var cls = item.MachineryClassId; cls is { } id && chain.Add(id); cls = classes.GetValueOrDefault(id)?.ParentId) { }
            foreach (var t in templates.Where(t => chain.Contains(t.MachineryClassId!.Value)).OrderBy(t => t.Code))
                result.Add(new LibrarySuggestion(item, t, planned.Contains((item.Id, t.Id)), t.HoursInterval is > 0 && !item.HasHourMeter));
        }
        return result;
    }

    /// <summary>Adds the chosen template jobs to the plan, all or none. Jobs start counting today, or from the date of their last completion when one is given.</summary>
    public async Task<List<PlannedJob>> ApplyLibraryAsync(Guid companyId, Guid vesselId, IEnumerable<(Guid MachineryItemId, Guid TemplateId)> chosen, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The library is applied by the office.");
        await RequireEnabledAsync(db, companyId, vesselId);

        var pairs = chosen.Distinct().ToList();
        var items = await db.MachineryItems.AsNoTracking().Where(m => m.CompanyId == companyId && m.VesselId == vesselId).ToDictionaryAsync(m => m.Id);
        var templateIds = pairs.Select(p => p.TemplateId).Distinct().ToList();
        var templates = await db.JobTemplates.AsNoTracking().Where(t => t.CompanyId == companyId && templateIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var existing = (await db.PlannedJobs.AsNoTracking().Where(j => j.VesselId == vesselId && j.TemplateId != null).Select(j => new { j.MachineryItemId, j.TemplateId }).ToListAsync())
            .Select(j => (j.MachineryItemId, j.TemplateId!.Value)).ToHashSet();

        var created = new List<PlannedJob>();
        foreach (var (itemId, templateId) in pairs)
        {
            if (existing.Contains((itemId, templateId))) continue;
            var item = items.GetValueOrDefault(itemId) ?? throw new InvalidOperationException("One of the machinery items is not on this vessel.");
            var template = templates.GetValueOrDefault(templateId) ?? throw new InvalidOperationException("One of the templates does not exist.");
            created.Add(await StageJobAsync(db, companyId, item, new NewPlannedJob(itemId, templateId), actor, template));
        }
        await db.SaveChangesAsync();
        return created;
    }

    // ---- things that happen (either side; only ever added) ----

    public async Task<RunningHoursReading> RecordReadingAsync(Guid companyId, Guid machineryItemId, DateTime date, decimal hours, bool isCorrection, string note, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.MachineryItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineryItemId && m.CompanyId == companyId) ?? throw new InvalidOperationException("That machinery is not on the plan.");
        await RequireEnabledAsync(db, companyId, item.VesselId);
        if (!item.HasHourMeter) throw new InvalidOperationException("This machinery has no hour meter.");
        if (hours < 0) throw new InvalidOperationException("Running hours cannot be negative.");
        if (date.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("A reading cannot be dated in the future.");

        if (!isCorrection)
        {
            // Hours only go up. A reading that does not is either a mistake or the correction of one; say so rather than store it quietly.
            var effective = PmsScheduler.EffectiveReadings((await db.RunningHoursReadings.AsNoTracking().Where(r => r.MachineryItemId == item.Id).ToListAsync()).Select(PmsProjection.InputOf));
            var before = effective.LastOrDefault(r => r.Date.Date < date.Date);
            var after = effective.FirstOrDefault(r => r.Date.Date > date.Date);
            if (effective.FirstOrDefault(r => r.Date.Date == date.Date) is { } same && same.Hours != hours)
                throw new InvalidOperationException("There is already a reading for that date. To change it, enter the right figure as a correction.");
            if (before is not null && hours < before.Hours || after is not null && hours > after.Hours)
                throw new InvalidOperationException("Running hours cannot go down between readings. If an earlier reading was wrong, enter this one as a correction.");
        }

        var reading = new RunningHoursReading
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = item.VesselId, MachineryItemId = item.Id, ReadingDate = Day(date), Hours = hours,
            IsCorrection = isCorrection, Note = (note ?? "").Trim(), AuthorUserId = actor.UserId, AuthorName = actor.Name, At = DateTime.UtcNow,
        };
        db.RunningHoursReadings.Add(reading);
        await db.SaveChangesAsync();
        await PmsProjection.RefreshMachineryAsync(db, item.Id);
        return reading;
    }

    public Task<JobCompletion> CompleteJobAsync(Guid companyId, NewCompletion input, Actor actor) => CompleteCoreAsync(companyId, input, actor, byInspection: false)!;

    /// <summary>
    /// Completes an inspection job because its tank inspection was submitted. Returns null when planned maintenance is off for the vessel (the inspection stands on its own then).
    /// </summary>
    public Task<JobCompletion?> CompleteJobByInspectionAsync(Guid companyId, Guid jobId, DateTime doneDate, string comments, Actor actor)
        => CompleteCoreAsync(companyId, new NewCompletion(jobId, doneDate, Comments: comments), actor, byInspection: true);

    private async Task<JobCompletion?> CompleteCoreAsync(Guid companyId, NewCompletion input, Actor actor, bool byInspection)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == input.PlannedJobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Job not found.");
        if (byInspection && !await ModuleGate.IsEnabledAsync(db, companyId, job.VesselId, PolicyModules.Pms)) return null;
        await RequireEnabledAsync(db, companyId, job.VesselId);
        if (job.TankId is not null && !byInspection) throw new InvalidOperationException("This job is done by inspecting the tank: complete it by submitting the tank inspection.");
        if (!job.IsActive) throw new InvalidOperationException("This job is no longer on the plan.");
        if (input.DoneDate.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("A job cannot be done in the future.");
        if (input.HoursAtDone is < 0) throw new InvalidOperationException("Running hours cannot be negative.");
        if (input.TimeSpentMinutes is < 0) throw new InvalidOperationException("Time spent cannot be negative.");

        var completion = new JobCompletion
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = job.VesselId, PlannedJobId = job.Id, DoneDate = Day(input.DoneDate), HoursAtDone = input.HoursAtDone,
            PerformedBy = input.PerformedBy.Trim() == "" ? actor.Name : input.PerformedBy.Trim(), TimeSpentMinutes = input.TimeSpentMinutes, Comments = input.Comments.Trim(),
            IsImported = input.IsImported, AuthorUserId = actor.UserId, AuthorName = actor.Name, At = DateTime.UtcNow,
        };
        db.JobCompletions.Add(completion);
        await db.SaveChangesAsync();
        await PmsProjection.RefreshJobAsync(db, job.Id);
        return completion;
    }

    public async Task<JobVerification> VerifyCompletionAsync(Guid companyId, Guid completionId, string note, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var completion = await db.JobCompletions.AsNoTracking().FirstOrDefaultAsync(c => c.Id == completionId && c.CompanyId == companyId) ?? throw new InvalidOperationException("Completion not found.");
        var job = await db.PlannedJobs.AsNoTracking().FirstAsync(j => j.Id == completion.PlannedJobId);
        await RequireEnabledAsync(db, companyId, job.VesselId);
        if (!job.RequiresVerification) throw new InvalidOperationException("This job does not need a second sign-off.");
        if (await db.JobVerifications.AnyAsync(v => v.CompletionId == completionId)) throw new InvalidOperationException("This completion is already verified.");

        var verification = new JobVerification
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = job.VesselId, CompletionId = completionId, VerifiedBy = actor.Name,
            Note = (note ?? "").Trim(), AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.JobVerifications.Add(verification);
        await db.SaveChangesAsync();
        return verification;
    }

    // ---- reading ----

    public async Task<List<PlannedJob>> ListJobsAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.PlannedJobs.AsNoTracking().Where(j => j.CompanyId == companyId && j.IsActive);
        if (vesselId is { } v) query = query.Where(j => j.VesselId == v);
        return await query.OrderBy(j => j.NextDueDate == null).ThenBy(j => j.NextDueDate).ThenBy(j => j.Code).ToListAsync();
    }

    /// <summary>The templates a job can be made from (those in use).</summary>
    public async Task<List<JobTemplate>> ListTemplatesAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.JobTemplates.AsNoTracking().Where(t => t.CompanyId == companyId);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        return await query.OrderBy(t => t.Code).ToListAsync();
    }

    /// <summary>The latest reading of each hour meter on the vessel (the effective one: a correction beats an ordinary reading for its date).</summary>
    public async Task<Dictionary<Guid, RunningHoursReading>> LatestReadingsAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var readings = await db.RunningHoursReadings.AsNoTracking().Where(r => r.CompanyId == companyId && r.VesselId == vesselId).ToListAsync();
        var result = new Dictionary<Guid, RunningHoursReading>();
        foreach (var group in readings.GroupBy(r => r.MachineryItemId))
        {
            var effective = PmsScheduler.EffectiveReadings(group.Select(PmsProjection.InputOf));
            if (effective.LastOrDefault() is { } latest) result[group.Key] = group.First(r => r.Id == latest.Id);
        }
        return result;
    }

    public async Task<List<MachineryItem>> ListMachineryAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.MachineryItems.AsNoTracking().Where(m => m.CompanyId == companyId && m.VesselId == vesselId && m.IsActive).OrderBy(m => m.Name).ToListAsync();
    }

    public async Task<PmsJobDetail?> GetJobAsync(Guid companyId, Guid jobId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId);
        if (job is null) return null;
        var item = await db.MachineryItems.AsNoTracking().FirstAsync(m => m.Id == job.MachineryItemId);
        var completions = (await db.JobCompletions.AsNoTracking().Where(c => c.PlannedJobId == jobId).ToListAsync()).OrderByDescending(c => c.DoneDate).ThenByDescending(c => c.At).ToList();
        var ids = completions.Select(c => c.Id).ToList();
        var verified = (await db.JobVerifications.AsNoTracking().Where(v => ids.Contains(v.CompletionId)).Select(v => v.CompletionId).ToListAsync()).ToHashSet();
        return new PmsJobDetail(job, item, completions, verified);
    }

    public async Task<List<RunningHoursReading>> ReadingsAsync(Guid companyId, Guid machineryItemId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await db.RunningHoursReadings.AsNoTracking().Where(r => r.CompanyId == companyId && r.MachineryItemId == machineryItemId).ToListAsync())
            .OrderByDescending(r => r.ReadingDate).ThenByDescending(r => r.At).ToList();
    }

    // ---- deferrals ----

    /// <summary>Asks to put a job off to a later date. It changes nothing until it is approved.</summary>
    public async Task<JobDeferralRequest> RequestDeferralAsync(Guid companyId, Guid jobId, DateTime requestedDue, string reason, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Job not found.");
        await RequireEnabledAsync(db, companyId, job.VesselId);
        if (!job.IsActive) throw new InvalidOperationException("This job is no longer on the plan.");
        if (job.NextDueDate is not { } due) throw new InvalidOperationException("This job has no due date to put off.");
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Say why the job needs to be put off.");
        var requested = Day(requestedDue);
        if (requested <= Day(due)) throw new InvalidOperationException($"The new date must be after the current due date ({due:dd MMM yyyy}).");
        if (requested > Day(due).AddYears(1)) throw new InvalidOperationException("A job cannot be put off by more than a year.");

        var sameCycle = await db.JobDeferralRequests.AsNoTracking().Where(r => r.PlannedJobId == jobId && r.LastDoneAtRequest == job.LastDoneDate).Select(r => r.Id).ToListAsync();
        if (sameCycle.Count > 0 && await db.JobDeferralDecisions.AsNoTracking().CountAsync(d => sameCycle.Contains(d.RequestId)) < sameCycle.Count)
            throw new InvalidOperationException("A request for this job is already waiting for a decision.");

        var request = new JobDeferralRequest
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = job.VesselId, PlannedJobId = jobId, CurrentDueDate = Day(due), RequestedDueDate = requested,
            LastDoneAtRequest = job.LastDoneDate, Reason = reason.Trim(), IsCritical = job.IsCritical, RequestedByName = actor.Name, AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.JobDeferralRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>
    /// Decides a request. The office may decide any; onboard, only the Master may, and only to approve within <see cref="PmsDeferralRules"/>
    /// (a critical job, or more than the limit, is the office's to approve). Anyone entitled may reject. The date granted cannot be later than the one asked for.
    /// </summary>
    public async Task<JobDeferralDecision> DecideDeferralAsync(Guid companyId, Guid requestId, bool approve, DateTime? approvedDate, string comment, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var request = await db.JobDeferralRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId) ?? throw new InvalidOperationException("Request not found.");
        await RequireEnabledAsync(db, companyId, request.VesselId);
        if (await db.JobDeferralDecisions.AnyAsync(d => d.RequestId == requestId)) throw new InvalidOperationException("This request has already been decided.");
        var job = await db.PlannedJobs.AsNoTracking().FirstAsync(j => j.Id == request.PlannedJobId);
        if (job.LastDoneDate != request.LastDoneAtRequest) throw new InvalidOperationException("The job has been done since this was asked, so there is nothing to decide.");

        var granted = approve ? Day(approvedDate ?? request.RequestedDueDate) : (DateTime?)null;
        if (granted is { } g)
        {
            if (g <= Day(request.CurrentDueDate)) throw new InvalidOperationException("The date granted must be after the current due date.");
            if (g > Day(request.RequestedDueDate)) throw new InvalidOperationException("The date granted cannot be later than the one asked for.");
        }

        var role = "office";
        if (db.Node.IsVessel)
        {
            role = "master";
            if (actor.UserId is not { } uid || !await db.CrewMembers.AsNoTracking().AnyAsync(c => c.UserId == uid && c.IsMaster && c.IsActive && c.LoginEnabled && c.VesselId == request.VesselId))
                throw new UnauthorizedAccessException("Only the Master can decide a deferral onboard.");
            if (granted is { } date && !PmsDeferralRules.MasterMayApprove(request.IsCritical, request.CurrentDueDate, date))
                throw new InvalidOperationException(request.IsCritical
                    ? "This is a critical job: the office decides its deferral."
                    : $"The Master may put a job off by up to {PmsDeferralRules.MasterMaxDays} days; beyond that the office decides.");
        }
        if (!approve && string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException("Say why the request is refused.");

        var decision = new JobDeferralDecision
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = request.VesselId, RequestId = requestId, Approved = approve, ApprovedDueDate = granted, Comment = (comment ?? "").Trim(),
            DecidedByName = actor.Name, DecidedByRole = role, IsCritical = request.IsCritical, AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.JobDeferralDecisions.Add(decision);
        await db.SaveChangesAsync();
        await PmsProjection.RefreshJobAsync(db, job.Id);
        return decision;
    }

    /// <summary>Can this person approve deferrals onboard (is the Master of the vessel)? The office always can.</summary>
    public async Task<bool> MayDecideDeferralsAsync(Guid companyId, Guid vesselId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!db.Node.IsVessel) return true;
        return actor.UserId is { } uid && await db.CrewMembers.AsNoTracking().AnyAsync(c => c.UserId == uid && c.IsMaster && c.IsActive && c.LoginEnabled && c.VesselId == vesselId);
    }

    public async Task<List<DeferralView>> DeferralsOfJobAsync(Guid companyId, Guid jobId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var requests = (await db.JobDeferralRequests.AsNoTracking().Where(r => r.CompanyId == companyId && r.PlannedJobId == jobId).ToListAsync()).OrderByDescending(r => r.At).ToList();
        var ids = requests.Select(r => r.Id).ToList();
        var decisions = (await db.JobDeferralDecisions.AsNoTracking().Where(d => ids.Contains(d.RequestId)).ToListAsync()).ToLookup(d => d.RequestId);
        return requests.Select(r =>
        {
            var all = decisions[r.Id].OrderBy(d => d.At).ThenBy(d => d.Id).ToList();
            return new DeferralView(r, all.LastOrDefault(), all);
        }).ToList();
    }

    /// <summary>Requests still waiting for a decision (not those for a cycle the job has since finished).</summary>
    public async Task<List<PendingDeferral>> PendingDeferralsAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Pms)).ToDictionary(v => v.Id, v => v.Name);
        var query = db.JobDeferralRequests.AsNoTracking().Where(r => r.CompanyId == companyId && !db.JobDeferralDecisions.Any(d => d.RequestId == r.Id));
        if (vesselId is { } v) query = query.Where(r => r.VesselId == v);
        var requests = (await query.ToListAsync()).Where(r => enabled.ContainsKey(r.VesselId)).OrderBy(r => r.At).ToList();
        if (requests.Count == 0) return [];

        var jobIds = requests.Select(r => r.PlannedJobId).Distinct().ToList();
        var jobs = await db.PlannedJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id);
        var itemIds = jobs.Values.Select(j => j.MachineryItemId).Distinct().ToList();
        var items = await db.MachineryItems.AsNoTracking().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        return requests.Where(r => jobs.TryGetValue(r.PlannedJobId, out var j) && j.LastDoneDate == r.LastDoneAtRequest).Select(r =>
        {
            var j = jobs[r.PlannedJobId];
            return new PendingDeferral(r, j, enabled[r.VesselId], items.GetValueOrDefault(j.MachineryItemId, ""),
                PmsDeferralRules.MasterMayApprove(r.IsCritical, r.CurrentDueDate, r.RequestedDueDate));
        }).ToList();
    }

    // ---- editing the plan (the side that made it) ----

    /// <summary>Changes a job's definition. Only the side that made the job may; the other side asks with a plan change request.</summary>
    public async Task UpdateJobAsync(Guid companyId, Guid jobId, JobEdit edit, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(edit.Title)) throw new InvalidOperationException("A job needs a title.");
        RequireInterval(edit.CalendarInterval, edit.HoursInterval);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Job not found.");
        await RequireEnabledAsync(db, companyId, job.VesselId);
        var item = await db.MachineryItems.AsNoTracking().FirstAsync(m => m.Id == job.MachineryItemId);
        if (edit.HoursInterval is > 0 && !item.HasHourMeter) throw new InvalidOperationException($"Running hours need an hour meter on {item.Name}.");
        if (edit.DepartmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept && d.CompanyId == companyId)) throw new InvalidOperationException("That department does not exist.");

        (job.Title, job.Instructions, job.Type) = (edit.Title.Trim(), edit.Instructions.Trim(), edit.Type);
        (job.CalendarInterval, job.CalendarUnit, job.HoursInterval, job.Anchor, job.GraceDays) = (edit.CalendarInterval, edit.CalendarUnit, edit.HoursInterval, edit.Anchor, edit.GraceDays);
        (job.IsCritical, job.DepartmentId, job.EstimatedMinutes, job.RequiresVerification, job.IsActive) = (edit.IsCritical, edit.DepartmentId, edit.EstimatedMinutes, edit.RequiresVerification, edit.IsActive);
        job.FixedAnchorDate = job.Anchor == PmsAnchor.FixedCalendar ? job.FixedAnchorDate ?? job.StartDate : null;
        await db.SaveChangesAsync();
        await PmsProjection.RefreshJobAsync(db, job.Id);
    }

    /// <summary>Says which tank an inspection job is done on (or clears it). The tank must be on the same vessel; only the side that made the job may change it.</summary>
    public async Task SetJobTankAsync(Guid companyId, Guid jobId, Guid? tankId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Job not found.");
        await RequireEnabledAsync(db, companyId, job.VesselId);
        if (tankId is { } t)
        {
            if (job.Type != PmsJobType.Inspection) throw new InvalidOperationException("Only an inspection job can be done on a tank.");
            var tank = await db.Tanks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == t && x.CompanyId == companyId) ?? throw new InvalidOperationException("Tank not found.");
            if (tank.VesselId != job.VesselId) throw new InvalidOperationException("That tank is on another vessel.");
            if (!tank.IsActive) throw new InvalidOperationException("That tank is no longer in use.");
        }
        job.TankId = tankId;
        await db.SaveChangesAsync();
    }

    public async Task UpdateMachineryAsync(Guid companyId, Guid itemId, MachineryEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.Name)) throw new InvalidOperationException("Give the machinery a name.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.MachineryItems.FirstOrDefaultAsync(m => m.Id == itemId && m.CompanyId == companyId) ?? throw new InvalidOperationException("Machinery not found.");
        await RequireEnabledAsync(db, companyId, item.VesselId);
        if (!edit.HasHourMeter && item.HasHourMeter && await db.PlannedJobs.AnyAsync(j => j.MachineryItemId == itemId && j.IsActive && j.HoursInterval > 0))
            throw new InvalidOperationException("Jobs on this machinery run on hours, so the hour meter has to stay.");
        if (edit.MachineryClassId is { } cls && !await db.MachineryClasses.AnyAsync(c => c.Id == cls && c.CompanyId == companyId)) throw new InvalidOperationException("That machinery class does not exist.");
        (item.Name, item.Code, item.Maker, item.Model, item.Serial) = (edit.Name.Trim(), edit.Code.Trim(), edit.Maker.Trim(), edit.Model.Trim(), edit.Serial.Trim());
        (item.MachineryClassId, item.IsCritical, item.HasHourMeter, item.IsActive) = (edit.MachineryClassId, edit.IsCritical, edit.HasHourMeter, edit.IsActive);
        await db.SaveChangesAsync();
    }

    // ---- plan change requests (a vessel asking the office to change what the office made) ----

    public async Task<PlanChangeRequest> RequestPlanChangeAsync(Guid companyId, NewPlanChange input, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(input.Reason)) throw new InvalidOperationException("Say why the change is needed.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!db.Node.IsVessel) throw new InvalidOperationException("Plan change requests are made onboard; the office changes the plan directly.");

        Guid vesselId;
        string owner;
        if (input.Kind == PlanChangeKind.MachineryChange)
        {
            var item = await db.MachineryItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == input.MachineryItemId && m.CompanyId == companyId) ?? throw new InvalidOperationException("That machinery is not on the plan.");
            (vesselId, owner) = (item.VesselId, item.OwnerStage);
        }
        else
        {
            var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == input.PlannedJobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Job not found.");
            (vesselId, owner) = (job.VesselId, job.OwnerStage);
            if (input.Kind == PlanChangeKind.JobInterval && input.Proposed.CalendarInterval is not > 0 && input.Proposed.HoursInterval is not > 0)
                throw new InvalidOperationException("Propose a calendar interval, a running-hours interval, or both.");
        }
        await RequireEnabledAsync(db, companyId, vesselId);
        if (owner == SyncStages.Vessel) throw new InvalidOperationException("The vessel made this itself, so it can change it directly.");

        var request = new PlanChangeRequest
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, Kind = input.Kind, PlannedJobId = input.PlannedJobId, MachineryItemId = input.MachineryItemId,
            Proposed = System.Text.Json.JsonSerializer.Serialize(input.Proposed), Reason = input.Reason.Trim(), RequestedByName = actor.Name, AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.PlanChangeRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>The office decides. Approving makes the change to the plan (the office owns it), so it reaches the vessel like any other change.</summary>
    public async Task<PlanChangeDecision> DecidePlanChangeAsync(Guid companyId, Guid requestId, bool approve, string comment, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The office decides plan changes.");
        var request = await db.PlanChangeRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId) ?? throw new InvalidOperationException("Request not found.");
        if (await db.PlanChangeDecisions.AnyAsync(d => d.RequestId == requestId)) throw new InvalidOperationException("This request has already been decided.");
        if (!approve && string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException("Say why the request is refused.");

        var proposal = System.Text.Json.JsonSerializer.Deserialize<PlanChangeProposal>(request.Proposed) ?? new PlanChangeProposal();
        Guid? changedJob = null;
        if (approve)
        {
            if (request.Kind == PlanChangeKind.MachineryChange)
            {
                var item = await db.MachineryItems.FirstOrDefaultAsync(m => m.Id == request.MachineryItemId) ?? throw new InvalidOperationException("The machinery is no longer on the plan.");
                if (proposal.Name is { } name && !string.IsNullOrWhiteSpace(name)) item.Name = name.Trim();
                if (proposal.IsCritical is { } critical) item.IsCritical = critical;
                if (proposal.MachineryClassId is { } cls) item.MachineryClassId = cls;
                if (proposal.HasHourMeter is { } meter)
                {
                    if (!meter && item.HasHourMeter && await db.PlannedJobs.AnyAsync(j => j.MachineryItemId == item.Id && j.IsActive && j.HoursInterval > 0))
                        throw new InvalidOperationException("Jobs on this machinery run on hours, so the hour meter has to stay.");
                    item.HasHourMeter = meter;
                }
            }
            else
            {
                var job = await db.PlannedJobs.FirstOrDefaultAsync(j => j.Id == request.PlannedJobId) ?? throw new InvalidOperationException("The job is no longer on the plan.");
                if (request.Kind == PlanChangeKind.JobDeactivate) job.IsActive = false;
                else
                {
                    var item = await db.MachineryItems.AsNoTracking().FirstAsync(m => m.Id == job.MachineryItemId);
                    var calendar = proposal.CalendarInterval ?? job.CalendarInterval;
                    var hours = proposal.HoursInterval ?? job.HoursInterval;
                    if (hours is > 0 && !item.HasHourMeter) throw new InvalidOperationException($"Running hours need an hour meter on {item.Name}.");
                    (job.CalendarInterval, job.CalendarUnit, job.HoursInterval) = (calendar, proposal.CalendarUnit ?? job.CalendarUnit, hours);
                    if (proposal.GraceDays is { } grace) job.GraceDays = grace;
                }
                changedJob = job.Id;
            }
        }
        var decision = new PlanChangeDecision
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = request.VesselId, RequestId = requestId, Approved = approve, Comment = (comment ?? "").Trim(),
            DecidedByName = actor.Name, AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.PlanChangeDecisions.Add(decision);
        await db.SaveChangesAsync();
        if (changedJob is { } id) await PmsProjection.RefreshJobAsync(db, id);
        return decision;
    }

    public async Task<List<PlanChangeView>> PlanChangesAsync(Guid companyId, bool pendingOnly, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.PlanChangeRequests.AsNoTracking().Where(r => r.CompanyId == companyId);
        if (vesselId is { } v) query = query.Where(r => r.VesselId == v);
        var requests = (await query.ToListAsync()).OrderByDescending(r => r.At).ToList();
        if (requests.Count == 0) return [];
        var ids = requests.Select(r => r.Id).ToList();
        var decisions = (await db.PlanChangeDecisions.AsNoTracking().Where(d => ids.Contains(d.RequestId)).ToListAsync()).GroupBy(d => d.RequestId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.At).ThenByDescending(d => d.Id).First());
        var vessels = await db.Vessels.AsNoTracking().Where(x => x.CompanyId == companyId).ToDictionaryAsync(x => x.Id, x => x.Name);
        var jobIds = requests.Where(r => r.PlannedJobId != null).Select(r => r.PlannedJobId!.Value).ToList();
        var itemIds = requests.Where(r => r.MachineryItemId != null).Select(r => r.MachineryItemId!.Value).ToList();
        var jobs = await db.PlannedJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.Code + " " + j.Title);
        var items = await db.MachineryItems.AsNoTracking().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        return requests.Where(r => !pendingOnly || !decisions.ContainsKey(r.Id))
            .Select(r => new PlanChangeView(r, decisions.GetValueOrDefault(r.Id), vessels.GetValueOrDefault(r.VesselId, ""),
                r.PlannedJobId is { } j ? jobs.GetValueOrDefault(j, "") : items.GetValueOrDefault(r.MachineryItemId ?? Guid.Empty, ""))).ToList();
    }

    // ---- template updates: proposed, never silent ----

    /// <summary>Jobs made from a template that has since moved on, each with what would change. A job can be updated only by the side that owns it.</summary>
    public async Task<List<TemplateUpdate>> TemplateUpdatesAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Pms)).ToDictionary(v => v.Id, v => v.Name);
        var templates = await db.JobTemplates.AsNoTracking().Where(t => t.CompanyId == companyId).ToDictionaryAsync(t => t.Id);
        var jobsQuery = db.PlannedJobs.AsNoTracking().Where(j => j.CompanyId == companyId && j.IsActive && j.TemplateId != null);
        if (vesselId is { } v) jobsQuery = jobsQuery.Where(j => j.VesselId == v);
        var jobs = (await jobsQuery.ToListAsync()).Where(j => enabled.ContainsKey(j.VesselId) && templates.TryGetValue(j.TemplateId!.Value, out var t) && j.TemplateVersion < t.Version).ToList();
        if (jobs.Count == 0) return [];
        var itemIds = jobs.Select(j => j.MachineryItemId).Distinct().ToList();
        var items = await db.MachineryItems.AsNoTracking().Where(m => itemIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name);
        var mine = db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office;
        return jobs.Select(j =>
        {
            var t = templates[j.TemplateId!.Value];
            return new TemplateUpdate(j, t, enabled[j.VesselId], items.GetValueOrDefault(j.MachineryItemId, ""), PmsTemplateDiff.Of(j, t), j.OwnerStage == mine);
        }).OrderBy(u => u.Vessel).ThenBy(u => u.Job.Code).ToList();
    }

    /// <summary>Brings the chosen jobs up to their template's current version. Jobs the other side owns are left alone (and counted as skipped); the rest are all updated or none.</summary>
    public async Task<(int Updated, int Skipped)> ApplyTemplateUpdatesAsync(Guid companyId, IEnumerable<Guid> jobIds, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var mine = db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office;
        var ids = jobIds.Distinct().ToList();
        var jobs = await db.PlannedJobs.Where(j => j.CompanyId == companyId && ids.Contains(j.Id) && j.TemplateId != null).ToListAsync();
        var templates = await db.JobTemplates.AsNoTracking().Where(t => t.CompanyId == companyId).ToDictionaryAsync(t => t.Id);
        var items = await db.MachineryItems.AsNoTracking().Where(m => jobs.Select(j => j.MachineryItemId).Contains(m.Id)).ToDictionaryAsync(m => m.Id);

        int updated = 0, skipped = 0;
        var touched = new List<Guid>();
        foreach (var job in jobs)
        {
            var t = templates[job.TemplateId!.Value];
            if (job.OwnerStage != mine || job.TemplateVersion >= t.Version) { skipped++; continue; }
            if (t.HoursInterval is > 0 && !items[job.MachineryItemId].HasHourMeter)
                throw new InvalidOperationException($"{job.Code}: the new version runs on hours, and {items[job.MachineryItemId].Name} has no hour meter.");
            (job.Title, job.Instructions, job.Type, job.TemplateVersion) = (t.Title, t.Instructions, t.Type, t.Version);
            (job.CalendarInterval, job.CalendarUnit, job.HoursInterval, job.Anchor, job.GraceDays) = (t.CalendarInterval, t.CalendarUnit, t.HoursInterval, t.Anchor, t.GraceDays);
            (job.IsCritical, job.DepartmentId, job.EstimatedMinutes, job.RequiresVerification) = (t.IsCritical, t.DepartmentId, t.EstimatedMinutes, t.RequiresVerification);
            job.FixedAnchorDate = job.Anchor == PmsAnchor.FixedCalendar ? job.FixedAnchorDate ?? job.StartDate : null;
            touched.Add(job.Id);
            updated++;
        }
        await db.SaveChangesAsync();
        foreach (var id in touched) await PmsProjection.RefreshJobAsync(db, id);
        return (updated, skipped);
    }

    // ---- spares (what to order for the jobs on a machinery item) ----

    public async Task<List<Item>> SearchItemsAsync(Guid companyId, string term, int take = 12)
    {
        var t = (term ?? "").Trim();
        if (t.Length < 2) return [];
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Items.AsNoTracking().Where(i => i.CompanyId == companyId && i.IsActive && (EF.Functions.ILike(i.Code, $"%{t}%") || EF.Functions.ILike(i.Name, $"%{t}%")))
            .OrderBy(i => i.Code).Take(take).ToListAsync();
    }

    public async Task<List<SpareRow>> ListSparesAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var spares = await db.MachinerySpares.AsNoTracking().Where(s => s.CompanyId == companyId && s.VesselId == vesselId).OrderBy(s => s.CreatedAt).ToListAsync();
        var ids = spares.Select(s => s.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        return spares.Select(s => new SpareRow(s, items.GetValueOrDefault(s.ItemId)?.Code ?? "", items.GetValueOrDefault(s.ItemId)?.Name ?? "(item removed)", items.GetValueOrDefault(s.ItemId)?.UnitOfMeasure ?? "")).ToList();
    }

    public async Task<MachinerySpare> AddSpareAsync(Guid companyId, Guid machineryItemId, Guid itemId, decimal quantity, string note, Actor actor)
    {
        if (quantity <= 0) throw new InvalidOperationException("The quantity must be more than zero.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var machinery = await db.MachineryItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineryItemId && m.CompanyId == companyId) ?? throw new InvalidOperationException("That machinery is not on the plan.");
        await RequireEnabledAsync(db, companyId, machinery.VesselId);
        if (!await db.Items.AnyAsync(i => i.Id == itemId && i.CompanyId == companyId && i.IsActive)) throw new InvalidOperationException("That item is not in the catalog.");
        if (await db.MachinerySpares.AnyAsync(s => s.MachineryItemId == machineryItemId && s.ItemId == itemId)) throw new InvalidOperationException("That item is already on this machinery's spares list.");

        var spare = new MachinerySpare
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = machinery.VesselId, MachineryItemId = machineryItemId, ItemId = itemId, Quantity = quantity,
            Note = (note ?? "").Trim(), CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        db.MachinerySpares.Add(spare);
        await db.SaveChangesAsync();
        return spare;
    }

    /// <summary>Changes the quantity, or removes the spare when it is zero. Only the side that added it may.</summary>
    public async Task SetSpareQuantityAsync(Guid companyId, Guid spareId, decimal quantity)
    {
        if (quantity < 0) throw new InvalidOperationException("The quantity cannot be negative.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var spare = await db.MachinerySpares.FirstOrDefaultAsync(s => s.Id == spareId && s.CompanyId == companyId) ?? throw new InvalidOperationException("Spare not found.");
        if (quantity == 0) db.MachinerySpares.Remove(spare);
        else spare.Quantity = quantity;
        await db.SaveChangesAsync();
    }

    // ---- rules ----

    /// <summary>Dates in the plan are whole UTC days (the database stores them as UTC).</summary>
    private static DateTime Day(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    private static void RequireInterval(int? calendar, int? hours)
    {
        if (calendar is not > 0 && hours is not > 0) throw new InvalidOperationException("Give a calendar interval, a running-hours interval, or both.");
    }

    private static void RequireOffice(ChandleryDbContext db)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException("The library is maintained by the office.");
    }
}
