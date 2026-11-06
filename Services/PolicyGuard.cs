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
