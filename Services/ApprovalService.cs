using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record ApprovalRuleRow(ApprovalRule Rule, string Department, string Approver);
public record PoApprovalRow(PoApproval Approval, string ApproverName, string DecidedByName);
public record PendingApprovalRow(PurchaseOrder Po, string Supplier, string Department, int Level, decimal Total);

/// <summary>Decides who must approve a new PO, from the company's approval matrix.</summary>
public static class ApprovalEngine
{
    /// <summary>
    /// Call after the PO has been saved (it needs an Id). Picks the tier of the matrix that covers the PO total and creates
    /// one pending slot per approver per level; with no matching rules the PO is approved straight away.
    /// Rules for the PO's department are used when it has any, otherwise the rules without a department.
    /// </summary>
    public static async Task RouteAsync(ChandleryDbContext db, PurchaseOrder po, Actor actor)
    {
        var rules = await db.ApprovalRules.Where(r => r.CompanyId == po.CompanyId).ToListAsync();
        var scoped = po.DepartmentId.HasValue ? rules.Where(r => r.DepartmentId == po.DepartmentId).ToList() : new List<ApprovalRule>();
        if (scoped.Count == 0) scoped = rules.Where(r => r.DepartmentId == null).ToList();

        var total = po.Total;
        var covering = scoped.Where(r => r.UpToAmount == null || r.UpToAmount >= total).ToList();

        List<ApprovalRule> chain;
        if (covering.Count > 0)
        {
            var finite = covering.Where(r => r.UpToAmount.HasValue).Select(r => r.UpToAmount!.Value).ToList();
            decimal? tier = finite.Count > 0 ? finite.Min() : null;
            chain = covering.Where(r => r.UpToAmount == tier).ToList();
        }
        else
        {
            // The PO is above every tier and there is no unlimited one: use the highest tier rather than skipping approval.
            var highest = scoped.Where(r => r.UpToAmount.HasValue).Select(r => r.UpToAmount!.Value).DefaultIfEmpty().Max();
            chain = scoped.Where(r => r.UpToAmount == highest).ToList();
        }

        chain = chain.GroupBy(r => (r.Level, r.ApproverUserId)).Select(g => g.First()).ToList();
        if (chain.Count == 0)
        {
            po.Status = PoStatus.Approved;
            AuditLog.Add(db, po.CompanyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
                "APPROVED automatically: no approval rules apply to this purchase order");
            return;
        }

        po.Status = PoStatus.PendingApproval;
        foreach (var rule in chain)
        {
            db.PoApprovals.Add(new PoApproval
            {
                CompanyId = po.CompanyId,
                PurchaseOrderId = po.Id,
                Level = rule.Level,
                ApproverUserId = rule.ApproverUserId
            });
        }

        var userIds = chain.Select(r => r.ApproverUserId).Distinct().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var levels = chain.GroupBy(r => r.Level).OrderBy(g => g.Key)
            .Select(g => $"level {g.Key}: {string.Join(" or ", g.Select(r => names.GetValueOrDefault(r.ApproverUserId) ?? "?"))}");
        AuditLog.Add(db, po.CompanyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
            $"SENT for approval ({string.Join("; ", levels)})");
    }
}

/// <summary>The approval matrix and PO approval decisions.</summary>
public class ApprovalService(IDbContextFactory<ChandleryDbContext> factory)
{
    // ---- Matrix ----

