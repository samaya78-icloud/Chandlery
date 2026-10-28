using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

// ---- Requests ----

/// <summary>What a supplier fills in to invoice a purchase order.</summary>
public class SubmitInvoice
{
    public int PurchaseOrderId { get; set; }
    public string SupplierInvoiceRef { get; set; } = "";
    public DateTime? InvoiceDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime? DueDate { get; set; }
    public decimal InvoiceAmount { get; set; }
    public decimal AdditionalItemsCost { get; set; }
    public decimal AdditionalDeliveryCost { get; set; }
    public decimal DiscountOrCredit { get; set; }
    public int BankAccountId { get; set; }
    public string Remarks { get; set; } = "";
    public string VarianceReason { get; set; } = "";
    public List<int> DeliveryOrderIds { get; set; } = new();
}

/// <summary>What the company fills in to enter an invoice that has no purchase order.</summary>
public class NewSpotInvoice
{
    public int SupplierId { get; set; }
    public string SupplierInvoiceRef { get; set; } = "";
    public DateTime? InvoiceDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime? DueDate { get; set; }
    public string Currency { get; set; } = "USD";
    public int? DepartmentId { get; set; }
    public int? VesselId { get; set; }
    public string CostCentre { get; set; } = "";
    public string Remarks { get; set; } = "";
    public int? TaxCodeId { get; set; }
    public int? BankAccountId { get; set; }
    public decimal AdditionalItemsCost { get; set; }
    public decimal AdditionalDeliveryCost { get; set; }
    public decimal DiscountOrCredit { get; set; }
    /// <summary>Catalog items (limited to the department's categories) with quantity, price and discount.</summary>
    public List<NewRfqLine> Lines { get; set; } = new();
}

public record UploadedFile(string FileName, string ContentType, byte[] Data);

// ---- Views ----

public record InvoiceListRow(Invoice Inv, string SupplierName, string PoNumber);
public record InvoiceApprovalRow(InvoiceApproval Approval, string ApproverName, string DecidedByName);
public record PendingInvoiceRow(Invoice Inv, string Supplier, int Level);

/// <summary>The three-way comparison: what the PO said, what was delivered, what the invoice asks for.</summary>
public record InvoiceMatch(decimal PoTotal, decimal DeliveredValue, decimal ExpectedCharges, decimal Variance)
{
    public decimal Expected => DeliveredValue + ExpectedCharges;
    public bool HasVariance => Math.Abs(Variance) > 0.01m;
}

public class InvoiceInfo
{
    public Invoice Invoice { get; init; } = default!;
    public Supplier Supplier { get; init; } = default!;
    public PurchaseOrder? Po { get; init; }
    public List<DeliveryOrder> Deliveries { get; init; } = new();
    public List<InvoiceDocument> Documents { get; init; } = new();
    public List<InvoiceApprovalRow> Approvals { get; init; } = new();
    public SupplierBankAccount? Bank { get; init; }
    public TaxCode? Tax { get; init; }
    public string Department { get; init; } = "";
    public string Vessel { get; init; } = "";
    public InvoiceMatch? Match { get; init; }
    public Dictionary<int, Item> Items { get; init; } = new();
}

/// <summary>A PO a supplier can invoice: approved, with deliveries not yet on an invoice.</summary>
public record InvoiceablePo(PurchaseOrder Po, int OpenDeliveries, decimal OpenValue);

public record SubmitContext(PurchaseOrder Po, List<DeliveryOrder> Deliveries, List<SupplierBankAccount> Banks, Invoice? Existing, List<InvoiceDocument> ExistingDocs, Dictionary<int, Item> Items);

public class InvoiceService(IDbContextFactory<ChandleryDbContext> factory, IFileStore files)
{
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private static readonly string[] AllowedExtensions = { ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".xlsx", ".docx" };

