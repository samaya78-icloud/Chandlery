using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>A file picked in a form that is not saved yet (the requisition does not exist until the form is submitted).</summary>
public class PendingAttachment
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool Share { get; set; }
}

public record AttachmentView(int Id, int? RequisitionLineId, string FileName, string ContentType, long SizeBytes, bool ShareWithSuppliers, string UploadedByName, DateTime UploadedAt)
{
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal);
}

public static class AttachmentRules
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const int MaxPerTarget = 10;
    public const string AcceptList = ".pdf,.png,.jpg,.jpeg,.webp,.gif";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp", [".gif"] = "image/gif"
    };

    /// <summary>The content type is taken from the extension, never from what the browser claims, so a file cannot pose as something else.</summary>
    public static string ContentTypeFor(string fileName) =>
        ContentTypes.GetValueOrDefault(Path.GetExtension(fileName)) ?? throw new InvalidOperationException($"{fileName}: only images (PNG, JPG, WEBP, GIF) and PDF files can be attached.");

    /// <summary>Only these types are ever shown inline; anything else is a download, whatever the file claims to be.</summary>
    public static bool IsInlineSafe(string? contentType) =>
        contentType is "application/pdf" or "image/png" or "image/jpeg" or "image/webp" or "image/gif";

    public static void Validate(PendingAttachment file)
    {
        ContentTypeFor(file.FileName);
        if (file.Data.Length == 0) throw new InvalidOperationException($"{file.FileName} is empty.");
        if (file.Data.LongLength > MaxBytes) throw new InvalidOperationException($"{file.FileName} is larger than 10 MB.");
    }
}

/// <summary>Files on requisitions (header and line level), and which of them suppliers may see on an RFQ.</summary>
public class AttachmentService(IDbContextFactory<ChandleryDbContext> factory, IFileStore files)
{
    private static AttachmentView ToView(Attachment a) =>
        new(a.Id, a.RequisitionLineId, a.FileName, a.ContentType, a.SizeBytes, a.ShareWithSuppliers, a.UploadedByName, a.UploadedAt);

    /// <summary>Saves files picked while creating a requisition, inside the caller's context so they commit together with it.</summary>
    internal async Task AddNewAsync(ChandleryDbContext db, Requisition requisition, IReadOnlyList<PendingAttachment> header, IReadOnlyList<IReadOnlyList<PendingAttachment>> perLine, Actor actor)
    {
        foreach (var file in header.Concat(perLine.SelectMany(l => l))) AttachmentRules.Validate(file);
        if (header.Count > AttachmentRules.MaxPerTarget || perLine.Any(l => l.Count > AttachmentRules.MaxPerTarget))
            throw new InvalidOperationException($"You can attach up to {AttachmentRules.MaxPerTarget} files to the requisition and to each line.");

        for (var i = -1; i < perLine.Count; i++)
        {
            var group = i < 0 ? header : perLine[i];
            int? lineId = i < 0 ? null : requisition.Lines[i].Id;
            foreach (var file in group) await AddRowAsync(db, requisition, lineId, file, actor);
        }
    }

    private async Task AddRowAsync(ChandleryDbContext db, Requisition requisition, int? lineId, PendingAttachment file, Actor actor)
    {
        var name = Path.GetFileName(file.FileName);
        await using var stream = new MemoryStream(file.Data);
        var key = await files.SaveAsync(requisition.CompanyId, stream);
        db.Attachments.Add(new Attachment
        {
            CompanyId = requisition.CompanyId, RequisitionId = requisition.Id, RequisitionLineId = lineId, FileName = name,
            ContentType = AttachmentRules.ContentTypeFor(name), SizeBytes = file.Data.LongLength, StoredKey = key,
            ShareWithSuppliers = file.Share, UploadedByName = actor.Name, UploadedAt = DateTime.UtcNow
        });
        AuditLog.Add(db, requisition.CompanyId, AuditEntityTypes.Requisition, requisition.Id, actor,
            $"ATTACHED {name}{(lineId.HasValue ? " to a line" : "")}{(file.Share ? " (shown to suppliers on the RFQ)" : " (internal)")}");
    }

    public async Task<List<AttachmentView>> ListAsync(int companyId, int requisitionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rows = await db.Attachments.Where(a => a.CompanyId == companyId && a.RequisitionId == requisitionId)
            .OrderBy(a => a.Id).ToListAsync();
        return rows.Select(ToView).ToList();
    }

    /// <summary>Adds one file to an existing requisition (header when <paramref name="lineId"/> is null).</summary>
    public async Task AddAsync(int companyId, int requisitionId, int? lineId, PendingAttachment file, Actor actor)
    {
        AttachmentRules.Validate(file);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var requisition = await db.Requisitions.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == requisitionId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("Requisition not found.");
        if (requisition.Status == RequisitionStatus.Cancelled) throw new InvalidOperationException("This requisition is cancelled.");
        if (lineId.HasValue && requisition.Lines.All(l => l.Id != lineId.Value)) throw new InvalidOperationException("Line not found on this requisition.");

        var count = await db.Attachments.CountAsync(a => a.RequisitionId == requisitionId && a.RequisitionLineId == lineId);
        if (count >= AttachmentRules.MaxPerTarget)
            throw new InvalidOperationException($"You can attach up to {AttachmentRules.MaxPerTarget} files here.");

        await AddRowAsync(db, requisition, lineId, file, actor);
        await db.SaveChangesAsync();
    }

