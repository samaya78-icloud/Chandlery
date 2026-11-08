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

/// <summary>
/// Tasks and their history. The only changes are new entries; a task is never edited. Each side may add progress; the office alone
/// verifies, and changes priority, target date and assignee of a task the vessel raised (the vessel does so for its own tasks).
/// </summary>
public class WorklistService(IDbContextFactory<ChandleryDbContext> factory)
{
    // ---- the module switch ----

    /// <summary>Is Worklist switched on for this vessel (its own switch, else its group's, up the tree; none means off)?</summary>
    public static async Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId)
    {
        var vessel = await db.Vessels.FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId);
        if (vessel is null) return false;
        var groups = await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
        var switches = await db.VesselModuleSwitches.Where(m => m.CompanyId == companyId).ToListAsync();
        return PolicyResolver.ResolveModule(vessel.Id, PolicyResolver.GroupChain(groups, vessel.GroupId), switches, groups.ToDictionary(g => g.Id, g => g.Name), PolicyModules.Worklist).Enabled;
    }

    /// <summary>Is Worklist on for at least one vessel of the company (for showing it in the office menu)?</summary>
    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        foreach (var vessel in await db.Vessels.Where(v => v.CompanyId == companyId).Select(v => v.Id).ToListAsync())
            if (await IsEnabledForVesselAsync(db, companyId, vessel)) return true;
        return false;
    }

    // ---- writing ----

    public async Task<WorklistTask> CreateTaskAsync(Guid companyId, NewWorklistTask input, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

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
        await db.SaveChangesAsync();
        return task;
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
        var existing = await db.WorklistEntries.AsNoTracking().Where(e => e.TaskId == taskId).ToListAsync();
        WorklistProjection.Apply(task, WorklistProjection.Replay([.. existing, entry]));
        db.WorklistEntries.Add(entry);
        await db.SaveChangesAsync();
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
        var result = new List<(Guid, string)>();
        foreach (var v in await db.Vessels.Where(v => v.CompanyId == companyId).OrderBy(v => v.Name).ToListAsync())
            if (await IsEnabledForVesselAsync(db, companyId, v.Id)) result.Add((v.Id, v.Name));
        return result;
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
