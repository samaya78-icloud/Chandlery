using System.Text.Json.Nodes;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>What the office's list is narrowed by. Everything left out matches everything.</summary>
public record FormFilter(Guid? VesselId = null, Guid? TypeId = null, string? Department = null, int? Year = null, int? Month = null,
    FormStatus? Status = null, string? Keyword = null, bool AwaitingReview = false);

public record FormReportRow(FormEntry Entry, FormStatus Status, string Vessel);

/// <summary>A deficiency or a finding found on a form, as a Worklist task on the vessel.</summary>
public record NewFormAction(string Title, string Description = "", WorklistPriority Priority = WorklistPriority.Normal, DateTime? Target = null, string? Assignee = null);

public record FormYearRow(FormType Type, FormYearCell[] Months);

/// <summary>
/// The office's side of forms beyond a single review: finding entries, turning what a form shows into Worklist tasks, the files that leave the app (CSV, Excel) and the
/// year view. Reading only, apart from the action items, which are Worklist tasks linked to the entry.
/// </summary>
public class FormReports(IDbContextFactory<ChandleryDbContext> factory, FormService forms, WorklistService worklist)
{
    public const int MaxRows = 300;

    /// <summary>Entries by month, year, form, department, vessel, state and a word in the reference, title or answers. Newest first, at most <see cref="MaxRows"/>.</summary>
    public async Task<List<FormReportRow>> SearchAsync(Guid companyId, FormFilter filter)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.FormEntries.AsNoTracking().Where(e => e.CompanyId == companyId);
        if (filter.VesselId is { } vessel) query = query.Where(e => e.VesselId == vessel);
        if (filter.TypeId is { } type) query = query.Where(e => e.FormTypeId == type);
        if (!string.IsNullOrWhiteSpace(filter.Department))
        {
            var department = filter.Department.Trim();
            var typeIds = await db.FormTypes.AsNoTracking().Where(t => t.CompanyId == companyId && t.Department == department).Select(t => t.Id).ToListAsync();
            query = query.Where(e => typeIds.Contains(e.FormTypeId));
        }
        if (filter.Year is { } year)
        {
            if (filter.Month is { } month)
            {
                var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
                query = query.Where(e => e.FormDate >= from && e.FormDate < from.AddMonths(1));
            }
            else
            {
                var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                query = query.Where(e => e.FormDate >= from && e.FormDate < from.AddYears(1));
            }
        }
        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var pattern = "%" + filter.Keyword.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(e => EF.Functions.ILike(e.Code, pattern) || EF.Functions.ILike(e.FormTitle, pattern) || EF.Functions.ILike(e.FormCode, pattern) || EF.Functions.ILike(e.Values, pattern));
        }
        // a draft has not been shown to the office yet
        query = query.Where(e => e.SubmissionCount > 0 || filter.Status == FormStatus.Draft);

        var entries = await query.OrderByDescending(e => e.FormDate).ThenByDescending(e => e.CreatedAt).Take(filter.Status is null && !filter.AwaitingReview ? MaxRows : 2000).ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        var reviews = (await db.FormReviews.AsNoTracking().Where(r => ids.Contains(r.EntryId)).ToListAsync()).ToLookup(r => r.EntryId);
        var names = await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        var rows = entries.Select(e => new FormReportRow(e, FormStatusRules.Of(e.SubmissionCount, reviews[e.Id]), names.GetValueOrDefault(e.VesselId, ""))).ToList();
        if (filter.AwaitingReview) rows = rows.Where(r => r.Status is FormStatus.Submitted or FormStatus.InReview).ToList();
        if (filter.Status is { } status) rows = rows.Where(r => r.Status == status).ToList();
        return rows.Take(MaxRows).ToList();
    }

    /// <summary>The departments forms are filed under, for the filter.</summary>
    public async Task<List<string>> DepartmentsAsync(Guid companyId)
        => (await forms.TypesAsync(companyId)).Select(t => t.Department).Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();

    // ---- action items ----

    /// <summary>The Worklist tasks raised from an entry.</summary>
    public Task<List<WorklistTask>> ActionsAsync(Guid companyId, Guid entryId) => worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.Form, entryId);

    /// <summary>
    /// A deficiency or finding as a Worklist task on the entry's vessel, linked back to the entry. By the office. The worklist has to be switched on for the vessel; if
    /// not, the person is told so and nothing is made.
    /// </summary>
    public async Task<WorklistTask> AddActionAsync(Guid companyId, Guid entryId, NewFormAction action, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("Actions from a form are raised by the office.");
        var entry = await db.FormEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == entryId && e.CompanyId == companyId) ?? throw new InvalidOperationException("Entry not found.");
        if (entry.SubmissionCount == 0) throw new InvalidOperationException("The form has not been submitted yet.");
        if ((action.Title ?? "").Trim().Length < 3) throw new InvalidOperationException("Give the action a title.");
        var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == "Office").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        var body = $"{action.Description?.Trim()}{Environment.NewLine}{Environment.NewLine}From form {entry.Code} ({entry.FormCode} {entry.FormTitle}, {entry.FormDate:dd MMM yyyy}).".TrimStart();
        var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(entry.VesselId, action.Title.Trim(), body, source, Priority: action.Priority, Target: action.Target, Assignee: action.Assignee ?? ""), actor);
        await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Form, LinkTarget: entry.Id, LinkLabel: $"Form {entry.Code}"), actor);
        return task;
    }

    // ---- files ----

    /// <summary>
    /// The entries of one form as a table: one column per field and per grid cell, across the versions the entries were filled on (the newest version's order first).
    /// Drafts are left out; a vessel and a year may narrow it.
    /// </summary>
    public async Task<(FormType Type, List<FormExportColumn> Columns, List<FormExportRow> Rows)?> ExportAsync(Guid companyId, Guid typeId, Guid? vesselId = null, int? year = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var type = await db.FormTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId && t.CompanyId == companyId);
        if (type is null) return null;
        var found = await SearchAsync(companyId, new FormFilter(vesselId, typeId, Year: year));
        var rows = found.Where(r => r.Status != FormStatus.Draft).OrderBy(r => r.Entry.FormDate).ThenBy(r => r.Entry.Code).ToList();

        var versionIds = rows.Select(r => r.Entry.VersionId).Distinct().ToList();
        var versions = await db.FormVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).OrderByDescending(v => v.Number).ToListAsync();
        var values = rows.Select(r => JsonNode.Parse(r.Entry.Values) as JsonObject ?? []).ToList();
        var columns = new List<FormExportColumn>();
        foreach (var version in versions)
            if (FormDef.TryRead(version.Definition, out _) is { } def)
                foreach (var column in FormExport.Columns(def, values))
                    if (columns.All(c => c.Key != column.Key)) columns.Add(column);
        return (type, columns, rows.Select(r => FormExport.Row(columns, r.Entry, r.Vessel, r.Status)).ToList());
    }

    // ---- the year ----

    /// <summary>For one vessel and a year: each form that applies, and per month how many entries and the least settled state among them.</summary>
    public async Task<List<FormYearRow>> YearAsync(Guid companyId, Guid vesselId, int year)
    {
        var applicable = (await forms.ApplicableAsync(companyId, vesselId)).Select(a => a.Type).ToList();
        var entries = (await forms.ListAsync(companyId, vesselId)).Where(r => r.Entry.FormDate.Year == year).ToList();
        var types = applicable.Concat((await forms.TypesAsync(companyId)).Where(t => entries.Any(e => e.Entry.FormTypeId == t.Id))).DistinctBy(t => t.Id).OrderBy(t => t.Code).ToList();
        return types.Select(t => new FormYearRow(t, FormYearView.Months(entries.Where(e => e.Entry.FormTypeId == t.Id).Select(e => (e.Entry.FormDate, e.Status)), year))).ToList();
    }
}
