namespace Chandlery.Models;

/// <summary>
/// A file (image or PDF) attached to a requisition, either to the requisition as a whole (RequisitionLineId null) or to one of its lines.
/// Files are internal unless <see cref="ShareWithSuppliers"/> is set, in which case the suppliers invited to an RFQ made from the
/// requisition can see them while quoting.
/// </summary>
public class Attachment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid RequisitionId { get; set; }

    /// <summary>The requisition line this file belongs to; null for a file on the requisition header.</summary>
    public Guid? RequisitionLineId { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StoredKey { get; set; } = "";

    public bool ShareWithSuppliers { get; set; }
    public string UploadedByName { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}