    public async Task SetShareAsync(int companyId, int attachmentId, bool share, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.CompanyId == companyId)
            ?? throw new InvalidOperationException("File not found.");
        if (a.ShareWithSuppliers == share) return;
        a.ShareWithSuppliers = share;
        AuditLog.Add(db, companyId, AuditEntityTypes.Requisition, a.RequisitionId, actor,
            share ? $"SHARED {a.FileName} with suppliers on the RFQ" : $"STOPPED sharing {a.FileName} with suppliers (internal only)");
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int companyId, int attachmentId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.CompanyId == companyId)
            ?? throw new InvalidOperationException("File not found.");
        db.Attachments.Remove(a);
        AuditLog.Add(db, companyId, AuditEntityTypes.Requisition, a.RequisitionId, actor, $"REMOVED attachment {a.FileName}");
        await db.SaveChangesAsync();
        files.Delete(a.StoredKey);
    }

    /// <summary>
    /// The files on the requisition behind an RFQ, for the company's own users: everything, with the sharing flag.
    /// Line files are keyed by the RFQ line they belong to.
    /// </summary>
    public async Task<RfqFiles> ForRfqAsync(int companyId, int rfqId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rfq = await db.Rfqs.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfqId && r.CompanyId == companyId);
        return rfq is null ? RfqFiles.Empty : await BuildRfqFilesAsync(db, rfq, sharedOnly: false);
    }

    /// <summary>The files a supplier may see on an RFQ: only shared ones, and only when the supplier was invited.</summary>
    public async Task<RfqFiles> ForRfqSupplierAsync(int supplierId, int rfqId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rfq = await db.Rfqs.IgnoreQueryFilters().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfqId);
        if (rfq is null || !rfq.InvitedSupplierIds.Contains(supplierId)) return RfqFiles.Empty;
        return await BuildRfqFilesAsync(db, rfq, sharedOnly: true);
    }

    private static async Task<RfqFiles> BuildRfqFilesAsync(ChandleryDbContext db, Rfq rfq, bool sharedOnly)
    {
        if (rfq.RequisitionId is not { } requisitionId) return RfqFiles.Empty;
        var query = db.Attachments.IgnoreQueryFilters().Where(a => a.RequisitionId == requisitionId && a.CompanyId == rfq.CompanyId);
        if (sharedOnly) query = query.Where(a => a.ShareWithSuppliers);
        var rows = await query.OrderBy(a => a.Id).ToListAsync();

        var header = rows.Where(a => a.RequisitionLineId is null).Select(ToView).ToList();
        var byLine = new Dictionary<int, List<AttachmentView>>();
        foreach (var line in rfq.Lines.Where(l => l.RequisitionLineId.HasValue))
        {
            var mine = rows.Where(a => a.RequisitionLineId == line.RequisitionLineId).Select(ToView).ToList();
            if (mine.Count > 0) byLine[line.Id] = mine;
        }
        return new RfqFiles(header, byLine);
    }

    /// <summary>
    /// Opens a file if the caller may see it: the company that owns it, or a supplier invited to an RFQ made from the requisition
    /// (for a line file, an RFQ that includes the line) when the file is shared.
    /// </summary>
    public async Task<(Attachment File, Stream Content)?> OpenAsync(int attachmentId, int? companyId, int? supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var a = await db.Attachments.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == attachmentId);
        if (a is null) return null;

        var allowed = companyId.HasValue && a.CompanyId == companyId;
        if (!allowed && supplierId.HasValue && a.ShareWithSuppliers)
        {
            var sid = supplierId.Value;
            allowed = await db.Rfqs.IgnoreQueryFilters().AnyAsync(r => r.RequisitionId == a.RequisitionId && r.CompanyId == a.CompanyId
                && r.InvitedSupplierIds.Contains(sid)
                && (a.RequisitionLineId == null || r.Lines.Any(l => l.RequisitionLineId == a.RequisitionLineId)));
        }
        if (!allowed) return null;

        var content = files.OpenRead(a.StoredKey);
        return content is null ? null : (a, content);
    }
}

/// <summary>Files for one RFQ: those on the requisition header, and those on lines, keyed by RFQ line id.</summary>
public record RfqFiles(List<AttachmentView> Header, Dictionary<int, List<AttachmentView>> ByLine)
{
    public static readonly RfqFiles Empty = new(new(), new());
    public bool Any => Header.Count > 0 || ByLine.Count > 0;
}