    /// <summary>What a supplier may upload as their invoice: a PDF or a picture, so it can be previewed (and read) later.</summary>
    private static readonly string[] VisualExtensions = { ".pdf", ".png", ".jpg", ".jpeg", ".webp" };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    };

    /// <summary>The type is decided by the extension, never by what the browser says, because stored files can be shown inline.</summary>
    private static string ContentTypeFor(string fileName) => ContentTypes.GetValueOrDefault(Path.GetExtension(fileName)) ?? "application/octet-stream";

    // ---- Calculations ----

    /// <summary>The value of the goods on the given deliveries at the PO's prices, after the PO's header discount.</summary>
    public static decimal DeliveredValue(PurchaseOrder po, IEnumerable<DeliveryOrder> deliveries)
    {
        var received = deliveries.SelectMany(d => d.Lines).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var subtotal = po.Lines.Sum(l => Money.LineNet(received.GetValueOrDefault(l.ItemId), l.UnitPrice, l.DiscountPct));
        return subtotal - Money.HeaderDiscount(subtotal, po.DiscountPct);
    }

    /// <summary>The PO's additional charges are expected on its first live invoice only.</summary>
    private static async Task<decimal> ExpectedChargesAsync(ChandleryDbContext db, PurchaseOrder po, int thisInvoiceId)
    {
        var earlier = await db.Invoices.AnyAsync(i => i.PurchaseOrderId == po.Id && i.Status != InvoiceStatus.Rejected
                                                      && i.Id != thisInvoiceId && (thisInvoiceId == 0 || i.Id < thisInvoiceId));
        return earlier ? 0m : po.AdditionalCharges;
    }

    private static async Task<InvoiceMatch> MatchAsync(ChandleryDbContext db, PurchaseOrder po, IEnumerable<DeliveryOrder> deliveries, Invoice? invoice)
    {
        var delivered = DeliveredValue(po, deliveries);
        var charges = await ExpectedChargesAsync(db, po, invoice?.Id ?? 0);
        var variance = (invoice?.TotalExclTax ?? 0m) - (delivered + charges);
        return new InvoiceMatch(po.Total, delivered, charges, variance);
    }

    private static async Task<string> NextNumberAsync(ChandleryDbContext db) =>
        $"INV-{1001 + (await db.Invoices.IgnoreQueryFilters().MaxAsync(i => (int?)i.Id) ?? 0)}";

    // ---- Company: lists and detail ----

    public async Task<GridResult<InvoiceListRow>> GetPageAsync(int companyId, GridRequest req)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var q = db.Invoices.Where(i => i.CompanyId == companyId);
        q = await FilterAsync(db, q, req);
        return await PageAsync(db, q, req);
    }

    public async Task<GridResult<InvoiceListRow>> GetSupplierPageAsync(int supplierId, GridRequest req)
    {
        await using var db = await factory.CreateDbContextAsync();
        var q = db.Invoices.Where(i => i.SupplierId == supplierId);
        q = await FilterAsync(db, q, req);
        return await PageAsync(db, q, req);
    }

    private static async Task<IQueryable<Invoice>> FilterAsync(ChandleryDbContext db, IQueryable<Invoice> q, GridRequest req)
    {
        if (req.Like("invoice") is { } numberLike) q = q.Where(i => EF.Functions.ILike(i.InvoiceNumber, numberLike));
        if (req.Like("ref") is { } refLike) q = q.Where(i => EF.Functions.ILike(i.SupplierInvoiceRef, refLike));
        if (Enum.TryParse<InvoiceStatus>(req.Select("status"), out var status)) q = q.Where(i => i.Status == status);
        if (Enum.TryParse<InvoiceKind>(req.Select("kind"), out var kind)) q = q.Where(i => i.Kind == kind);
        if (req.FromUtc("date") is { } from) q = q.Where(i => i.InvoiceDate >= from);
        if (req.ToUtcExclusive("date") is { } to) q = q.Where(i => i.InvoiceDate < to);
        if (req.Like("supplier") is { } supplierLike)
        {
            var ids = await db.Suppliers.Where(s => EF.Functions.ILike(s.Name, supplierLike)).Select(s => s.Id).ToListAsync();
            q = q.Where(i => ids.Contains(i.SupplierId));
        }
        if (req.Like("po") is { } poLike)
        {
            var ids = await db.PurchaseOrders.IgnoreQueryFilters().Where(p => EF.Functions.ILike(p.PoNumber, poLike)).Select(p => p.Id).ToListAsync();
            q = q.Where(i => i.PurchaseOrderId != null && ids.Contains(i.PurchaseOrderId.Value));
        }
        return q;
    }

    private static async Task<GridResult<InvoiceListRow>> PageAsync(ChandleryDbContext db, IQueryable<Invoice> q, GridRequest req)
    {
        var total = await q.CountAsync();
        var desc = req.SortDesc;
        IOrderedQueryable<Invoice> ordered = req.SortKey switch
        {
            "invoice" => GridSort.By(q, i => i.InvoiceNumber, desc),
            "ref" => GridSort.By(q, i => i.SupplierInvoiceRef, desc),
            "supplier" => GridSort.By(q, i => db.Suppliers.Where(s => s.Id == i.SupplierId).Select(s => s.Name).FirstOrDefault(), desc),
            "po" => GridSort.By(q, i => db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == i.PurchaseOrderId).Select(p => p.PoNumber).FirstOrDefault(), desc),
            "kind" => GridSort.By(q, i => i.Kind, desc),
            "date" => GridSort.By(q, i => i.InvoiceDate, desc),
            "status" => GridSort.By(q, i => i.Status, desc),
            _ => q.OrderByDescending(i => i.SubmittedAt)
        };
        var rows = await ordered.ThenByDescending(i => i.Id).Skip(req.Skip).Take(req.PageSize).ToListAsync();

        var supplierIds = rows.Select(r => r.SupplierId).Distinct().ToList();
        var poIds = rows.Where(r => r.PurchaseOrderId.HasValue).Select(r => r.PurchaseOrderId!.Value).Distinct().ToList();
        var suppliers = await db.Suppliers.Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var pos = await db.PurchaseOrders.IgnoreQueryFilters().Where(p => poIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PoNumber);
        return new GridResult<InvoiceListRow>(
            rows.Select(i => new InvoiceListRow(i, suppliers.GetValueOrDefault(i.SupplierId) ?? "",
                i.PurchaseOrderId.HasValue ? pos.GetValueOrDefault(i.PurchaseOrderId.Value) ?? "" : "")).ToList(), total);
    }

    public async Task<int> GetCountAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Invoices.CountAsync(i => i.CompanyId == companyId);
    }

    public async Task<int> GetSupplierCountAsync(int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Invoices.CountAsync(i => i.SupplierId == supplierId);
    }

    /// <summary>Everything the invoice page shows. Pass companyId for the buyer's view or supplierId for the supplier's view.</summary>
    public async Task<InvoiceInfo?> GetDetailAsync(int invoiceId, int? companyId, int? supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId
            && (companyId == null || i.CompanyId == companyId) && (supplierId == null || i.SupplierId == supplierId));
        if (invoice is null) return null;

        var supplier = await db.Suppliers.FirstAsync(s => s.Id == invoice.SupplierId);
        var po = invoice.PurchaseOrderId.HasValue
            ? await db.PurchaseOrders.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == invoice.PurchaseOrderId.Value) : null;
        var deliveries = await db.DeliveryOrders.IgnoreQueryFilters().Where(d => d.InvoiceId == invoice.Id).OrderBy(d => d.ReceivedDate).ToListAsync();
        var docs = await db.InvoiceDocuments.IgnoreQueryFilters().Where(d => d.InvoiceId == invoice.Id).OrderBy(d => d.Id).ToListAsync();
        var bank = invoice.BankAccountId.HasValue ? await db.SupplierBankAccounts.FirstOrDefaultAsync(b => b.Id == invoice.BankAccountId.Value) : null;
        var tax = invoice.TaxCodeId.HasValue ? await db.TaxCodes.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == invoice.TaxCodeId.Value) : null;
        var department = invoice.DepartmentId.HasValue
            ? await db.Departments.IgnoreQueryFilters().Where(d => d.Id == invoice.DepartmentId.Value).Select(d => d.Name).FirstOrDefaultAsync() : null;
        var vessel = invoice.VesselId.HasValue
            ? await db.Vessels.IgnoreQueryFilters().Where(v => v.Id == invoice.VesselId.Value).Select(v => v.Name).FirstOrDefaultAsync() : null;

        var approvalRows = await db.InvoiceApprovals.IgnoreQueryFilters().Where(a => a.InvoiceId == invoice.Id).OrderBy(a => a.Level).ThenBy(a => a.Id).ToListAsync();
        var userIds = approvalRows.Select(a => a.ApproverUserId).Concat(approvalRows.Where(a => a.DecidedByUserId.HasValue).Select(a => a.DecidedByUserId!.Value)).Distinct().ToList();
        var names = await db.Users.IgnoreQueryFilters().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        InvoiceMatch? match = po is null ? null : await MatchAsync(db, po, deliveries, invoice);
        var itemIds = (po?.Lines.Select(l => l.ItemId) ?? Enumerable.Empty<int>()).Distinct().ToList();
        var items = await db.Items.IgnoreQueryFilters().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        return new InvoiceInfo
        {
            Invoice = invoice, Supplier = supplier, Po = po, Deliveries = deliveries, Documents = docs, Bank = bank, Tax = tax,
            Department = department ?? "", Vessel = vessel ?? "", Match = match, Items = items,
            Approvals = approvalRows.Select(a => new InvoiceApprovalRow(a, names.GetValueOrDefault(a.ApproverUserId) ?? "?",
                a.DecidedByUserId.HasValue ? names.GetValueOrDefault(a.DecidedByUserId.Value) ?? "?" : "")).ToList()
        };
    }

    // ---- Supplier: submitting ----

    /// <summary>Approved POs of this supplier that have deliveries which no invoice covers yet.</summary>
    public async Task<List<InvoiceablePo>> GetInvoiceablePosAsync(int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var open = await db.DeliveryOrders.Where(d => d.SupplierId == supplierId && d.InvoiceId == null).ToListAsync();
        if (open.Count == 0) return new();
        var poIds = open.Select(d => d.PurchaseOrderId).Distinct().ToList();
        var pos = await db.PurchaseOrders.Where(p => poIds.Contains(p.Id) && p.SupplierId == supplierId && p.Status == PoStatus.Approved).ToListAsync();
        return pos.OrderByDescending(p => p.CreatedDate).Select(p =>
        {
            var mine = open.Where(d => d.PurchaseOrderId == p.Id).ToList();
            return new InvoiceablePo(p, mine.Count, DeliveredValue(p, mine));
        }).ToList();
    }

    /// <summary>What the submit form needs. When <paramref name="invoiceId"/> is given, the (rejected) invoice is being corrected.</summary>
    public async Task<SubmitContext?> GetSubmitContextAsync(int supplierId, int poId, int? invoiceId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.SupplierId == supplierId && p.Status == PoStatus.Approved);
        if (po is null) return null;

        Invoice? existing = null;
        if (invoiceId.HasValue)
        {
            existing = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId.Value && i.SupplierId == supplierId
                && i.PurchaseOrderId == poId && i.Status == InvoiceStatus.Rejected);
            if (existing is null) return null;
        }

        var deliveries = await db.DeliveryOrders.Where(d => d.PurchaseOrderId == poId && d.SupplierId == supplierId && d.InvoiceId == null)
            .OrderBy(d => d.ReceivedDate).ToListAsync();
        var banks = await db.SupplierBankAccounts.Where(b => b.SupplierId == supplierId).OrderBy(b => b.BankName).ToListAsync();
        var docs = existing is null ? new List<InvoiceDocument>() : await db.InvoiceDocuments.Where(d => d.InvoiceId == existing.Id).ToListAsync();
        var itemIds = po.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        return new SubmitContext(po, deliveries, banks, existing, docs, items);
    }

    public async Task<Invoice> SubmitAsync(int supplierId, SubmitInvoice request, List<UploadedFile> uploads, Actor actor, int? resubmitInvoiceId = null)
    {
        await using var db = await factory.CreateDbContextAsync();

        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == request.PurchaseOrderId && p.SupplierId == supplierId && p.Status == PoStatus.Approved)
            ?? throw new InvalidOperationException("That purchase order is not available for invoicing.");
        var companyId = po.CompanyId;

        Invoice? invoice = null;
        if (resubmitInvoiceId.HasValue)
            invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == resubmitInvoiceId.Value && i.SupplierId == supplierId
                    && i.PurchaseOrderId == po.Id && i.Status == InvoiceStatus.Rejected)
                ?? throw new InvalidOperationException("Only a rejected invoice can be corrected and sent again.");

        var reference = request.SupplierInvoiceRef.Trim();
        if (reference == "") throw new InvalidOperationException("Enter your invoice number.");
        if (!request.InvoiceDate.HasValue) throw new InvalidOperationException("Enter the invoice date.");
        if (request.InvoiceDate.Value.Date > DateTime.UtcNow.Date.AddDays(1)) throw new InvalidOperationException("The invoice date cannot be in the future.");
        if (request.DueDate.HasValue && request.DueDate.Value.Date < request.InvoiceDate.Value.Date) throw new InvalidOperationException("The due date cannot be before the invoice date.");
        if (request.InvoiceAmount < 0 || request.AdditionalItemsCost < 0 || request.AdditionalDeliveryCost < 0 || request.DiscountOrCredit < 0)
            throw new InvalidOperationException("Amounts cannot be negative.");

        var excludeId = invoice?.Id ?? 0;
        if (await db.Invoices.AnyAsync(i => i.CompanyId == companyId && i.SupplierId == supplierId && i.SupplierInvoiceRef == reference
                                            && i.Status != InvoiceStatus.Rejected && i.Id != excludeId))
            throw new InvalidOperationException($"You have already sent an invoice numbered {reference} to this buyer.");

        var bank = await db.SupplierBankAccounts.FirstOrDefaultAsync(b => b.Id == request.BankAccountId && b.SupplierId == supplierId)
            ?? throw new InvalidOperationException("Choose the bank account to be paid into (add one on your profile first).");

        if (request.DeliveryOrderIds.Count == 0) throw new InvalidOperationException("Tick the deliveries this invoice covers.");
        var deliveries = await db.DeliveryOrders.Where(d => request.DeliveryOrderIds.Contains(d.Id) && d.PurchaseOrderId == po.Id && d.SupplierId == supplierId
                                                            && (d.InvoiceId == null || d.InvoiceId == excludeId)).ToListAsync();
        if (deliveries.Count != request.DeliveryOrderIds.Distinct().Count())
            throw new InvalidOperationException("One of the deliveries is already on another invoice.");

        var existingDocs = invoice is null ? 0 : await db.InvoiceDocuments.CountAsync(d => d.InvoiceId == invoice.Id);
        if (uploads.Count + existingDocs == 0) throw new InvalidOperationException("Attach at least one supporting document, such as your invoice as a PDF.");
        foreach (var upload in uploads) ValidateUpload(upload, VisualExtensions);

        if (invoice is null)
        {
            invoice = new Invoice
            {
                CompanyId = companyId, InvoiceNumber = await NextNumberAsync(db), Kind = InvoiceKind.PoInvoice,
                PurchaseOrderId = po.Id, SupplierId = supplierId
            };
            db.Invoices.Add(invoice);
        }

        invoice.SupplierInvoiceRef = reference;
        invoice.InvoiceDate = DateTime.SpecifyKind(request.InvoiceDate.Value.Date, DateTimeKind.Utc);
        invoice.DueDate = request.DueDate.HasValue ? DateTime.SpecifyKind(request.DueDate.Value.Date, DateTimeKind.Utc) : null;
        invoice.Currency = po.Currency;
        invoice.InvoiceAmount = request.InvoiceAmount;
        invoice.AdditionalItemsCost = request.AdditionalItemsCost;
        invoice.AdditionalDeliveryCost = request.AdditionalDeliveryCost;
        invoice.DiscountOrCredit = request.DiscountOrCredit;
        invoice.BankAccountId = bank.Id;
        invoice.DepartmentId = po.DepartmentId;
        invoice.VesselId = po.VesselId;
        invoice.Remarks = request.Remarks.Trim();
        invoice.VarianceReason = request.VarianceReason.Trim();
        invoice.RejectionReason = "";
        invoice.Status = InvoiceStatus.Submitted;
        invoice.SubmittedByName = actor.Name;
        invoice.SubmittedAt = DateTime.UtcNow;
        invoice.VerifiedByName = "";
        invoice.VerifiedAt = null;
        if (invoice.TotalExclTax <= 0) throw new InvalidOperationException("The invoice total must be above zero.");

        if (resubmitInvoiceId.HasValue)
            db.InvoiceApprovals.RemoveRange(await db.InvoiceApprovals.Where(a => a.InvoiceId == invoice.Id).ToListAsync());
        await db.SaveChangesAsync(); // gives a new invoice its id

        foreach (var d in deliveries) d.InvoiceId = invoice.Id;
        await SaveDocumentsAsync(db, invoice, uploads, actor);

        AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor,
            (resubmitInvoiceId.HasValue ? "RESUBMITTED after correction: " : "SUBMITTED by supplier: ") +
            $"{invoice.InvoiceNumber} against {po.PoNumber}, {deliveries.Count} delivery(ies), total {Money.Format(invoice.TotalExclTax, invoice.Currency)} before tax");
        await db.SaveChangesAsync();
        return invoice;
    }

    private static void ValidateUpload(UploadedFile upload, string[]? allowed = null)
    {
        if (upload.Data.Length == 0) throw new InvalidOperationException($"{upload.FileName} is empty.");
        if (upload.Data.LongLength > MaxFileBytes) throw new InvalidOperationException($"{upload.FileName} is larger than 20 MB.");
        if (!(allowed ?? AllowedExtensions).Contains(Path.GetExtension(upload.FileName).ToLowerInvariant()))
            throw new InvalidOperationException(allowed is null
                ? $"{upload.FileName}: only PDF, PNG, JPG, WEBP, XLSX and DOCX files are accepted."
                : $"{upload.FileName}: upload your invoice as a PDF or an image (PNG, JPG or WEBP).");
    }

    private async Task SaveDocumentsAsync(ChandleryDbContext db, Invoice invoice, List<UploadedFile> uploads, Actor actor)
    {
        foreach (var upload in uploads)
        {
            await using var stream = new MemoryStream(upload.Data);
            var key = await files.SaveAsync(invoice.CompanyId, stream);
            db.InvoiceDocuments.Add(new InvoiceDocument
            {
                CompanyId = invoice.CompanyId, InvoiceId = invoice.Id, FileName = Path.GetFileName(upload.FileName),
                ContentType = ContentTypeFor(upload.FileName),
                SizeBytes = upload.Data.LongLength, StoredKey = key, UploadedByName = actor.Name, UploadedAt = DateTime.UtcNow
            });
            AuditLog.Add(db, invoice.CompanyId, AuditEntityTypes.Invoice, invoice.Id, actor, $"ATTACHED {Path.GetFileName(upload.FileName)}");
        }
    }

    // ---- Documents ----

    /// <summary>Opens a document for download if the caller may see it: the buyer that owns the invoice or the supplier that sent it.</summary>
    public async Task<(InvoiceDocument Doc, Stream Content)?> OpenDocumentAsync(int documentId, int? companyId, int? supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var doc = await db.InvoiceDocuments.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return null;
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == doc.InvoiceId);
        if (invoice is null) return null;
        var allowed = (companyId.HasValue && invoice.CompanyId == companyId) || (supplierId.HasValue && invoice.SupplierId == supplierId);
        if (!allowed) return null;
        var content = files.OpenRead(doc.StoredKey);
        return content is null ? null : (doc, content);
    }

    // ---- Company: verify, reject, approve, finalize ----

    private static async Task ReleaseDeliveriesAsync(ChandleryDbContext db, int invoiceId)
    {
        foreach (var d in await db.DeliveryOrders.Where(d => d.InvoiceId == invoiceId).ToListAsync()) d.InvoiceId = null;
    }

    public async Task VerifyAsync(int companyId, int invoiceId, Actor actor, int? taxCodeId, string varianceReason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId)
            ?? throw new InvalidOperationException("Invoice not found.");
        if (invoice.Status != InvoiceStatus.Submitted) throw new InvalidOperationException("This invoice is not waiting for verification.");

        PurchaseOrder? po = null;
        if (invoice.PurchaseOrderId.HasValue)
        {
            po = await db.PurchaseOrders.FirstAsync(p => p.Id == invoice.PurchaseOrderId.Value);
            var deliveries = await db.DeliveryOrders.Where(d => d.InvoiceId == invoice.Id).ToListAsync();
            if (deliveries.Count == 0) throw new InvalidOperationException("No delivery is linked to this invoice.");
            var match = await MatchAsync(db, po, deliveries, invoice);
            if (match.HasVariance && string.IsNullOrWhiteSpace(varianceReason))
                throw new InvalidOperationException(
                    $"The invoice is {Money.Format(Math.Abs(match.Variance), invoice.Currency)} {(match.Variance > 0 ? "above" : "below")} the delivered value. Give the reason for the difference.");
        }

        if (taxCodeId.HasValue)
        {
            var tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == taxCodeId.Value && t.CompanyId == companyId)
                ?? throw new InvalidOperationException("Tax code not found.");
            invoice.TaxCodeId = tax.Id;
            invoice.TaxRatePct = tax.RatePct;
        }
        else
        {
            invoice.TaxCodeId = null;
            invoice.TaxRatePct = 0m;
        }

        invoice.VarianceReason = (varianceReason ?? "").Trim();
        invoice.VerifiedByName = actor.Name;
        invoice.VerifiedAt = DateTime.UtcNow;
        AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor,
            "VERIFIED" + (invoice.TaxCodeId.HasValue ? $" with tax {invoice.TaxRatePct:0.##}%" : " without tax") +
            (invoice.VarianceReason == "" ? "" : $"; variance reason: {invoice.VarianceReason}"));

        await RouteAsync(db, invoice, po?.DepartmentId ?? invoice.DepartmentId, actor);
        await db.SaveChangesAsync();
    }

    /// <summary>Creates the approval slots from the matrix by the invoice total before tax; no rules means approved at once.</summary>
    private static async Task RouteAsync(ChandleryDbContext db, Invoice invoice, int? departmentId, Actor actor)
    {
        var rules = await db.ApprovalRules.Where(r => r.CompanyId == invoice.CompanyId).ToListAsync();
        var comparable = await ApprovalEngine.ComparableAsync(db, invoice.CompanyId, invoice.Currency, DateTime.UtcNow, invoice.TotalExclTax);
        invoice.UsdRate = comparable.Rate;
        var chain = ApprovalEngine.ResolveChain(rules, departmentId, comparable.Amount);
        if (chain.Count == 0)
        {
            invoice.Status = InvoiceStatus.Approved;
            AuditLog.Add(db, invoice.CompanyId, AuditEntityTypes.Invoice, invoice.Id, actor, "APPROVED automatically: no approval rules apply to this invoice");
            return;
        }

        invoice.Status = InvoiceStatus.PendingApproval;
        foreach (var rule in chain)
            db.InvoiceApprovals.Add(new InvoiceApproval { CompanyId = invoice.CompanyId, InvoiceId = invoice.Id, Level = rule.Level, ApproverUserId = rule.ApproverUserId });

        var levelNames = await ApprovalLevelNames.LoadAsync(db, invoice.CompanyId);
        var userIds = chain.Select(r => r.ApproverUserId).Distinct().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var levels = chain.GroupBy(r => r.Level).OrderBy(g => g.Key)
            .Select(g => $"{levelNames.Name(g.Key)}: {string.Join(" or ", g.Select(r => names.GetValueOrDefault(r.ApproverUserId) ?? "?"))}");
        AuditLog.Add(db, invoice.CompanyId, AuditEntityTypes.Invoice, invoice.Id, actor,
            $"SENT for approval ({string.Join("; ", levels)})" + (comparable.Note == "" ? "" : $"; {comparable.Note}"));
    }

    /// <summary>Sends a submitted invoice back to the supplier with a reason; its deliveries are freed so it can be corrected.</summary>
    public async Task RejectAsync(int companyId, int invoiceId, Actor actor, string reason)
    {
        reason = (reason ?? "").Trim();
        if (reason == "") throw new InvalidOperationException("Give a reason for rejecting the invoice.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId)
            ?? throw new InvalidOperationException("Invoice not found.");
        if (invoice.Status != InvoiceStatus.Submitted) throw new InvalidOperationException("Only an invoice waiting for verification can be rejected here.");

        invoice.Status = InvoiceStatus.Rejected;
        invoice.RejectionReason = reason;
        await ReleaseDeliveriesAsync(db, invoice.Id);
        AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor, $"REJECTED at verification: {reason}");
        await db.SaveChangesAsync();
    }

    public async Task DecideAsync(int companyId, int invoiceId, Actor actor, bool approve, string comment)
    {
        if (actor.UserId is not { } userId) throw new InvalidOperationException("You must be signed in.");
        comment = (comment ?? "").Trim();
        if (!approve && comment == "") throw new InvalidOperationException("Give a reason when rejecting.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId)
            ?? throw new InvalidOperationException("Invoice not found.");
        if (invoice.Status != InvoiceStatus.PendingApproval) throw new InvalidOperationException("This invoice is no longer waiting for approval.");

        var levelNames = await ApprovalLevelNames.LoadAsync(db, companyId);
        var slots = await db.InvoiceApprovals.Where(a => a.InvoiceId == invoiceId).ToListAsync();
        var current = ApprovalEngine.CurrentLevel(slots.Select(a => (a.Level, a.Decision))) ?? throw new InvalidOperationException("Nothing is waiting for approval.");
        var mine = slots.FirstOrDefault(a => a.Level == current && a.ApproverUserId == userId && a.Decision == ApprovalDecision.Pending)
            ?? throw new InvalidOperationException("It is not your turn to approve this invoice.");

        mine.Decision = approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        mine.DecidedByUserId = userId;
        mine.DecidedAt = DateTime.UtcNow;
        mine.Comment = comment;

        var commentText = comment == "" ? "" : $": {comment}";
        if (!approve)
        {
            invoice.Status = InvoiceStatus.Rejected;
            invoice.RejectionReason = comment;
            await ReleaseDeliveriesAsync(db, invoice.Id);
            AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor, $"REJECTED ({levelNames.Name(current)}){commentText}");
        }
        else if (ApprovalEngine.CurrentLevel(slots.Select(a => (a.Level, a.Decision))) is null)
        {
            invoice.Status = InvoiceStatus.Approved;
            AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor, $"APPROVED ({levelNames.Name(current)}){commentText}; fully approved, ready to finalize");
        }
        else
        {
            AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor,
                $"APPROVED ({levelNames.Name(current)}){commentText}; now waiting for {levelNames.Name(ApprovalEngine.CurrentLevel(slots.Select(a => (a.Level, a.Decision)))!.Value)}");
        }
        await db.SaveChangesAsync();
    }

    public async Task FinalizeAsync(int companyId, int invoiceId, Actor actor, bool isCompanyAdmin)
    {
        if (!isCompanyAdmin) throw new InvalidOperationException("Only a company admin can finalize an invoice.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.CompanyId == companyId)
            ?? throw new InvalidOperationException("Invoice not found.");
        if (invoice.Status != InvoiceStatus.Approved) throw new InvalidOperationException("Only a fully approved invoice can be finalized.");

        invoice.Status = InvoiceStatus.Finalized;
        invoice.FinalizedByName = actor.Name;
        invoice.FinalizedAt = DateTime.UtcNow;
        AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor, "FINALIZED");
        await db.SaveChangesAsync();
    }

    public async Task<List<PendingInvoiceRow>> GetPendingForUserAsync(int companyId, int userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var ids = await db.InvoiceApprovals.Where(a => a.CompanyId == companyId && a.ApproverUserId == userId && a.Decision == ApprovalDecision.Pending)
            .Select(a => a.InvoiceId).Distinct().ToListAsync();
        if (ids.Count == 0) return new();

        var invoices = await db.Invoices.Where(i => ids.Contains(i.Id) && i.Status == InvoiceStatus.PendingApproval).ToListAsync();
        var slots = await db.InvoiceApprovals.Where(a => ids.Contains(a.InvoiceId)).ToListAsync();
        var suppliers = await db.Suppliers.Where(s => invoices.Select(i => i.SupplierId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);

        var rows = new List<PendingInvoiceRow>();
        foreach (var invoice in invoices.OrderBy(i => i.SubmittedAt))
        {
            var mine = slots.Where(a => a.InvoiceId == invoice.Id).ToList();
            var current = ApprovalEngine.CurrentLevel(mine.Select(a => (a.Level, a.Decision)));
            if (current is null || !mine.Any(a => a.Level == current && a.ApproverUserId == userId && a.Decision == ApprovalDecision.Pending)) continue;
            rows.Add(new PendingInvoiceRow(invoice, suppliers.GetValueOrDefault(invoice.SupplierId) ?? "", current.Value));
        }
        return rows;
    }

    /// <summary>Invoices waiting for the buyer: to verify, and to finalize (the dashboard queue).</summary>
    public async Task<(int ToVerify, int ToFinalize)> GetQueueCountsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var toVerify = await db.Invoices.CountAsync(i => i.CompanyId == companyId && i.Status == InvoiceStatus.Submitted);
        var toFinalize = await db.Invoices.CountAsync(i => i.CompanyId == companyId && i.Status == InvoiceStatus.Approved);
        return (toVerify, toFinalize);
    }

    // ---- Company: spot invoice ----

    public async Task<Invoice> CreateSpotAsync(int companyId, NewSpotInvoice request, List<UploadedFile> uploads, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId
                && db.CompanySuppliers.Any(cs => cs.CompanyId == companyId && cs.SupplierId == s.Id))
            ?? throw new InvalidOperationException("Choose a supplier linked to your company.");

        var reference = request.SupplierInvoiceRef.Trim();
        if (reference == "") throw new InvalidOperationException("Enter the supplier's invoice number.");
        if (!request.InvoiceDate.HasValue) throw new InvalidOperationException("Enter the invoice date.");
        if (request.DueDate.HasValue && request.DueDate.Value.Date < request.InvoiceDate.Value.Date) throw new InvalidOperationException("The due date cannot be before the invoice date.");
        if (await db.Invoices.AnyAsync(i => i.CompanyId == companyId && i.SupplierId == supplier.Id && i.SupplierInvoiceRef == reference && i.Status != InvoiceStatus.Rejected))
            throw new InvalidOperationException($"{supplier.Name} already has an invoice numbered {reference}.");

        if (request.Lines.Count == 0) throw new InvalidOperationException("Add at least one line.");
        if (request.Lines.GroupBy(l => l.ItemId).Any(g => g.Count() > 1)) throw new InvalidOperationException("An item can only appear once on an invoice.");
        if (request.Lines.Any(l => l.Quantity <= 0 || l.UnitPrice < 0)) throw new InvalidOperationException("Every line needs a quantity above zero and a price.");
        if (request.Lines.Any(l => l.DiscountPct is < 0 or > 100)) throw new InvalidOperationException("Discounts must be between 0 and 100 percent.");
        if (request.AdditionalItemsCost < 0 || request.AdditionalDeliveryCost < 0 || request.DiscountOrCredit < 0) throw new InvalidOperationException("Amounts cannot be negative.");
        if (uploads.Count == 0) throw new InvalidOperationException("Attach the supplier's invoice or another supporting document.");
        foreach (var upload in uploads) ValidateUpload(upload);

        if (request.DepartmentId.HasValue && !await db.Departments.AnyAsync(d => d.Id == request.DepartmentId.Value && d.CompanyId == companyId))
            throw new InvalidOperationException("Department not found.");
        if (request.VesselId.HasValue && !await db.Vessels.AnyAsync(v => v.Id == request.VesselId.Value && v.CompanyId == companyId))
            throw new InvalidOperationException("Vessel not found.");
        if (request.BankAccountId.HasValue && !await db.SupplierBankAccounts.AnyAsync(b => b.Id == request.BankAccountId.Value && b.SupplierId == supplier.Id))
            throw new InvalidOperationException("That bank account does not belong to this supplier.");

        TaxCode? tax = null;
        if (request.TaxCodeId.HasValue)
            tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == request.TaxCodeId.Value && t.CompanyId == companyId)
                ?? throw new InvalidOperationException("Tax code not found.");

        var itemIds = request.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => i.CompanyId == companyId && itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        if (items.Count != itemIds.Count) throw new InvalidOperationException("One or more items were not found in your catalog.");
        await ProcurementService.EnsureItemsAllowedAsync(db, companyId, request.DepartmentId, itemIds);

        var invoice = new Invoice
        {
            CompanyId = companyId, InvoiceNumber = await NextNumberAsync(db), Kind = InvoiceKind.SpotInvoice, SupplierId = supplier.Id,
            SupplierInvoiceRef = reference,
            InvoiceDate = DateTime.SpecifyKind(request.InvoiceDate.Value.Date, DateTimeKind.Utc),
            DueDate = request.DueDate.HasValue ? DateTime.SpecifyKind(request.DueDate.Value.Date, DateTimeKind.Utc) : null,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant(),
            AdditionalItemsCost = request.AdditionalItemsCost, AdditionalDeliveryCost = request.AdditionalDeliveryCost,
            DiscountOrCredit = request.DiscountOrCredit,
            TaxCodeId = tax?.Id, TaxRatePct = tax?.RatePct ?? 0m, BankAccountId = request.BankAccountId,
            DepartmentId = request.DepartmentId, VesselId = request.VesselId, CostCentre = request.CostCentre.Trim(), Remarks = request.Remarks.Trim(),
            SubmittedByName = actor.Name, SubmittedAt = DateTime.UtcNow, VerifiedByName = actor.Name, VerifiedAt = DateTime.UtcNow,
            Lines = request.Lines.Select(l => new InvoiceLine
            {
                ItemId = l.ItemId,
                Description = $"{items[l.ItemId].Code} · {items[l.ItemId].Name}" + (string.IsNullOrWhiteSpace(l.Description) ? "" : $" ({l.Description.Trim()})"),
                Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPct = l.DiscountPct
            }).ToList()
        };
        invoice.InvoiceAmount = invoice.Lines.Sum(l => Money.LineNet(l.Quantity, l.UnitPrice, l.DiscountPct));
        if (invoice.TotalExclTax <= 0) throw new InvalidOperationException("The invoice total must be above zero.");

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        AuditLog.Add(db, companyId, AuditEntityTypes.Invoice, invoice.Id, actor,
            $"CREATED spot invoice {invoice.InvoiceNumber} from {supplier.Name} ({reference}): {invoice.Lines.Count} lines, total {Money.Format(invoice.TotalExclTax, invoice.Currency)} before tax");
        await SaveDocumentsAsync(db, invoice, uploads, actor);
        await RouteAsync(db, invoice, invoice.DepartmentId, actor);
        await db.SaveChangesAsync();
        return invoice;
    }

    // ---- Tax codes ----

    public async Task<List<TaxCode>> GetTaxCodesAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.TaxCodes.Where(t => t.CompanyId == companyId).OrderBy(t => t.Code).ToListAsync();
    }

    public async Task AddTaxCodeAsync(int companyId, string code, string description, decimal ratePct)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code == "") throw new InvalidOperationException("Enter a code.");
        if (ratePct is < 0 or > 100) throw new InvalidOperationException("The rate must be between 0 and 100 percent.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (await db.TaxCodes.AnyAsync(t => t.CompanyId == companyId && t.Code == code)) throw new InvalidOperationException($"{code} already exists.");
        db.TaxCodes.Add(new TaxCode { CompanyId = companyId, Code = code, Description = (description ?? "").Trim(), RatePct = ratePct });
        await db.SaveChangesAsync();
    }

    public async Task DeleteTaxCodeAsync(int companyId, int taxCodeId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var code = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == taxCodeId && t.CompanyId == companyId);
        if (code is null) return;
        if (await db.Invoices.AnyAsync(i => i.TaxCodeId == taxCodeId))
            throw new InvalidOperationException($"{code.Code} is used by invoices, so it cannot be deleted.");
        db.TaxCodes.Remove(code);
        await db.SaveChangesAsync();
    }

    // ---- Supplier bank accounts ----

    public async Task<List<SupplierBankAccount>> GetBankAccountsAsync(int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.SupplierBankAccounts.Where(b => b.SupplierId == supplierId).OrderBy(b => b.BankName).ThenBy(b => b.AccountNumber).ToListAsync();
    }

    public async Task AddBankAccountAsync(int supplierId, SupplierBankAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.BankName)) throw new InvalidOperationException("Enter the bank name.");
        if (string.IsNullOrWhiteSpace(account.AccountName)) throw new InvalidOperationException("Enter the account holder's name.");
        if (string.IsNullOrWhiteSpace(account.AccountNumber)) throw new InvalidOperationException("Enter the account number or IBAN.");

        await using var db = await factory.CreateDbContextAsync();
        var number = account.AccountNumber.Trim();
        if (await db.SupplierBankAccounts.AnyAsync(b => b.SupplierId == supplierId && b.AccountNumber == number))
            throw new InvalidOperationException("That account number is already saved.");
        db.SupplierBankAccounts.Add(new SupplierBankAccount
        {
            SupplierId = supplierId, BankName = account.BankName.Trim(), AccountName = account.AccountName.Trim(), AccountNumber = number,
            SwiftCode = (account.SwiftCode ?? "").Trim().ToUpperInvariant(),
            Currency = string.IsNullOrWhiteSpace(account.Currency) ? "USD" : account.Currency.Trim().ToUpperInvariant()
        });
        await db.SaveChangesAsync();
    }

    public async Task DeleteBankAccountAsync(int supplierId, int accountId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var account = await db.SupplierBankAccounts.FirstOrDefaultAsync(b => b.Id == accountId && b.SupplierId == supplierId);
        if (account is null) return;
        if (await db.Invoices.AnyAsync(i => i.BankAccountId == accountId))
            throw new InvalidOperationException("This account is used on invoices, so it cannot be deleted.");
        db.SupplierBankAccounts.Remove(account);
        await db.SaveChangesAsync();
    }
}
