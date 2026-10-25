using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record SearchHit(string Group, string Title, string Subtitle, string Href);

/// <summary>Finds records by number or name across the signed-in user's own data, for the Ctrl+K palette.</summary>
public class SearchService(IDbContextFactory<ChandleryDbContext> factory)
{
    private const int PerGroup = 5;

    private static string Like(string term) =>
        "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public async Task<List<SearchHit>> SearchCompanyAsync(int companyId, string term)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2) return hits;
        var like = Like(term);

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        foreach (var r in await db.Requisitions.Where(r => r.CompanyId == companyId && EF.Functions.ILike(r.ReqnNumber, like))
                     .OrderByDescending(r => r.ReqnDate).Take(PerGroup).ToListAsync())
            hits.Add(new SearchHit("Requisitions", r.ReqnNumber, r.Status.ToString(), DocLinks.For(DocKind.Requisition, r.Id)));

        foreach (var r in await db.Rfqs.Where(r => r.CompanyId == companyId && (EF.Functions.ILike(r.RfqNumber, like) || EF.Functions.ILike(r.Vessel, like)))
                     .OrderByDescending(r => r.CreatedDate).Take(PerGroup).ToListAsync())
            hits.Add(new SearchHit("RFQs", r.RfqNumber, $"{r.Vessel} · {r.Status}", DocLinks.For(DocKind.Rfq, r.Id)));

        var pos = await db.PurchaseOrders.Where(p => p.CompanyId == companyId && EF.Functions.ILike(p.PoNumber, like))
            .OrderByDescending(p => p.CreatedDate).Take(PerGroup).ToListAsync();
        var supplierNames = await db.Suppliers.Where(s => pos.Select(p => p.SupplierId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        foreach (var p in pos)
            hits.Add(new SearchHit("Purchase orders", p.PoNumber, $"{supplierNames.GetValueOrDefault(p.SupplierId)} · {Money.Format(p.Total, p.Currency)}", DocLinks.For(DocKind.PurchaseOrder, p.Id)));

        var linked = db.CompanySuppliers.Where(cs => cs.CompanyId == companyId).Select(cs => cs.SupplierId);
        foreach (var s in await db.Suppliers.Where(s => linked.Contains(s.Id) && (EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.ContactEmail, like)))
                     .OrderBy(s => s.Name).Take(PerGroup).ToListAsync())
            hits.Add(new SearchHit("Suppliers", s.Name, s.ContactEmail, DocLinks.For(DocKind.Supplier, s.Id)));

        foreach (var v in await db.Vessels.Where(v => v.CompanyId == companyId && (EF.Functions.ILike(v.Name, like) || (v.ImoNumber != null && EF.Functions.ILike(v.ImoNumber, like))))
                     .OrderBy(v => v.Name).Take(PerGroup).ToListAsync())
            hits.Add(new SearchHit("Vessels", v.Name, v.ImoNumber is null ? "" : "IMO " + v.ImoNumber, DocLinks.For(DocKind.Vessel, v.Id)));

        foreach (var i in await db.Items.Where(i => i.CompanyId == companyId && (EF.Functions.ILike(i.Code, like) || EF.Functions.ILike(i.Name, like)))
                     .OrderBy(i => i.Code).Take(6).ToListAsync())
            hits.Add(new SearchHit("Items", i.Code, i.Name, DocLinks.For(DocKind.Item, i.Id)));

        foreach (var d in await db.Departments.Where(d => d.CompanyId == companyId && EF.Functions.ILike(d.Name, like))
                     .OrderBy(d => d.Name).Take(PerGroup).ToListAsync())
            hits.Add(new SearchHit("Departments", d.Name, "", DocLinks.For(DocKind.Department, d.Id)));

        return hits;
    }

    public async Task<List<SearchHit>> SearchSupplierAsync(int supplierId, string term)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2) return hits;
        var like = Like(term);

        await using var db = await factory.CreateDbContextAsync();

        foreach (var r in await db.Rfqs.IgnoreQueryFilters()
                     .Where(r => r.InvitedSupplierIds.Contains(supplierId) && (EF.Functions.ILike(r.RfqNumber, like) || EF.Functions.ILike(r.Vessel, like)))
                     .OrderByDescending(r => r.CreatedDate).Take(PerGroup).ToListAsync())
            hits.Add(new SearchHit("RFQs", r.RfqNumber, r.Vessel, DocLinks.For(DocKind.Rfq, r.Id, supplierPortal: true)));

        foreach (var p in await db.PurchaseOrders.IgnoreQueryFilters()
                     .Where(p => p.SupplierId == supplierId && p.Status == PoStatus.Approved && EF.Functions.ILike(p.PoNumber, like))
                     .OrderByDescending(p => p.CreatedDate).Take(PerGroup).ToListAsync())
            hits.Add(new SearchHit("Purchase orders", p.PoNumber, Money.Format(p.Total, p.Currency), DocLinks.For(DocKind.PurchaseOrder, p.Id, supplierPortal: true)));

        return hits;
    }
}
