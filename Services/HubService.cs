using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

// Light summaries used by the hub pages, the lineage strip and the shared mini-tables.
public record RfqBrief(int Id, string Number, string Vessel, int? VesselId, DateTime Created, DateTime? Due, RfqStatus Status, int SupplierCount);
public record InvBrief(int Id, string Number, int PoId, string Supplier, InvoiceStatus Status, decimal Total, string Currency);
public record DoBrief(int Id, string Number, int PoId, string PoNumber, DateTime Received, int Lines, bool Locked);
public record PoBrief(int Id, string Number, int SupplierId, string Supplier, DateTime Created, string Currency, decimal Total, PoStatus Status, DeliveryStatus Delivery, bool IsSpot);
public record ReqnBrief(int Id, string Number, int VesselId, string Vessel, int? DepartmentId, string Department, DateTime Date, RequisitionStatus Status, int Lines);
public record PriceRow(DateTime Date, int PoId, string PoNumber, int SupplierId, string Supplier, decimal Quantity, decimal UnitPrice, decimal DiscountPct, string Currency);

public record SupplierHub(Supplier Supplier, List<RfqBrief> Rfqs, List<PoBrief> Pos, int QuotesSubmitted);
public record VesselHub(Vessel Vessel, List<ReqnBrief> Requisitions, List<RfqBrief> Rfqs, List<PoBrief> Pos);
public record ItemHub(Item Item, string CategoryPath, List<PriceRow> Prices, List<RfqBrief> Rfqs, List<ReqnBrief> Requisitions);
public record DepartmentHub(Department Department, List<ReqnBrief> Requisitions, List<PoBrief> Pos, List<ApprovalRuleRow> Rules);

/// <summary>The document chain around one record: Requisition -> RFQs -> POs -> deliveries.</summary>
public record RelatedGraph(ReqnBrief? Requisition, List<RfqBrief> Rfqs, List<PoBrief> Pos, List<DoBrief> Dos, List<InvBrief> Invoices)
{
    public bool IsEmpty => Requisition is null && Rfqs.Count == 0 && Pos.Count == 0 && Dos.Count == 0 && Invoices.Count == 0;
}

/// <summary>Read-only look-ups that connect records to everything related to them, so no page is a dead end.</summary>
public class HubService(IDbContextFactory<ChandleryDbContext> factory)
{
    private const int Limit = 200;

    // ---- brief builders (take an open context so callers can batch) ----

    private static async Task<List<RfqBrief>> BriefsAsync(ChandleryDbContext db, IQueryable<Rfq> query)
    {
        var rfqs = await query.OrderByDescending(r => r.CreatedDate).Take(Limit).ToListAsync();
        return rfqs.Select(r => new RfqBrief(r.Id, r.RfqNumber, r.Vessel, r.VesselId, r.CreatedDate, r.DueDate, r.Status, r.InvitedSupplierIds.Count)).ToList();
    }

    private static async Task<List<PoBrief>> BriefsAsync(ChandleryDbContext db, IQueryable<PurchaseOrder> query)
    {
        var pos = await query.OrderByDescending(p => p.CreatedDate).Take(Limit).ToListAsync();
        var supplierIds = pos.Select(p => p.SupplierId).Distinct().ToList();
        var names = await db.Suppliers.Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        return pos.Select(p => new PoBrief(p.Id, p.PoNumber, p.SupplierId, names.GetValueOrDefault(p.SupplierId) ?? "", p.CreatedDate,
            p.Currency, p.Total, p.Status, p.DeliveryStatus, p.IsSpot)).ToList();
    }

    private static async Task<List<ReqnBrief>> BriefsAsync(ChandleryDbContext db, IQueryable<Requisition> query, int companyId)
    {
        var rows = await query.OrderByDescending(r => r.ReqnDate).Take(Limit)
            .Select(r => new { Req = r, Lines = r.Lines.Count }).ToListAsync();
        var vessels = await db.Vessels.Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        var depts = await db.Departments.Where(d => d.CompanyId == companyId).ToDictionaryAsync(d => d.Id, d => d.Name);
        return rows.Select(x => new ReqnBrief(x.Req.Id, x.Req.ReqnNumber, x.Req.VesselId, vessels.GetValueOrDefault(x.Req.VesselId) ?? "",
            x.Req.DepartmentId, x.Req.DepartmentId.HasValue ? depts.GetValueOrDefault(x.Req.DepartmentId.Value) ?? "" : "",
            x.Req.ReqnDate, x.Req.Status, x.Lines)).ToList();
    }

