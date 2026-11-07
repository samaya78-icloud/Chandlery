using System.Text;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>Where an order stands on its way to the supplier, in the words the screens use.</summary>
public record DispatchView(DateTime? ViaSystemAt, DateTime? EmailedAt, string EmailedTo, DateTime? AcknowledgedAt, string AcknowledgedBy)
{
    public string Summary =>
        AcknowledgedAt is not null ? "Acknowledged by the supplier"
        : EmailedAt is not null && ViaSystemAt is not null ? "Sent through the system and by email"
        : ViaSystemAt is not null ? "Sent through the system"
        : EmailedAt is not null ? "Sent by email"
        : "Not sent yet";

    public static DispatchView None { get; } = new(null, null, "", null, "");
}

/// <summary>
/// Dispatch of purchase orders. Through the system: an approved order is available to the supplier in the portal at once, which is
/// recorded when it happens. By email: the vessel prepares a message (an .eml file for its own mail program) with the order attached
/// and the office in copy; Chandlery never sends it, it only records that the vessel prepared it. The supplier can acknowledge.
/// </summary>
public class DispatchService(IDbContextFactory<ChandleryDbContext> factory, PoDocumentService documents)
{
    // ---- recording ----

    /// <summary>Marks the order as made available to the supplier now. Adds to the caller's context, so it is saved with the caller's change.</summary>
    public static async Task TouchViaSystemAsync(ChandleryDbContext db, PurchaseOrder po, bool onlyIfMissing = false)
    {
        var row = await db.PoDispatches.FirstOrDefaultAsync(d => d.PurchaseOrderId == po.Id);
        if (row is null)
        {
            db.PoDispatches.Add(new PoDispatch { CompanyId = po.CompanyId, PurchaseOrderId = po.Id, ViaSystemAt = DateTime.UtcNow });
            return;
        }
        if (onlyIfMissing && row.ViaSystemAt is not null) return;
        row.ViaSystemAt = DateTime.UtcNow;
    }

