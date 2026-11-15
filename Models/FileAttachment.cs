namespace Chandlery.Models;

/// <summary>
/// A file (a photo or a PDF) that belongs to a record of any module: a chat message first, incidents and permits when they want one. Unlike a requisition's
/// <see cref="Attachment"/>, the bytes are kept in the record itself, because the sync engine carries records but not yet files: this is what lets a photo taken
/// onboard reach the office and the other way round. The size is therefore capped small (see <c>FileRules</c>) and photos are shrunk before they are kept.
/// Written once and never changed. A file whose owner stays ashore (an office-only message) never travels to a vessel.
/// </summary>
public class FileAttachment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>The vessel the owner belongs to, copied when the file is kept. Null for an owner with no vessel; such files never leave the office.</summary>
    public Guid? VesselId { get; set; }

    /// <summary>What owns the file (for example <c>ChatMessage</c>) and its id. Who may open the file is decided by the owner's own rules.</summary>
    public string OwnerType { get; set; } = "";
    public Guid OwnerId { get; set; }

    /// <summary>True when the owner never leaves the office, so neither does the file.</summary>
    public bool KeepAshore { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of <see cref="Content"/> in lower case, so a copy that arrives damaged can be told from the original.</summary>
    public string Sha256 { get; set; } = "";
    public byte[] Content { get; set; } = [];

    public Guid? UploadedByUserId { get; set; }
    public string UploadedByName { get; set; } = "";
    public DateTime UploadedAt { get; set; }

    /// <summary>The side that kept the file (see <see cref="SyncStages"/>).</summary>
    public string OwnerStage { get; set; } = SyncStages.Office;
}