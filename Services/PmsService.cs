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
    private static async Task<PlannedJob> StageJobAsync(ChandleryDbContext db, Guid companyId, MachineryItem item, NewPlannedJob input, Actor actor, JobTemplate? known = null)
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

    public async Task<JobCompletion> CompleteJobAsync(Guid companyId, NewCompletion input, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == input.PlannedJobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Job not found.");
        await RequireEnabledAsync(db, companyId, job.VesselId);
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
