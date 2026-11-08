using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record ImportResult(int MachineryAdded, int JobsAdded, int FromLibrary, int OwnJobs, int HistoryRecorded, int Skipped, List<string> Problems);

/// <summary>
/// Brings a vessel's existing maintenance plan (a spreadsheet) onto the plan: machinery along its location, system, equipment path, jobs from the library where a
/// template matches and as the vessel's own where none does, and the last time each was done as read-only history. Nothing already on the plan is changed, so the same
/// file can be imported again. The whole file goes in or none of it does.
/// </summary>
public class PmsImportService(IDbContextFactory<ChandleryDbContext> factory)
{
    private const int Batch = 400;

    private static string Key(IEnumerable<string> path) => string.Join("/", path.Select(p => p.Trim().ToLowerInvariant()));

    /// <summary>Works out what the file would do, against what the vessel already has.</summary>
    public async Task<ImportPlan> PreviewAsync(Guid companyId, Guid vesselId, ImportTable table, ImportMapping map)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await PmsService.IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Planned maintenance is not switched on for this vessel.");
        var templates = await db.JobTemplates.AsNoTracking().Where(t => t.CompanyId == companyId && t.IsActive).ToListAsync();
        var (paths, jobKeys) = await ExistingAsync(db, vesselId);
        return PmsImportPlanner.Plan(table, map, templates, jobKeys, paths, DateTime.UtcNow.Date);
    }

    private static async Task<(HashSet<string> Paths, HashSet<string> JobKeys)> ExistingAsync(ChandleryDbContext db, Guid vesselId)
    {
        var items = await db.MachineryItems.AsNoTracking().Where(m => m.VesselId == vesselId).ToListAsync();
        var byId = items.ToDictionary(i => i.Id);
        var pathOf = items.ToDictionary(i => i.Id, i => Names(i, byId));
        var paths = pathOf.Values.Select(p => string.Join(" › ", p).ToLowerInvariant()).ToHashSet();
        var jobs = await db.PlannedJobs.AsNoTracking().Where(j => j.VesselId == vesselId).Select(j => new { j.MachineryItemId, j.Code }).ToListAsync();
        var keys = jobs.Where(j => pathOf.ContainsKey(j.MachineryItemId)).Select(j => Key(pathOf[j.MachineryItemId]) + "|" + j.Code.Trim().ToLowerInvariant()).ToHashSet();
        return (paths, keys);
    }

    private static List<string> Names(MachineryItem item, IReadOnlyDictionary<Guid, MachineryItem> all)
    {
        var names = new List<string>();
        var seen = new HashSet<Guid>();
        for (var cur = item; cur is not null && seen.Add(cur.Id); cur = cur.ParentId is { } p && all.TryGetValue(p, out var parent) ? parent : null) names.Insert(0, cur.Name.Trim());
        return names;
    }

    public async Task<ImportResult> ApplyAsync(Guid companyId, Guid vesselId, IReadOnlyList<ImportRow> rows, Func<ImportRow, bool> useTemplate, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await PmsService.IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Planned maintenance is not switched on for this vessel.");
        var mine = db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office;
        var now = DateTime.UtcNow;
        var today = now.Date;

        var existing = await db.MachineryItems.Where(m => m.VesselId == vesselId).ToListAsync();                       // tracked: a missing hour meter may need switching on
        var byId = existing.ToDictionary(i => i.Id);
        var index = existing.ToDictionary(i => Key(Names(i, byId)), i => i);
        var (_, jobKeys) = await ExistingAsync(db, vesselId);
        var departments = await db.Departments.AsNoTracking().Where(d => d.CompanyId == companyId).ToDictionaryAsync(d => d.Name.Trim().ToLowerInvariant(), d => d.Id);

        var problems = new List<string>();
        var todo = new List<(ImportRow Row, JobTemplate? Template)>();
        var seen = new HashSet<string>(jobKeys);
        foreach (var row in rows.Where(r => r.IsValid))
        {
            var template = useTemplate(row) ? row.Match : null;
            var key = Key(row.Path) + "|" + row.Code.Trim().ToLowerInvariant();
            if (!seen.Add(key)) continue;                                                                               // already on the plan (or twice in the file)
            var hours = template is not null ? template.HoursInterval : row.HoursInterval;
            if (hours is > 0 && index.TryGetValue(Key(row.Path), out var held) && !held.HasHourMeter)
            {
                if (held.OwnerStage != mine) { problems.Add($"Line {row.Line} ({row.Code}): {held.Name} has no hour meter and belongs to the {(mine == SyncStages.Vessel ? "office" : "vessel")}, so the job was left out."); seen.Remove(key); continue; }
                held.HasHourMeter = true;
            }
            todo.Add((row, template));
        }

        var needMeter = todo.Where(t => (t.Template is not null ? t.Template.HoursInterval : t.Row.HoursInterval) is > 0).Select(t => Key(t.Row.Path)).ToHashSet();
        int machineryAdded = 0, jobsAdded = 0, fromLibrary = 0, history = 0;

        await using var transaction = await db.Database.BeginTransactionAsync();
        var created = new Dictionary<string, Guid>();                                                                    // machinery made by this import, by path
        var existingIds = index.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
        for (var from = 0; from < todo.Count; from += Batch)
        {
            foreach (var (row, template) in todo.Skip(from).Take(Batch))
            {
                var itemId = EnsurePath(row.Path, db, companyId, vesselId, actor, now, existingIds, created, needMeter, ref machineryAdded);
                var job = new PlannedJob
                {
                    Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, MachineryItemId = itemId, StartDate = today, CreatedByName = actor.Name, CreatedAt = now,
                };
                if (template is not null)
                {
                    (job.TemplateId, job.TemplateVersion, job.Code, job.Title, job.Instructions, job.Type) = (template.Id, template.Version, template.Code, template.Title, template.Instructions, template.Type);
                    (job.CalendarInterval, job.CalendarUnit, job.HoursInterval, job.Anchor, job.GraceDays) = (template.CalendarInterval, template.CalendarUnit, template.HoursInterval, template.Anchor, template.GraceDays);
                    (job.IsCritical, job.DepartmentId, job.EstimatedMinutes, job.RequiresVerification) = (template.IsCritical || row.Critical, template.DepartmentId, template.EstimatedMinutes, template.RequiresVerification);
                    fromLibrary++;
                }
                else
                {
                    (job.Code, job.Title, job.Instructions, job.Type) = (row.Code, row.Title, row.Instructions, PmsJobType.Check);
                    (job.CalendarInterval, job.CalendarUnit, job.HoursInterval) = (row.CalendarInterval, row.CalendarUnit, row.HoursInterval);
                    job.IsCritical = row.Critical;
                    job.DepartmentId = row.Department != "" && departments.TryGetValue(row.Department.Trim().ToLowerInvariant(), out var dept) ? dept : null;
                }
                job.FixedAnchorDate = job.Anchor == PmsAnchor.FixedCalendar ? today : null;

                var completions = new List<PmsCompletionInput>();
                if (row.LastDoneDate is { } done)
                {
                    var completion = new JobCompletion
                    {
                        Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, PlannedJobId = job.Id, DoneDate = DateTime.SpecifyKind(done.Date, DateTimeKind.Utc), HoursAtDone = row.LastDoneHours,
                        PerformedBy = "Imported", Comments = "Imported from the previous plan", IsImported = true, AuthorUserId = actor.UserId, AuthorName = actor.Name, At = now,
                    };
                    completions.Add(PmsProjection.InputOf(completion));
                    db.JobCompletions.Add(completion);
                    history++;
                }
                PmsProjection.Apply(job, PmsScheduler.Compute(PmsProjection.InputOf(job), completions, []));
                db.PlannedJobs.Add(job);
                jobsAdded++;
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        await transaction.CommitAsync();
        return new ImportResult(machineryAdded, jobsAdded, fromLibrary, jobsAdded - fromLibrary, history, rows.Count(r => r.IsValid) - todo.Count, problems);
    }

    /// <summary>Finds or makes each machinery item along the path (the equipment last), returning the equipment's id.</summary>
    private static Guid EnsurePath(string[] path, ChandleryDbContext db, Guid companyId, Guid vesselId, Actor actor, DateTime now, Dictionary<string, Guid> existing, Dictionary<string, Guid> created,
        HashSet<string> needMeter, ref int added)
    {
        Guid? parent = null;
        for (var depth = 1; depth <= path.Length; depth++)
        {
            var key = Key(path.Take(depth));
            if (existing.TryGetValue(key, out var held)) { parent = held; continue; }
            if (created.TryGetValue(key, out var made)) { parent = made; continue; }
            var item = new MachineryItem
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, ParentId = parent, Name = path[depth - 1].Trim(), CreatedByName = actor.Name, CreatedAt = now,
                HasHourMeter = needMeter.Contains(key),
            };
            db.MachineryItems.Add(item);
            created[key] = item.Id;
            parent = item.Id;
            added++;
        }
        return parent!.Value;
    }
}
