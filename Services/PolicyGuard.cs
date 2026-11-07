using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public enum PolicyAction
{
    /// <summary>Raising a requisition for the vessel.</summary>
    Requisition,

    /// <summary>Buying without a requisition: a spot PO or an RFQ raised directly.</summary>
    DirectPurchase
}

/// <summary>
/// Applies vessel policy to real work. Policy is only enforced once the company has written at least one rule, so a company
/// that has not set any up keeps working exactly as before; from the first rule on, anything not allowed is refused.
/// </summary>
public static class PolicyGuard
{
    private record Loaded(Vessel Vessel, List<Guid> Chain, List<VesselPolicy> Rows, List<Category> Categories, Dictionary<Guid, string> GroupNames);

    private static async Task<Loaded?> LoadAsync(ChandleryDbContext db, Guid companyId, Guid? vesselId)
    {
        if (vesselId is null) return null;
        if (!await db.VesselPolicies.AnyAsync(p => p.CompanyId == companyId)) return null;

        var vessel = await db.Vessels.FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId);
        if (vessel is null) return null;
        var groups = await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
        return new Loaded(
            vessel,
            PolicyResolver.GroupChain(groups, vessel.GroupId),
            await db.VesselPolicies.Where(p => p.CompanyId == companyId).ToListAsync(),
            await db.Categories.Where(c => c.CompanyId == companyId).ToListAsync(),
            groups.ToDictionary(g => g.Id, g => g.Name));
    }

    private static Decision<bool> Decide(Loaded l, Guid? departmentId, Guid categoryId, PolicyAction action)
    {
        var resolved = PolicyResolver.Resolve(l.Vessel.Id, l.Chain, l.Rows, l.Categories, l.GroupNames, departmentId, categoryId, DateTime.UtcNow);
        return action == PolicyAction.Requisition ? resolved.CanRequisition : resolved.CanPurchaseDirect;
    }

    /// <summary>
    /// May the Master approve this order onboard? Needs a Master approval limit (USD) for every category on it and a total within the
    /// tightest of them, and no category's monthly budget used up. With no policy rows at all, or no limit set, the answer is no
    /// (the office decides), never "no limit".
    /// </summary>
    public static async Task<MasterAuthority> MasterAuthorityAsync(ChandleryDbContext db, PurchaseOrder po, decimal totalUsd, DateTime now)
    {
        var loaded = await LoadAsync(db, po.CompanyId, po.VesselId);
        if (loaded is null) return new MasterAuthority(false, "the office has not set an approval limit for this vessel", null);

        var itemIds = po.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => i.CompanyId == po.CompanyId && itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.CategoryId);
        var paths = Category.BuildTree(loaded.Categories).ToDictionary(t => t.Category.Id, t => t.Path);
        var rate = po.UsdRate is > 0 ? po.UsdRate.Value : 1m;
        decimal? tightest = null;
        var budgetsChecked = new HashSet<Guid>();

        // This month's orders for the vessel, for the budget rows.
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var others = await db.PurchaseOrders.Where(p => p.VesselId == po.VesselId && p.Id != po.Id && p.CreatedDate >= monthStart && p.Status != PoStatus.Rejected).ToListAsync();
        var otherItemIds = others.SelectMany(p => p.Lines).Select(l => l.ItemId).Distinct().ToList();
        var otherCategories = await db.Items.Where(i => otherItemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.CategoryId);

        foreach (var categoryId in items.Values.Distinct())
        {
            var resolved = PolicyResolver.Resolve(loaded.Vessel.Id, loaded.Chain, loaded.Rows, loaded.Categories, loaded.GroupNames, po.DepartmentId, categoryId, now);
            var path = paths.GetValueOrDefault(categoryId, "this category");
            if (resolved.MasterApprovalLimitUsd.Value is not { } limit)
                return new MasterAuthority(false, $"no Master approval limit is set for {path}", null);
            tightest = tightest is null ? limit : Math.Min(tightest.Value, limit);

            if (resolved.MonthlyBudgetUsd.Value is { } budget && resolved.MonthlyBudgetUsd.Row is { } row && budgetsChecked.Add(row.Id))
            {
                bool Covered(Guid itemCategory, Guid? orderDepartment)
                    => (row.DepartmentId is null || row.DepartmentId == orderDepartment)
                       && (row.CategoryId is null || CategoryIds.Descendants(loaded.Categories, row.CategoryId.Value).Contains(itemCategory));

                decimal Net(PoLine l) => Money.LineNet(l.Quantity, l.UnitPrice, l.DiscountPct);
                var thisOrder = po.Lines.Where(l => items.TryGetValue(l.ItemId, out var c) && Covered(c, po.DepartmentId)).Sum(Net) / rate;
                var spent = others.Sum(p => p.Lines.Where(l => otherCategories.TryGetValue(l.ItemId, out var c) && Covered(c, p.DepartmentId)).Sum(Net) / (p.UsdRate is > 0 ? p.UsdRate.Value : 1m));
                if (spent + thisOrder > budget)
                    return new MasterAuthority(false, $"the monthly budget for {path} would be exceeded (USD {spent + thisOrder:N2} of {budget:N2})", tightest);
            }
        }

        if (tightest is null) return new MasterAuthority(false, "the order has no items to check", null);
        return totalUsd <= tightest
            ? new MasterAuthority(true, "", tightest)
            : new MasterAuthority(false, $"USD {totalUsd:N2} is above the Master's limit of USD {tightest:N2}", tightest);
    }

    /// <summary>The categories the vessel may use for this action, or null when policy is not in use (everything allowed).</summary>
    public static async Task<HashSet<Guid>?> AllowedCategoriesAsync(ChandleryDbContext db, Guid companyId, Guid? vesselId, Guid? departmentId, PolicyAction action)
    {
        var loaded = await LoadAsync(db, companyId, vesselId);
        if (loaded is null) return null;
        return loaded.Categories.Where(c => Decide(loaded, departmentId, c.Id, action).Value).Select(c => c.Id).ToHashSet();
    }

    /// <summary>Refuses the action when the vessel's policy does not allow it for one of the items' categories, naming the deciding rule.</summary>
    public static async Task EnsureAsync(ChandleryDbContext db, Guid companyId, Guid? vesselId, Guid? departmentId, IEnumerable<Guid> itemIds, PolicyAction action)
    {
        var loaded = await LoadAsync(db, companyId, vesselId);
        if (loaded is null) return;

        var ids = itemIds.Distinct().ToList();
        var categoryIds = await db.Items.Where(i => i.CompanyId == companyId && ids.Contains(i.Id)).Select(i => i.CategoryId).Distinct().ToListAsync();
        var paths = Category.BuildTree(loaded.Categories).ToDictionary(t => t.Category.Id, t => t.Path);

        foreach (var categoryId in categoryIds)
        {
            var decision = Decide(loaded, departmentId, categoryId, action);
            if (decision.Value) continue;

            var what = action == PolicyAction.Requisition ? "raise requisitions" : "buy directly";
            throw new InvalidOperationException(
                $"Policy: {loaded.Vessel.Name} may not {what} for {paths.GetValueOrDefault(categoryId, "this category")} ({(decision.Row is null ? "no rule allows it" : "denied by " + decision.Source)}).");
        }
    }
}
