using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public class NewRequisition
{
    public RfqType Type { get; set; } = RfqType.Stores;
    public int VesselId { get; set; }
    public int? DepartmentId { get; set; }
    public int? DeliveryPortId { get; set; }
    public Urgency Urgency { get; set; } = Urgency.Normal;
    public DateTime? ReqnDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime? VesselEta { get; set; }
    public DateTime? VesselEtd { get; set; }
    public DateTime? EstDeliveryDate { get; set; }
    public bool IsBreakdown { get; set; }
    public string Remarks { get; set; } = "";
    public List<NewRfqLine> Lines { get; set; } = new();
}

/// <summary>A row of the requisition list.</summary>
public record RequisitionListRow(Requisition Requisition, string Vessel, string Department, int LineCount, int RfqCount);

/// <summary>Requisitions: the vessel's request for items, entered once and turned into RFQs for many suppliers.</summary>
public class RequisitionService(IDbContextFactory<ChandleryDbContext> factory)
{
    private static DateTime? Utc(DateTime? date) =>
        date.HasValue ? DateTime.SpecifyKind(date.Value.Date, DateTimeKind.Utc) : null;

    public async Task<Requisition> CreateAsync(int companyId, NewRequisition request, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (!await db.Vessels.AnyAsync(v => v.Id == request.VesselId && v.CompanyId == companyId))
            throw new InvalidOperationException("Choose a vessel.");
        if (request.DepartmentId.HasValue && !await db.Departments.AnyAsync(d => d.Id == request.DepartmentId.Value && d.CompanyId == companyId))
            throw new InvalidOperationException("Department not found for this company.");
        if (!request.DeliveryPortId.HasValue)
            throw new InvalidOperationException("Choose the delivery port.");
        if (!await db.Ports.AnyAsync(p => p.Id == request.DeliveryPortId.Value))
            throw new InvalidOperationException("Delivery port not found.");
        if (request.VesselEta.HasValue && request.VesselEtd.HasValue && request.VesselEtd.Value.Date < request.VesselEta.Value.Date)
            throw new InvalidOperationException("The vessel ETD can't be before the ETA.");
        if (request.Lines.Count == 0)
            throw new InvalidOperationException("Add at least one line item.");
        if (request.Lines.Any(l => l.Quantity <= 0))
            throw new InvalidOperationException("Every line needs a quantity above zero.");

        var itemIds = request.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => i.CompanyId == companyId && itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        if (items.Count != itemIds.Count)
            throw new InvalidOperationException("One or more items were not found in your catalog.");
        await ProcurementService.EnsureItemsAllowedAsync(db, companyId, request.DepartmentId, itemIds);

        var isSpares = request.Type == RfqType.Spares;
        var nextSeq = 1001 + await db.Requisitions.IgnoreQueryFilters().CountAsync();

        var requisition = new Requisition
        {
            CompanyId = companyId,
            ReqnNumber = $"REQ-{nextSeq}",
            Type = request.Type,
            VesselId = request.VesselId,
            DepartmentId = request.DepartmentId,
            DeliveryPortId = request.DeliveryPortId,
            Urgency = request.Urgency,
            ReqnDate = Utc(request.ReqnDate) ?? DateTime.UtcNow.Date,
            VesselEta = Utc(request.VesselEta),
            VesselEtd = Utc(request.VesselEtd),
            EstDeliveryDate = Utc(request.EstDeliveryDate),
            IsBreakdown = request.IsBreakdown,
            Remarks = request.Remarks.Trim(),
            Status = RequisitionStatus.Open,
            CreatedByUserId = actor.UserId,
            CreatedByName = actor.Name,
            CreatedAt = DateTime.UtcNow,
            Lines = request.Lines.Select(l => new RequisitionLine
            {
                ItemId = l.ItemId,
                Quantity = l.Quantity,
                ApprovedQuantity = l.Quantity,
                Uom = items[l.ItemId].UnitOfMeasure,
                Description = string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim(),
                DrawingNo = isSpares && !string.IsNullOrWhiteSpace(l.DrawingNo) ? l.DrawingNo.Trim() : null
            }).ToList()
        };
        db.Requisitions.Add(requisition);
        await db.SaveChangesAsync();

        AuditLog.Add(db, companyId, AuditEntityTypes.Requisition, requisition.Id, actor,
            $"CREATED New Requisition {requisition.ReqnNumber} with {requisition.Lines.Count} lines");
        await db.SaveChangesAsync();
        return requisition;
    }

