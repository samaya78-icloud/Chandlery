using System.Security.Cryptography;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Chandlery.Services;

/// <summary>
/// What may be attached to a record and how big it may be. The bytes travel inside the synced record (the sync engine carries no files yet), so the limits are small:
/// a photo is shrunk before it is kept, and a PDF has to be small to begin with.
/// </summary>
public static class FileRules
{
    /// <summary>The most a kept file may weigh, after a photo has been shrunk.</summary>
    public const int MaxStoredBytes = 1_000_000;

    /// <summary>The most a person may pick: a camera photo is far larger than what is kept.</summary>
    public const int MaxPickedBytes = 12 * 1024 * 1024;

    public const int MaxPerOwner = 3;
    public const int LongestSidePixels = 1600;
    public const string AcceptList = ".png,.jpg,.jpeg,.webp,.gif,.pdf";

    /// <summary>The type of a file by what its first bytes say it is, or null when it is none of the kinds that may be attached.</summary>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8') return "image/gif";
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P') return "image/webp";
        if (data.Length >= 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-') return "application/pdf";
        return null;
    }

    /// <summary>
    /// Checks a picked file before anything is kept: a known kind, whose bytes are what the name says (a program renamed to .jpg is refused), not empty and not absurdly large.
    /// Returns the content type, taken from the bytes and never from what the browser claims.
    /// </summary>
    public static string Check(string fileName, byte[] data)
    {
        var byName = AttachmentRules.ContentTypeFor(fileName);
        if (data.Length == 0) throw new InvalidOperationException($"{fileName} is empty.");
        if (data.LongLength > MaxPickedBytes) throw new InvalidOperationException($"{fileName} is larger than {MaxPickedBytes / (1024 * 1024)} MB.");
        var actual = Sniff(data);
        if (actual is null || actual != byName) throw new InvalidOperationException($"{fileName} is not the kind of file its name says.");
        return actual;
    }

    public static bool IsImage(string? contentType) => contentType?.StartsWith("image/", StringComparison.Ordinal) == true;
}

/// <summary>Shrinks a photo so it can be kept and sent over a weak link: no side longer than 1600 pixels, saved as a JPEG. A PDF is left as it is.</summary>
public static class ImageShrinker
{
    private const int KeepAsIsBytes = 350_000;

    private static SKBitmap Decode(string fileName, byte[] data)
    {
        try { return SKBitmap.Decode(data) ?? throw new InvalidOperationException($"{fileName} cannot be read as a picture."); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new InvalidOperationException($"{fileName} cannot be read as a picture."); }   // a damaged file, or one that only looks like a picture
    }

    public static (byte[] Data, string FileName, string ContentType) Shrink(string fileName, string contentType, byte[] data)
    {
        if (!FileRules.IsImage(contentType)) return (data, fileName, contentType);

        using var bitmap = Decode(fileName, data);
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (contentType == "image/jpeg" && longest <= FileRules.LongestSidePixels && data.Length <= KeepAsIsBytes) return (data, fileName, contentType);

        foreach (var (side, quality) in new[] { (FileRules.LongestSidePixels, 80), (1200, 70), (900, 60), (600, 55) })
        {
            var scale = Math.Min(1d, (double)side / longest);
            var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.White);                                                      // a transparent picture is kept on white, since a JPEG has no transparency
            surface.Canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height));
            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            var bytes = encoded.ToArray();
            if (bytes.Length <= FileRules.MaxStoredBytes) return (bytes, Path.ChangeExtension(fileName, ".jpg"), "image/jpeg");
        }
        throw new InvalidOperationException($"{fileName} is too detailed to shrink small enough to send.");
    }
}

public record FileView(Guid Id, string FileName, string ContentType, long SizeBytes)
{
    public bool IsImage => FileRules.IsImage(ContentType);
}

/// <summary>
/// Files that belong to records of any module (see <see cref="FileAttachment"/>). The module that owns a file decides who may open it; this keeps and finds them.
/// A file is kept in the same save as the record that owns it, so the two stand or fall together.
/// </summary>
public class FileAttachmentService(IDbContextFactory<ChandleryDbContext> factory)
{
    /// <summary>The owner type of a file that belongs to a chat message.</summary>
    public const string ChatOwner = "ChatMessage";

    /// <summary>
    /// Adds the files to the caller's context, to be saved with the owning record. Each is checked, photos are shrunk, and the content type is taken from the bytes.
    /// Throws, adding nothing, when any file is refused.
    /// </summary>
    public static List<FileAttachment> Add(ChandleryDbContext db, Guid companyId, Guid? vesselId, string ownerType, Guid ownerId, bool keepAshore,
        IReadOnlyList<PendingAttachment> files, Guid? userId, string userName)
    {
        if (files.Count > FileRules.MaxPerOwner) throw new InvalidOperationException($"You can attach up to {FileRules.MaxPerOwner} files at a time.");
        var kept = new List<FileAttachment>();
        foreach (var file in files)
        {
            var type = FileRules.Check(file.FileName, file.Data);
            var (data, name, contentType) = ImageShrinker.Shrink(file.FileName, type, file.Data);
            if (data.Length > FileRules.MaxStoredBytes)
                throw new InvalidOperationException($"{file.FileName} is larger than {FileRules.MaxStoredBytes / 1_000_000} MB: a PDF has to be smaller to be sent.");
            kept.Add(new FileAttachment
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, OwnerType = ownerType, OwnerId = ownerId, KeepAshore = keepAshore,
                FileName = SafeName(name), ContentType = contentType, SizeBytes = data.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), Content = data,
                UploadedByUserId = userId, UploadedByName = userName, UploadedAt = DateTime.UtcNow, OwnerStage = db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office,
            });
        }
        db.FileAttachments.AddRange(kept);
        return kept;
    }

    /// <summary>The name without any path, and short enough to keep.</summary>
    public static string SafeName(string name)
    {
        var plain = Path.GetFileName(name.Replace('\\', '/')).Trim();
        if (plain.Length == 0) plain = "file";
        return plain.Length <= 120 ? plain : plain[..100] + Path.GetExtension(plain);
    }

    /// <summary>What is attached to each of these owners, without the bytes.</summary>
    public static async Task<Dictionary<Guid, List<FileView>>> ListAsync(ChandleryDbContext db, string ownerType, IEnumerable<Guid> ownerIds)
    {
        var ids = ownerIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.FileAttachments.AsNoTracking().Where(f => f.OwnerType == ownerType && ids.Contains(f.OwnerId))
            .Select(f => new { f.OwnerId, f.Id, f.FileName, f.ContentType, f.SizeBytes, f.UploadedAt }).ToListAsync();
        return rows.OrderBy(r => r.UploadedAt).ThenBy(r => r.Id).GroupBy(r => r.OwnerId).ToDictionary(g => g.Key, g => g.Select(r => new FileView(r.Id, r.FileName, r.ContentType, r.SizeBytes)).ToList());
    }

    /// <summary>One file with its bytes, whoever's company it is. The caller must have decided that the person may open it.</summary>
    public async Task<FileAttachment?> FindAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.FileAttachments.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == id);
    }
}
