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

/// <summary>The project a document is on. Inherited means it has no link of its own and takes the project of its requisition or order.</summary>
public record DocProject(Guid ProjectId, string Code, string Title, ProjectStatus Status, bool Inherited);

public record ProjectOption(Guid Id, string Code, string Title, ProjectStatus Status);

/// <summary>A project as the list shows it: with its vessel, budget, what it has cost, and its days.</summary>
public record ProjectListRow(ProjectRow Row, string VesselName, decimal Budget, decimal Contingency, ProjectCost Cost, int? PlannedDays, int? ActualDays)
{
    public decimal Variance => ProjectCostRules.Variance(Budget, Cost.ToDate);

    /// <summary>Over budget only means something once a budget has been set.</summary>
    public bool OverBudget => Budget > 0 && Cost.ToDate > Budget;
}

/// <summary>NotStarted is planning or approved: work has not begun.</summary>
public record ProjectCounts(int Ongoing, int NotStarted, int OverBudget, int RequestsWaiting = 0);

/// <summary>A request from the vessel with the office's answer, if there is one yet.</summary>
public record ProjectRequestView(ProjectItemRequest Request, ProjectItemDecision? Decision);

public record ImportTask(Guid Id, string Code, string Title, bool Urgent, DateTime? Target);

public record ImportJob(Guid Id, string Code, string Title, string Machinery, DateTime? Due, bool Critical);

public record ImportCandidates(string? NatureName, bool WorklistOn, bool PmsOn, List<ImportTask> Tasks, List<ImportJob> Jobs);

public record ProjectImportOutcome(int Added, List<string> Notes);

public enum ProjectCostBucket { Committed, Invoiced, Settled, NotCounted }

/// <summary>One document behind the cost figures. Settled is an order whose invoices have replaced it; NotCounted is one not yet approved or finalized (or rejected).</summary>
public record ProjectCostLine(ProjectLinkKind Kind, Guid DocId, string Number, string Supplier, string Status, string Currency, decimal Amount, decimal AmountUsd, ProjectCostBucket Bucket);

public record ProjectCostSummary(ProjectCost Cost, List<ProjectCostLine> Lines, List<ProjectCostEntry> Entries, int Unconverted);

internal record ProjectDocs(HashSet<Guid> Requisitions, List<PurchaseOrder> Orders, List<Invoice> Invoices);

public record ProjectRow(Project Project, ProjectStatus Status, DateTime? ActualStart, DateTime? ActualEnd, int ItemsOpen, int ItemsDone, int ItemsDropped);

/// <summary>
/// Projects: planned and run by the office, with the vessel adding notes and links. A project's status is worked out from the events the office records and a closed project is
/// locked. Budget, estimates and cost entries are kept in tables that never leave the office, and a vessel installation cannot write them.
/// </summary>
public class ProjectService(IDbContextFactory<ChandleryDbContext> factory, WorklistService? worklist = null)
{
    public const int MaxItems = 300;

    private static DateTime Day(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    internal static void RequireOffice(ChandleryDbContext db)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException("Projects are planned and run by the office; onboard you can read them and add notes.");
    }

    /// <summary>A company admin, or an office user the admin has named a project manager. Everyone else can read projects and add notes and links.</summary>
    internal static async Task<bool> MayManageAsync(ChandleryDbContext db, Guid companyId, Guid? userId)
        => userId is { } uid && await db.Users.AsNoTracking().AnyAsync(u => u.Id == uid && u.CompanyId == companyId && u.IsEnabled
            && (u.Role == UserRole.CompanyAdmin || (u.Role == UserRole.CompanyUser && u.IsProjectManager)));

    internal static async Task RequireManagerAsync(ChandleryDbContext db, Guid companyId, Actor actor)
    {
        if (!await MayManageAsync(db, companyId, actor.UserId)) throw new InvalidOperationException("Only a company admin or a project manager can do this.");
    }

    public async Task<bool> MayManageAsync(Guid companyId, Guid? userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return !db.Node.IsVessel && await MayManageAsync(db, companyId, userId);
    }

