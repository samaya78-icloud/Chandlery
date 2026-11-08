using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record SpareRow(MachinerySpare Spare, string ItemCode, string ItemName, string Uom);

/// <summary>A requisition ready to be completed: the vessel, department, urgency and a note filled in, and a line for each spare kept for the job's machinery.</summary>
public record RequisitionDraft(NewRequisition Requisition, PlannedJob Job, string Machinery, int SpareLines, List<string> SpareSummary);

public record CloneResult(string Vessel, int ItemsAdded, int JobsAdded, int SparesAdded, string? Problem = null);

public enum DriftKind { MissingHere, ExtraHere, Different }

public record DriftRow(DriftKind Kind, string Machinery, string JobCode, string Detail);

/// <summary>Compares a vessel's plan with a reference vessel's (a sister ship). Machinery is matched by its name along the tree, jobs by their code.</summary>
public static class PmsDrift
{
    public static string PathOf(MachineryItem item, IReadOnlyDictionary<Guid, MachineryItem> all)
    {
        var names = new List<string>();
        var seen = new HashSet<Guid>();
        for (var cur = item; cur is not null && seen.Add(cur.Id); cur = cur.ParentId is { } p && all.TryGetValue(p, out var parent) ? parent : null)
            names.Insert(0, cur.Name.Trim());
        return string.Join(" › ", names);
    }

    public static List<DriftRow> Compare(IReadOnlyList<MachineryItem> referenceItems, IReadOnlyList<PlannedJob> referenceJobs, IReadOnlyList<MachineryItem> items, IReadOnlyList<PlannedJob> jobs)
    {
        var refById = referenceItems.ToDictionary(i => i.Id);
        var byId = items.ToDictionary(i => i.Id);
        var refPaths = referenceItems.Select(i => (Item: i, Path: PathOf(i, refById))).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Item, StringComparer.OrdinalIgnoreCase);
        var paths = items.Select(i => (Item: i, Path: PathOf(i, byId))).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Item, StringComparer.OrdinalIgnoreCase);

        var rows = new List<DriftRow>();
        foreach (var (path, refItem) in refPaths)
        {
            var refItemJobs = referenceJobs.Where(j => j.MachineryItemId == refItem.Id).ToList();
            if (!paths.TryGetValue(path, out var item))
            {
                rows.Add(new DriftRow(DriftKind.MissingHere, path, "", $"Machinery not on this vessel ({refItemJobs.Count} job(s) go with it)"));
                continue;
            }
            var mine = jobs.Where(j => j.MachineryItemId == item.Id).ToList();
            foreach (var r in refItemJobs)
            {
                var j = mine.FirstOrDefault(x => string.Equals(x.Code, r.Code, StringComparison.OrdinalIgnoreCase));
                if (j is null) { rows.Add(new DriftRow(DriftKind.MissingHere, path, r.Code, $"{r.Title}: not on this vessel's plan")); continue; }
                var differences = Differences(r, j);
                if (differences.Count > 0) rows.Add(new DriftRow(DriftKind.Different, path, r.Code, string.Join("; ", differences)));
            }
            foreach (var j in mine.Where(j => !refItemJobs.Any(r => string.Equals(r.Code, j.Code, StringComparison.OrdinalIgnoreCase))))
                rows.Add(new DriftRow(DriftKind.ExtraHere, path, j.Code, $"{j.Title}: only on this vessel"));
        }
        foreach (var (path, item) in paths.Where(p => !refPaths.ContainsKey(p.Key)))
            rows.Add(new DriftRow(DriftKind.ExtraHere, path, "", $"Machinery only on this vessel ({jobs.Count(j => j.MachineryItemId == item.Id)} job(s))"));

        return rows.OrderBy(r => r.Kind).ThenBy(r => r.Machinery, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.JobCode, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> Differences(PlannedJob reference, PlannedJob mine)
    {
        var list = new List<string>();
        string Cal(PlannedJob j) => j.CalendarInterval is > 0 ? $"{j.CalendarInterval} {j.CalendarUnit.ToString().ToLowerInvariant()}" : "none";
        string Hrs(PlannedJob j) => j.HoursInterval is > 0 ? $"{j.HoursInterval:N0} h" : "none";
        if (Cal(reference) != Cal(mine)) list.Add($"calendar interval {Cal(mine)} here, {Cal(reference)} on the reference");
        if (Hrs(reference) != Hrs(mine)) list.Add($"running hours {Hrs(mine)} here, {Hrs(reference)} on the reference");
        if (reference.IsCritical != mine.IsCritical) list.Add(mine.IsCritical ? "critical here only" : "critical on the reference only");
        if (reference.Anchor != mine.Anchor) list.Add("counted differently (fixed dates or from the last completion)");
        if (reference.TemplateVersion != mine.TemplateVersion) list.Add($"library version {mine.TemplateVersion?.ToString() ?? "none"} here, {reference.TemplateVersion?.ToString() ?? "none"} on the reference");
        return list;
    }
}

