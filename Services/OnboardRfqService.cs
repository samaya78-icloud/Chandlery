using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record OnboardRfqRow(Guid Id, string RfqNumber, DateTime Created, RfqStatus Status, string OwnerStage, int Lines, int Invited, int Quotes, string? RequisitionNumber);

public record OnboardRfqView(Rfq Rfq, Dictionary<Guid, string> Suppliers, List<Quote> Quotes, Dictionary<Guid, (string Code, string Name)> Items,
    string? RequisitionNumber, Guid? RequisitionId, string? PortName)
{
    /// <summary>The vessel may work on an RFQ only while it owns it.</summary>
    public bool Editable => Rfq.OwnerStage == SyncStages.Vessel;
}

/// <summary>What a vessel sees of its RFQs and the quotes on them.</summary>
public class OnboardRfqService(IDbContextFactory<ChandleryDbContext> factory)
{
    public async Task<List<OnboardRfqRow>> ListAsync(Guid companyId, string? search = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rfqs = await db.Rfqs.AsNoTracking().Include(r => r.Lines).OrderByDescending(r => r.CreatedDate).ToListAsync();
        var quoteCounts = await db.Quotes.AsNoTracking().GroupBy(q => q.RfqId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var numbers = await db.Requisitions.AsNoTracking().Select(r => new { r.Id, r.ReqnNumber }).ToDictionaryAsync(r => r.Id, r => r.ReqnNumber);
        var rows = rfqs.Select(r => new OnboardRfqRow(r.Id, r.RfqNumber, r.CreatedDate, r.Status, r.OwnerStage, r.Lines.Count, r.InvitedSupplierIds.Count,
            quoteCounts.GetValueOrDefault(r.Id), r.RequisitionId is { } rid ? numbers.GetValueOrDefault(rid) : null));
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(r => r.RfqNumber.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        return rows.ToList();
    }

    public async Task<OnboardRfqView?> GetAsync(Guid companyId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rfq = await db.Rfqs.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id);
        if (rfq is null) return null;

        var suppliers = await db.Suppliers.AsNoTracking().Where(s => rfq.InvitedSupplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var quotes = await db.Quotes.AsNoTracking().Where(q => q.RfqId == id).OrderBy(q => q.SubmittedDate).ToListAsync();
        var itemIds = rfq.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => (i.Code, i.Name));
        string? number = rfq.RequisitionId is { } rid ? await db.Requisitions.AsNoTracking().Where(r => r.Id == rid).Select(r => r.ReqnNumber).FirstOrDefaultAsync() : null;
        var port = rfq.DeliveryPortId is { } pid ? await db.Ports.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync() : null;
        return new OnboardRfqView(rfq, suppliers, quotes, items, number, rfq.RequisitionId, port);
    }

    /// <summary>The RFQs raised from one requisition, for showing where it stands.</summary>
    public async Task<List<OnboardRfqRow>> ForRequisitionAsync(Guid companyId, Guid requisitionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var ids = (await db.Rfqs.AsNoTracking().Where(r => r.RequisitionId == requisitionId).Select(r => r.Id).ToListAsync()).ToHashSet();
        return (await ListAsync(companyId)).Where(r => ids.Contains(r.Id)).OrderBy(r => r.Created).ToList();
    }
}
