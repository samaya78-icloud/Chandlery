using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>One invitation as the yard sees it in its list.</summary>
public record YardInvitation(Guid TenderId, string ProjectTitle, string VesselName, ProjectType Type, DateTime? PlannedStart, DateTime? PlannedEnd, TenderStatus Status,
    DateTime InvitedAt, DateTime? ValidUntil, decimal? QuoteTotal, bool CanQuote);

/// <summary>A job on the specification as a yard sees it: what is to be done, and nothing about its source, cost or state at the owner.</summary>
public record YardItem(Guid Id, string Group, string Description);

/// <summary>Everything a yard is shown for one tender: the ship and the jobs, its own quote and its own files. Never a budget, another yard's price, or the owner's costs.</summary>
public record YardTenderView(YardInvitation Invitation, string Scope, List<YardItem> Items, ProjectTender Tender, Dictionary<Guid, decimal> Prices, List<ProjectFileView> Files);

/// <summary>
/// What a yard's own login can do: see the tenders its yard was invited to, read the specification to price, enter or change its quote and attach its quotation, until the tender is
/// awarded or the work is complete. Every call is scoped to the yard and company the login belongs to, and refuses onboard: a vessel never holds a yard's prices.
/// </summary>
public class YardPortalService(IDbContextFactory<ChandleryDbContext> factory, IFileStore files)
{
    private async Task<ChandleryDbContext> OpenAsync(Guid companyId)
    {
        var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        ProjectService.RequireOffice(db);
        return db;
    }

    private static bool Open(ProjectStatus status) => status is not (ProjectStatus.Complete or ProjectStatus.Closed);

    private static async Task<(ProjectTender Tender, Project Project, ProjectStatus Status)> LoadAsync(ChandleryDbContext db, Guid companyId, Guid yardId, Guid tenderId)
    {
        var tender = await db.ProjectTenders.FirstOrDefaultAsync(t => t.Id == tenderId && t.CompanyId == companyId && t.YardId == yardId) ?? throw new InvalidOperationException("Tender not found.");
        var project = await db.Projects.Include(p => p.Items).FirstAsync(p => p.Id == tender.ProjectId);
        var status = ProjectStatusRules.Of(await db.ProjectStatusEvents.AsNoTracking().Where(e => e.ProjectId == project.Id).ToListAsync());
        return (tender, project, status);
    }

    private static YardInvitation Invitation(ProjectTender t, Project p, string vessel, ProjectStatus status, decimal? total)
        => new(t.Id, p.Title, vessel, p.Type, p.PlannedStart, p.PlannedEnd, t.Status, t.InvitedAt, t.ValidUntil, total, Open(status) && t.Status is TenderStatus.Invited or TenderStatus.Quoted);

    public async Task<List<YardInvitation>> InvitationsAsync(Guid companyId, Guid yardId)
    {
        await using var db = await OpenAsync(companyId);
        var tenders = await db.ProjectTenders.AsNoTracking().Where(t => t.CompanyId == companyId && t.YardId == yardId).OrderByDescending(t => t.InvitedAt).ToListAsync();
        var ids = tenders.Select(t => t.ProjectId).ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var vessels = await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        var events = (await db.ProjectStatusEvents.AsNoTracking().Where(e => ids.Contains(e.ProjectId)).ToListAsync()).ToLookup(e => e.ProjectId);
        var tenderIds = tenders.Select(t => t.Id).ToList();
        var sums = (await db.ProjectTenderLines.AsNoTracking().Where(l => tenderIds.Contains(l.TenderId)).ToListAsync()).GroupBy(l => l.TenderId).ToDictionary(g => g.Key, g => g.Sum(l => l.AmountUsd));
        return tenders.Select(t => Invitation(t, projects[t.ProjectId], vessels.GetValueOrDefault(projects[t.ProjectId].VesselId) ?? "", ProjectStatusRules.Of(events[t.ProjectId].ToList()),
            t.QuotedAt is null ? null : sums.GetValueOrDefault(t.Id) + t.OtherChargesUsd)).ToList();
    }

