using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>What the Master may approve onboard for a purchase order, and why not when the answer is no.</summary>
public record MasterAuthority(bool Allowed, string Reason, decimal? LimitUsd);

/// <summary>
/// Where a new purchase order goes for approval. Ashore that is the office's approval matrix. Onboard it is the Master, within the
/// limit and monthly budget the office set for the vessel (policy rows); anything above, or with no limit set, or with no Master
/// aboard, is handed over to the office, which approves it through its own matrix. A Master facing a genuine emergency may approve
/// above the limit with a written justification; that order is flagged for the office to review afterwards.
/// </summary>
public static class ApprovalRouting
{
    public static Task RouteAsync(ChandleryDbContext db, PurchaseOrder po, Actor actor, string? emergencyJustification = null)
        => db.Node.IsVessel && db.Node.SyncEnabled
            ? OnboardRouteAsync(db, po, actor, emergencyJustification)
            : ApprovalEngine.RouteAsync(db, po, actor);

    private static async Task OnboardRouteAsync(ChandleryDbContext db, PurchaseOrder po, Actor actor, string? emergencyJustification)
    {
        var comparable = await ApprovalEngine.ComparableAsync(db, po.CompanyId, po.Currency, po.CreatedDate, po.Total);
        po.UsdRate = comparable.Rate;
        po.Status = PoStatus.PendingApproval;

        var authority = await PolicyGuard.MasterAuthorityAsync(db, po, comparable.Amount, DateTime.UtcNow);
        var masters = await db.CrewMembers.Where(c => c.VesselId == po.VesselId && c.IsMaster && c.IsActive && c.LoginEnabled && c.PasswordHash != "").ToListAsync();
        var emergency = !authority.Allowed && !string.IsNullOrWhiteSpace(emergencyJustification);

        if (masters.Count == 0 || (!authority.Allowed && !emergency))
        {
            po.HandoverRequested = true;
            var why = masters.Count == 0 && authority.Allowed ? "there is no Master with a login aboard" : authority.Reason;
            AuditLog.Add(db, po.CompanyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
                $"HANDED OVER to the office for approval: {why}" + (comparable.Note == "" ? "" : $"; {comparable.Note}"));
            return;
        }

        foreach (var master in masters)
            db.PoApprovals.Add(new PoApproval { CompanyId = po.CompanyId, PurchaseOrderId = po.Id, Level = 1, ApproverUserId = master.UserId });

        if (emergency)
        {
            po.IsEmergency = true;
            po.EmergencyJustification = emergencyJustification!.Trim();
            AuditLog.Add(db, po.CompanyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
                $"EMERGENCY purchase above the Master's limit ({authority.Reason}); justification: {po.EmergencyJustification}");
        }
        AuditLog.Add(db, po.CompanyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
            $"SENT to the Master for approval ({string.Join(" or ", masters.Select(m => m.FullName))})" + (comparable.Note == "" ? "" : $"; {comparable.Note}"));
    }
}
