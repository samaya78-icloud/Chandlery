using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record YardInput(Guid? Id, string Name, string Location = "", string ContactName = "", string ContactEmail = "", string Notes = "", bool IsActive = true);

public record TenderView(ProjectTender Tender, string YardName, List<ProjectTenderLine> Lines);

/// <summary>What a yard quoted. Lines map a specification item to its price; an item left out (or null) is one the yard did not price.</summary>
public record TenderQuoteInput(string YardReference, DateTime? ValidUntil, decimal OtherChargesUsd, string Remarks, IReadOnlyDictionary<Guid, decimal?> Lines);

public record PhaseInput(Guid? Id, string Name, DateTime? PlannedStart, DateTime? PlannedEnd, DateTime? ActualStart, DateTime? ActualEnd);

public record DocumentInput(Guid? Id, string Name, DocumentCheckStatus Status, DateTime? DueDate, DateTime? ReceivedOn, string Reference = "", string Note = "");

public record MissingDocumentsRow(Guid ProjectId, string Code, string Title, string VesselName, ProjectStatus Status, List<string> Missing, int Overdue);

/// <summary>A linked risk assessment, resolved to its latest version so a link never points at a superseded one.</summary>
public record RiskLinkView(ProjectRiskLink Link, Guid? AssessmentId, string Title, int Version, string ResidualBand);

public record RiskCandidate(string Code, string Title);

public record ProjectFileView(Guid Id, Guid? TenderId, string FileName, string ContentType, long SizeBytes, string UploadedByName, DateTime UploadedAt)
{
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal);
}

/// <summary>
/// The office's planning tools for a project: the yard list, tenders and their comparison, phases of the stay, the documents checklist and links to risk
/// assessments. All of it stays at the office; a vessel installation cannot write any of it. Changing any of it needs the same permission as planning the project,
/// and a closed project is locked.
/// </summary>
public class ProjectPlanningService(IDbContextFactory<ChandleryDbContext> factory, IFileStore files)
{
    private static DateTime? Day(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v.Date, DateTimeKind.Utc) : null;

    private async Task<ChandleryDbContext> OfficeAsync(Guid companyId, Actor? manager = null)
    {
        var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        ProjectService.RequireOffice(db);
        if (manager is not null) await ProjectService.RequireManagerAsync(db, companyId, manager);
        return db;
    }

    // ---- yards ----