    public async Task<YardTenderView> TenderAsync(Guid companyId, Guid yardId, Guid tenderId)
    {
        await using var db = await OpenAsync(companyId);
        var (tender, project, status) = await LoadAsync(db, companyId, yardId, tenderId);
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == project.VesselId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";
        var lines = await db.ProjectTenderLines.AsNoTracking().Where(l => l.TenderId == tenderId).ToListAsync();
        var items = project.Items.Where(i => i.Status != ProjectItemStatus.Dropped).OrderBy(i => i.SortOrder).Select(i => new YardItem(i.Id, i.Group, i.Description)).ToList();
        var fileRows = await db.ProjectFiles.AsNoTracking().Where(f => f.TenderId == tenderId).OrderBy(f => f.UploadedAt).ToListAsync();
        return new YardTenderView(Invitation(tender, project, vessel, status, tender.QuotedAt is null ? null : lines.Sum(l => l.AmountUsd) + tender.OtherChargesUsd), project.Description, items, tender,
            lines.ToDictionary(l => l.ItemId, l => l.AmountUsd), fileRows.Select(f => new ProjectFileView(f.Id, f.TenderId, f.FileName, f.ContentType, f.SizeBytes, f.UploadedByName, f.UploadedAt)).ToList());
    }

    /// <summary>The yard enters or changes its quote. Refused once the work is complete or the tender is decided.</summary>
    public async Task SubmitQuoteAsync(Guid companyId, Guid yardId, Guid tenderId, TenderQuoteInput input)
    {
        await using var db = await OpenAsync(companyId);
        var (tender, project, status) = await LoadAsync(db, companyId, yardId, tenderId);
        if (!Open(status)) throw new InvalidOperationException("This tender is closed: the work is already complete.");
        await ProjectPlanningService.ApplyQuoteAsync(db, companyId, tender, project, input, byYard: true);
        await db.SaveChangesAsync();
    }

    /// <summary>The yard attaches its quotation (an image or PDF) to its own tender, while it can still change its quote.</summary>
    public async Task AddFileAsync(Guid companyId, Guid yardId, Guid tenderId, PendingAttachment file, string uploadedBy)
    {
        await using var db = await OpenAsync(companyId);
        var (tender, project, status) = await LoadAsync(db, companyId, yardId, tenderId);
        if (!Open(status) || tender.Status is not (TenderStatus.Invited or TenderStatus.Quoted)) throw new InvalidOperationException("This tender can no longer take files.");
        await ProjectPlanningService.StoreFileAsync(db, files, companyId, project.Id, tenderId, file, uploadedBy + " (yard)");
    }

