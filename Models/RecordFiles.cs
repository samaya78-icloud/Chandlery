namespace Chandlery.Models;

/// <summary>
/// A photo or a statement kept with a safety record (an incident report or a permit). The record is a part of the document it belongs to, so it travels with it; the file itself
/// stays on the device that took it until the sync engine gets its file transfer (spec 03, phase 2). Until then the other side sees the record and says where the file is held.
/// </summary>
public abstract class RecordFile
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file in hex: what the file transfer will match on, and a way to show a copy is the one that was attached.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Where the file sits in the file store of the device that took it. It means nothing on any other device.</summary>
    public string StoredKey { get; set; } = "";

    /// <summary>What the file shows or says, in a few words.</summary>
    public string Caption { get; set; } = "";
    public string ByName { get; set; } = "";
    public Guid? ByCrewId { get; set; }
    public DateTime At { get; set; }

    /// <summary>"Vessel" or "Office": which side holds the file.</summary>
    public string HeldBy { get; set; } = "";
}

public class IncidentFile : RecordFile
{
    public Guid IncidentId { get; set; }
}

public class PermitFile : RecordFile
{
    public Guid PermitId { get; set; }
}