    /// <summary>Names (or removes) an office user as a project manager. A company admin always is one, so the flag changes nothing for them.</summary>
    public async Task SetProjectManagerAsync(Guid companyId, Guid userId, bool isManager)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.CompanyId == companyId && (u.Role == UserRole.CompanyAdmin || u.Role == UserRole.CompanyUser))
            ?? throw new InvalidOperationException("User not found.");
        user.IsProjectManager = isManager;
        await db.SaveChangesAsync();
    }

    /// <summary>The office users a project's superintendent can be picked from.</summary>
    public async Task<List<(Guid Id, string Name)>> OfficeUsersAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var users = await db.Users.AsNoTracking().Where(u => u.CompanyId == companyId && u.IsEnabled && (u.Role == UserRole.CompanyAdmin || u.Role == UserRole.CompanyUser)).OrderBy(u => u.DisplayName).ToListAsync();
        return users.Select(u => (u.Id, u.DisplayName)).ToList();
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
    internal static async Task<(Project Project, ProjectStatus Status)> LoadEditableAsync(ChandleryDbContext db, Guid companyId, Guid id)
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
        await RequireManagerAsync(db, companyId, actor);
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
        await RequireManagerAsync(db, companyId, actor);
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
    public async Task<List<string>> SaveItemsAsync(Guid companyId, Guid id, IReadOnlyList<ProjectItemInput> items, Actor actor)
    {
        if (items.Count > MaxItems) throw new InvalidOperationException($"A project can hold at most {MaxItems} specification items.");
        if (items.Any(i => string.IsNullOrWhiteSpace(i.Description))) throw new InvalidOperationException("Describe every specification item.");
        var named = items.Where(i => i.Source is ProjectItemSource.WorklistTask or ProjectItemSource.PmsJob && i.SourceId is not null).Select(i => (i.Source, i.SourceId)).ToList();
        if (named.Count != named.Distinct().Count()) throw new InvalidOperationException("The same Worklist task or PMS job is on the specification twice.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
        var (project, _) = await LoadEditableAsync(db, companyId, id);

        var keep = new HashSet<Guid>();
        var justDone = new List<Guid>();
        var order = 0;
        foreach (var input in items)
        {
            var item = input.Id is { } existing ? project.Items.FirstOrDefault(i => i.Id == existing) : null;
            var before = item?.Status ?? ProjectItemStatus.Open;
            if (item is null)
            {
                item = new ProjectItem { Id = Guid.CreateVersion7() };
                project.Items.Add(item);
                db.Entry(item).State = EntityState.Added;
            }
            (item.Group, item.Description) = (string.IsNullOrWhiteSpace(input.Group) ? "General" : input.Group.Trim(), input.Description.Trim());
            (item.Source, item.SourceId, item.SourceRef, item.Status, item.SortOrder) = (input.Source, input.SourceId, (input.SourceRef ?? "").Trim(), input.Status, order++);
            keep.Add(item.Id);
            if (item.Source == ProjectItemSource.WorklistTask && item.SourceId is { } taskId && item.Status == ProjectItemStatus.Done && before != ProjectItemStatus.Done) justDone.Add(taskId);
        }
        var removed = project.Items.Where(i => !keep.Contains(i.Id)).ToList();
        foreach (var gone in removed) project.Items.Remove(gone);
        if (removed.Count > 0)
        {
            var goneIds = removed.Select(r => r.Id).ToList();
            db.ProjectItemCosts.RemoveRange(await db.ProjectItemCosts.Where(c => goneIds.Contains(c.ItemId)).ToListAsync());
        }
        await db.SaveChangesAsync();

        // An item marked Done closes the Worklist task it came from. The task keeps its own rules: one that needs the office to verify it stays open until it is verified.
        var notes = new List<string>();
        foreach (var taskId in justDone) await CompleteTaskAsync(companyId, taskId, project.Code, actor, notes);
        return notes;
    }

    private async Task CompleteTaskAsync(Guid companyId, Guid taskId, string projectCode, Actor actor, List<string> notes)
    {
        if (worklist is null) return;
        var detail = await worklist.GetAsync(companyId, taskId);
        if (detail is null || detail.Task.Status is WorklistStatus.Completed or WorklistStatus.Cancelled) return;
        try
        {
            await worklist.AddEntryAsync(companyId, taskId, new WorklistEntryInput(WorklistEntryType.Completed, Text: $"Done at the yard (project {projectCode})."), actor);
            notes.Add(detail.Task.RequiresVerification ? $"Worklist task {detail.Task.Code} is completed; the office still has to verify it." : $"Worklist task {detail.Task.Code} is completed.");
        }
        catch (InvalidOperationException ex)
        {
            notes.Add($"Worklist task {detail.Task.Code} could not be completed: {ex.Message}");
        }
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
        await RequireManagerAsync(db, companyId, actor);
        await LoadEditableAsync(db, companyId, id);
        if (await db.ProjectApprovalRequests.AnyAsync(r => r.ProjectId == id && r.Kind == ProjectRequestKind.Budget && r.Status == ProjectRequestStatus.Pending))
            throw new InvalidOperationException("The budget is with the approvers. Withdraw the submission to change it.");
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
    public async Task SetItemCostAsync(Guid companyId, Guid projectId, Guid itemId, decimal? estimatedUsd, decimal? quotedUsd, Actor actor)
    {
        if (estimatedUsd < 0 || quotedUsd < 0) throw new InvalidOperationException("An amount cannot be negative.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
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
        await RequireManagerAsync(db, companyId, actor);
        await LoadEditableAsync(db, companyId, projectId);
        var entry = new ProjectCostEntry { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Date = Day(date), Description = description.Trim(), Reference = (reference ?? "").Trim(), AmountUsd = amountUsd, ByName = actor.Name, At = DateTime.UtcNow };
        db.ProjectCostEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    public async Task RemoveCostEntryAsync(Guid companyId, Guid entryId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
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
        await RequireManagerAsync(db, companyId, actor);
        var record = await RecordCoreAsync(db, companyId, id, kind, date, note, actor, throughMatrix: false);
        await db.SaveChangesAsync();
        return record;
    }

    /// <summary>The checks and the event for one step, added to the context but not saved. Approving is refused here when the approval matrix applies, unless it comes through the matrix.</summary>
    private static async Task<ProjectStatusEvent> RecordCoreAsync(ChandleryDbContext db, Guid companyId, Guid id, ProjectEventKind kind, DateTime date, string note, Actor actor, bool throughMatrix)
    {
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

        var lines = kind == ProjectEventKind.Approved ? await db.ProjectBudgetLines.AsNoTracking().Where(l => l.ProjectId == id).ToListAsync() : [];
        if (kind == ProjectEventKind.Approved && !throughMatrix && (await ChainAsync(db, companyId, ProjectCostRules.Budget(lines) + ProjectCostRules.Contingency(lines))).Count > 0)
            throw new InvalidOperationException("This budget needs approval through the approval matrix: submit it for approval.");

        var record = new ProjectStatusEvent
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = project.VesselId, ProjectId = id, Kind = kind, Date = Day(day), Note = (note ?? "").Trim(),
            ByUserId = actor.UserId, ByName = actor.Name, At = DateTime.UtcNow,
        };
        db.ProjectStatusEvents.Add(record);
        if (kind == ProjectEventKind.Approved)
        {
            db.ProjectBudgetApprovals.Add(new ProjectBudgetApproval
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = id, EventId = record.Id, TotalUsd = ProjectCostRules.Budget(lines), ContingencyUsd = ProjectCostRules.Contingency(lines),
                ApprovedByName = actor.Name, At = record.At,
            });
        }
        return record;
    }

    // ---- approval through the matrix (office) ----

    /// <summary>Who must approve an amount: the company's matrix rows without a department (a project belongs to none), at the tier that covers it. Empty when the company has none.</summary>
    private static async Task<List<ApprovalRule>> ChainAsync(ChandleryDbContext db, Guid companyId, decimal amountUsd)
        => ApprovalEngine.ResolveChain(await db.ApprovalRules.AsNoTracking().Where(r => r.CompanyId == companyId).ToListAsync(), null, amountUsd);

    private static void OpenRequest(ChandleryDbContext db, ProjectApprovalRequest request, List<ApprovalRule> chain)
    {
        db.ProjectApprovalRequests.Add(request);
        foreach (var rule in chain)
            db.ProjectApprovalSlots.Add(new ProjectApprovalSlot { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, RequestId = request.Id, Level = rule.Level, ApproverUserId = rule.ApproverUserId });
    }

    /// <summary>
    /// Asks for the budget to be approved. With no matrix rules the project is approved on the spot (as before); otherwise the people the matrix names for this total
    /// are asked, level by level, and the budget is held still until they answer or the submission is withdrawn.
    /// </summary>
    public async Task<ProjectApprovalRequest> SubmitAsync(Guid companyId, Guid projectId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
        var (_, status) = await LoadEditableAsync(db, companyId, projectId);
        if (status != ProjectStatus.Planning) throw new InvalidOperationException("Only a project being planned can be submitted for approval.");
        if (await db.ProjectApprovalRequests.AnyAsync(r => r.ProjectId == projectId && r.Kind == ProjectRequestKind.Budget && r.Status == ProjectRequestStatus.Pending))
            throw new InvalidOperationException("This budget is already with the approvers.");

        var lines = await db.ProjectBudgetLines.AsNoTracking().Where(l => l.ProjectId == projectId).ToListAsync();
        var (total, contingency) = (ProjectCostRules.Budget(lines), ProjectCostRules.Contingency(lines));
        var chain = await ChainAsync(db, companyId, total + contingency);
        var request = new ProjectApprovalRequest
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Kind = ProjectRequestKind.Budget, AmountUsd = total + contingency, ContingencyUsd = contingency,
            Status = chain.Count == 0 ? ProjectRequestStatus.Approved : ProjectRequestStatus.Pending, SubmittedByUserId = actor.UserId, SubmittedByName = actor.Name, SubmittedAt = DateTime.UtcNow,
        };
        OpenRequest(db, request, chain);
        if (chain.Count == 0)
        {
            request.ClosedAt = request.SubmittedAt;
            await RecordCoreAsync(db, companyId, projectId, ProjectEventKind.Approved, DateTime.UtcNow, "No approval rules apply to this budget.", actor, throughMatrix: true);
        }
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>
    /// Asks for extra work and money on an approved project: a variation. The same matrix decides who approves, by the variation's amount. When approved it adds a specification
    /// item (so the yard and the vessel see the job) and a budget line (so the budget and what is left of it include it).
    /// </summary>
    public async Task<ProjectApprovalRequest> RequestVariationAsync(Guid companyId, Guid projectId, string group, string description, decimal amountUsd, string reason, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("Describe the extra work.");
        if (amountUsd <= 0) throw new InvalidOperationException("Enter the extra amount, above zero.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
        var (project, status) = await LoadEditableAsync(db, companyId, projectId);
        if (status is not (ProjectStatus.Approved or ProjectStatus.Ongoing)) throw new InvalidOperationException("A variation changes an approved project that has not yet finished; before that, change the budget.");

        var chain = await ChainAsync(db, companyId, amountUsd);
        var request = new ProjectApprovalRequest
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Kind = ProjectRequestKind.Variation, AmountUsd = amountUsd, Group = string.IsNullOrWhiteSpace(group) ? "Variations" : group.Trim(),
            Description = description.Trim(), Reason = (reason ?? "").Trim(), Status = chain.Count == 0 ? ProjectRequestStatus.Approved : ProjectRequestStatus.Pending,
            SubmittedByUserId = actor.UserId, SubmittedByName = actor.Name, SubmittedAt = DateTime.UtcNow,
        };
        OpenRequest(db, request, chain);
        if (chain.Count == 0) await ApplyVariationAsync(db, project, request);
        await db.SaveChangesAsync();
        return request;
    }

    private static async Task ApplyVariationAsync(ChandleryDbContext db, Project project, ProjectApprovalRequest request)
    {
        if (project.Items.Count >= MaxItems) throw new InvalidOperationException($"A project can hold at most {MaxItems} specification items.");
        var item = new ProjectItem
        {
            Id = Guid.CreateVersion7(), Group = request.Group, Description = request.Description, Source = ProjectItemSource.Variation, SourceId = request.Id, SourceRef = "Variation",
            Status = ProjectItemStatus.Open, SortOrder = project.Items.Count == 0 ? 0 : project.Items.Max(i => i.SortOrder) + 1,
        };
        project.Items.Add(item);
        db.Entry(item).State = EntityState.Added;
        var order = await db.ProjectBudgetLines.Where(l => l.ProjectId == project.Id).Select(l => (int?)l.SortOrder).MaxAsync() ?? -1;
        var label = "Variation: " + request.Description;
        db.ProjectBudgetLines.Add(new ProjectBudgetLine { Id = Guid.CreateVersion7(), CompanyId = project.CompanyId, ProjectId = project.Id, Category = label.Length > 100 ? label[..100] : label, PlannedUsd = request.AmountUsd, SortOrder = order + 1 });
        request.ItemId = item.Id;
        request.ClosedAt = DateTime.UtcNow;
    }

    /// <summary>The person who asked takes the request back (before it is decided), for instance to change the budget and ask again.</summary>
    public async Task WithdrawRequestAsync(Guid companyId, Guid requestId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
        var request = await db.ProjectApprovalRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId) ?? throw new InvalidOperationException("Request not found.");
        if (request.Status != ProjectRequestStatus.Pending) throw new InvalidOperationException("This request has already been decided.");
        request.Status = ProjectRequestStatus.Withdrawn;
        request.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>An approver answers a request that is at their level. The last approval approves the project (or adds the variation); a rejection needs a reason and ends the request.</summary>
    public async Task DecideApprovalAsync(Guid companyId, Guid requestId, Actor actor, bool approve, string comment)
    {
        if (actor.UserId is not { } userId) throw new InvalidOperationException("You must be signed in.");
        comment = (comment ?? "").Trim();
        if (!approve && comment == "") throw new InvalidOperationException("Give a reason when rejecting.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var request = await db.ProjectApprovalRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId) ?? throw new InvalidOperationException("Request not found.");
        if (request.Status != ProjectRequestStatus.Pending) throw new InvalidOperationException("This request is no longer waiting for approval.");
        var slots = await db.ProjectApprovalSlots.Where(s => s.RequestId == requestId).ToListAsync();
        var current = ApprovalEngine.CurrentLevel(slots.Select(s => (s.Level, s.Decision))) ?? throw new InvalidOperationException("Nothing is waiting for approval.");
        var mine = slots.FirstOrDefault(s => s.Level == current && s.ApproverUserId == userId && s.Decision == ApprovalDecision.Pending)
            ?? throw new InvalidOperationException("It is not your turn to approve this request.");
        mine.Decision = approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        (mine.DecidedByUserId, mine.DecidedAt, mine.Comment) = (userId, DateTime.UtcNow, comment);

        if (!approve)
        {
            request.Status = ProjectRequestStatus.Rejected;
            request.ClosedAt = mine.DecidedAt;
        }
        else if (ApprovalEngine.CurrentLevel(slots.Select(s => (s.Level, s.Decision))) is null)
        {
            var (project, _) = await LoadEditableAsync(db, companyId, request.ProjectId);
            request.Status = ProjectRequestStatus.Approved;
            request.ClosedAt = mine.DecidedAt;
            if (request.Kind == ProjectRequestKind.Budget)
                await RecordCoreAsync(db, companyId, request.ProjectId, ProjectEventKind.Approved, DateTime.UtcNow, "Approved through the approval matrix.", actor, throughMatrix: true);
            else
                await ApplyVariationAsync(db, project, request);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Whether the approval matrix would have to approve the budget as it stands: the page then offers "Submit for approval" instead of "Approve".</summary>
    public async Task<bool> NeedsMatrixAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return false;
        var lines = await db.ProjectBudgetLines.AsNoTracking().Where(l => l.ProjectId == projectId).ToListAsync();
        return (await ChainAsync(db, companyId, ProjectCostRules.Budget(lines) + ProjectCostRules.Contingency(lines))).Count > 0;
    }

    public record ApprovalRequestView(ProjectApprovalRequest Request, List<ApprovalSlotView> Slots, bool YourTurn);

    /// <summary>The requests made on a project, newest first, each with who was asked and what they said.</summary>
    public async Task<List<ApprovalRequestView>> RequestsOfAsync(Guid companyId, Guid projectId, Guid? forUserId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return [];
        var requests = await db.ProjectApprovalRequests.AsNoTracking().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.SubmittedAt).ThenByDescending(r => r.Id).ToListAsync();
        var ids = requests.Select(r => r.Id).ToList();
        var slots = (await db.ProjectApprovalSlots.AsNoTracking().Where(s => ids.Contains(s.RequestId)).OrderBy(s => s.Level).ThenBy(s => s.Id).ToListAsync()).ToLookup(s => s.RequestId);
        var userIds = slots.SelectMany(g => g).SelectMany(s => new[] { s.ApproverUserId, s.DecidedByUserId ?? s.ApproverUserId }).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var levelNames = await ApprovalLevelNames.LoadAsync(db, companyId);
        return requests.Select(r => new ApprovalRequestView(r, slots[r.Id].Select(s => new ApprovalSlotView(s.Level, levelNames.Name(s.Level), names.GetValueOrDefault(s.ApproverUserId) ?? "?", s.Decision,
            s.DecidedByUserId is { } d ? names.GetValueOrDefault(d) ?? "?" : "", s.DecidedAt, s.Comment)).ToList(),
            r.Status == ProjectRequestStatus.Pending && forUserId is { } me && ApprovalEngine.CurrentLevel(slots[r.Id].Select(s => (s.Level, s.Decision))) is { } level
                && slots[r.Id].Any(s => s.Level == level && s.ApproverUserId == me && s.Decision == ApprovalDecision.Pending))).ToList();
    }

    public record ProjectPendingRow(ProjectApprovalRequest Request, string ProjectCode, string ProjectTitle, int Level, string LevelName);

    /// <summary>The project requests waiting for this person to answer: those whose current level names them.</summary>
    public async Task<List<ProjectPendingRow>> PendingForUserAsync(Guid companyId, Guid userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return [];
        var mineIds = await db.ProjectApprovalSlots.AsNoTracking().Where(s => s.ApproverUserId == userId && s.Decision == ApprovalDecision.Pending).Select(s => s.RequestId).Distinct().ToListAsync();
        if (mineIds.Count == 0) return [];
        var requests = await db.ProjectApprovalRequests.AsNoTracking().Where(r => mineIds.Contains(r.Id) && r.Status == ProjectRequestStatus.Pending).OrderBy(r => r.SubmittedAt).ToListAsync();
        var slots = (await db.ProjectApprovalSlots.AsNoTracking().Where(s => mineIds.Contains(s.RequestId)).ToListAsync()).ToLookup(s => s.RequestId);
        var projects = await db.Projects.AsNoTracking().Where(p => requests.Select(r => r.ProjectId).Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var levelNames = await ApprovalLevelNames.LoadAsync(db, companyId);
        var rows = new List<ProjectPendingRow>();
        foreach (var r in requests)
        {
            var level = ApprovalEngine.CurrentLevel(slots[r.Id].Select(s => (s.Level, s.Decision)));
            if (level is null || !slots[r.Id].Any(s => s.Level == level && s.ApproverUserId == userId && s.Decision == ApprovalDecision.Pending)) continue;
            var p = projects.GetValueOrDefault(r.ProjectId);
            rows.Add(new ProjectPendingRow(r, p?.Code ?? "", p?.Title ?? "", level.Value, levelNames.Name(level.Value)));
        }
        return rows;
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
    /// must be for the same vessel. An order or invoice with no link of its own already belongs to the project of what it came from, but saying so again writes a link of its own, which then holds even
    /// if the requisition is moved later.
    /// </summary>
    public async Task<ProjectLink> LinkAsync(Guid companyId, Guid projectId, ProjectLinkKind kind, Guid docId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        if (!await IsEnabledForVesselAsync(db, companyId, project.VesselId)) throw new InvalidOperationException("Projects are not switched on for this vessel.");
        if (!await BelongsToVesselAsync(db, companyId, project.VesselId, kind, docId)) throw new InvalidOperationException($"That {KindLabel(kind)} was not found for this vessel.");

        var own = await db.ProjectLinks.AsNoTracking().Where(l => l.Kind == kind && l.DocId == docId).ToListAsync();
        var latest = own.OrderBy(l => l.At).ThenBy(l => l.Id).LastOrDefault();
        if (latest is { Added: true } && latest.ProjectId == projectId) return latest;               // already so: nothing to add
        return await WriteLinkAsync(db, companyId, project, kind, docId, added: true, actor);
    }

    /// <summary>Takes a document off the project it belongs to (its own link, or the one it takes from its requisition or order).</summary>
    public async Task<ProjectLink> UnlinkAsync(Guid companyId, Guid projectId, ProjectLinkKind kind, Guid docId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        var (current, _) = await ResolveAsync(db, companyId, kind, docId);
        if (current != projectId) throw new InvalidOperationException($"That {KindLabel(kind)} is not linked to this project.");
        return await WriteLinkAsync(db, companyId, project, kind, docId, added: false, actor);
    }

    /// <summary>Puts a document on a project, moves it, or (with null) takes it off whichever project it is on. Does nothing when it is already so.</summary>
    public async Task SetProjectAsync(Guid companyId, ProjectLinkKind kind, Guid docId, Guid? projectId, Actor actor)
    {
        var current = await ProjectOfDocAsync(companyId, kind, docId);
        if (projectId is { } target)
        {
            if (current?.ProjectId == target) return;
            await LinkAsync(companyId, target, kind, docId, actor);
        }
        else if (current is not null)
            await UnlinkAsync(companyId, current.ProjectId, kind, docId, actor);
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

    /// <summary>The project a document is on, by its own latest link or else by what it came from; and whether it is only by what it came from.</summary>
    private static async Task<(Guid? Project, bool Inherited)> ResolveAsync(ChandleryDbContext db, Guid companyId, ProjectLinkKind kind, Guid docId)
    {
        var own = await db.ProjectLinks.AsNoTracking().Where(l => l.Kind == kind && l.DocId == docId).ToListAsync();
        Guid? parent = null;
        if (own.Count == 0)
        {
            if (kind == ProjectLinkKind.PurchaseOrder)
            {
                var requisitionId = await db.PurchaseOrders.AsNoTracking().Where(p => p.Id == docId && p.CompanyId == companyId).Select(p => p.RequisitionId).FirstOrDefaultAsync();
                if (requisitionId is { } r) parent = (await ResolveAsync(db, companyId, ProjectLinkKind.Requisition, r)).Project;
            }
            else if (kind == ProjectLinkKind.Invoice)
            {
                var orderId = await db.Invoices.AsNoTracking().Where(i => i.Id == docId && i.CompanyId == companyId).Select(i => i.PurchaseOrderId).FirstOrDefaultAsync();
                if (orderId is { } o) parent = (await ResolveAsync(db, companyId, ProjectLinkKind.PurchaseOrder, o)).Project;
            }
        }
        return ProjectInheritance.Resolve(own, parent);
    }

    /// <summary>The project a document belongs to, or null.</summary>
    public async Task<Guid?> ProjectOfAsync(Guid companyId, ProjectLinkKind kind, Guid docId) => (await ProjectOfDocAsync(companyId, kind, docId))?.ProjectId;

    /// <summary>The project a document belongs to, with what to show about it; null when it is on none.</summary>
    public async Task<DocProject?> ProjectOfDocAsync(Guid companyId, ProjectLinkKind kind, Guid docId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (id, inherited) = await ResolveAsync(db, companyId, kind, docId);
        if (id is not { } projectId) return null;
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId);
        return project is null ? null : new DocProject(project.Id, project.Code, project.Title, ProjectStatusRules.Of(await db.ProjectStatusEvents.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync()), inherited);
    }

    /// <summary>The projects of a vessel that can still take documents (not closed). Empty when the module is off for it.</summary>
    public async Task<List<ProjectOption>> OpenProjectsAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) return [];
        var projects = await db.Projects.AsNoTracking().Where(p => p.CompanyId == companyId && p.VesselId == vesselId).OrderByDescending(p => p.CreatedAt).ToListAsync();
        var ids = projects.Select(p => p.Id).ToList();
        var events = (await db.ProjectStatusEvents.AsNoTracking().Where(e => ids.Contains(e.ProjectId)).ToListAsync()).ToLookup(e => e.ProjectId);
        return projects.Select(p => new ProjectOption(p.Id, p.Code, p.Title, ProjectStatusRules.Of(events[p.Id]))).Where(o => ProjectStatusRules.IsEditable(o.Status)).ToList();
    }

    /// <summary>What belongs to a project now: its requisitions, and the orders and invoices that are on it by their own link or by what they came from.</summary>
    private static async Task<ProjectDocs> DocsAsync(ChandleryDbContext db, Guid companyId, Guid projectId)
    {
        // Documents that have ever been linked to this project are the starting point; whether each still belongs is decided from every link written about it.
        var named = await db.ProjectLinks.AsNoTracking().Where(l => l.ProjectId == projectId).Select(l => new { l.Kind, l.DocId }).Distinct().ToListAsync();
        async Task<Dictionary<Guid, List<ProjectLink>>> EventsAsync(ProjectLinkKind kind, IReadOnlyCollection<Guid> ids)
            => (await db.ProjectLinks.AsNoTracking().Where(l => l.Kind == kind && ids.Contains(l.DocId)).ToListAsync()).GroupBy(l => l.DocId).ToDictionary(g => g.Key, g => g.ToList());

        var requisitionIds = named.Where(n => n.Kind == ProjectLinkKind.Requisition).Select(n => n.DocId).ToList();
        var requisitionEvents = await EventsAsync(ProjectLinkKind.Requisition, requisitionIds);
        var requisitions = requisitionIds.Where(id => ProjectInheritance.Resolve(requisitionEvents.GetValueOrDefault(id) ?? [], null).Project == projectId).ToHashSet();

        var directOrders = named.Where(n => n.Kind == ProjectLinkKind.PurchaseOrder).Select(n => n.DocId).ToList();
        var candidateOrders = await db.PurchaseOrders.AsNoTracking().Where(p => p.CompanyId == companyId && (directOrders.Contains(p.Id) || (p.RequisitionId != null && requisitions.Contains(p.RequisitionId.Value)))).ToListAsync();
        var orderEvents = await EventsAsync(ProjectLinkKind.PurchaseOrder, candidateOrders.Select(p => p.Id).ToList());
        var orders = candidateOrders.Where(p => ProjectInheritance.Resolve(orderEvents.GetValueOrDefault(p.Id) ?? [], p.RequisitionId is { } r && requisitions.Contains(r) ? projectId : null).Project == projectId).ToList();

        var orderIds = orders.Select(p => p.Id).ToHashSet();
        var directInvoices = named.Where(n => n.Kind == ProjectLinkKind.Invoice).Select(n => n.DocId).ToList();
        var candidateInvoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.CompanyId == companyId && (directInvoices.Contains(i.Id) || (i.PurchaseOrderId != null && orderIds.Contains(i.PurchaseOrderId.Value)))).ToListAsync();
        var invoiceEvents = await EventsAsync(ProjectLinkKind.Invoice, candidateInvoices.Select(i => i.Id).ToList());
        var invoices = candidateInvoices.Where(i => ProjectInheritance.Resolve(invoiceEvents.GetValueOrDefault(i.Id) ?? [], i.PurchaseOrderId is { } o && orderIds.Contains(o) ? projectId : null).Project == projectId).ToList();
        return new ProjectDocs(requisitions, orders, invoices);
    }

    /// <summary>The documents that belong to a project now.</summary>
    public async Task<List<(ProjectLinkKind Kind, Guid DocId)>> LinksOfAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var docs = await DocsAsync(db, companyId, projectId);
        return docs.Requisitions.Select(id => (ProjectLinkKind.Requisition, id))
            .Concat(docs.Orders.Select(p => (ProjectLinkKind.PurchaseOrder, p.Id))).Concat(docs.Invoices.Select(i => (ProjectLinkKind.Invoice, i.Id)))
            .OrderBy(k => k.Item1).ThenBy(k => k.Item2).ToList();
    }

    /// <summary>
    /// What the project has cost so far, in US dollars: approved orders not yet invoiced (committed), finalized invoices (invoiced), and entries made by hand (other), with the document behind
    /// every figure. An order or invoice in another currency is converted at the rate it carries, else the company's rate for its date; when there is none it is counted at its own amount and
    /// reported. Office only: a vessel is never shown cost.
    /// </summary>
    public async Task<ProjectCostSummary> CostAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.CompanyId == companyId)) throw new InvalidOperationException("Project not found.");
        var docs = await DocsAsync(db, companyId, projectId);
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => docs.Orders.Select(o => o.SupplierId).Concat(docs.Invoices.Select(i => i.SupplierId)).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var unconverted = 0;
        var rates = new Dictionary<(string, DateTime), decimal?>();

        async Task<decimal> UsdAsync(decimal amount, string currency, decimal? stored, DateTime date)
        {
            if (currency == "USD") return amount;
            var rate = stored;
            if (rate is null)
            {
                var key = (currency, date.Date);
                if (!rates.TryGetValue(key, out rate)) rates[key] = rate = (await Fx.LookupAsync(db, companyId, currency, date))?.UnitsPerUsd;
            }
            if (rate is null) { unconverted++; return amount; }
            return Fx.ToUsd(amount, rate.Value);
        }

        var lines = new List<ProjectCostLine>();
        var costOrders = new List<CostPo>();
        var costInvoices = new List<CostInvoice>();
        var invoicedOrders = docs.Invoices.Where(i => i.Status == InvoiceStatus.Finalized && i.PurchaseOrderId is not null).Select(i => i.PurchaseOrderId!.Value).ToHashSet();

        foreach (var po in docs.Orders.OrderBy(p => p.CreatedDate))
        {
            var usd = await UsdAsync(po.Total, po.Currency, po.UsdRate, po.CreatedDate);
            var counted = po.Status == PoStatus.Approved && !po.IsDummy;
            if (counted) costOrders.Add(new CostPo(po.Id, usd));
            var bucket = !counted ? ProjectCostBucket.NotCounted : invoicedOrders.Contains(po.Id) ? ProjectCostBucket.Settled : ProjectCostBucket.Committed;
            lines.Add(new ProjectCostLine(ProjectLinkKind.PurchaseOrder, po.Id, po.PoNumber, suppliers.GetValueOrDefault(po.SupplierId) ?? "", po.IsDummy ? "Record-only" : po.Status.ToString(), po.Currency, po.Total, usd, bucket));
        }
        foreach (var inv in docs.Invoices.OrderBy(i => i.InvoiceDate))
        {
            var counted = inv.Status == InvoiceStatus.Finalized;
            var usd = await UsdAsync(inv.TotalExclTax, inv.Currency, inv.UsdRate, inv.InvoiceDate);
            if (counted) costInvoices.Add(new CostInvoice(inv.PurchaseOrderId, usd));
            lines.Add(new ProjectCostLine(ProjectLinkKind.Invoice, inv.Id, inv.InvoiceNumber, suppliers.GetValueOrDefault(inv.SupplierId) ?? "", inv.Status.ToString(), inv.Currency, inv.TotalExclTax, usd,
                counted ? ProjectCostBucket.Invoiced : ProjectCostBucket.NotCounted));
        }

        var entries = await db.ProjectCostEntries.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync();
        var cost = ProjectCostRules.Of(costOrders, costInvoices, entries.Select(e => e.AmountUsd));
        return new ProjectCostSummary(cost, lines, entries.OrderBy(e => e.Date).ThenBy(e => e.At).ToList(), unconverted);
    }

    // ---- adding from the Worklist and maintenance ----

    /// <summary>The Worklist natures a company can name as "deferred to drydock".</summary>
    public async Task<List<(Guid Id, string Name)>> NatureOptionsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await NaturesAsync(db, companyId)).Select(n => (n.Id, n.Name)).ToList();
    }

    private static async Task<List<WorklistListItem>> NaturesAsync(ChandleryDbContext db, Guid companyId)
        => await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Nature && i.IsActive).OrderBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync();

    /// <summary>The nature in use as "deferred to drydock": the one chosen, else one whose name says drydock; null when there is none.</summary>
    public async Task<(Guid Id, string Name)?> DrydockNatureAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var chosen = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.ProjectDrydockNatureId).FirstOrDefaultAsync();
        var natures = await NaturesAsync(db, companyId);
        var id = ProjectImportRules.ResolveNature(chosen, natures.Select(n => (n.Id, n.Name)));
        return id is { } found ? (found, natures.First(n => n.Id == found).Name) : null;
    }

    /// <summary>A company admin names the nature that means "deferred to drydock" (null goes back to looking for one whose name says so).</summary>
    public async Task SetDrydockNatureAsync(Guid companyId, Guid? natureId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        if (actor.UserId is not { } uid || !await db.Users.AsNoTracking().AnyAsync(u => u.Id == uid && u.CompanyId == companyId && u.IsEnabled && u.Role == UserRole.CompanyAdmin))
            throw new InvalidOperationException("Only a company admin can choose this.");
        if (natureId is { } nature && !await db.WorklistListItems.AnyAsync(i => i.Id == nature && i.CompanyId == companyId && i.Kind == WorklistListKind.Nature && i.IsActive))
            throw new InvalidOperationException("That nature is not in the list.");
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId) ?? throw new InvalidOperationException("Company not found.");
        company.ProjectDrydockNatureId = natureId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// What can be added to this project's specification: the vessel's open Worklist tasks of the drydock nature, and its maintenance jobs due within the window (or overdue). Anything already
    /// on the specification is left out.
    /// </summary>
    public async Task<ImportCandidates> ImportCandidatesAsync(Guid companyId, Guid projectId, int dueWithinDays = 90)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var project = await db.Projects.AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId) ?? throw new InvalidOperationException("Project not found.");
        var onSpec = project.Items.Where(i => i.SourceId != null).Select(i => (i.Source, i.SourceId!.Value)).ToHashSet();
        var today = DateTime.UtcNow.Date;

        var worklistOn = await WorklistService.IsEnabledForVesselAsync(db, companyId, project.VesselId);
        var nature = await DrydockNatureAsync(companyId);
        var tasks = new List<ImportTask>();
        if (worklistOn && nature is { } n)
        {
            var found = await db.WorklistTasks.AsNoTracking().Where(t => t.CompanyId == companyId && t.VesselId == project.VesselId && t.NatureId == n.Id).ToListAsync();
            tasks = found.Where(t => !t.IsClosed && !onSpec.Contains((ProjectItemSource.WorklistTask, t.Id)))
                .OrderBy(t => t.TargetDate == null).ThenBy(t => t.TargetDate).ThenBy(t => t.Code)
                .Select(t => new ImportTask(t.Id, t.Code, t.Title, t.Priority == WorklistPriority.Urgent, t.TargetDate)).ToList();
        }

        var pmsOn = await ModuleGate.IsEnabledAsync(db, companyId, project.VesselId, PolicyModules.Pms);
        var jobs = new List<ImportJob>();
        if (pmsOn)
        {
            var machinery = await db.MachineryItems.AsNoTracking().Where(m => m.VesselId == project.VesselId).ToDictionaryAsync(m => m.Id, m => m.Name);
            var found = await db.PlannedJobs.AsNoTracking().Where(j => j.CompanyId == companyId && j.VesselId == project.VesselId && j.IsActive && j.NextDueDate != null).ToListAsync();
            jobs = found.Where(j => ProjectImportRules.JobIsDue(j.NextDueDate, today, dueWithinDays) && !onSpec.Contains((ProjectItemSource.PmsJob, j.Id)))
                .OrderBy(j => j.NextDueDate).ThenBy(j => j.Code)
                .Select(j => new ImportJob(j.Id, j.Code, j.Title, machinery.GetValueOrDefault(j.MachineryItemId) ?? "", j.NextDueDate, j.IsCritical)).ToList();
        }
        return new ImportCandidates(nature?.Name, worklistOn, pmsOn, tasks, jobs);
    }

    /// <summary>
    /// Adds the chosen tasks and jobs to the specification in one step. Each item remembers where it came from, so adding the same one twice adds nothing; a task also gets a note on its
    /// own timeline saying it is on the project. Only tasks and jobs of this project's vessel can be added.
    /// </summary>
    public async Task<ProjectImportOutcome> ImportAsync(Guid companyId, Guid projectId, IReadOnlyCollection<Guid> taskIds, IReadOnlyCollection<Guid> jobIds, Actor actor)
    {
        if (taskIds.Count + jobIds.Count == 0) throw new InvalidOperationException("Choose at least one task or job to add.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        var onSpec = project.Items.Where(i => i.SourceId != null).Select(i => (i.Source, i.SourceId!.Value)).ToHashSet();
        var notes = new List<string>();
        var linked = new List<WorklistTask>();
        var order = project.Items.Count == 0 ? 0 : project.Items.Max(i => i.SortOrder) + 1;
        var added = 0;

        void Add(ProjectItemSource source, Guid sourceId, string group, string description, string reference)
        {
            if (project.Items.Count >= MaxItems) throw new InvalidOperationException($"A project can hold at most {MaxItems} specification items.");
            var item = new ProjectItem { Id = Guid.CreateVersion7(), Group = group, Description = description, Source = source, SourceId = sourceId, SourceRef = reference, Status = ProjectItemStatus.Open, SortOrder = order++ };
            project.Items.Add(item);
            db.Entry(item).State = EntityState.Added;
            onSpec.Add((source, sourceId));
            added++;
        }

        var tasks = await db.WorklistTasks.AsNoTracking().Where(t => t.CompanyId == companyId && taskIds.Contains(t.Id)).ToListAsync();
        notes.AddRange(taskIds.Where(id => tasks.All(t => t.Id != id)).Select(_ => "A task could not be found."));
        foreach (var task in tasks)
        {
            if (task.VesselId != project.VesselId) throw new InvalidOperationException($"Task {task.Code} is for another vessel.");
            if (onSpec.Contains((ProjectItemSource.WorklistTask, task.Id))) { notes.Add($"{task.Code} is already on the specification."); continue; }
            Add(ProjectItemSource.WorklistTask, task.Id, "Deferred jobs", task.Title, task.Code);
            linked.Add(task);
        }

        var jobs = await db.PlannedJobs.AsNoTracking().Where(j => j.CompanyId == companyId && jobIds.Contains(j.Id)).ToListAsync();
        notes.AddRange(jobIds.Where(id => jobs.All(j => j.Id != id)).Select(_ => "A maintenance job could not be found."));
        var machinery = await db.MachineryItems.AsNoTracking().Where(m => m.VesselId == project.VesselId).ToDictionaryAsync(m => m.Id, m => m.Name);
        foreach (var job in jobs)
        {
            if (job.VesselId != project.VesselId) throw new InvalidOperationException($"Job {job.Code} is for another vessel.");
            if (onSpec.Contains((ProjectItemSource.PmsJob, job.Id))) { notes.Add($"{job.Code} is already on the specification."); continue; }
            Add(ProjectItemSource.PmsJob, job.Id, machinery.GetValueOrDefault(job.MachineryItemId) ?? "Machinery", job.Title, job.Code);
        }
        await db.SaveChangesAsync();

        if (worklist is not null)
            foreach (var task in linked)
            {
                try { await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Project, LinkTarget: project.Id, LinkLabel: $"Project {project.Code}"), actor); }
                catch (InvalidOperationException ex) { notes.Add($"{task.Code} is on the specification, but its task could not be told: {ex.Message}"); }
            }
        return new ProjectImportOutcome(added, notes);
    }

    /// <summary>
    /// The specification items for maintenance jobs that are Done here but whose completion has not been recorded in maintenance since the work began. Recording it stays a maintenance
    /// action, so the job rules apply; this only says which are waiting for it.
    /// </summary>
    public async Task<HashSet<Guid>> PmsWaitingAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var project = await db.Projects.AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId);
        if (project is null) return [];
        var jobItems = project.Items.Where(i => i.Source == ProjectItemSource.PmsJob && i.SourceId != null && i.Status == ProjectItemStatus.Done).ToList();
        if (jobItems.Count == 0) return [];
        var events = await db.ProjectStatusEvents.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync();
        var since = (ProjectStatusRules.ActualStart(events) ?? project.PlannedStart ?? project.CreatedAt).Date;
        var ids = jobItems.Select(i => i.SourceId!.Value).ToList();
        var recorded = (await db.JobCompletions.AsNoTracking().Where(c => ids.Contains(c.PlannedJobId) && c.DoneDate >= since).Select(c => c.PlannedJobId).Distinct().ToListAsync()).ToHashSet();
        return jobItems.Where(i => !recorded.Contains(i.SourceId!.Value)).Select(i => i.Id).ToHashSet();
    }

    // ---- requests from the vessel ----

    /// <summary>The vessel asks for another job on the project. Written once; the office adds it to the specification or declines it, with a reason.</summary>
    public async Task<ProjectItemRequest> AddItemRequestAsync(Guid companyId, Guid projectId, string description, string reason, Actor actor)
    {
        var text = (description ?? "").Trim();
        if (text == "") throw new InvalidOperationException("Describe the job you are asking for.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (project, _) = await LoadEditableAsync(db, companyId, projectId);
        if (!await IsEnabledForVesselAsync(db, companyId, project.VesselId)) throw new InvalidOperationException("Projects are not switched on for this vessel.");
        if (project.Items.Count >= MaxItems) throw new InvalidOperationException($"A project can hold at most {MaxItems} specification items.");

        var request = new ProjectItemRequest
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = project.VesselId, ProjectId = projectId, Description = text, Reason = (reason ?? "").Trim(),
            RequestedByUserId = actor.UserId, RequestedByName = actor.Name, At = DateTime.UtcNow,
        };
        db.ProjectItemRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>The requests made on a project, oldest first, each with the answer if there is one.</summary>
    public async Task<List<ProjectRequestView>> RequestsAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var requests = await db.ProjectItemRequests.AsNoTracking().Where(r => r.ProjectId == projectId).OrderBy(r => r.At).ThenBy(r => r.Id).ToListAsync();
        var ids = requests.Select(r => r.Id).ToList();
        var decisions = (await db.ProjectItemDecisions.AsNoTracking().Where(d => ids.Contains(d.RequestId)).ToListAsync()).ToDictionary(d => d.RequestId);
        return requests.Select(r => new ProjectRequestView(r, decisions.GetValueOrDefault(r.Id))).ToList();
    }

    /// <summary>
    /// The office answers a request. Accepting adds the job to the specification (under the group given) and records which item it became; declining needs a reason. A request is
    /// answered once.
    /// </summary>
    public async Task<ProjectItemDecision> DecideRequestAsync(Guid companyId, Guid requestId, bool accept, string group, string note, Actor actor)
    {
        if (!accept && string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("Say why the request is declined.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireManagerAsync(db, companyId, actor);
        var request = await db.ProjectItemRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId) ?? throw new InvalidOperationException("Request not found.");
        if (await db.ProjectItemDecisions.AnyAsync(d => d.RequestId == requestId)) throw new InvalidOperationException("This request has already been answered.");
        var (project, _) = await LoadEditableAsync(db, companyId, request.ProjectId);

        Guid? itemId = null;
        if (accept)
        {
            if (project.Items.Count >= MaxItems) throw new InvalidOperationException($"A project can hold at most {MaxItems} specification items.");
            var item = new ProjectItem
            {
                Id = Guid.CreateVersion7(), Group = string.IsNullOrWhiteSpace(group) ? "Vessel requests" : group.Trim(), Description = request.Description, Source = ProjectItemSource.VesselRequest,
                SourceId = request.Id, SourceRef = "Vessel request", Status = ProjectItemStatus.Open, SortOrder = project.Items.Count == 0 ? 0 : project.Items.Max(i => i.SortOrder) + 1,
            };
            project.Items.Add(item);
            db.Entry(item).State = EntityState.Added;
            itemId = item.Id;
        }
        var decision = new ProjectItemDecision
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = request.VesselId, ProjectId = request.ProjectId, RequestId = requestId, Accepted = accept, Note = (note ?? "").Trim(),
            ItemId = itemId, DecidedByUserId = actor.UserId, DecidedByName = actor.Name, At = DateTime.UtcNow,
        };
        db.ProjectItemDecisions.Add(decision);
        await db.SaveChangesAsync();
        return decision;
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

    /// <summary>The projects with their budget, cost to date and days, for the list and the dashboard. Onboard the money is left empty: a vessel is never shown budget or cost.</summary>
    public async Task<List<ProjectListRow>> ListWithMoneyAsync(Guid companyId, Guid? vesselId = null)
    {
        var rows = await ListAsync(companyId, vesselId);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var onVessel = db.Node.IsVessel;
        var vessels = await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        var lines = (await db.ProjectBudgetLines.AsNoTracking().Where(l => l.CompanyId == companyId).ToListAsync()).ToLookup(l => l.ProjectId);
        var result = new List<ProjectListRow>();
        foreach (var row in rows)
        {
            var budget = onVessel ? [] : lines[row.Project.Id].ToList();
            var cost = onVessel ? new ProjectCost(0, 0, 0) : (await CostAsync(companyId, row.Project.Id)).Cost;
            result.Add(new ProjectListRow(row, vessels.GetValueOrDefault(row.Project.VesselId) ?? "", ProjectCostRules.Budget(budget), ProjectCostRules.Contingency(budget), cost,
                ProjectCostRules.PlannedDays(row.Project.PlannedStart, row.Project.PlannedEnd), ProjectCostRules.ActualDays(row.ActualStart, row.ActualEnd, DateTime.UtcNow.Date)));
        }
        return result;
    }

    /// <summary>What the dashboard shows; null when the module is off for every vessel.</summary>
    public async Task<ProjectCounts?> CountsAsync(Guid companyId)
    {
        if (!await IsEnabledForCompanyAsync(companyId)) return null;
        var live = (await ListWithMoneyAsync(companyId)).Where(r => r.Row.Status != ProjectStatus.Closed).ToList();
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var liveIds = live.Select(r => r.Row.Project.Id).ToList();
        var waiting = await db.ProjectItemRequests.AsNoTracking().CountAsync(r => liveIds.Contains(r.ProjectId) && !db.ProjectItemDecisions.Any(d => d.RequestId == r.Id));
        return new ProjectCounts(live.Count(r => r.Row.Status == ProjectStatus.Ongoing), live.Count(r => r.Row.Status is ProjectStatus.Planning or ProjectStatus.Approved), live.Count(r => r.OverBudget), waiting);
    }
}