    // ---- hubs ----

    public async Task<SupplierHub?> GetSupplierAsync(int companyId, int supplierId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (!await db.CompanySuppliers.AnyAsync(cs => cs.CompanyId == companyId && cs.SupplierId == supplierId)) return null;
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId);
        if (supplier is null) return null;

        var rfqs = await BriefsAsync(db, db.Rfqs.Where(r => r.CompanyId == companyId && r.InvitedSupplierIds.Contains(supplierId)));
        var pos = await BriefsAsync(db, db.PurchaseOrders.Where(p => p.CompanyId == companyId && p.SupplierId == supplierId));
        var quotes = await db.Quotes.CountAsync(q => q.CompanyId == companyId && q.SupplierId == supplierId);
        return new SupplierHub(supplier, rfqs, pos, quotes);
    }

    public async Task<VesselHub?> GetVesselAsync(int companyId, int vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var vessel = await db.Vessels.FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId);
        if (vessel is null) return null;

        var reqns = await BriefsAsync(db, db.Requisitions.Where(r => r.CompanyId == companyId && r.VesselId == vesselId), companyId);
        var rfqQuery = db.Rfqs.Where(r => r.CompanyId == companyId && r.VesselId == vesselId);
        var rfqs = await BriefsAsync(db, rfqQuery);
        var rfqIds = rfqs.Select(r => r.Id).ToList();
        var pos = await BriefsAsync(db, db.PurchaseOrders.Where(p => p.CompanyId == companyId
            && (p.VesselId == vesselId || (p.RfqId != null && rfqIds.Contains(p.RfqId.Value)))));
        return new VesselHub(vessel, reqns, rfqs, pos);
    }

    public async Task<ItemHub?> GetItemAsync(int companyId, int itemId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == companyId);
        if (item is null) return null;

        var categories = await db.Categories.ToListAsync();
        var path = Category.BuildTree(categories).FirstOrDefault(t => t.Category.Id == item.CategoryId).Path ?? "—";

        var pos = await db.PurchaseOrders.Where(p => p.CompanyId == companyId && p.Lines.Any(l => l.ItemId == itemId))
            .OrderByDescending(p => p.CreatedDate).Take(Limit).ToListAsync();
        var supplierIds = pos.Select(p => p.SupplierId).Distinct().ToList();
        var names = await db.Suppliers.Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var prices = pos.SelectMany(p => p.Lines.Where(l => l.ItemId == itemId).Select(l => new PriceRow(
            p.CreatedDate, p.Id, p.PoNumber, p.SupplierId, names.GetValueOrDefault(p.SupplierId) ?? "", l.Quantity, l.UnitPrice, l.DiscountPct, p.Currency))).ToList();

        var rfqs = await BriefsAsync(db, db.Rfqs.Where(r => r.CompanyId == companyId && r.Lines.Any(l => l.ItemId == itemId)));
        var reqns = await BriefsAsync(db, db.Requisitions.Where(r => r.CompanyId == companyId && r.Lines.Any(l => l.ItemId == itemId)), companyId);
        return new ItemHub(item, path, prices, rfqs, reqns);
    }

    public async Task<DepartmentHub?> GetDepartmentAsync(int companyId, int departmentId, ApprovalService approvals)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var dept = await db.Departments.FirstOrDefaultAsync(d => d.Id == departmentId && d.CompanyId == companyId);
        if (dept is null) return null;

        var reqns = await BriefsAsync(db, db.Requisitions.Where(r => r.CompanyId == companyId && r.DepartmentId == departmentId), companyId);
        var pos = await BriefsAsync(db, db.PurchaseOrders.Where(p => p.CompanyId == companyId && p.DepartmentId == departmentId));
        var rules = (await approvals.GetRulesAsync(companyId)).Where(r => r.Rule.DepartmentId == departmentId).ToList();
        return new DepartmentHub(dept, reqns, pos, rules);
    }

    private static async Task<List<InvBrief>> InvoiceBriefsAsync(ChandleryDbContext db, int companyId, List<int> poIds, Dictionary<int, string> poNumbers)
    {
        var rows = await db.Invoices.Where(i => i.CompanyId == companyId && i.PurchaseOrderId != null && poIds.Contains(i.PurchaseOrderId.Value))
            .OrderBy(i => i.SubmittedAt).ToListAsync();
        var suppliers = await db.Suppliers.Where(s => rows.Select(r => r.SupplierId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        return rows.Select(i => new InvBrief(i.Id, i.InvoiceNumber, i.PurchaseOrderId!.Value, suppliers.GetValueOrDefault(i.SupplierId) ?? "", i.Status, i.TotalExclTax, i.Currency)).ToList();
    }

    // ---- lineage ----

    /// <summary>Everything connected to a requisition, RFQ or PO: the requisition, all its RFQs, and the POs raised from them.</summary>
    public async Task<RelatedGraph> GetRelatedAsync(int companyId, DocKind kind, int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        int? requisitionId = null;
        List<int> rfqIds = new();
        List<int> directPoIds = new();

        if (kind == DocKind.Invoice)
        {
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId);
            if (invoice is null) return new RelatedGraph(null, new(), new(), new(), new());
            if (invoice.PurchaseOrderId is { } invoicePo) return await GetRelatedAsync(companyId, DocKind.PurchaseOrder, invoicePo);
            return new RelatedGraph(null, new(), new(), new(), new());
        }

        switch (kind)
        {
            case DocKind.Requisition:
                requisitionId = id;
                break;
            case DocKind.Rfq:
                var rfq = await db.Rfqs.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId);
                if (rfq is null) return new RelatedGraph(null, new(), new(), new(), new());
                requisitionId = rfq.RequisitionId;
                if (requisitionId is null) rfqIds.Add(rfq.Id);
                break;
            case DocKind.PurchaseOrder:
                var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == companyId);
                if (po is null) return new RelatedGraph(null, new(), new(), new(), new());
                if (po.RfqId is { } poRfqId)
                {
                    var parent = await db.Rfqs.FirstOrDefaultAsync(r => r.Id == poRfqId);
                    requisitionId = parent?.RequisitionId;
                    if (requisitionId is null) rfqIds.Add(poRfqId);
                }
                else
                {
                    directPoIds.Add(po.Id); // a spot PO stands alone
                }
                break;
        }

        if (requisitionId.HasValue)
            rfqIds = await db.Rfqs.Where(r => r.CompanyId == companyId && r.RequisitionId == requisitionId).Select(r => r.Id).ToListAsync();

        var requisition = requisitionId.HasValue
            ? (await BriefsAsync(db, db.Requisitions.Where(r => r.Id == requisitionId.Value && r.CompanyId == companyId), companyId)).FirstOrDefault()
            : null;
        var rfqs = rfqIds.Count == 0 ? new() : await BriefsAsync(db, db.Rfqs.Where(r => rfqIds.Contains(r.Id)));
        var pos = rfqIds.Count + directPoIds.Count == 0
            ? new()
            : await BriefsAsync(db, db.PurchaseOrders.Where(p => p.CompanyId == companyId
                && ((p.RfqId != null && rfqIds.Contains(p.RfqId.Value)) || directPoIds.Contains(p.Id))));

        var poIds = pos.Select(p => p.Id).ToList();
        var poNumbers = pos.ToDictionary(p => p.Id, p => p.Number);
        var deliveries = poIds.Count == 0
            ? new List<DoBrief>()
            : (await db.DeliveryOrders.Where(d => d.CompanyId == companyId && poIds.Contains(d.PurchaseOrderId))
                    .OrderBy(d => d.ReceivedDate).ThenBy(d => d.Id).ToListAsync())
                .Select(d => new DoBrief(d.Id, d.DoNumber, d.PurchaseOrderId, poNumbers.GetValueOrDefault(d.PurchaseOrderId) ?? "",
                    d.ReceivedDate, d.Lines.Count, d.InvoiceId.HasValue)).ToList();

        var invoices = poIds.Count == 0
            ? new List<InvBrief>()
            : await InvoiceBriefsAsync(db, companyId, poIds, poNumbers);

        return new RelatedGraph(requisition, rfqs.OrderBy(r => r.Created).ToList(), pos.OrderBy(p => p.Created).ToList(), deliveries, invoices);
    }
}