/// <summary>What ties planned maintenance to other modules and to sister vessels: spares to purchasing, defects to the worklist, cloning and drift between vessels.</summary>
public class PmsIntegrationService(IDbContextFactory<ChandleryDbContext> factory, PmsService pms, WorklistService worklist)
{
    // ---- spares to purchasing ----

    /// <summary>A requisition filled in from a job: its machinery's spares become the lines, a critical job makes it urgent.</summary>
    public async Task<RequisitionDraft?> RequisitionDraftAsync(Guid companyId, Guid jobId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId);
        if (job is null) return null;
        var item = await db.MachineryItems.AsNoTracking().FirstAsync(m => m.Id == job.MachineryItemId);
        var spares = await db.MachinerySpares.AsNoTracking().Where(s => s.MachineryItemId == item.Id).ToListAsync();
        var itemIds = spares.Select(s => s.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.IsActive).ToDictionaryAsync(i => i.Id);
        var active = items.Keys.ToHashSet();

        var requisition = new NewRequisition
        {
            Type = RfqType.Spares, VesselId = job.VesselId, DepartmentId = job.DepartmentId, Urgency = job.IsCritical ? Urgency.Urgent : Urgency.Normal,
            Remarks = $"For maintenance job {job.Code}: {job.Title} on {item.Name}",
            Lines = spares.Where(s => active.Contains(s.ItemId)).OrderBy(s => s.CreatedAt).Select(s => new NewRfqLine { ItemId = s.ItemId, Quantity = s.Quantity, Description = s.Note }).ToList(),
        };
        var summary = spares.Where(s => active.Contains(s.ItemId)).OrderBy(s => s.CreatedAt).Select(s => $"{items[s.ItemId].Code} {items[s.ItemId].Name} × {s.Quantity:0.##}").ToList();
        return new RequisitionDraft(requisition, job, item.Name, requisition.Lines.Count, summary);
    }

    // ---- defects to the worklist ----

    /// <summary>Raises a worklist task for a fault found on a job, linked to the job both ways. Needs the Worklist module on for the vessel.</summary>
    public async Task<WorklistTask> RaiseDefectAsync(Guid companyId, Guid jobId, string? title, string description, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Job not found.");
        var item = await db.MachineryItems.AsNoTracking().FirstAsync(m => m.Id == job.MachineryItemId);
        var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == "Vessel").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();

        var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(job.VesselId, string.IsNullOrWhiteSpace(title) ? $"Defect on {item.Name}: {job.Title}" : title.Trim(),
            description ?? "", source, DepartmentId: job.DepartmentId, Priority: job.IsCritical ? WorklistPriority.Urgent : WorklistPriority.Normal), actor);
        await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.PmsJob, LinkTarget: job.Id, LinkLabel: $"Maintenance job {job.Code}"), actor);
        return task;
    }

    public Task<List<WorklistTask>> TasksForJobAsync(Guid companyId, Guid jobId) => worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.PmsJob, jobId);

    // ---- sister vessels ----

    /// <summary>
    /// Copies one vessel's plan to others: machinery (matched by its name along the tree, added when missing), jobs (matched by code, added when missing, with the
    /// definition and library version they have on the source, counting from today) and, if asked, spares. Nothing already on the target is changed, so it can be repeated.
    /// Each vessel is copied all or nothing. Office only.
    /// </summary>
    public async Task<List<CloneResult>> ClonePlanAsync(Guid companyId, Guid sourceVesselId, IEnumerable<Guid> targetVesselIds, bool includeSpares, Actor actor)
    {
        await using (var check = await factory.CreateDbContextAsync())
        {
            check.CurrentCompanyId = companyId;
            if (check.Node.IsVessel) throw new InvalidOperationException("Plans are cloned by the office.");
            var enabledIds = (await ModuleGate.EnabledVesselsAsync(check, companyId, PolicyModules.Pms)).ToDictionary(v => v.Id, v => v.Name);
            if (!enabledIds.ContainsKey(sourceVesselId)) throw new InvalidOperationException("Planned maintenance is not switched on for the vessel to copy from.");
            var results = new List<CloneResult>();
            foreach (var target in targetVesselIds.Distinct().Where(t => t != sourceVesselId))
            {
                var name = enabledIds.GetValueOrDefault(target) ?? (await check.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == target))?.Name ?? target.ToString();
                results.Add(enabledIds.ContainsKey(target) ? await CloneOneAsync(companyId, sourceVesselId, target, name, includeSpares, actor) : new CloneResult(name, 0, 0, 0, "Planned maintenance is off for this vessel."));
            }
            return results;
        }
    }

    private async Task<CloneResult> CloneOneAsync(Guid companyId, Guid sourceVesselId, Guid targetVesselId, string targetName, bool includeSpares, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var sourceItems = await db.MachineryItems.AsNoTracking().Where(m => m.VesselId == sourceVesselId && m.IsActive).ToListAsync();
        var sourceJobs = await db.PlannedJobs.AsNoTracking().Where(j => j.VesselId == sourceVesselId && j.IsActive).ToListAsync();
        var sourceSpares = includeSpares ? await db.MachinerySpares.AsNoTracking().Where(s => s.VesselId == sourceVesselId).ToListAsync() : [];
        var targetItems = await db.MachineryItems.Where(m => m.VesselId == targetVesselId).ToListAsync();
        var targetJobs = await db.PlannedJobs.AsNoTracking().Where(j => j.VesselId == targetVesselId).ToListAsync();
        var targetSpares = await db.MachinerySpares.AsNoTracking().Where(s => s.VesselId == targetVesselId).ToListAsync();

        var sourceById = sourceItems.ToDictionary(i => i.Id);
        var targetById = targetItems.ToDictionary(i => i.Id);
        var existing = targetItems.Select(i => (Path: PmsDrift.PathOf(i, targetById), Item: i)).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Item, StringComparer.OrdinalIgnoreCase);

        // Parents before children, so a copied item can point at its copied parent.
        var ordered = new List<MachineryItem>();
        var placed = new HashSet<Guid>();
        void Place(MachineryItem m)
        {
            if (!placed.Add(m.Id)) return;
            if (m.ParentId is { } p && sourceById.TryGetValue(p, out var parent)) Place(parent);
            ordered.Add(m);
        }
        foreach (var m in sourceItems.OrderBy(m => m.Name)) Place(m);

        var map = new Dictionary<Guid, MachineryItem>();
        int itemsAdded = 0, jobsAdded = 0, sparesAdded = 0;
        foreach (var src in ordered)
        {
            var path = PmsDrift.PathOf(src, sourceById);
            if (existing.TryGetValue(path, out var held)) { map[src.Id] = held; continue; }
            var copy = new MachineryItem
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = targetVesselId, ParentId = src.ParentId is { } p && map.TryGetValue(p, out var mappedParent) ? mappedParent.Id : null,
                Code = src.Code, Name = src.Name, Maker = src.Maker, Model = src.Model, Serial = "", MachineryClassId = src.MachineryClassId, IsCritical = src.IsCritical, HasHourMeter = src.HasHourMeter,
                CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
            };
            db.MachineryItems.Add(copy);
            existing[path] = copy;
            map[src.Id] = copy;
            itemsAdded++;
        }

        var codesByItem = targetJobs.GroupBy(j => j.MachineryItemId).ToDictionary(g => g.Key, g => g.Select(j => j.Code.ToLowerInvariant()).ToHashSet());
        foreach (var job in sourceJobs.OrderBy(j => j.Code))
        {
            if (!map.TryGetValue(job.MachineryItemId, out var item)) continue;
            var codes = codesByItem.TryGetValue(item.Id, out var set) ? set : codesByItem[item.Id] = [];
            if (!codes.Add(job.Code.ToLowerInvariant())) continue;
            var staged = await PmsService.StageJobAsync(db, companyId, item, new NewPlannedJob(item.Id, Code: job.Code, Title: job.Title, Instructions: job.Instructions, Type: job.Type,
                CalendarInterval: job.CalendarInterval, CalendarUnit: job.CalendarUnit, HoursInterval: job.HoursInterval, Anchor: job.Anchor, FixedAnchorDate: job.FixedAnchorDate, GraceDays: job.GraceDays,
                IsCritical: job.IsCritical, DepartmentId: job.DepartmentId, EstimatedMinutes: job.EstimatedMinutes, RequiresVerification: job.RequiresVerification, StartDate: DateTime.UtcNow), actor);
            (staged.TemplateId, staged.TemplateVersion) = (job.TemplateId, job.TemplateVersion);
            jobsAdded++;
        }

        foreach (var spare in sourceSpares)
        {
            if (!map.TryGetValue(spare.MachineryItemId, out var item)) continue;
            if (targetSpares.Any(s => s.MachineryItemId == item.Id && s.ItemId == spare.ItemId) || db.MachinerySpares.Local.Any(s => s.MachineryItemId == item.Id && s.ItemId == spare.ItemId)) continue;
            db.MachinerySpares.Add(new MachinerySpare { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = targetVesselId, MachineryItemId = item.Id, ItemId = spare.ItemId, Quantity = spare.Quantity, Note = spare.Note, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow });
            sparesAdded++;
        }

        await db.SaveChangesAsync();
        return new CloneResult(targetName, itemsAdded, jobsAdded, sparesAdded);
    }

    /// <summary>How a vessel's plan differs from a reference vessel's.</summary>
    public async Task<List<DriftRow>> DriftAsync(Guid companyId, Guid referenceVesselId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Pms)).Select(v => v.Id).ToHashSet();
        if (!enabled.Contains(referenceVesselId) || !enabled.Contains(vesselId)) throw new InvalidOperationException("Planned maintenance has to be switched on for both vessels.");
        var items = await db.MachineryItems.AsNoTracking().Where(m => (m.VesselId == referenceVesselId || m.VesselId == vesselId) && m.IsActive).ToListAsync();
        var jobs = await db.PlannedJobs.AsNoTracking().Where(j => (j.VesselId == referenceVesselId || j.VesselId == vesselId) && j.IsActive).ToListAsync();
        return PmsDrift.Compare(items.Where(i => i.VesselId == referenceVesselId).ToList(), jobs.Where(j => j.VesselId == referenceVesselId).ToList(),
            items.Where(i => i.VesselId == vesselId).ToList(), jobs.Where(j => j.VesselId == vesselId).ToList());
    }
}
