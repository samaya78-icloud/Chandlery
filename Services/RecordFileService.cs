using System.Security.Cryptography;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public static class RecordFileRules
{
    public const int MaxPerRecord = 10;

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Which side this device is, as written on a file record.</summary>
    public static string ThisSide => NodeContext.Current.IsVessel ? "Vessel" : "Office";
}

/// <summary>A file kept with a record, as a screen shows it: <see cref="Here"/> says whether this device holds the bytes, so the screen can offer it or say where it is.</summary>
public sealed record RecordFileView(Guid Id, string FileName, string ContentType, long SizeBytes, string Caption, string ByName, DateTime At, string HeldBy, bool Here)
{
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal);

    /// <param name="side">"Vessel" or "Office": which side the device showing it is.</param>
    public static RecordFileView Of(RecordFile f, string side) => new(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.Caption, f.ByName, f.At, f.HeldBy, f.HeldBy == side);
}

/// <summary>
/// Photos and statements kept with an incident report or a permit. The record is part of the document and travels with it; the bytes stay in the file store of the device that took
/// them, until the sync engine gets its file transfer. Who may add, and who may see, is decided by the incident and permit services; this only stores and opens the bytes.
/// </summary>
public class RecordFileService(IFileStore files, IncidentService incidents, PermitService permits, IDbContextFactory<ChandleryDbContext> factory, CertificateService? certificates = null)
{
    private async Task<string> SideAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return db.Node.IsVessel ? "Vessel" : "Office";
    }

    private static T Describe<T>(PendingAttachment upload, string key, string caption) where T : RecordFile, new() => new()
    {
        FileName = Path.GetFileName(upload.FileName), ContentType = AttachmentRules.ContentTypeFor(upload.FileName), SizeBytes = upload.Data.LongLength,
        Sha256 = RecordFileRules.Sha256Hex(upload.Data), StoredKey = key, Caption = (caption ?? "").Trim(),
    };

    private async Task<T> StoreAsync<T>(Guid companyId, PendingAttachment upload, string caption, Func<T, Task<T>> add) where T : RecordFile, new()
    {
        AttachmentRules.Validate(upload);
        if ((caption ?? "").Trim().Length > 300) throw new InvalidOperationException("The caption is longer than 300 characters.");
        await using var stream = new MemoryStream(upload.Data);
        var key = await files.SaveAsync(companyId, stream);
        try { return await add(Describe<T>(upload, key, caption!)); }
        catch { files.Delete(key); throw; }                                    // a refused file leaves nothing behind
    }

    public Task<IncidentFile> AddToIncidentAsync(Guid companyId, Guid incidentId, Actor actor, PendingAttachment upload, string caption = "")
        => StoreAsync<IncidentFile>(companyId, upload, caption, f => incidents.AddFileAsync(companyId, incidentId, actor, f));

    public Task<PermitFile> AddToPermitAsync(Guid companyId, Guid permitId, Actor actor, PendingAttachment upload, string caption = "")
        => StoreAsync<PermitFile>(companyId, upload, caption, f => permits.AddFileAsync(companyId, permitId, actor, f));

    /// <summary>A scan of a certificate or survey: kept as an event of its own, by either side.</summary>
    public Task<CertificateFile> AddToCertificateAsync(Guid companyId, Guid certificateId, Actor actor, PendingAttachment upload, string caption = "")
        => StoreAsync<CertificateFile>(companyId, upload, caption, f => (certificates ?? throw new InvalidOperationException("Certificates are not available.")).AddFileAsync(companyId, certificateId, actor, f));

    /// <summary>
    /// A file, if the person may see the record it belongs to. The content is null when the bytes are not on this device: the vessel took the photo and it has not been sent ashore.
    /// </summary>
    public async Task<(RecordFile File, Stream? Content)?> OpenAsync(Guid companyId, string kind, Guid ownerId, Guid fileId, Actor actor)
    {
        RecordFile? file;
        if (kind == "incident") file = (await incidents.GetAsync(companyId, ownerId, actor))?.Incident.Files.FirstOrDefault(f => f.Id == fileId);
        else if (kind == "permit") file = (await permits.GetAsync(companyId, ownerId, actor))?.Permit.Files.FirstOrDefault(f => f.Id == fileId);
        else if (kind == "certificate" && certificates is not null) file = (await certificates.GetAsync(companyId, ownerId))?.Files.FirstOrDefault(f => f.Id == fileId);
        else return null;
        if (file is null) return null;
        return (file, file.HeldBy == await SideAsync() ? files.OpenRead(file.StoredKey) : null);
    }
}