    public async Task<List<ProjectYard>> YardsAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.ProjectYards.AsNoTracking().Where(y => y.CompanyId == companyId);
        if (!includeInactive) query = query.Where(y => y.IsActive);
        return await query.OrderBy(y => y.Name).ToListAsync();
    }

    public async Task<ProjectYard> SaveYardAsync(Guid companyId, YardInput input, Actor actor)
    {
        var name = (input.Name ?? "").Trim();
        if (name == "") throw new InvalidOperationException("Give the yard a name.");
        await using var db = await OfficeAsync(companyId, actor);
        if (await db.ProjectYards.AnyAsync(y => y.CompanyId == companyId && y.Id != input.Id && y.Name.ToLower() == name.ToLower())) throw new InvalidOperationException($"There is already a yard called {name}.");
        var yard = input.Id is { } id ? await db.ProjectYards.FirstOrDefaultAsync(y => y.Id == id && y.CompanyId == companyId) ?? throw new InvalidOperationException("Yard not found.") : null;
        if (yard is null) db.ProjectYards.Add(yard = new ProjectYard { Id = Guid.CreateVersion7(), CompanyId = companyId });
        (yard.Name, yard.Location, yard.ContactName, yard.ContactEmail, yard.Notes, yard.IsActive) = (name, input.Location.Trim(), input.ContactName.Trim(), input.ContactEmail.Trim(), input.Notes.Trim(), input.IsActive);
        await db.SaveChangesAsync();
        return yard;
    }

    // ---- tenders ----

    private static void RequireOpenForTender(ProjectStatus status)
    {
        if (status is ProjectStatus.Complete or ProjectStatus.Closed) throw new InvalidOperationException("Tenders are for planning: this project's work is already complete.");
    }

    public async Task<List<TenderView>> TendersAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var tenders = await db.ProjectTenders.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync();
        var ids = tenders.Select(t => t.Id).ToList();
        var lines = (await db.ProjectTenderLines.AsNoTracking().Where(l => ids.Contains(l.TenderId)).ToListAsync()).ToLookup(l => l.TenderId);
        var yards = await db.ProjectYards.AsNoTracking().Where(y => y.CompanyId == companyId).ToDictionaryAsync(y => y.Id, y => y.Name);
        return tenders.Select(t => new TenderView(t, yards.GetValueOrDefault(t.YardId) ?? "", lines[t.Id].ToList())).OrderBy(v => v.YardName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<TenderComparison> CompareAsync(Guid companyId, Guid projectId)
    {
        var tenders = await TendersAsync(companyId, projectId);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var items = await db.ProjectItems.AsNoTracking().Where(i => i.ProjectId == projectId).ToListAsync();
        return ProjectPlanningRules.Compare(items, tenders.Select(v => (v.Tender, v.YardName, v.Lines)).ToList());
    }

    public async Task<ProjectTender> InviteAsync(Guid companyId, Guid projectId, Guid yardId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var (_, status) = await ProjectService.LoadEditableAsync(db, companyId, projectId);
        RequireOpenForTender(status);
        var yard = await db.ProjectYards.FirstOrDefaultAsync(y => y.Id == yardId && y.CompanyId == companyId && y.IsActive) ?? throw new InvalidOperationException("Choose a yard from the list.");
        if (await db.ProjectTenders.AnyAsync(t => t.ProjectId == projectId && t.YardId == yardId)) throw new InvalidOperationException($"{yard.Name} has already been invited.");
        var tender = new ProjectTender { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, YardId = yardId, Status = TenderStatus.Invited, InvitedAt = DateTime.UtcNow, InvitedByName = actor.Name };
        db.ProjectTenders.Add(tender);
        await db.SaveChangesAsync();
        return tender;
    }

    private static async Task<(ProjectTender Tender, Project Project)> LoadTenderAsync(ChandleryDbContext db, Guid companyId, Guid tenderId)
    {
        var tender = await db.ProjectTenders.FirstOrDefaultAsync(t => t.Id == tenderId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Tender not found.");
        var (project, status) = await ProjectService.LoadEditableAsync(db, companyId, tender.ProjectId);
        RequireOpenForTender(status);
        return (tender, project);
    }

    /// <summary>Records what the yard quoted, replacing any earlier quote from it. A quote needs at least one price or an other charge.</summary>
    public async Task<ProjectTender> RecordQuoteAsync(Guid companyId, Guid tenderId, TenderQuoteInput input, Actor actor)
    {
        if (input.OtherChargesUsd < 0 || input.Lines.Values.Any(v => v < 0)) throw new InvalidOperationException("A price cannot be negative.");
        await using var db = await OfficeAsync(companyId, actor);
        var (tender, project) = await LoadTenderAsync(db, companyId, tenderId);
        if (tender.Status is not (TenderStatus.Invited or TenderStatus.Quoted)) throw new InvalidOperationException("Only a yard that is still being considered can have its quote changed.");
        var live = project.Items.Where(i => i.Status != ProjectItemStatus.Dropped).Select(i => i.Id).ToHashSet();
        var priced = input.Lines.Where(kv => kv.Value is not null).ToList();
        if (priced.Any(kv => !live.Contains(kv.Key))) throw new InvalidOperationException("A price was given for an item that is not on this project.");
        if (priced.Count == 0 && input.OtherChargesUsd == 0) throw new InvalidOperationException("Enter at least one price before saving the quote.");
        db.ProjectTenderLines.RemoveRange(await db.ProjectTenderLines.Where(l => l.TenderId == tenderId).ToListAsync());
        foreach (var kv in priced) db.ProjectTenderLines.Add(new ProjectTenderLine { Id = Guid.CreateVersion7(), CompanyId = companyId, TenderId = tenderId, ItemId = kv.Key, AmountUsd = kv.Value!.Value });
        (tender.Status, tender.QuotedAt, tender.YardReference, tender.ValidUntil, tender.OtherChargesUsd, tender.Remarks) =
            (TenderStatus.Quoted, DateTime.UtcNow, input.YardReference.Trim(), Day(input.ValidUntil), input.OtherChargesUsd, input.Remarks.Trim());
        await db.SaveChangesAsync();
        return tender;
    }

    /// <summary>The yard will not quote.</summary>
    public async Task DeclineAsync(Guid companyId, Guid tenderId, string reason, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var (tender, _) = await LoadTenderAsync(db, companyId, tenderId);
        if (tender.Status != TenderStatus.Invited) throw new InvalidOperationException("Only a yard that has not quoted can be marked as declined.");
        (tender.Status, tender.Remarks) = (TenderStatus.Declined, (reason ?? "").Trim());
        await db.SaveChangesAsync();
    }

    /// <summary>Takes back an invitation. A yard that has quoted stays on the record; only one that has not quoted can be removed.</summary>
    public async Task RemoveTenderAsync(Guid companyId, Guid tenderId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var (tender, _) = await LoadTenderAsync(db, companyId, tenderId);
        if (tender.Status is not (TenderStatus.Invited or TenderStatus.Declined)) throw new InvalidOperationException("A yard that has quoted stays on the record.");
        db.ProjectTenders.Remove(tender);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Awards the project to a yard that has quoted. The yard's name becomes the project's yard (so the vessel sees it), the other quotes are marked not awarded, and
    /// each price the yard gave becomes that item's quoted cost, the baseline the budget is checked against. Awarding another yard later swaps it all over.
    /// </summary>
    public async Task<ProjectTender> AwardAsync(Guid companyId, Guid tenderId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var (tender, project) = await LoadTenderAsync(db, companyId, tenderId);
        if (tender.Status is not (TenderStatus.Quoted or TenderStatus.NotAwarded or TenderStatus.Awarded)) throw new InvalidOperationException("Only a yard that has quoted can be awarded the project.");
        var lines = await db.ProjectTenderLines.Where(l => l.TenderId == tenderId).ToListAsync();
        if (lines.Count == 0 && tender.OtherChargesUsd == 0) throw new InvalidOperationException("This yard has not priced anything.");
        var yard = await db.ProjectYards.FirstAsync(y => y.Id == tender.YardId);

        foreach (var other in await db.ProjectTenders.Where(t => t.ProjectId == tender.ProjectId && t.Id != tenderId && (t.Status == TenderStatus.Quoted || t.Status == TenderStatus.Awarded)).ToListAsync())
            (other.Status, other.AwardedAt, other.AwardedByName) = (TenderStatus.NotAwarded, null, "");
        (tender.Status, tender.AwardedAt, tender.AwardedByName) = (TenderStatus.Awarded, DateTime.UtcNow, actor.Name);
        project.Yard = yard.Name;

        var costs = await db.ProjectItemCosts.Where(c => c.ProjectId == tender.ProjectId).ToListAsync();
        foreach (var line in lines)
        {
            var cost = costs.FirstOrDefault(c => c.ItemId == line.ItemId);
            if (cost is null) db.ProjectItemCosts.Add(new ProjectItemCost { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = tender.ProjectId, ItemId = line.ItemId, QuotedUsd = line.AmountUsd });
            else cost.QuotedUsd = line.AmountUsd;
        }
        await db.SaveChangesAsync();
        return tender;
    }

    // ---- phases ----

    public async Task<List<ProjectPhase>> PhasesAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.ProjectPhases.AsNoTracking().Where(p => p.ProjectId == projectId).OrderBy(p => p.SortOrder).ToListAsync();
    }

    /// <summary>Replaces the phases with those given, in the order given.</summary>
    public async Task SavePhasesAsync(Guid companyId, Guid projectId, IReadOnlyList<PhaseInput> phases, Actor actor)
    {
        foreach (var p in phases)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidOperationException("Give every phase a name.");
            if (ProjectPlanningRules.CheckPhase(p.PlannedStart, p.PlannedEnd) is { } planned) throw new InvalidOperationException($"{p.Name.Trim()}: {planned}");
            if (ProjectPlanningRules.CheckPhase(p.ActualStart, p.ActualEnd) is { } actual) throw new InvalidOperationException($"{p.Name.Trim()}: {actual}");
        }
        await using var db = await OfficeAsync(companyId, actor);
        await ProjectService.LoadEditableAsync(db, companyId, projectId);
        db.ProjectPhases.RemoveRange(await db.ProjectPhases.Where(p => p.ProjectId == projectId).ToListAsync());
        var order = 0;
        foreach (var p in phases)
            db.ProjectPhases.Add(new ProjectPhase { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Name = p.Name.Trim(), PlannedStart = Day(p.PlannedStart), PlannedEnd = Day(p.PlannedEnd), ActualStart = Day(p.ActualStart), ActualEnd = Day(p.ActualEnd), SortOrder = order++ });
        await db.SaveChangesAsync();
    }

    /// <summary>The standard phases for the project's type, added only when it has none yet.</summary>
    public async Task<int> AddStandardPhasesAsync(Guid companyId, Guid projectId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var (project, _) = await ProjectService.LoadEditableAsync(db, companyId, projectId);
        if (await db.ProjectPhases.AnyAsync(p => p.ProjectId == projectId)) throw new InvalidOperationException("This project already has phases.");
        var order = 0;
        foreach (var name in ProjectPlanningRules.StandardPhases(project.Type))
            db.ProjectPhases.Add(new ProjectPhase { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Name = name, SortOrder = order++ });
        await db.SaveChangesAsync();
        return order;
    }

    // ---- documents checklist ----

    public async Task<List<ProjectDocumentCheck>> DocumentsAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.ProjectDocumentChecks.AsNoTracking().Where(d => d.ProjectId == projectId).OrderBy(d => d.SortOrder).ToListAsync();
    }

    /// <summary>Replaces the checklist with the rows given. Marking a document received without a date takes today.</summary>
    public async Task SaveDocumentsAsync(Guid companyId, Guid projectId, IReadOnlyList<DocumentInput> documents, Actor actor)
    {
        if (documents.Any(d => string.IsNullOrWhiteSpace(d.Name))) throw new InvalidOperationException("Give every document a name.");
        await using var db = await OfficeAsync(companyId, actor);
        await ProjectService.LoadEditableAsync(db, companyId, projectId);
        db.ProjectDocumentChecks.RemoveRange(await db.ProjectDocumentChecks.Where(d => d.ProjectId == projectId).ToListAsync());
        var order = 0;
        foreach (var d in documents)
            db.ProjectDocumentChecks.Add(new ProjectDocumentCheck
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Name = d.Name.Trim(), Status = d.Status, DueDate = Day(d.DueDate),
                ReceivedOn = d.Status == DocumentCheckStatus.Received ? Day(d.ReceivedOn ?? DateTime.UtcNow) : null, Reference = d.Reference.Trim(), Note = d.Note.Trim(), SortOrder = order++,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>Adds the standard documents for the project's type that are not already on the list.</summary>
    public async Task<int> AddStandardDocumentsAsync(Guid companyId, Guid projectId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var (project, _) = await ProjectService.LoadEditableAsync(db, companyId, projectId);
        var existing = await db.ProjectDocumentChecks.Where(d => d.ProjectId == projectId).ToListAsync();
        var order = existing.Count == 0 ? 0 : existing.Max(d => d.SortOrder) + 1;
        var added = 0;
        foreach (var name in ProjectPlanningRules.StandardDocuments(project.Type).Where(n => existing.All(e => !string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))))
        {
            db.ProjectDocumentChecks.Add(new ProjectDocumentCheck { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, Name = name, Status = DocumentCheckStatus.Required, SortOrder = order++ });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>The missing-documents report: every project that is not closed and still waits on a document, most overdue first.</summary>
    public async Task<List<MissingDocumentsRow>> MissingDocumentsAsync(Guid companyId, DateTime? today = null)
    {
        var day = (today ?? DateTime.UtcNow).Date;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var missing = (await db.ProjectDocumentChecks.AsNoTracking().Where(d => d.CompanyId == companyId && d.Status == DocumentCheckStatus.Required).OrderBy(d => d.SortOrder).ToListAsync()).GroupBy(d => d.ProjectId).ToList();
        if (missing.Count == 0) return [];
        var ids = missing.Select(g => g.Key).ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync();
        var events = (await db.ProjectStatusEvents.AsNoTracking().Where(e => ids.Contains(e.ProjectId)).ToListAsync()).ToLookup(e => e.ProjectId);
        var vessels = await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        return missing.Select(g =>
            {
                var p = projects.First(x => x.Id == g.Key);
                return new MissingDocumentsRow(p.Id, p.Code, p.Title, vessels.GetValueOrDefault(p.VesselId) ?? "", ProjectStatusRules.Of(events[p.Id].ToList()), g.Select(d => d.Name).ToList(), g.Count(d => ProjectPlanningRules.IsOverdue(d, day)));
            })
            .Where(r => r.Status != ProjectStatus.Closed).OrderByDescending(r => r.Overdue).ThenByDescending(r => r.Missing.Count).ThenBy(r => r.Code).ToList();
    }

    // ---- risk links ----

    public async Task<List<RiskLinkView>> RiskLinksAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var links = await db.ProjectRiskLinks.AsNoTracking().Where(l => l.ProjectId == projectId).OrderBy(l => l.AssessmentCode).ToListAsync();
        var codes = links.Select(l => l.AssessmentCode).ToList();
        var latest = (await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && codes.Contains(a.Code)).ToListAsync())
            .GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Version).First());
        return links.Select(l => latest.TryGetValue(l.AssessmentCode, out var a)
            ? new RiskLinkView(l, a.Id, a.JobDescription, a.Version, a.HighestResidualBand) : new RiskLinkView(l, null, "", 0, "")).ToList();
    }

    /// <summary>The vessel's assessments (latest version of each) that the project is not yet linked to.</summary>
    public async Task<List<RiskCandidate>> RiskCandidatesAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId) ?? throw new InvalidOperationException("Project not found.");
        var linked = (await db.ProjectRiskLinks.AsNoTracking().Where(l => l.ProjectId == projectId).Select(l => l.AssessmentCode).ToListAsync()).ToHashSet();
        var all = await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && a.VesselId == project.VesselId && a.SubmissionCount > 0).ToListAsync();
        return all.GroupBy(a => a.Code).Select(g => g.OrderByDescending(a => a.Version).First()).Where(a => !linked.Contains(a.Code)).OrderBy(a => a.Code)
            .Select(a => new RiskCandidate(a.Code, a.JobDescription)).ToList();
    }

    public async Task LinkRiskAsync(Guid companyId, Guid projectId, string code, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        await ProjectService.LoadEditableAsync(db, companyId, projectId);
        code = (code ?? "").Trim();
        if (!await db.RiskAssessments.AnyAsync(a => a.CompanyId == companyId && a.Code == code)) throw new InvalidOperationException("Risk assessment not found.");
        if (await db.ProjectRiskLinks.AnyAsync(l => l.ProjectId == projectId && l.AssessmentCode == code)) throw new InvalidOperationException($"{code} is already linked.");
        db.ProjectRiskLinks.Add(new ProjectRiskLink { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, AssessmentCode = code, LinkedByName = actor.Name, At = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task UnlinkRiskAsync(Guid companyId, Guid linkId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var link = await db.ProjectRiskLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.CompanyId == companyId) ?? throw new InvalidOperationException("Link not found.");
        await ProjectService.LoadEditableAsync(db, companyId, link.ProjectId);
        db.ProjectRiskLinks.Remove(link);
        await db.SaveChangesAsync();
    }

    // ---- files kept with the project (a yard's quotation, a survey report) ----

    public async Task<List<ProjectFileView>> FilesAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rows = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == projectId).OrderBy(f => f.UploadedAt).ThenBy(f => f.Id).ToListAsync();
        return rows.Select(f => new ProjectFileView(f.Id, f.TenderId, f.FileName, f.ContentType, f.SizeBytes, f.UploadedByName, f.UploadedAt)).ToList();
    }

    /// <summary>Keeps a file with the project, or with one of its tenders (the yard's quotation). Images and PDFs only, up to 10 MB, and up to 10 for each place.</summary>
    public async Task<ProjectFile> AddFileAsync(Guid companyId, Guid projectId, Guid? tenderId, PendingAttachment file, Actor actor)
    {
        AttachmentRules.Validate(file);
        await using var db = await OfficeAsync(companyId, actor);
        await ProjectService.LoadEditableAsync(db, companyId, projectId);
        if (tenderId is { } tid && !await db.ProjectTenders.AnyAsync(t => t.Id == tid && t.ProjectId == projectId)) throw new InvalidOperationException("That tender is not on this project.");
        if (await db.ProjectFiles.CountAsync(f => f.ProjectId == projectId && f.TenderId == tenderId) >= AttachmentRules.MaxPerTarget)
            throw new InvalidOperationException($"You can keep up to {AttachmentRules.MaxPerTarget} files here.");
        var name = Path.GetFileName(file.FileName);
        await using var stream = new MemoryStream(file.Data);
        var key = await files.SaveAsync(companyId, stream);
        var row = new ProjectFile
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, TenderId = tenderId, FileName = name, ContentType = AttachmentRules.ContentTypeFor(name),
            SizeBytes = file.Data.LongLength, StoredKey = key, UploadedByName = actor.Name, UploadedAt = DateTime.UtcNow,
        };
        db.ProjectFiles.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public async Task DeleteFileAsync(Guid companyId, Guid fileId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var row = await db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.CompanyId == companyId) ?? throw new InvalidOperationException("File not found.");
        await ProjectService.LoadEditableAsync(db, companyId, row.ProjectId);
        db.ProjectFiles.Remove(row);
        await db.SaveChangesAsync();
        files.Delete(row.StoredKey);
    }

    /// <summary>Opens a file for the company that owns it, at the office. Onboard there is nothing to open: the vessel never holds a yard's prices.</summary>
    public async Task<(ProjectFile File, Stream Content)?> OpenFileAsync(Guid companyId, Guid fileId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return null;
        var row = await db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.CompanyId == companyId);
        if (row is null) return null;
        var content = files.OpenRead(row.StoredKey);
        return content is null ? null : (row, content);
    }

    // ---- groups of projects (one drydock across sister ships) ----

    public async Task<List<(ProjectGroup Group, List<Guid> ProjectIds)>> GroupsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var groups = await db.ProjectGroups.AsNoTracking().Where(g => g.CompanyId == companyId).OrderBy(g => g.Name).ToListAsync();
        var members = (await db.ProjectGroupMembers.AsNoTracking().Where(m => m.CompanyId == companyId).ToListAsync()).ToLookup(m => m.GroupId);
        return groups.Select(g => (g, members[g.Id].Select(m => m.ProjectId).ToList())).ToList();
    }

    /// <summary>The group a project is in, if any.</summary>
    public async Task<(Guid Id, string Name)?> GroupOfAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var group = await (from m in db.ProjectGroupMembers.AsNoTracking() join g in db.ProjectGroups.AsNoTracking() on m.GroupId equals g.Id where m.ProjectId == projectId select g).FirstOrDefaultAsync();
        return group is null ? null : (group.Id, group.Name);
    }

    public async Task<ProjectGroup> CreateGroupAsync(Guid companyId, string name, string note, Actor actor)
    {
        name = (name ?? "").Trim();
        if (name == "") throw new InvalidOperationException("Give the group a name.");
        await using var db = await OfficeAsync(companyId, actor);
        if (await db.ProjectGroups.AnyAsync(g => g.CompanyId == companyId && g.Name.ToLower() == name.ToLower())) throw new InvalidOperationException($"There is already a group called {name}.");
        var group = new ProjectGroup { Id = Guid.CreateVersion7(), CompanyId = companyId, Name = name, Note = (note ?? "").Trim(), CreatedAt = DateTime.UtcNow, CreatedByName = actor.Name };
        db.ProjectGroups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }

    /// <summary>Deletes the group. Its projects are untouched: they simply stop being grouped.</summary>
    public async Task DeleteGroupAsync(Guid companyId, Guid groupId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var group = await db.ProjectGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId) ?? throw new InvalidOperationException("Group not found.");
        db.ProjectGroupMembers.RemoveRange(await db.ProjectGroupMembers.Where(m => m.GroupId == groupId).ToListAsync());
        db.ProjectGroups.Remove(group);
        await db.SaveChangesAsync();
    }

    /// <summary>Puts a project in a group. A project is in one group at most, and a group has at most one project for each vessel.</summary>
    public async Task AddToGroupAsync(Guid companyId, Guid groupId, Guid projectId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var group = await db.ProjectGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId) ?? throw new InvalidOperationException("Group not found.");
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.CompanyId == companyId) ?? throw new InvalidOperationException("Project not found.");
        if (await db.ProjectGroupMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId) is { } existing)
            throw new InvalidOperationException(existing.GroupId == groupId ? "That project is already in this group." : "That project is already in another group; take it out of that one first.");
        var memberIds = await db.ProjectGroupMembers.Where(m => m.GroupId == groupId).Select(m => m.ProjectId).ToListAsync();
        if (await db.Projects.AnyAsync(p => memberIds.Contains(p.Id) && p.VesselId == project.VesselId))
            throw new InvalidOperationException($"{group.Name} already has a project for that vessel.");
        db.ProjectGroupMembers.Add(new ProjectGroupMember { Id = Guid.CreateVersion7(), CompanyId = companyId, GroupId = groupId, ProjectId = projectId });
        await db.SaveChangesAsync();
    }

    public async Task RemoveFromGroupAsync(Guid companyId, Guid projectId, Actor actor)
    {
        await using var db = await OfficeAsync(companyId, actor);
        var member = await db.ProjectGroupMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.CompanyId == companyId);
        if (member is null) return;
        db.ProjectGroupMembers.Remove(member);
        await db.SaveChangesAsync();
    }
}
