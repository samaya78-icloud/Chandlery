using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record NewProject(Guid VesselId, ProjectType Type, string Title, string Description = "", Guid? SuperintendentUserId = null, string SuperintendentName = "",
    DateTime? PlannedStart = null, DateTime? PlannedEnd = null, string Yard = "", bool IsUwild = false);

public record ProjectEdit(ProjectType Type, string Title, string Description, Guid? SuperintendentUserId, string SuperintendentName, DateTime? PlannedStart, DateTime? PlannedEnd,
    string Yard, bool IsUwild);

public record ProjectItemInput(Guid? Id, string Group, string Description, ProjectItemSource Source = ProjectItemSource.Manual, Guid? SourceId = null, string SourceRef = "",
    ProjectItemStatus Status = ProjectItemStatus.Open);

public record BudgetLineInput(string Category, decimal PlannedUsd, decimal ContingencyPct = 0);

public record NewProjectNote(DateTime Date, string Text, int? ProgressPct = null, Guid? ItemId = null);

public record ProjectDetail(Project Project, List<ProjectStatusEvent> Events, List<ProjectNote> Notes, ProjectStatus Status, DateTime? ActualStart, DateTime? ActualEnd);

public record ProjectRow(Project Project, ProjectStatus Status, DateTime? ActualStart, DateTime? ActualEnd, int ItemsOpen, int ItemsDone, int ItemsDropped);

/// <summary>
/// Projects: planned and run by the office, with the vessel adding notes and links. A project's status is worked out from the events the office records and a closed project is
/// locked. Budget, estimates and cost entries are kept in tables that never leave the office, and a vessel installation cannot write them.
/// </summary>
public class ProjectService(IDbContextFactory<ChandleryDbContext> factory)
{
    public const int MaxItems = 300;