    public async Task<Requisition?> GetAsync(int companyId, int requisitionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Requisitions.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == requisitionId && r.CompanyId == companyId);
    }

    public async Task<int> GetCountAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Requisitions.CountAsync(r => r.CompanyId == companyId);
    }

    /// <summary>Earlier requisitions as copy sources for the "copy lines from previous" tool.</summary>
    public async Task<List<Requisition>> GetRecentAsync(int companyId, int take = 50)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Requisitions.Include(r => r.Lines).Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync();
    }

    public async Task<List<Rfq>> GetRfqsAsync(int companyId, int requisitionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Rfqs.Where(r => r.CompanyId == companyId && r.RequisitionId == requisitionId)
            .OrderByDescending(r => r.CreatedDate).ToListAsync();
    }

    /// <summary>For each requisition line already sent on an RFQ, the number of the latest RFQ that carried it.</summary>
    public async Task<Dictionary<int, NameRef>> GetLineRfqNumbersAsync(int companyId, int requisitionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rfqs = await db.Rfqs.Include(r => r.Lines)
            .Where(r => r.CompanyId == companyId && r.RequisitionId == requisitionId)
            .OrderBy(r => r.CreatedDate).ToListAsync();

        var map = new Dictionary<int, NameRef>();
        foreach (var rfq in rfqs)
            foreach (var line in rfq.Lines.Where(l => l.RequisitionLineId.HasValue))
                map[line.RequisitionLineId!.Value] = new NameRef(rfq.Id, rfq.RfqNumber);
        return map;
    }

    public async Task<GridResult<RequisitionListRow>> GetPageAsync(int companyId, GridRequest req)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var q = db.Requisitions.Where(r => r.CompanyId == companyId);

        if (req.Like("reqn") is { } reqnLike) q = q.Where(r => EF.Functions.ILike(r.ReqnNumber, reqnLike));
        if (Enum.TryParse<RfqType>(req.Select("type"), out var type)) q = q.Where(r => r.Type == type);
        if (Enum.TryParse<Urgency>(req.Select("urgency"), out var urgency)) q = q.Where(r => r.Urgency == urgency);
        if (Enum.TryParse<RequisitionStatus>(req.Select("status"), out var status)) q = q.Where(r => r.Status == status);
        if (req.FromUtc("date") is { } from) q = q.Where(r => r.ReqnDate >= from);
        if (req.ToUtcExclusive("date") is { } to) q = q.Where(r => r.ReqnDate < to);
        if (int.TryParse(req.Select("rfqs"), out var rfqCount))
            q = q.Where(r => db.Rfqs.Count(x => x.RequisitionId == r.Id) == rfqCount);
        if (req.Like("vessel") is { } vesselLike)
        {
            var vesselIds = await db.Vessels.Where(v => v.CompanyId == companyId && EF.Functions.ILike(v.Name, vesselLike)).Select(v => v.Id).ToListAsync();
            q = q.Where(r => vesselIds.Contains(r.VesselId));
        }
        if (req.Like("department") is { } deptLike)
        {
            var deptIds = await db.Departments.Where(d => d.CompanyId == companyId && EF.Functions.ILike(d.Name, deptLike)).Select(d => d.Id).ToListAsync();
            q = q.Where(r => r.DepartmentId != null && deptIds.Contains(r.DepartmentId.Value));
        }

        var total = await q.CountAsync();
        var desc = req.SortDesc;
        IOrderedQueryable<Requisition> ordered = req.SortKey switch
        {
            "reqn" => GridSort.By(q, r => r.ReqnNumber, desc),
            "vessel" => GridSort.By(q, r => db.Vessels.Where(v => v.Id == r.VesselId).Select(v => v.Name).FirstOrDefault(), desc),
            "department" => GridSort.By(q, r => db.Departments.Where(d => d.Id == r.DepartmentId).Select(d => d.Name).FirstOrDefault(), desc),
            "type" => GridSort.By(q, r => r.Type, desc),
            "urgency" => GridSort.By(q, r => r.Urgency, desc),
            "date" => GridSort.By(q, r => r.ReqnDate, desc),
            "lines" => GridSort.By(q, r => r.Lines.Count, desc),
            "rfqs" => GridSort.By(q, r => db.Rfqs.Count(x => x.RequisitionId == r.Id), desc),
            "status" => GridSort.By(q, r => r.Status, desc),
            _ => q.OrderByDescending(r => r.ReqnDate)
        };
        var page = await ordered.ThenByDescending(r => r.Id)
            .Skip(req.Skip).Take(req.PageSize)
            .Select(r => new { Requisition = r, Lines = r.Lines.Count, Rfqs = db.Rfqs.Count(x => x.RequisitionId == r.Id) })
            .ToListAsync();

        var vesselNames = await db.Vessels.Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        var deptNames = await db.Departments.Where(d => d.CompanyId == companyId).ToDictionaryAsync(d => d.Id, d => d.Name);

        var rows = page.Select(x => new RequisitionListRow(
            x.Requisition,
            vesselNames.GetValueOrDefault(x.Requisition.VesselId) ?? "",
            x.Requisition.DepartmentId.HasValue ? deptNames.GetValueOrDefault(x.Requisition.DepartmentId.Value) ?? "" : "",
            x.Lines, x.Rfqs)).ToList();
        return new GridResult<RequisitionListRow>(rows, total);
    }

    public async Task<List<string>> GetRfqCountOptionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var counts = await db.Requisitions.Where(r => r.CompanyId == companyId)
            .Select(r => db.Rfqs.Count(x => x.RequisitionId == r.Id)).Distinct().ToListAsync();
        return counts.Order().Select(n => n.ToString()).ToList();
    }

    /// <summary>The approver trims or confirms the quantity that may be ordered.</summary>
    public async Task SetApprovedQuantitiesAsync(int companyId, int requisitionId, Dictionary<int, decimal> approvedByLineId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var requisition = await db.Requisitions.Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == requisitionId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("Requisition not found.");
        if (requisition.Status is RequisitionStatus.Cancelled or RequisitionStatus.Ordered)
            throw new InvalidOperationException("This requisition can no longer be changed.");

        var items = await db.Items.Where(i => requisition.Lines.Select(l => l.ItemId).Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        foreach (var line in requisition.Lines)
        {
            if (!approvedByLineId.TryGetValue(line.Id, out var qty) || qty == line.ApprovedQuantity) continue;
            if (qty < 0) throw new InvalidOperationException("Approved quantity can't be negative.");

            var name = items.GetValueOrDefault(line.ItemId)?.Name ?? $"item {line.ItemId}";
            AuditLog.Add(db, companyId, AuditEntityTypes.Requisition, requisition.Id, actor,
                $"CHANGED Approved Qty for {name} from [{Money.Qty(line.ApprovedQuantity)}] to [{Money.Qty(qty)}]");
            line.ApprovedQuantity = qty;
        }
        await db.SaveChangesAsync();
    }

    public async Task CancelAsync(int companyId, int requisitionId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var requisition = await db.Requisitions.FirstOrDefaultAsync(r => r.Id == requisitionId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("Requisition not found.");
        if (requisition.Status == RequisitionStatus.Cancelled) return;
        if (requisition.Status == RequisitionStatus.Ordered)
            throw new InvalidOperationException("Purchase orders were already raised from this requisition.");

        requisition.Status = RequisitionStatus.Cancelled;
        AuditLog.Add(db, companyId, AuditEntityTypes.Requisition, requisition.Id, actor, "CANCELLED Requisition");
        await db.SaveChangesAsync();
    }
}
