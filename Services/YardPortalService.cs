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
}
