using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record NewWorklistTask(Guid VesselId, string Title, string Description = "", Guid? SourceId = null, Guid? NatureId = null, Guid? CategoryId = null,
    Guid? DepartmentId = null, WorklistPriority Priority = WorklistPriority.Normal, DateTime? Planned = null, DateTime? Target = null, string Assignee = "");

/// <summary>What one new timeline entry says. Which fields matter depends on the type.</summary>
public record WorklistEntryInput(WorklistEntryType Type, string Text = "", WorklistStatus? Status = null, int? Percent = null, WorklistPriority? Priority = null,
    DateTime? Date = null, string? Assignee = null, string? Title = null, string? Description = null, string? LinkType = null, Guid? LinkTarget = null, string? LinkLabel = null);

public record WorklistDetail(WorklistTask Task, List<WorklistEntry> Entries);

public record WorklistCounts(int Overdue, int DueThisWeek, int AwaitingVerification);

/// <summary>What a task is linked to: a requisition, an order, or another task (a copy or its origin).</summary>
public record WorklistLink(string Type, Guid Target, string Label, DateTime At);

public record LinkCandidate(string Type, Guid Id, string Label);

public record TransferResult(List<WorklistTask> Created, List<string> Skipped);

public static class WorklistLinkTypes
{
    public const string Requisition = "requisition";
    public const string Order = "order";
    public const string Task = "task";
    public const string PmsJob = "pmsjob";
    public const string TankInspection = "tankinspection";
    public const string RiskAssessment = "riskassessment";
    public const string RestBreach = "restbreach";
    public const string Project = "project";

    /// <summary>Where the link opens, on the office or on a vessel.</summary>
    public static string Href(string type, Guid target, bool onVessel) => type switch
    {
        Requisition => $"/company/requisitions/{target}",
        Order => onVessel ? $"/vessel/orders/{target}" : $"/company/purchase-orders/{target}",
        Task => $"/worklist/{target}",
        PmsJob => $"/pms/jobs/{target}",
        TankInspection => $"/tanks/inspections/{target}",
        RiskAssessment => $"/risk/{target}",
        RestBreach => $"/rest/breaches?note={target}",
        Project => $"/projects/{target}",
        _ => "#",
    };
}

/// <summary>
/// Tasks and their history. The only changes are new entries; a task is never edited. Each side may add progress; the office alone
/// verifies, and changes priority, target date and assignee of a task the vessel raised (the vessel does so for its own tasks).
/// </summary>
public class WorklistService(IDbContextFactory<ChandleryDbContext> factory)
{
    /// <summary>Runs just before a new entry is saved. Lets a test make the other side's entry arrive at exactly that moment; unused otherwise.</summary>
    public Func<Task>? BeforeEntrySaved { get; set; }

    // ---- the module switch ----

