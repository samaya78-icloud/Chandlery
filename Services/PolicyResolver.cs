using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>A decided value and the row that decided it (null row = nothing matched, so the default applied).</summary>
public record Decision<T>(T Value, VesselPolicy? Row, string Source);

/// <summary>The final policy for one vessel, department and category combination.</summary>
public record ResolvedPolicy(
    Decision<bool> CanRequisition,
    Decision<bool> CanPurchaseDirect,
    Decision<decimal?> MasterApprovalLimitUsd,
    Decision<decimal?> MonthlyBudgetUsd);

/// <summary>
/// Works out what a vessel may do. Pure and free of the database, so the same code can run ashore and onboard.
/// Rows are collected from the vessel itself and every group above it. For each capability the most specific row that
/// states it wins: the vessel before its deepest group before the levels above it; at the same level, a row naming a
/// department and category beats one naming only a category, then only a department, then a catch-all. An explicit false
/// overrides inherited allows. Nothing matching means not allowed.
/// </summary>
public static class PolicyResolver
{
    /// <summary>Group ids from the vessel's own node up to its level 1 group.</summary>
    public static List<Guid> GroupChain(IEnumerable<VesselGroup> groups, Guid? groupId)
    {
        var byId = groups.ToDictionary(g => g.Id);
        var chain = new List<Guid>();
        while (groupId.HasValue && byId.TryGetValue(groupId.Value, out var g) && !chain.Contains(g.Id))
        {
            chain.Add(g.Id);
            groupId = g.ParentId;
        }
        return chain;
    }

    public static ResolvedPolicy Resolve(
        Guid vesselId, IReadOnlyList<Guid> groupChain, IEnumerable<VesselPolicy> rows, IReadOnlyCollection<Category> categories,
        IReadOnlyDictionary<Guid, string> groupNames, Guid? departmentId, Guid? categoryId, DateTime today)
    {
        var catById = categories.ToDictionary(c => c.Id);
        // The queried category and everything above it: a row for "Provisions" also covers "Provisions > Dry".
        var catAncestors = new HashSet<Guid>();
        for (var id = categoryId; id.HasValue && catById.TryGetValue(id.Value, out var c) && catAncestors.Add(c.Id); id = c.ParentCategoryId) { }

        int CatDepth(Guid id)
        {
            var depth = 0;
            for (Guid? cur = id; cur.HasValue && catById.TryGetValue(cur.Value, out var c) && c.ParentCategoryId.HasValue; cur = c.ParentCategoryId) depth++;
            return depth;
        }

        var candidates = new List<(int Rank, int Subject, VesselPolicy Row)>();
        foreach (var row in rows)
        {
            if (row.ValidFrom.HasValue && row.ValidFrom.Value.Date > today.Date) continue;
            if (row.ValidTo.HasValue && row.ValidTo.Value.Date < today.Date) continue;

            int rank;
            if (row.VesselId.HasValue) { if (row.VesselId != vesselId) continue; rank = 0; }
            else if (row.GroupId.HasValue) { var idx = groupChain.ToList().IndexOf(row.GroupId.Value); if (idx < 0) continue; rank = 1 + idx; }
            else continue;

            if (row.DepartmentId.HasValue && row.DepartmentId != departmentId) continue;
            if (row.CategoryId.HasValue && !catAncestors.Contains(row.CategoryId.Value)) continue;

            var subject = (row.DepartmentId.HasValue ? 1000 : 0) + (row.CategoryId.HasValue ? 100 + CatDepth(row.CategoryId.Value) : 0);
            candidates.Add((rank, subject, row));
        }

        var ordered = candidates.OrderBy(c => c.Rank).ThenByDescending(c => c.Subject).ToList();

        string Where(VesselPolicy row) => row.VesselId.HasValue
            ? "this vessel"
            : $"group {(groupNames.TryGetValue(row.GroupId!.Value, out var n) ? n : "?")}";

        string Subject(VesselPolicy row) =>
            row.DepartmentId.HasValue && row.CategoryId.HasValue ? "for this department and category"
            : row.CategoryId.HasValue ? "for this category"
            : row.DepartmentId.HasValue ? "for this department" : "for everything";

        Decision<T?> Pick<T>(Func<VesselPolicy, T?> get, string none) where T : struct
        {
            foreach (var c in ordered)
            {
                var value = get(c.Row);
                if (value is not null) return new Decision<T?>(value, c.Row, $"{Where(c.Row)}, {Subject(c.Row)}");
            }
            return new Decision<T?>(null, null, none);
        }

        var req = Pick<bool>(r => r.CanRequisition, "no rule allows it");
        var direct = Pick<bool>(r => r.CanPurchaseDirect, "no rule allows it");
        var limit = Pick<decimal>(r => r.MasterApprovalLimitUsd, "no limit set, the office approves");
        var budget = Pick<decimal>(r => r.MonthlyBudgetUsd, "no budget cap");

        return new ResolvedPolicy(
            new Decision<bool>(req.Value ?? false, req.Row, req.Source),
            new Decision<bool>(direct.Value ?? false, direct.Row, direct.Source),
            new Decision<decimal?>(limit.Value, limit.Row, limit.Source),
            new Decision<decimal?>(budget.Value, budget.Row, budget.Source));
    }

    /// <summary>Whether a module is on for the vessel: the vessel's own switch, else its deepest group's, up the tree; none means off.</summary>
    public static (bool Enabled, string Source) ResolveModule(
        Guid vesselId, IReadOnlyList<Guid> groupChain, IEnumerable<VesselModuleSwitch> switches, IReadOnlyDictionary<Guid, string> groupNames, string module)
    {
        var mine = switches.Where(s => s.Module == module).ToList();
        var own = mine.FirstOrDefault(s => s.VesselId == vesselId);
        if (own is not null) return (own.Enabled, "this vessel");
        for (var i = 0; i < groupChain.Count; i++)
        {
            var hit = mine.FirstOrDefault(s => s.GroupId == groupChain[i]);
            if (hit is not null) return (hit.Enabled, $"group {(groupNames.TryGetValue(groupChain[i], out var n) ? n : "?")}");
        }
        return (false, "no rule switches it on");
    }
}