    private static DateTime Day(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    private static void RequireOffice(ChandleryDbContext db)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException("Projects are planned and run by the office; onboard you can read them and add notes.");
    }

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Projects);

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Projects);
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    private static async Task<ProjectStatus> StatusOfAsync(ChandleryDbContext db, Guid projectId)
        => ProjectStatusRules.Of(await db.ProjectStatusEvents.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync());

    /// <summary>Loads a project with its items for changing, refusing when it is closed.</summary>
    private static async Task<(Project Project, ProjectStatus Status)> LoadEditableAsync(ChandleryDbContext db, Guid companyId, Guid id)
    {
        var project = await db.Projects.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId) ?? throw new InvalidOperationException("Project not found.");
        var status = await StatusOfAsync(db, id);
        if (!ProjectStatusRules.IsEditable(status)) throw new InvalidOperationException("This project is closed and locked.");
        return (project, status);
    }

    private static void CheckDates(DateTime? start, DateTime? end)
    {
        if (start is { } s && end is { } e && e.Date < s.Date) throw new InvalidOperationException("The planned end cannot be before the planned start.");
    }

    // ---- planning (office) ----

    public async Task<Project> CreateAsync(Guid companyId, NewProject input, Actor actor)
    {
        var title = (input.Title ?? "").Trim();
        if (title == "") throw new InvalidOperationException("Give the project a title.");
        CheckDates(input.PlannedStart, input.PlannedEnd);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        if (!await db.Vessels.AnyAsync(v => v.Id == input.VesselId && v.CompanyId == companyId)) throw new InvalidOperationException("Vessel not found.");
        if (!await IsEnabledForVesselAsync(db, companyId, input.VesselId)) throw new InvalidOperationException("Projects are not switched on for this vessel.");

        var project = new Project
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = input.VesselId, Code = await DocumentNumbers.NextAsync(db, companyId, input.VesselId, DocType.Project),
            Type = input.Type, Title = title, Description = (input.Description ?? "").Trim(), SuperintendentUserId = input.SuperintendentUserId, SuperintendentName = (input.SuperintendentName ?? "").Trim(),
            PlannedStart = input.PlannedStart is { } s ? Day(s) : null, PlannedEnd = input.PlannedEnd is { } e ? Day(e) : null, Yard = (input.Yard ?? "").Trim(), IsUwild = input.IsUwild,
            CreatedByUserId = actor.UserId, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return project;
    }

    public async Task UpdateAsync(Guid companyId, Guid id, ProjectEdit edit, Actor actor)
    {
        var title = (edit.Title ?? "").Trim();
        if (title == "") throw new InvalidOperationException("Give the project a title.");
        CheckDates(edit.PlannedStart, edit.PlannedEnd);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var (project, _) = await LoadEditableAsync(db, companyId, id);
        (project.Type, project.Title, project.Description) = (edit.Type, title, (edit.Description ?? "").Trim());
        (project.SuperintendentUserId, project.SuperintendentName) = (edit.SuperintendentUserId, (edit.SuperintendentName ?? "").Trim());
        (project.PlannedStart, project.PlannedEnd) = (edit.PlannedStart is { } s ? Day(s) : null, edit.PlannedEnd is { } e ? Day(e) : null);
        (project.Yard, project.IsUwild) = ((edit.Yard ?? "").Trim(), edit.IsUwild);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Replaces the specification with the items given (an id keeps the item; one that is missing is removed). The same Worklist task or PMS job may be named by only one item, so an
    /// import repeated by mistake adds nothing.
    /// </summary>
    public async Task SaveItemsAsync(Guid companyId, Guid id, IReadOnlyList<ProjectItemInput> items, Actor actor)
    {
        if (items.Count > MaxItems) throw new InvalidOperationException($"A project can hold at most {MaxItems} specification items.");
        if (items.Any(i => string.IsNullOrWhiteSpace(i.Description))) throw new InvalidOperationException("Describe every specification item.");
        var named = items.Where(i => i.Source is ProjectItemSource.WorklistTask or ProjectItemSource.PmsJob && i.SourceId is not null).Select(i => (i.Source, i.SourceId)).ToList();
        if (named.Count != named.Distinct().Count()) throw new InvalidOperationException("The same Worklist task or PMS job is on the specification twice.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var (project, _) = await LoadEditableAsync(db, companyId, id);

        var keep = new HashSet<Guid>();
        var order = 0;
        foreach (var input in items)
        {
            var item = input.Id is { } existing ? project.Items.FirstOrDefault(i => i.Id == existing) : null;
            if (item is null)
            {
                item = new ProjectItem { Id = Guid.CreateVersion7() };
                project.Items.Add(item);
                db.Entry(item).State = EntityState.Added;
            }
            (item.Group, item.Description) = (string.IsNullOrWhiteSpace(input.Group) ? "General" : input.Group.Trim(), input.Description.Trim());
            (item.Source, item.SourceId, item.SourceRef, item.Status, item.SortOrder) = (input.Source, input.SourceId, (input.SourceRef ?? "").Trim(), input.Status, order++);
            keep.Add(item.Id);
        }
        var removed = project.Items.Where(i => !keep.Contains(i.Id)).ToList();
        foreach (var gone in removed) project.Items.Remove(gone);
        if (removed.Count > 0)
        {
            var goneIds = removed.Select(r => r.Id).ToList();
            db.ProjectItemCosts.RemoveRange(await db.ProjectItemCosts.Where(c => goneIds.Contains(c.ItemId)).ToListAsync());
        }
        await db.SaveChangesAsync();
    }

    // ---- money (office only; never leaves the office) ----

    /// <summary>Replaces the budget lines with those given.</summary>
    public async Task SaveBudgetAsync(Guid companyId, Guid id, IReadOnlyList<BudgetLineInput> lines, Actor actor)
    {
        if (lines.Any(l => string.IsNullOrWhiteSpace(l.Category))) throw new InvalidOperationException("Give every budget line a category.");
        if (lines.Any(l => l.PlannedUsd < 0)) throw new InvalidOperationException("A budget line cannot be negative.");
        if (lines.Any(l => l.ContingencyPct is < 0 or > 100)) throw new InvalidOperationException("Contingency is a percentage from 0 to 100.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await LoadEditableAsync(db, companyId, id);
        db.ProjectBudgetLines.RemoveRange(await db.ProjectBudgetLines.Where(l => l.ProjectId == id).ToListAsync());
        var order = 0;
        foreach (var line in lines)
            db.ProjectBudgetLines.Add(new ProjectBudgetLine { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = id, Category = line.Category.Trim(), PlannedUsd = line.PlannedUsd, ContingencyPct = line.ContingencyPct, SortOrder = order++ });
        await db.SaveChangesAsync();
    }

    public async Task<List<ProjectBudgetLine>> BudgetAsync(Guid companyId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.ProjectBudgetLines.AsNoTracking().Where(l => l.ProjectId == id).OrderBy(l => l.SortOrder).ToListAsync();
    }

    /// <summary>The estimate and the yard's quote for one item; blank clears it.</summary>
    public async Task SetItemCostAsync(Guid companyId, Guid projectId, Guid itemId, decimal? estimatedUsd, decimal? quotedUsd)
    {
        if (estimatedUsd < 0 || quotedUsd < 0) throw new InvalidOperationException("An amount cannot be negative.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        if (project.Items.All(i => i.Id != itemId)) throw new InvalidOperationException("That item is not on this project.");
        var cost = await db.ProjectItemCosts.FirstOrDefaultAsync(c => c.ItemId == itemId);
        if (estimatedUsd is null && quotedUsd is null)
        {
            if (cost is not null) db.ProjectItemCosts.Remove(cost);
        }
        else
        {
            if (cost is null) db.ProjectItemCosts.Add(cost = new ProjectItemCost { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, ItemId = itemId });
            (cost.EstimatedUsd, cost.QuotedUsd) = (estimatedUsd, quotedUsd);
        }
        await db.SaveChangesAsync();
    }

    public async Task<List<ProjectItemCost>> ItemCostsAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.ProjectItemCosts.AsNoTracking().Where(c => c.ProjectId == projectId).ToListAsync();
    }

    /// <summary>A cost that did not come through procurement. A credit is entered as a negative amount.</summary>
    public async Task<ProjectCostEntry> AddCostEntryAsync(Guid companyId, Guid projectId, DateTime date, string description, string reference, decimal amountUsd, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("Say what the cost is for.");
        if (amountUsd == 0) throw new InvalidOperationException("Enter an amount.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await LoadEditableAsync(db, companyId, projectId);
        var entry = new ProjectCostEntry { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Date = Day(date), Description = description.Trim(), Reference = (reference ?? "").Trim(), AmountUsd = amountUsd, ByName = actor.Name, At = DateTime.UtcNow };
        db.ProjectCostEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    public async Task RemoveCostEntryAsync(Guid companyId, Guid entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var entry = await db.ProjectCostEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == companyId) ?? throw new InvalidOperationException("Cost entry not found.");
        await LoadEditableAsync(db, companyId, entry.ProjectId);
        db.ProjectCostEntries.Remove(entry);
        await db.SaveChangesAsync();
    }

    public async Task<List<ProjectCostEntry>> CostEntriesAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.ProjectCostEntries.AsNoTracking().Where(e => e.ProjectId == projectId).OrderBy(e => e.Date).ThenBy(e => e.At).ToListAsync();
    }

    // ---- the project's life (office) ----

    /// <summary>
    /// Records a step: approve, start, complete, close, or withdraw an approval. The step must follow from where the project is. Approving keeps what the budget came to at that moment;
    /// closing needs every item Done or Dropped and a reconciliation note, and locks the project.
    /// </summary>
    public async Task<ProjectStatusEvent> RecordAsync(Guid companyId, Guid id, ProjectEventKind kind, DateTime date, string note, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var project = await db.Projects.AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId) ?? throw new InvalidOperationException("Project not found.");
        var events = await db.ProjectStatusEvents.AsNoTracking().Where(e => e.ProjectId == id).ToListAsync();
        var status = ProjectStatusRules.Of(events);
        if (!ProjectStatusRules.Allows(status, kind))
            throw new InvalidOperationException(status == ProjectStatus.Closed ? "This project is closed and locked." : $"A project that is {status.ToString().ToLowerInvariant()} cannot be {Past(kind)}.");

        var today = DateTime.UtcNow.Date;
        var day = kind is ProjectEventKind.Started or ProjectEventKind.Completed ? date.Date : today;
        if (day > today) throw new InvalidOperationException("That date is in the future.");
        if (kind == ProjectEventKind.Completed && ProjectStatusRules.ActualStart(events) is { } started && day < started) throw new InvalidOperationException("Work cannot finish before it started.");
        if (kind == ProjectEventKind.Closed)
        {
            var problems = ProjectStatusRules.CloseProblems(project.Items, note);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
        }

        var record = new ProjectStatusEvent
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = project.VesselId, ProjectId = id, Kind = kind, Date = Day(day), Note = (note ?? "").Trim(),
            ByUserId = actor.UserId, ByName = actor.Name, At = DateTime.UtcNow,
        };
        db.ProjectStatusEvents.Add(record);
        if (kind == ProjectEventKind.Approved)
        {
            var lines = await db.ProjectBudgetLines.AsNoTracking().Where(l => l.ProjectId == id).ToListAsync();
            db.ProjectBudgetApprovals.Add(new ProjectBudgetApproval
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = id, EventId = record.Id, TotalUsd = ProjectCostRules.Budget(lines), ContingencyUsd = ProjectCostRules.Contingency(lines),
                ApprovedByName = actor.Name, At = record.At,
            });
        }
        await db.SaveChangesAsync();
        return record;
    }

    private static string Past(ProjectEventKind kind) => kind switch
    {
        ProjectEventKind.Approved => "approved", ProjectEventKind.Started => "started", ProjectEventKind.Completed => "completed", ProjectEventKind.Closed => "closed", _ => "withdrawn",
    };

    /// <summary>What the budget came to at each approval, newest last.</summary>
    public async Task<List<ProjectBudgetApproval>> ApprovalsAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.ProjectBudgetApprovals.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.At).ToListAsync();
    }

    // ---- contributions from either side ----

    /// <summary>A progress note or daily yard report, written by whichever side is on the project.</summary>
    public async Task<ProjectNote> AddNoteAsync(Guid companyId, Guid projectId, NewProjectNote input, Actor actor)
    {
        var text = (input.Text ?? "").Trim();
        if (text == "") throw new InvalidOperationException("Write the note.");
        if (input.ProgressPct is < 0 or > 100) throw new InvalidOperationException("Progress is a percentage from 0 to 100.");
        if (input.Date.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("A note cannot be dated in the future.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        if (!await IsEnabledForVesselAsync(db, companyId, project.VesselId)) throw new InvalidOperationException("Projects are not switched on for this vessel.");
        if (input.ItemId is { } itemId && project.Items.All(i => i.Id != itemId)) throw new InvalidOperationException("That item is not on this project.");

        var note = new ProjectNote
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = project.VesselId, ProjectId = projectId, ItemId = input.ItemId, Date = Day(input.Date), Text = text,
            ProgressPct = input.ProgressPct, AuthorUserId = actor.UserId, AuthorName = actor.Name, At = DateTime.UtcNow,
        };
        db.ProjectNotes.Add(note);
        await db.SaveChangesAsync();
        return note;
    }

    /// <summary>
    /// Says a requisition, order or invoice belongs to the project. A document belongs to one project: linking it to another moves it. Written by whichever side makes the link; the document
    /// must be for the same vessel.
    /// </summary>
    public async Task<ProjectLink> LinkAsync(Guid companyId, Guid projectId, ProjectLinkKind kind, Guid docId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        if (!await IsEnabledForVesselAsync(db, companyId, project.VesselId)) throw new InvalidOperationException("Projects are not switched on for this vessel.");
        if (!await BelongsToVesselAsync(db, companyId, project.VesselId, kind, docId)) throw new InvalidOperationException($"That {KindLabel(kind)} was not found for this vessel.");

        var held = ProjectLinkRules.Effective(await db.ProjectLinks.AsNoTracking().Where(l => l.Kind == kind && l.DocId == docId).ToListAsync());
        if (held.TryGetValue((kind, docId), out var already) && already == projectId)
            return await db.ProjectLinks.AsNoTracking().Where(l => l.Kind == kind && l.DocId == docId && l.ProjectId == projectId && l.Added).OrderByDescending(l => l.At).ThenByDescending(l => l.Id).FirstAsync();
        return await WriteLinkAsync(db, companyId, project, kind, docId, added: true, actor);
    }

    public async Task<ProjectLink> UnlinkAsync(Guid companyId, Guid projectId, ProjectLinkKind kind, Guid docId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        var held = ProjectLinkRules.Effective(await db.ProjectLinks.AsNoTracking().Where(l => l.Kind == kind && l.DocId == docId).ToListAsync());
        if (!held.TryGetValue((kind, docId), out var current) || current != projectId) throw new InvalidOperationException($"That {KindLabel(kind)} is not linked to this project.");
        return await WriteLinkAsync(db, companyId, project, kind, docId, added: false, actor);
    }

    private static async Task<ProjectLink> WriteLinkAsync(ChandleryDbContext db, Guid companyId, Project project, ProjectLinkKind kind, Guid docId, bool added, Actor actor)
    {
        var link = new ProjectLink { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = project.VesselId, ProjectId = project.Id, Kind = kind, DocId = docId, Added = added, ByUserId = actor.UserId, ByName = actor.Name, At = DateTime.UtcNow };
        db.ProjectLinks.Add(link);
        await db.SaveChangesAsync();
        return link;
    }

    private static string KindLabel(ProjectLinkKind kind) => kind switch { ProjectLinkKind.Requisition => "requisition", ProjectLinkKind.PurchaseOrder => "purchase order", _ => "invoice" };

    private static async Task<bool> BelongsToVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, ProjectLinkKind kind, Guid docId) => kind switch
    {
        ProjectLinkKind.Requisition => await db.Requisitions.AsNoTracking().AnyAsync(r => r.Id == docId && r.CompanyId == companyId && r.VesselId == vesselId),
        ProjectLinkKind.PurchaseOrder => await db.PurchaseOrders.AsNoTracking().AnyAsync(p => p.Id == docId && p.CompanyId == companyId && p.VesselId == vesselId),
        // A spot invoice names its vessel; an invoice against an order takes the order's.
        _ => await db.Invoices.AsNoTracking().AnyAsync(i => i.Id == docId && i.CompanyId == companyId
                && (i.VesselId == vesselId || (i.PurchaseOrderId != null && db.PurchaseOrders.Any(p => p.Id == i.PurchaseOrderId && p.VesselId == vesselId)))),
    };

    /// <summary>The project a document belongs to, or null.</summary>
    public async Task<Guid?> ProjectOfAsync(Guid companyId, ProjectLinkKind kind, Guid docId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var links = await db.ProjectLinks.AsNoTracking().Where(l => l.Kind == kind && l.DocId == docId).ToListAsync();
        return ProjectLinkRules.Effective(links).TryGetValue((kind, docId), out var project) ? project : null;
    }

    /// <summary>The documents that belong to a project now, from the links written.</summary>
    public async Task<List<(ProjectLinkKind Kind, Guid DocId)>> LinksOfAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        // A document may have been moved to another project, so the latest link for each of this project's documents must be read, not only this project's own.
        var docs = await db.ProjectLinks.AsNoTracking().Where(l => l.ProjectId == projectId).Select(l => new { l.Kind, l.DocId }).Distinct().ToListAsync();
        var docIds = docs.Select(d => d.DocId).ToList();
        var all = await db.ProjectLinks.AsNoTracking().Where(l => docIds.Contains(l.DocId)).ToListAsync();
        return ProjectLinkRules.Effective(all).Where(kv => kv.Value == projectId).Select(kv => kv.Key).OrderBy(k => k.Kind).ThenBy(k => k.DocId).ToList();
    }

    // ---- reading ----

    public async Task<ProjectDetail?> GetAsync(Guid companyId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var project = await db.Projects.AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId);
        if (project is null) return null;
        project.Items = project.Items.OrderBy(i => i.SortOrder).ToList();
        var events = ProjectStatusRules.Ordered(await db.ProjectStatusEvents.AsNoTracking().Where(e => e.ProjectId == id).ToListAsync());
        var notes = await db.ProjectNotes.AsNoTracking().Where(n => n.ProjectId == id).OrderBy(n => n.Date).ThenBy(n => n.At).ThenBy(n => n.Id).ToListAsync();
        return new ProjectDetail(project, events, notes, ProjectStatusRules.Of(events), ProjectStatusRules.ActualStart(events), ProjectStatusRules.ActualEnd(events));
    }

    public async Task<List<ProjectRow>> ListAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.Projects.AsNoTracking().Include(p => p.Items).Where(p => p.CompanyId == companyId);
        if (vesselId is { } v) query = query.Where(p => p.VesselId == v);
        var projects = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
        var ids = projects.Select(p => p.Id).ToList();
        var events = (await db.ProjectStatusEvents.AsNoTracking().Where(e => ids.Contains(e.ProjectId)).ToListAsync()).ToLookup(e => e.ProjectId);
        return projects.Select(p =>
        {
            var e = events[p.Id].ToList();
            return new ProjectRow(p, ProjectStatusRules.Of(e), ProjectStatusRules.ActualStart(e), ProjectStatusRules.ActualEnd(e),
                p.Items.Count(i => i.Status == ProjectItemStatus.Open), p.Items.Count(i => i.Status == ProjectItemStatus.Done), p.Items.Count(i => i.Status == ProjectItemStatus.Dropped));
        }).ToList();
    }
}