    /// <summary>Opens a file only if it belongs to one of this yard's own tenders.</summary>
    public async Task<(ProjectFile File, Stream Content)?> OpenFileAsync(Guid companyId, Guid yardId, Guid fileId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return null;
        var row = await db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId && f.CompanyId == companyId && f.TenderId != null);
        if (row is null || !await db.ProjectTenders.AnyAsync(t => t.Id == row.TenderId && t.YardId == yardId)) return null;
        var content = files.OpenRead(row.StoredKey);
        return content is null ? null : (row, content);
    }

    // ---- J12: declining, asking, revising history, and the yard's own contact details ----

    /// <summary>The yard says it will not quote. Only a yard that has not quoted can decline.</summary>
    public async Task DeclineAsync(Guid companyId, Guid yardId, Guid tenderId, string reason)
    {
        reason = (reason ?? "").Trim();
        if (reason == "") throw new InvalidOperationException("Say why you are declining.");
        await using var db = await OpenAsync(companyId);
        var (tender, project, status) = await LoadAsync(db, companyId, yardId, tenderId);
        if (!Open(status)) throw new InvalidOperationException("This tender is closed: the work is already complete.");
        if (tender.Status != TenderStatus.Invited) throw new InvalidOperationException("Only an invitation you have not quoted on can be declined.");
        (tender.Status, tender.Remarks) = (TenderStatus.Declined, reason);
        var yardName = await db.ProjectYards.Where(y => y.Id == yardId).Select(y => y.Name).FirstAsync();
        ProjectPlanningService.Notify(db, companyId, project.Id, tender.Id, $"{yardName} declined to quote on {project.Title}: {reason}");
        await db.SaveChangesAsync();
    }

    public record YardQuestion(Guid Id, string Question, DateTime AskedAt, string Answer, DateTime? AnsweredAt);

    /// <summary>The yard's own questions on a tender, with the answers given.</summary>
    public async Task<List<YardQuestion>> QuestionsAsync(Guid companyId, Guid yardId, Guid tenderId)
    {
        await using var db = await OpenAsync(companyId);
        await LoadAsync(db, companyId, yardId, tenderId);
        return (await db.ProjectTenderQuestions.AsNoTracking().Where(q => q.TenderId == tenderId).OrderBy(q => q.AskedAt).ToListAsync())
            .Select(q => new YardQuestion(q.Id, q.Question, q.AskedAt, q.Answer, q.AnsweredAt)).ToList();
    }

    public async Task AskAsync(Guid companyId, Guid yardId, Guid tenderId, string question, string askedBy)
    {
        question = (question ?? "").Trim();
        if (question == "") throw new InvalidOperationException("Write your question.");
        if (question.Length > 2000) throw new InvalidOperationException("A question can be at most 2000 characters.");
        await using var db = await OpenAsync(companyId);
        var (tender, project, status) = await LoadAsync(db, companyId, yardId, tenderId);
        if (!Open(status)) throw new InvalidOperationException("This tender is closed: the work is already complete.");
        if (await db.ProjectTenderQuestions.CountAsync(q => q.TenderId == tenderId && q.Answer == "") >= 5) throw new InvalidOperationException("You already have five questions waiting for an answer.");
        db.ProjectTenderQuestions.Add(new ProjectTenderQuestion { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = project.Id, TenderId = tenderId, Question = question, AskedAt = DateTime.UtcNow, AskedByName = askedBy });
        var yardName = await db.ProjectYards.Where(y => y.Id == yardId).Select(y => y.Name).FirstAsync();
        ProjectPlanningService.Notify(db, companyId, project.Id, tender.Id, $"{yardName} asked a question on {project.Title}: {(question.Length > 200 ? question[..200] + "…" : question)}");
        await db.SaveChangesAsync();
    }

    /// <summary>The yard's own earlier versions of its quote, oldest first.</summary>
    public async Task<List<ProjectTenderRevision>> RevisionsAsync(Guid companyId, Guid yardId, Guid tenderId)
    {
        await using var db = await OpenAsync(companyId);
        await LoadAsync(db, companyId, yardId, tenderId);
        return await db.ProjectTenderRevisions.AsNoTracking().Where(r => r.TenderId == tenderId).OrderBy(r => r.Number).ToListAsync();
    }

    public record YardProfile(string Name, string Location, string ContactName, string ContactEmail);

    public async Task<YardProfile> ProfileAsync(Guid companyId, Guid yardId)
    {
        await using var db = await OpenAsync(companyId);
        var y = await db.ProjectYards.AsNoTracking().FirstOrDefaultAsync(y => y.Id == yardId && y.CompanyId == companyId) ?? throw new InvalidOperationException("Yard not found.");
        return new YardProfile(y.Name, y.Location, y.ContactName, y.ContactEmail);
    }

    /// <summary>The yard keeps its own contact details up to date. Its name, notes and whether it is in use stay with the office.</summary>
    public async Task SaveProfileAsync(Guid companyId, Guid yardId, string location, string contactName, string contactEmail)
    {
        (location, contactName, contactEmail) = ((location ?? "").Trim(), (contactName ?? "").Trim(), (contactEmail ?? "").Trim());
        if (location.Length > 150 || contactName.Length > 120 || contactEmail.Length > 200) throw new InvalidOperationException("One of the details is too long.");
        if (contactEmail != "" && !(contactEmail.Contains('@') && contactEmail.IndexOf('@') > 0 && contactEmail.IndexOf('@') < contactEmail.Length - 1 && !contactEmail.Contains(' '))) throw new InvalidOperationException("That is not an email address.");
        await using var db = await OpenAsync(companyId);
        var y = await db.ProjectYards.FirstOrDefaultAsync(y => y.Id == yardId && y.CompanyId == companyId) ?? throw new InvalidOperationException("Yard not found.");
        (y.Location, y.ContactName, y.ContactEmail) = (location, contactName, contactEmail);
        await db.SaveChangesAsync();
    }
}