    /// <summary>The supplier confirms it has the order.</summary>
    public async Task AcknowledgeAsync(Guid supplierId, Guid poId, string byName)
    {
        await using var db = await factory.CreateDbContextAsync();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == poId && p.SupplierId == supplierId && p.Status == PoStatus.Approved)
            ?? throw new InvalidOperationException("Purchase order not found.");
        var row = await db.PoDispatches.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.PurchaseOrderId == poId);
        if (row is null)
        {
            row = new PoDispatch { CompanyId = po.CompanyId, PurchaseOrderId = poId };
            db.PoDispatches.Add(row);
        }
        if (row.AcknowledgedAt is not null) return;
        row.AcknowledgedAt = DateTime.UtcNow;
        row.AcknowledgedByName = byName;
        await db.SaveChangesAsync();
    }

    /// <summary>The office makes an order available to the supplier again (for example after the supplier lost it).</summary>
    public async Task ResendAsync(Guid companyId, Guid poId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("Only the office can do this.");
        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId)
            ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Status != PoStatus.Approved) throw new InvalidOperationException("Only an approved purchase order can be sent to the supplier.");
        await TouchViaSystemAsync(db, po);
        AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, poId, actor, "SENT the order to the supplier through the system again");
        await db.SaveChangesAsync();
    }

    /// <summary>The vessel notes that it prepared an email to the supplier. Only for orders the vessel owns; the record travels up with the order.</summary>
    public async Task RecordEmailedAsync(Guid companyId, Guid poId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId)
            ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Status != PoStatus.Approved) throw new InvalidOperationException("Only an approved purchase order can be sent to the supplier.");
        if (db.Node.SyncEnabled && po.OwnerStage != (db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office))
            throw new InvalidOperationException("This order is handled by the other side, which sends it to the supplier.");
        var supplier = await db.Suppliers.FirstAsync(s => s.Id == po.SupplierId);
        po.EmailedAt = DateTime.UtcNow;
        po.EmailedTo = supplier.ContactEmail;
        AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, poId, actor, $"PREPARED an email with the order for {supplier.ContactEmail}");
        await db.SaveChangesAsync();
    }

    // ---- reading ----

    public async Task<DispatchView> GetAsync(Guid companyId, Guid poId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ViewAsync(db, poId);
    }

    /// <summary>For the supplier's order list: where each of its orders stands.</summary>
    public async Task<Dictionary<Guid, DispatchView>> ForSupplierAsync(Guid supplierId, IEnumerable<Guid> poIds)
    {
        var ids = poIds.ToList();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.PoDispatches.IgnoreQueryFilters().AsNoTracking().Where(d => ids.Contains(d.PurchaseOrderId)).ToListAsync();
        return rows.ToDictionary(r => r.PurchaseOrderId, r => new DispatchView(r.ViaSystemAt, null, "", r.AcknowledgedAt, r.AcknowledgedByName));
    }

    public static async Task<DispatchView> ViewAsync(ChandleryDbContext db, Guid poId)
    {
        var po = await db.PurchaseOrders.AsNoTracking().Where(p => p.Id == poId).Select(p => new { p.EmailedAt, p.EmailedTo }).FirstOrDefaultAsync();
        var row = await db.PoDispatches.AsNoTracking().FirstOrDefaultAsync(d => d.PurchaseOrderId == poId);
        return new DispatchView(row?.ViaSystemAt, po?.EmailedAt, po?.EmailedTo ?? "", row?.AcknowledgedAt, row?.AcknowledgedByName ?? "");
    }

    // ---- the email ----

    /// <summary>
    /// A ready-to-open email message (.eml): to the supplier, the office in copy, the order as a PDF attachment. Marked unsent so mail
    /// programs open it as a draft for the sender to review and send. Nothing is sent from here.
    /// </summary>
    public async Task<(string FileName, byte[] Bytes)?> BuildEmlAsync(Guid companyId, Guid poId)
    {
        var doc = await documents.GetForCompanyAsync(companyId, poId);
        if (doc is null) return null;
        var pdf = PoPdf.Build(doc);
        var fileName = doc.Po.Status == PoStatus.Approved ? $"{doc.Po.PoNumber}.pdf" : $"{doc.Po.PoNumber}-DRAFT.pdf";

        var subject = $"Purchase order {doc.Po.PoNumber}" + (string.IsNullOrWhiteSpace(doc.Vessel) ? "" : $" for {doc.Vessel}");
        var body = $"Dear {doc.Supplier.Name},\r\n\r\nPlease find attached purchase order {doc.Po.PoNumber}" +
                   (string.IsNullOrWhiteSpace(doc.Vessel) ? "" : $" for {doc.Vessel}") + ".\r\n\r\n" +
                   "Please confirm receipt and the expected delivery.\r\n\r\nBest regards,\r\n" + doc.Company.Name + "\r\n";

        const string boundary = "=_chandlery_po_boundary";
        var sb = new StringBuilder();
        sb.Append("X-Unsent: 1\r\n");
        sb.Append($"To: {doc.Supplier.ContactEmail}\r\n");
        if (!string.IsNullOrWhiteSpace(doc.Company.ContactEmail)) sb.Append($"Cc: {doc.Company.ContactEmail}\r\n");
        sb.Append($"Subject: {subject}\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n{body}\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: application/pdf; name=\"{fileName}\"\r\nContent-Transfer-Encoding: base64\r\n");
        sb.Append($"Content-Disposition: attachment; filename=\"{fileName}\"\r\n\r\n");
        var base64 = Convert.ToBase64String(pdf);
        for (var i = 0; i < base64.Length; i += 76) sb.Append(base64, i, Math.Min(76, base64.Length - i)).Append("\r\n");
        sb.Append($"--{boundary}--\r\n");
        return ($"{doc.Po.PoNumber}.eml", new UTF8Encoding(false).GetBytes(sb.ToString()));
    }
}