    /// <summary>Is Worklist switched on for this vessel (its own switch, else its group's, up the tree; none means off)?</summary>
    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Worklist);

    private static Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(ChandleryDbContext db, Guid companyId) => ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Worklist);

    /// <summary>Is Worklist on for at least one vessel of the company (for showing it in the office menu)?</summary>
    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await EnabledVesselsAsync(db, companyId)).Count > 0;
    }

    /// <summary>Overdue, due-soon and awaiting-verification counts for the dashboards, counted in the database. Null when the worklist is off everywhere.</summary>
    public async Task<WorklistCounts?> CountsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var vessels = (await EnabledVesselsAsync(db, companyId)).Select(v => v.Id).ToList();
        if (vessels.Count == 0) return null;
        var today = DateTime.UtcNow.Date;
        var soon = today.AddDays(7);

        // The same rules as WorklistTask.IsClosed / IsOverdue / AwaitingVerification, written so the database can evaluate them.
        var tasks = db.WorklistTasks.AsNoTracking().Where(t => t.CompanyId == companyId && vessels.Contains(t.VesselId));
        var open = tasks.Where(t => t.Status != WorklistStatus.Cancelled && (t.Status != WorklistStatus.Completed || (t.RequiresVerification && t.VerifiedAt == null)));
        return new WorklistCounts(
            await open.CountAsync(t => t.Status != WorklistStatus.Completed && t.TargetDate != null && t.TargetDate < today),
            await open.CountAsync(t => t.TargetDate != null && t.TargetDate >= today && t.TargetDate <= soon),
            await tasks.CountAsync(t => t.RequiresVerification && t.Status == WorklistStatus.Completed && t.VerifiedAt == null));
    }

    // ---- writing ----

    public async Task<WorklistTask> CreateTaskAsync(Guid companyId, NewWorklistTask input, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (task, _) = await StageNewTaskAsync(db, companyId, input, actor);
        await db.SaveChangesAsync();
        return task;
    }

    /// <summary>Checks the input and adds the task and its first entry to the context, without saving.</summary>
    private static async Task<(WorklistTask Task, WorklistEntry Created)> StageNewTaskAsync(ChandleryDbContext db, Guid companyId, NewWorklistTask input, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("Give the task a title.");
        if (!await IsEnabledForVesselAsync(db, companyId, input.VesselId))
            throw new InvalidOperationException("The worklist is not switched on for this vessel.");

        var requiresVerification = false;
        if (input.SourceId is { } sourceId)
        {
            var source = await db.WorklistListItems.FirstOrDefaultAsync(i => i.Id == sourceId && i.CompanyId == companyId && i.Kind == WorklistListKind.Source)
                ?? throw new InvalidOperationException("That source is not in the list.");
            requiresVerification = source.RequiresVerification;
        }
        if (input.NatureId is { } natureId && !await db.WorklistListItems.AnyAsync(i => i.Id == natureId && i.Kind == WorklistListKind.Nature))
            throw new InvalidOperationException("That nature is not in the list.");
        if (input.CategoryId is { } categoryId && !await db.WorklistListItems.AnyAsync(i => i.Id == categoryId && i.Kind == WorklistListKind.Category))
            throw new InvalidOperationException("That category is not in the list.");
        if (input.DepartmentId is { } departmentId && !await db.Departments.AnyAsync(d => d.Id == departmentId && d.CompanyId == companyId))
            throw new InvalidOperationException("That department does not exist.");

        var now = DateTime.UtcNow;
        var task = new WorklistTask
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = input.VesselId,
            Code = await DocumentNumbers.NextAsync(db, companyId, input.VesselId, DocType.Worklist),
            SourceId = input.SourceId, NatureId = input.NatureId, CategoryId = input.CategoryId, DepartmentId = input.DepartmentId,
            RequiresVerification = requiresVerification, CreatedByUserId = actor.UserId, CreatedByName = actor.Name, CreatedAt = now,
        };
        var created = NewEntry(task, WorklistEntryType.Created, actor, now, "",
            WorklistProjection.Json(("title", input.Title.Trim()), ("description", input.Description.Trim()), ("priority", input.Priority),
                ("planned", input.Planned), ("target", input.Target), ("assignee", input.Assignee.Trim())));

        WorklistProjection.Apply(task, WorklistProjection.Replay([created]));
        db.WorklistTasks.Add(task);
        db.WorklistEntries.Add(created);
        return (task, created);
    }

    public async Task<WorklistEntry> AddEntryAsync(Guid companyId, Guid taskId, WorklistEntryInput input, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var task = await db.WorklistTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId)
            ?? throw new InvalidOperationException("Task not found.");
        if (!await IsEnabledForVesselAsync(db, companyId, task.VesselId))
            throw new InvalidOperationException("The worklist is not switched on for this vessel.");
        EnsureAllowed(db, task, input);

        var payload = BuildPayload(input);
        var entry = NewEntry(task, input.Type, actor, DateTime.UtcNow, (input.Text ?? "").Trim(), payload);
        if (BeforeEntrySaved is { } beforeSave) await beforeSave();
        db.WorklistEntries.Add(entry);
        await db.SaveChangesAsync();
        // The state is worked out from every entry now stored, not from a read made before saving: an entry that arrived from the other
        // side in between is then part of it, and the two sides cannot end up with different results.
        await WorklistProjection.RefreshAsync(db, taskId);
        return entry;
    }

    // ---- reading ----

    public async Task<WorklistDetail?> GetAsync(Guid companyId, Guid taskId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var task = await db.WorklistTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId);
        if (task is null) return null;
        var entries = await db.WorklistEntries.AsNoTracking().Where(e => e.TaskId == taskId).ToListAsync();
        return new WorklistDetail(task, entries.OrderBy(e => e.At).ThenBy(e => e.Id).ToList());
    }

    public async Task<List<WorklistTask>> ListAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.WorklistTasks.AsNoTracking().Where(t => t.CompanyId == companyId);
        if (vesselId is { } v) query = query.Where(t => t.VesselId == v);
        return await query.OrderBy(t => t.TargetDate == null).ThenBy(t => t.TargetDate).ThenBy(t => t.Code).ToListAsync();
    }

    /// <summary>The vessels the worklist is switched on for (on a vessel installation: its own vessel).</summary>
    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await EnabledVesselsAsync(db, companyId);
    }

    // ---- the lists (office maintains them; vessels read them) ----

    public async Task<List<WorklistListItem>> GetListsAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId);
        if (!includeInactive) query = query.Where(i => i.IsActive);
        return await query.OrderBy(i => i.Kind).ThenBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync();
    }

    public async Task<WorklistListItem> AddListItemAsync(Guid companyId, WorklistListKind kind, string name, Guid? parentId = null, bool requiresVerification = false)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the entry a name.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        name = name.Trim();
        if (await db.WorklistListItems.AnyAsync(i => i.CompanyId == companyId && i.Kind == kind && i.ParentId == parentId && i.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException($"\"{name}\" is already in the list.");
        if (parentId is { } parent && !await db.WorklistListItems.AnyAsync(i => i.Id == parent && i.Kind == kind))
            throw new InvalidOperationException("The parent is not in this list.");
        var order = await db.WorklistListItems.Where(i => i.CompanyId == companyId && i.Kind == kind && i.ParentId == parentId).Select(i => (int?)i.SortOrder).MaxAsync() ?? -1;
        var item = new WorklistListItem
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, Kind = kind, Name = name, ParentId = parentId, SortOrder = order + 1,
            RequiresVerification = kind == WorklistListKind.Source && requiresVerification,
        };
        db.WorklistListItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    /// <summary>Rename, switch off or on, and (for sources) change the verification flag. Tasks already raised keep what they were created with.</summary>
    public async Task UpdateListItemAsync(Guid companyId, Guid itemId, string name, bool isActive, bool requiresVerification)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Give the entry a name.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var item = await db.WorklistListItems.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Entry not found.");
        item.Name = name.Trim();
        item.IsActive = isActive;
        item.RequiresVerification = item.Kind == WorklistListKind.Source && requiresVerification;
        await db.SaveChangesAsync();
    }

    // ---- links and copies ----

    /// <summary>The links on a task, worked out from its link entries (oldest first).</summary>
    public static List<WorklistLink> LinksOf(IEnumerable<WorklistEntry> entries)
    {
        var links = new List<WorklistLink>();
        foreach (var e in entries.Where(e => e.Type == WorklistEntryType.Link).OrderBy(e => e.At).ThenBy(e => e.Id))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(e.Payload) ? "{}" : e.Payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("linkType", out var type) && root.TryGetProperty("target", out var target) && Guid.TryParse(target.ToString(), out var id)
                    && !links.Any(l => l.Type == type.ToString() && l.Target == id))
                    links.Add(new WorklistLink(type.ToString(), id, root.TryGetProperty("label", out var label) ? label.ToString() : "", e.At));
            }
            catch (System.Text.Json.JsonException) { /* an unreadable payload shows no link */ }
        }
        return links;
    }

    /// <summary>Requisitions and orders of a vessel a task can be linked to (the most recent ones).</summary>
    public async Task<List<LinkCandidate>> LinkCandidatesAsync(Guid companyId, Guid vesselId, int take = 40)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var reqns = await db.Requisitions.AsNoTracking().Where(r => r.CompanyId == companyId && r.VesselId == vesselId)
            .OrderByDescending(r => r.CreatedAt).Take(take).Select(r => new LinkCandidate(WorklistLinkTypes.Requisition, r.Id, r.ReqnNumber)).ToListAsync();
        var orders = await db.PurchaseOrders.AsNoTracking().Where(p => p.CompanyId == companyId && p.VesselId == vesselId)
            .OrderByDescending(p => p.CreatedDate).Take(take).Select(p => new LinkCandidate(WorklistLinkTypes.Order, p.Id, p.PoNumber)).ToListAsync();
        return [.. reqns, .. orders];
    }

    /// <summary>Tasks that carry a link to the given requisition, order or task.</summary>
    public async Task<List<WorklistTask>> TasksLinkedToAsync(Guid companyId, string linkType, Guid target)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var text = target.ToString();
        var candidates = await db.WorklistEntries.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.Type == WorklistEntryType.Link && e.Payload.Contains(text)).ToListAsync();
        var taskIds = candidates.Where(e => LinksOf([e]).Any(l => l.Type == linkType && l.Target == target)).Select(e => e.TaskId).Distinct().ToList();
        return await db.WorklistTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).OrderBy(t => t.Code).ToListAsync();
    }

    /// <summary>
    /// Copies a task to other vessels (the office does this, for faults found on a sister ship). Each copy is a new task on its vessel with the
    /// same wording, source, nature, category, department, priority and dates, and no assignee; the copy and the origin link to each other.
    /// Vessels the worklist is off for are skipped and named in the result.
    /// </summary>
    public async Task<TransferResult> TransferAsync(Guid companyId, Guid taskId, IEnumerable<Guid> vesselIds, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("Only the office can copy a task to other vessels.");
        var origin = await db.WorklistTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Task not found.");
        if (origin.Status == WorklistStatus.Cancelled) throw new InvalidOperationException("A cancelled task cannot be copied.");

        var enabled = (await EnabledVesselsAsync(db, companyId)).ToDictionary(v => v.Id, v => v.Name);
        var result = new TransferResult([], []);
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var vesselId in vesselIds.Distinct().Where(v => v != origin.VesselId))
        {
            if (!enabled.ContainsKey(vesselId))
            {
                result.Skipped.Add((await db.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vesselId))?.Name ?? vesselId.ToString());
                continue;
            }
            var (copy, created) = await StageNewTaskAsync(db, companyId, new NewWorklistTask(vesselId, origin.Title, origin.Description, origin.SourceId, origin.NatureId,
                origin.CategoryId, origin.DepartmentId, origin.Priority, origin.PlannedDate, origin.TargetDate), actor);
            db.WorklistEntries.Add(LinkEntry(copy, actor, created.At.AddMilliseconds(1), WorklistLinkTypes.Task, origin.Id, $"Copied from {origin.Code}"));
            db.WorklistEntries.Add(LinkEntry(origin, actor, DateTime.UtcNow, WorklistLinkTypes.Task, copy.Id, $"Copied to {copy.Code} ({enabled[vesselId]})"));
            result.Created.Add(copy);
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        if (result.Created.Count > 0) await WorklistProjection.RefreshAsync(db, origin.Id);
        return result;
    }

    private static WorklistEntry LinkEntry(WorklistTask task, Actor actor, DateTime at, string type, Guid target, string label)
        => NewEntry(task, WorklistEntryType.Link, actor, at, "", WorklistProjection.Json(("linkType", type), ("target", target.ToString()), ("label", label)));

    // ---- rules ----

    private static WorklistEntry NewEntry(WorklistTask task, WorklistEntryType type, Actor actor, DateTime at, string text, string payload) => new()
    {
        Id = Guid.CreateVersion7(), CompanyId = task.CompanyId, TaskId = task.Id, Type = type, Text = text, Payload = payload,
        AuthorUserId = actor.UserId, AuthorName = actor.Name, At = at,
    };

    /// <summary>Who may add which kind of entry (see the class comment).</summary>
    private static void EnsureAllowed(ChandleryDbContext db, WorklistTask task, WorklistEntryInput input)
    {
        if (input.Type == WorklistEntryType.Created) throw new InvalidOperationException("A task has only one starting entry.");
        var onVessel = db.Node.IsVessel;
        var raisedHere = task.OwnerStage == (onVessel ? SyncStages.Vessel : SyncStages.Office);

        if (input.Type == WorklistEntryType.Verified && onVessel)
            throw new InvalidOperationException("Only the office can verify a completed task.");
        if (input.Type == WorklistEntryType.Verified && task.Status != WorklistStatus.Completed)
            throw new InvalidOperationException("Only a completed task can be verified.");
        if (input.Type == WorklistEntryType.Verified && !task.RequiresVerification)
            throw new InvalidOperationException("This task does not need verification.");

        // The office shapes the task (priority, target date, assignee, wording, cancelling); the vessel does so only for tasks it raised itself.
        var shaping = input.Type is WorklistEntryType.Priority or WorklistEntryType.Target or WorklistEntryType.Assignee or WorklistEntryType.Details
            || (input.Type == WorklistEntryType.Status && input.Status == WorklistStatus.Cancelled);
        if (shaping && onVessel && !raisedHere)
            throw new InvalidOperationException("The office raised this task and decides its priority, dates, assignee and wording; add progress or a comment instead.");

        switch (input.Type)
        {
            case WorklistEntryType.Comment when string.IsNullOrWhiteSpace(input.Text):
                throw new InvalidOperationException("Write the comment.");
            case WorklistEntryType.Status when input.Status is null:
                throw new InvalidOperationException("Choose the status.");
            case WorklistEntryType.Status when input.Status == WorklistStatus.Completed:
                throw new InvalidOperationException("Use \"complete\" to close a task, with a note of what was done.");
            case WorklistEntryType.Percent when input.Percent is null or < 0 or > 100:
                throw new InvalidOperationException("Percent complete is between 0 and 100.");
            case WorklistEntryType.Target when input.Date is null || string.IsNullOrWhiteSpace(input.Text):
                throw new InvalidOperationException("Give the new target date and the reason.");
            case WorklistEntryType.Defer when input.Date is null || string.IsNullOrWhiteSpace(input.Text):
                throw new InvalidOperationException("A deferral needs a new target date and a reason.");
            case WorklistEntryType.Completed when string.IsNullOrWhiteSpace(input.Text):
                throw new InvalidOperationException("Say what was done to complete the task.");
            case WorklistEntryType.Details when string.IsNullOrWhiteSpace(input.Title):
                throw new InvalidOperationException("The task needs a title.");
            case WorklistEntryType.Priority when input.Priority is null:
                throw new InvalidOperationException("Choose the priority.");
            case WorklistEntryType.Link when string.IsNullOrWhiteSpace(input.LinkType) || input.LinkTarget is null:
                throw new InvalidOperationException("A link needs a type and a target.");
        }
        if (task.Status == WorklistStatus.Cancelled && input.Type != WorklistEntryType.Comment)
            throw new InvalidOperationException("This task was cancelled; only comments can be added.");
    }

    private static string BuildPayload(WorklistEntryInput i) => i.Type switch
    {
        WorklistEntryType.Status => WorklistProjection.Json(("status", i.Status)),
        WorklistEntryType.Percent => WorklistProjection.Json(("percent", i.Percent)),
        WorklistEntryType.Priority => WorklistProjection.Json(("priority", i.Priority)),
        WorklistEntryType.Target or WorklistEntryType.Defer => WorklistProjection.Json(("target", i.Date)),
        WorklistEntryType.Assignee => WorklistProjection.Json(("assignee", (i.Assignee ?? "").Trim())),
        WorklistEntryType.Details => WorklistProjection.Json(("title", i.Title?.Trim()), ("description", i.Description?.Trim())),
        WorklistEntryType.Link => WorklistProjection.Json(("linkType", i.LinkType), ("target", i.LinkTarget?.ToString()), ("label", i.LinkLabel)),
        _ => "{}",
    };
}