    public async Task<List<ApprovalRuleRow>> GetRulesAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rules = await db.ApprovalRules.Where(r => r.CompanyId == companyId).ToListAsync();
        var depts = await db.Departments.Where(d => d.CompanyId == companyId).ToDictionaryAsync(d => d.Id, d => d.Name);
        var users = await db.Users.Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return rules
            .Select(r => new ApprovalRuleRow(r,
                r.DepartmentId.HasValue ? depts.GetValueOrDefault(r.DepartmentId.Value) ?? "?" : "All departments",
                users.GetValueOrDefault(r.ApproverUserId) ?? "?"))
            .OrderBy(r => r.Department == "All departments" ? "" : r.Department)
            .ThenBy(r => r.Rule.UpToAmount ?? decimal.MaxValue)
            .ThenBy(r => r.Rule.Level)
            .ThenBy(r => r.Approver)
            .ToList();
    }

    public async Task AddRuleAsync(int companyId, int? departmentId, decimal? upToAmount, int level, int approverUserId)
    {
        if (level is < 1 or > 3) throw new InvalidOperationException("Level must be 1, 2 or 3.");
        if (upToAmount is <= 0) throw new InvalidOperationException("The amount limit must be above zero (or empty for no limit).");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (!await db.Users.AnyAsync(u => u.Id == approverUserId && u.CompanyId == companyId))
            throw new InvalidOperationException("Choose an approver from your company's users.");
        if (departmentId.HasValue && !await db.Departments.AnyAsync(d => d.Id == departmentId.Value && d.CompanyId == companyId))
            throw new InvalidOperationException("Department not found.");
        if (await db.ApprovalRules.AnyAsync(r => r.CompanyId == companyId && r.DepartmentId == departmentId
                                                 && r.UpToAmount == upToAmount && r.Level == level && r.ApproverUserId == approverUserId))
            throw new InvalidOperationException("That rule already exists.");

        db.ApprovalRules.Add(new ApprovalRule
        {
            CompanyId = companyId, DepartmentId = departmentId, UpToAmount = upToAmount, Level = level, ApproverUserId = approverUserId
        });
        await db.SaveChangesAsync();
    }

    public async Task DeleteRuleAsync(int companyId, int ruleId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rule = await db.ApprovalRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId);
        if (rule is null) return;
        db.ApprovalRules.Remove(rule);
        await db.SaveChangesAsync();
    }

    /// <summary>Copies every rule of one department (null = "All departments") to another that has none yet.</summary>
    public async Task<int> CopyRulesAsync(int companyId, int? fromDepartmentId, int? toDepartmentId)
    {
        if (fromDepartmentId == toDepartmentId) throw new InvalidOperationException("Choose two different departments.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (toDepartmentId.HasValue && !await db.Departments.AnyAsync(d => d.Id == toDepartmentId.Value && d.CompanyId == companyId))
            throw new InvalidOperationException("Target department not found.");
        if (await db.ApprovalRules.AnyAsync(r => r.CompanyId == companyId && r.DepartmentId == toDepartmentId))
            throw new InvalidOperationException("The target already has rules. Delete them first.");

        var source = await db.ApprovalRules.Where(r => r.CompanyId == companyId && r.DepartmentId == fromDepartmentId).ToListAsync();
        if (source.Count == 0) throw new InvalidOperationException("The source has no rules to copy.");

        foreach (var r in source)
            db.ApprovalRules.Add(new ApprovalRule { CompanyId = companyId, DepartmentId = toDepartmentId, UpToAmount = r.UpToAmount, Level = r.Level, ApproverUserId = r.ApproverUserId });
        await db.SaveChangesAsync();
        return source.Count;
    }

    // ---- Approvals on a PO ----

    public async Task<List<PoApprovalRow>> GetApprovalsAsync(int companyId, int poId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rows = await db.PoApprovals.Where(a => a.CompanyId == companyId && a.PurchaseOrderId == poId)
            .OrderBy(a => a.Level).ThenBy(a => a.Id).ToListAsync();
        var userIds = rows.Select(a => a.ApproverUserId).Concat(rows.Where(a => a.DecidedByUserId.HasValue).Select(a => a.DecidedByUserId!.Value)).Distinct().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        return rows.Select(a => new PoApprovalRow(a,
            names.GetValueOrDefault(a.ApproverUserId) ?? "?",
            a.DecidedByUserId.HasValue ? names.GetValueOrDefault(a.DecidedByUserId.Value) ?? "?" : "")).ToList();
    }

    /// <summary>The lowest level that nobody has approved yet; null once every level is approved.</summary>
    public static int? CurrentLevel(IEnumerable<PoApproval> approvals)
    {
        var list = approvals.ToList();
        foreach (var level in list.Select(a => a.Level).Distinct().OrderBy(l => l))
            if (!list.Any(a => a.Level == level && a.Decision == ApprovalDecision.Approved))
                return level;
        return null;
    }

    public async Task<List<PendingApprovalRow>> GetPendingForUserAsync(int companyId, int userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var poIds = await db.PoApprovals
            .Where(a => a.CompanyId == companyId && a.ApproverUserId == userId && a.Decision == ApprovalDecision.Pending)
            .Select(a => a.PurchaseOrderId).Distinct().ToListAsync();
        if (poIds.Count == 0) return new();

        var pos = await db.PurchaseOrders.Where(p => poIds.Contains(p.Id) && p.Status == PoStatus.PendingApproval).ToListAsync();
        var approvals = await db.PoApprovals.Where(a => poIds.Contains(a.PurchaseOrderId)).ToListAsync();
        var supplierNames = await db.Suppliers.Where(s => pos.Select(p => p.SupplierId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var depts = await db.Departments.Where(d => d.CompanyId == companyId).ToDictionaryAsync(d => d.Id, d => d.Name);

        var rows = new List<PendingApprovalRow>();
        foreach (var po in pos.OrderBy(p => p.CreatedDate))
        {
            var mine = approvals.Where(a => a.PurchaseOrderId == po.Id).ToList();
            var current = CurrentLevel(mine);
            if (current is null || !mine.Any(a => a.Level == current && a.ApproverUserId == userId && a.Decision == ApprovalDecision.Pending)) continue;
            rows.Add(new PendingApprovalRow(po, supplierNames.GetValueOrDefault(po.SupplierId) ?? "",
                po.DepartmentId.HasValue ? depts.GetValueOrDefault(po.DepartmentId.Value) ?? "" : "", current.Value, po.Total));
        }
        return rows;
    }

    public async Task<int> GetPendingCountForUserAsync(int companyId, int userId) => (await GetPendingForUserAsync(companyId, userId)).Count;

    public async Task DecideAsync(int companyId, int poId, Actor actor, bool approve, string comment)
    {
        if (actor.UserId is not { } userId) throw new InvalidOperationException("You must be signed in.");
        comment = (comment ?? "").Trim();
        if (!approve && comment == "") throw new InvalidOperationException("Give a reason when rejecting.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.Id == poId && p.CompanyId == companyId)
            ?? throw new InvalidOperationException("Purchase order not found.");
        if (po.Status != PoStatus.PendingApproval)
            throw new InvalidOperationException("This purchase order is no longer waiting for approval.");

        var approvals = await db.PoApprovals.Where(a => a.PurchaseOrderId == poId).ToListAsync();
        var current = CurrentLevel(approvals) ?? throw new InvalidOperationException("Nothing is waiting for approval.");
        var mine = approvals.FirstOrDefault(a => a.Level == current && a.ApproverUserId == userId && a.Decision == ApprovalDecision.Pending)
            ?? throw new InvalidOperationException("It is not your turn to approve this purchase order.");

        mine.Decision = approve ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        mine.DecidedByUserId = userId;
        mine.DecidedAt = DateTime.UtcNow;
        mine.Comment = comment;

        var commentText = comment == "" ? "" : $": {comment}";
        if (!approve)
        {
            po.Status = PoStatus.Rejected;
            AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor, $"REJECTED at level {current}{commentText}");
        }
        else if (CurrentLevel(approvals) is null)
        {
            po.Status = PoStatus.Approved;
            AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor, $"APPROVED at level {current}{commentText}");
            AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor, "APPROVED purchase order fully approved and released to the supplier");
        }
        else
        {
            AuditLog.Add(db, companyId, AuditEntityTypes.PurchaseOrder, po.Id, actor,
                $"APPROVED at level {current}{commentText}; now waiting for level {CurrentLevel(approvals)}");
        }

        await db.SaveChangesAsync();
    }
}