/// <summary>The configurable lists every company starts with. All editable by the office.</summary>
public static class WorklistLists
{
    private static readonly (string Name, bool Verify)[] Sources =
    [
        ("Internal audit", false), ("Class", true), ("Charterer inspection", false), ("Port state control", true), ("Incident", false),
        ("Safety officer", false), ("PMS deferment", false), ("Management", false), ("Vessel", false),
    ];

    private static readonly string[] Natures = ["Planned", "Emergency", "Deferred", "Survey", "Recommendation", "Condition"];

    private static readonly (string Name, string[] Children)[] Categories =
    [
        ("Safety", ["Fire fighting", "Lifesaving", "Safety management"]),
        ("Navigation", ["Equipment", "Charts and publications"]),
        ("Machinery", ["Main engine", "Auxiliary", "Electrical"]),
        ("Hull and structure", ["Deck", "Tanks and holds"]),
        ("Environment", []),
        ("Crew and welfare", []),
        ("Documentation", []),
    ];

    /// <summary>Adds the starter lists to a company that has none yet. Adds to the caller's context; the caller saves.</summary>
    public static async Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId)
    {
        if (await db.WorklistListItems.IgnoreQueryFilters().AnyAsync(i => i.CompanyId == companyId)) return;

        var order = 0;
        foreach (var (name, verify) in Sources)
            db.WorklistListItems.Add(new WorklistListItem { Id = Guid.CreateVersion7(), CompanyId = companyId, Kind = WorklistListKind.Source, Name = name, RequiresVerification = verify, SortOrder = order++ });
        order = 0;
        foreach (var name in Natures)
            db.WorklistListItems.Add(new WorklistListItem { Id = Guid.CreateVersion7(), CompanyId = companyId, Kind = WorklistListKind.Nature, Name = name, SortOrder = order++ });
        order = 0;
        foreach (var (name, children) in Categories)
        {
            var parent = new WorklistListItem { Id = Guid.CreateVersion7(), CompanyId = companyId, Kind = WorklistListKind.Category, Name = name, SortOrder = order++ };
            db.WorklistListItems.Add(parent);
            var child = 0;
            foreach (var childName in children)
                db.WorklistListItems.Add(new WorklistListItem { Id = Guid.CreateVersion7(), CompanyId = companyId, Kind = WorklistListKind.Category, Name = childName, ParentId = parent.Id, SortOrder = child++ });
        }
    }
}
