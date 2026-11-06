using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>A policy row with the names a person needs to read it.</summary>
public record PolicyRow(VesselPolicy Policy, string Target, string Department, string Category);

/// <summary>One department line of the policy matrix: the target's own row (if any) and what it inherits when that row says nothing.</summary>
public record MatrixRow(Guid? DepartmentId, string Department, VesselPolicy? Own, ResolvedPolicy Inherited);

/// <summary>What the "why can't this vessel buy this?" view shows.</summary>
public record EffectiveView(string Vessel, string GroupPath, ResolvedPolicy Policy, List<(string Module, bool Enabled, string Source)> Modules);

/// <summary>Vessel group tree (up to three levels, level 1 mandatory) and the policy rows attached to it.</summary>
public class VesselGroupService(IDbContextFactory<ChandleryDbContext> factory)
{
    public const string DefaultGroupName = "Fleet";

    // ---------- groups ----------

    /// <summary>All groups; creates the level 1 "Fleet" group (and puts any unplaced vessels in it) when the company has none.</summary>
    public async Task<List<VesselGroup>> GetGroupsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        await EnsureDefaultAsync(db, companyId);
        return await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
    }

    private static async Task EnsureDefaultAsync(ChandleryDbContext db, Guid companyId)
    {
        var hasTop = await db.VesselGroups.AnyAsync(g => g.CompanyId == companyId && g.ParentId == null);
        VesselGroup? top = null;
        if (!hasTop)
        {
            top = new VesselGroup { CompanyId = companyId, Name = DefaultGroupName, Level = 1 };
            db.VesselGroups.Add(top);
        }

        var unplaced = await db.Vessels.Where(v => v.CompanyId == companyId && v.GroupId == null).ToListAsync();
        if (unplaced.Count > 0)
        {
            top ??= await db.VesselGroups.Where(g => g.CompanyId == companyId && g.ParentId == null).OrderBy(g => g.SortOrder).ThenBy(g => g.Name).FirstAsync();
            foreach (var vessel in unplaced) vessel.GroupId = top.Id;
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
    }

    /// <summary>The group a new vessel goes into when none is chosen: the first level 1 group.</summary>
    internal static async Task<Guid> DefaultGroupIdAsync(ChandleryDbContext db, Guid companyId)
    {
        await EnsureDefaultAsync(db, companyId);
        return await db.VesselGroups.Where(g => g.CompanyId == companyId && g.ParentId == null)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).Select(g => g.Id).FirstAsync();
    }

    public async Task<VesselGroup> AddGroupAsync(Guid companyId, Guid? parentId, string name, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        await EnsureDefaultAsync(db, companyId);

        name = CleanName(name);
        var level = 1;
        if (parentId.HasValue)
        {
            var parent = await db.VesselGroups.FirstOrDefaultAsync(g => g.Id == parentId && g.CompanyId == companyId)
                ?? throw new InvalidOperationException("The parent group was not found.");
            level = parent.Level + 1;
            if (level > VesselGroup.MaxLevel)
                throw new InvalidOperationException($"Groups can only go {VesselGroup.MaxLevel} levels deep.");
        }
        await EnsureUniqueNameAsync(db, companyId, parentId, name, null);

        var group = new VesselGroup { CompanyId = companyId, ParentId = parentId, Name = name, Level = level };
        db.VesselGroups.Add(group);
        AuditLog.Add(db, companyId, AuditEntityTypes.VesselGroup, group.Id, actor, $"CREATED group {name} at level {level}");
        await db.SaveChangesAsync();
        return group;
    }

    public async Task RenameGroupAsync(Guid companyId, Guid groupId, string name, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var group = await db.VesselGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new InvalidOperationException("Group not found.");
        name = CleanName(name);
        await EnsureUniqueNameAsync(db, companyId, group.ParentId, name, groupId);
        var note = AuditLog.Changed("name", group.Name, name);
        group.Name = name;
        if (note is not null) AuditLog.Add(db, companyId, AuditEntityTypes.VesselGroup, groupId, actor, note);
        await db.SaveChangesAsync();
    }

    /// <summary>Moves a group (with everything under it) to a new parent, or to the top when parentId is null.</summary>
    public async Task MoveGroupAsync(Guid companyId, Guid groupId, Guid? parentId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var all = await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
        var group = all.FirstOrDefault(g => g.Id == groupId) ?? throw new InvalidOperationException("Group not found.");
        if (group.ParentId == parentId) return;

        var below = VesselGroup.Descendants(all, groupId);
        if (parentId.HasValue && below.Contains(parentId.Value))
            throw new InvalidOperationException("A group cannot be moved inside itself.");

        var newLevel = 1;
        if (parentId.HasValue)
            newLevel = (all.FirstOrDefault(g => g.Id == parentId) ?? throw new InvalidOperationException("The new parent was not found.")).Level + 1;

        var shift = newLevel - group.Level;
        var deepest = all.Where(g => below.Contains(g.Id)).Max(g => g.Level) + shift;
        if (deepest > VesselGroup.MaxLevel)
            throw new InvalidOperationException($"That move would put a group below level {VesselGroup.MaxLevel}.");
        await EnsureUniqueNameAsync(db, companyId, parentId, group.Name, groupId);

        var from = group.ParentId is null ? "the top" : all.First(g => g.Id == group.ParentId).Name;
        var to = parentId is null ? "the top" : all.First(g => g.Id == parentId).Name;
        group.ParentId = parentId;
        foreach (var g in all.Where(g => below.Contains(g.Id))) g.Level += shift;
        AuditLog.Add(db, companyId, AuditEntityTypes.VesselGroup, groupId, actor, $"MOVED group {group.Name} from {from} to {to}");
        await db.SaveChangesAsync();
    }

    public async Task DeleteGroupAsync(Guid companyId, Guid groupId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var group = await db.VesselGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new InvalidOperationException("Group not found.");

        if (await db.VesselGroups.AnyAsync(g => g.ParentId == groupId))
            throw new InvalidOperationException("Move or delete the groups inside it first.");
        if (await db.Vessels.AnyAsync(v => v.GroupId == groupId))
            throw new InvalidOperationException("Move its vessels to another group first.");
        if (group.ParentId is null && await db.VesselGroups.CountAsync(g => g.CompanyId == companyId && g.ParentId == null) == 1)
            throw new InvalidOperationException("At least one top level group is required.");

        db.VesselPolicies.RemoveRange(db.VesselPolicies.Where(p => p.GroupId == groupId));
        db.VesselModuleSwitches.RemoveRange(db.VesselModuleSwitches.Where(m => m.GroupId == groupId));
        db.VesselGroups.Remove(group);
        AuditLog.Add(db, companyId, AuditEntityTypes.VesselGroup, groupId, actor, $"DELETED group {group.Name}");
        await db.SaveChangesAsync();
    }

    public async Task AssignVesselAsync(Guid companyId, Guid vesselId, Guid groupId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var vessel = await db.Vessels.FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId)
            ?? throw new InvalidOperationException("Vessel not found.");
        var group = await db.VesselGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.CompanyId == companyId)
            ?? throw new InvalidOperationException("Group not found.");
        if (vessel.GroupId == groupId) return;
        vessel.GroupId = groupId;
        AuditLog.Add(db, companyId, AuditEntityTypes.VesselGroup, groupId, actor, $"ASSIGNED vessel {vessel.Name} to group {group.Name}");
        await db.SaveChangesAsync();
    }

    private static string CleanName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 80) throw new InvalidOperationException("Enter a group name of up to 80 characters.");
        return name;
    }

    private static async Task EnsureUniqueNameAsync(ChandleryDbContext db, Guid companyId, Guid? parentId, string name, Guid? exceptId)
    {
        if (await db.VesselGroups.AnyAsync(g => g.CompanyId == companyId && g.ParentId == parentId && g.Id != exceptId && g.Name.ToLower() == name.ToLower()))
            throw new InvalidOperationException($"There is already a group called {name} here.");
    }

    // ---------- policy rows ----------

    public async Task<List<PolicyRow>> GetPolicyRowsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rows = await db.VesselPolicies.Where(p => p.CompanyId == companyId).ToListAsync();
        var groups = (await db.VesselGroups.ToListAsync());
        var tree = VesselGroup.BuildTree(groups).ToDictionary(t => t.Group.Id, t => t.Path);
        var vessels = await db.Vessels.ToDictionaryAsync(v => v.Id, v => v.Name);
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var cats = Category.BuildTree(await db.Categories.ToListAsync()).ToDictionary(t => t.Category.Id, t => t.Path);

        return rows.Select(p => new PolicyRow(
                p,
                p.VesselId.HasValue ? "Vessel: " + vessels.GetValueOrDefault(p.VesselId.Value, "?") : "Group: " + tree.GetValueOrDefault(p.GroupId ?? Guid.Empty, "?"),
                p.DepartmentId.HasValue ? depts.GetValueOrDefault(p.DepartmentId.Value, "?") : "All departments",
                p.CategoryId.HasValue ? cats.GetValueOrDefault(p.CategoryId.Value, "?") : "All categories"))
            .OrderBy(r => r.Target).ThenBy(r => r.Department).ThenBy(r => r.Category).ToList();
    }

    public async Task SavePolicyAsync(Guid companyId, VesselPolicy input, Actor actor)
    {
        if (input.GroupId.HasValue == input.VesselId.HasValue)
            throw new InvalidOperationException("Choose either a group or a vessel.");
        if (input.CanRequisition is null && input.CanPurchaseDirect is null && input.MasterApprovalLimitUsd is null && input.MonthlyBudgetUsd is null)
            throw new InvalidOperationException("Set at least one of the rights, the Master limit or the budget.");
        if (input.MasterApprovalLimitUsd < 0 || input.MonthlyBudgetUsd < 0)
            throw new InvalidOperationException("Amounts cannot be negative.");
        if (input.ValidFrom.HasValue && input.ValidTo.HasValue && input.ValidTo < input.ValidFrom)
            throw new InvalidOperationException("The end date is before the start date.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        if (input.GroupId.HasValue && !await db.VesselGroups.AnyAsync(g => g.Id == input.GroupId && g.CompanyId == companyId))
            throw new InvalidOperationException("Group not found.");
        if (input.VesselId.HasValue && !await db.Vessels.AnyAsync(v => v.Id == input.VesselId && v.CompanyId == companyId))
            throw new InvalidOperationException("Vessel not found.");
        if (input.DepartmentId.HasValue && !await db.Departments.AnyAsync(d => d.Id == input.DepartmentId && d.CompanyId == companyId))
            throw new InvalidOperationException("Department not found.");
        if (input.CategoryId.HasValue && !await db.Categories.AnyAsync(c => c.Id == input.CategoryId && c.CompanyId == companyId))
            throw new InvalidOperationException("Category not found.");

        // One row per target and subject: saving the same combination updates it.
        var existing = await db.VesselPolicies.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.GroupId == input.GroupId && p.VesselId == input.VesselId
            && p.DepartmentId == input.DepartmentId && p.CategoryId == input.CategoryId && p.Id != input.Id);
        if (existing is not null)
            throw new InvalidOperationException("A rule for that target and subject already exists; edit it instead.");

        VesselPolicy row;
        var created = input.Id == Guid.Empty;
        if (created)
        {
            row = new VesselPolicy { CompanyId = companyId };
            db.VesselPolicies.Add(row);
        }
        else
        {
            row = await db.VesselPolicies.FirstOrDefaultAsync(p => p.Id == input.Id && p.CompanyId == companyId)
                ?? throw new InvalidOperationException("Rule not found.");
        }

        row.GroupId = input.GroupId;
        row.VesselId = input.VesselId;
        row.DepartmentId = input.DepartmentId;
        row.CategoryId = input.CategoryId;
        row.CanRequisition = input.CanRequisition;
        row.CanPurchaseDirect = input.CanPurchaseDirect;
        row.MasterApprovalLimitUsd = input.MasterApprovalLimitUsd;
        row.MonthlyBudgetUsd = input.MonthlyBudgetUsd;
        row.ValidFrom = input.ValidFrom?.Date;
        row.ValidTo = input.ValidTo?.Date;
        row.Note = (input.Note ?? "").Trim();

        AuditLog.Add(db, companyId, AuditEntityTypes.VesselPolicy, (row.GroupId ?? row.VesselId)!.Value, actor,
            $"{(created ? "ADDED" : "CHANGED")} rule: requisition {Tri(row.CanRequisition)}, direct purchase {Tri(row.CanPurchaseDirect)}, " +
            $"Master limit {(row.MasterApprovalLimitUsd?.ToString("N2") ?? "-")}, monthly budget {(row.MonthlyBudgetUsd?.ToString("N2") ?? "-")} USD");
        await db.SaveChangesAsync();
    }

    public async Task DeletePolicyAsync(Guid companyId, Guid policyId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var row = await db.VesselPolicies.FirstOrDefaultAsync(p => p.Id == policyId && p.CompanyId == companyId) ?? throw new InvalidOperationException("Rule not found.");
        db.VesselPolicies.Remove(row);
        AuditLog.Add(db, companyId, AuditEntityTypes.VesselPolicy, (row.GroupId ?? row.VesselId)!.Value, actor, "REMOVED a rule");
        await db.SaveChangesAsync();
    }

    private static string Tri(bool? value) => value is null ? "-" : value.Value ? "allowed" : "denied";

    // ---------- office user scope ----------

    public async Task<Dictionary<Guid, HashSet<Guid>>> GetAllUserScopesAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var rows = await db.UserGroupScopes.Where(s => s.CompanyId == companyId).ToListAsync();
        return rows.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.Select(s => s.GroupId).ToHashSet());
    }

    public async Task<HashSet<Guid>> GetUserScopeAsync(Guid companyId, Guid userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await db.UserGroupScopes.Where(s => s.CompanyId == companyId && s.UserId == userId).Select(s => s.GroupId).ToListAsync()).ToHashSet();
    }

    /// <summary>Replaces the groups an office user is limited to. An empty set removes the limit.</summary>
    public async Task SetUserScopeAsync(Guid companyId, Guid userId, IEnumerable<Guid> groupIds, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.CompanyId == companyId) ?? throw new InvalidOperationException("User not found.");
        var wanted = groupIds.Distinct().ToList();
        var groups = await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
        if (wanted.Any(id => groups.All(g => g.Id != id))) throw new InvalidOperationException("Group not found.");

        var existing = await db.UserGroupScopes.Where(s => s.CompanyId == companyId && s.UserId == userId).ToListAsync();
        db.UserGroupScopes.RemoveRange(existing.Where(s => !wanted.Contains(s.GroupId)));
        foreach (var id in wanted.Where(id => existing.All(s => s.GroupId != id)))
            db.UserGroupScopes.Add(new UserGroupScope { CompanyId = companyId, UserId = userId, GroupId = id });

        var names = string.Join(", ", groups.Where(g => wanted.Contains(g.Id)).Select(g => g.Name));
        AuditLog.Add(db, companyId, AuditEntityTypes.VesselPolicy, userId, actor, $"SCOPE of {user.DisplayName} set to {(wanted.Count == 0 ? "all vessels" : names)}");
        await db.SaveChangesAsync();
    }

    /// <summary>The vessels a user may see, or null when the user is not limited to any group.</summary>
    internal static async Task<HashSet<Guid>?> ScopedVesselIdsAsync(ChandleryDbContext db, Guid companyId, Guid? userId)
    {
        if (userId is null) return null;
        var scope = await db.UserGroupScopes.Where(s => s.CompanyId == companyId && s.UserId == userId).Select(s => s.GroupId).ToListAsync();
        if (scope.Count == 0) return null;

        var groups = await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
        var groupIds = new HashSet<Guid>();
        foreach (var id in scope) groupIds.UnionWith(VesselGroup.Descendants(groups, id));
        return (await db.Vessels.Where(v => v.CompanyId == companyId && v.GroupId != null && groupIds.Contains(v.GroupId.Value)).Select(v => v.Id).ToListAsync()).ToHashSet();
    }

    /// <summary>Refuses work on a vessel outside the user's groups.</summary>
    internal static async Task EnsureVesselInScopeAsync(ChandleryDbContext db, Guid companyId, Guid? userId, Guid? vesselId)
    {
        if (vesselId is null) return;
        var allowed = await ScopedVesselIdsAsync(db, companyId, userId);
        if (allowed is not null && !allowed.Contains(vesselId.Value))
            throw new InvalidOperationException("That vessel is outside the vessel groups you are responsible for.");
    }

    // ---------- matrix ----------

    /// <summary>Department-level rows for a group or vessel, each with the values inherited from the groups above.</summary>
    public async Task<List<MatrixRow>> GetMatrixAsync(Guid companyId, Guid targetId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var groups = await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
        var vessel = await db.Vessels.FirstOrDefaultAsync(v => v.Id == targetId && v.CompanyId == companyId);
        var isGroup = vessel is null && groups.Any(g => g.Id == targetId);
        if (vessel is null && !isGroup) throw new InvalidOperationException("Group or vessel not found.");

        var chain = vessel is not null
            ? PolicyResolver.GroupChain(groups, vessel.GroupId)
            : PolicyResolver.GroupChain(groups, groups.First(g => g.Id == targetId).ParentId);
        var vesselKey = vessel?.Id ?? Guid.Empty;
        var all = await db.VesselPolicies.Where(p => p.CompanyId == companyId).ToListAsync();
        var own = all.Where(p => vessel is not null ? p.VesselId == targetId : p.GroupId == targetId).ToList();
        var inheritedRows = all.Except(own).ToList();
        var cats = await db.Categories.Where(c => c.CompanyId == companyId).ToListAsync();
        var names = groups.ToDictionary(g => g.Id, g => g.Name);
        var depts = await db.Departments.Where(d => d.CompanyId == companyId).OrderBy(d => d.Name).ToListAsync();

        var result = new List<MatrixRow> { Row(null, "All departments") };
        result.AddRange(depts.Select(d => Row(d.Id, d.Name)));
        return result;

        MatrixRow Row(Guid? deptId, string name) => new(
            deptId, name,
            own.FirstOrDefault(p => p.DepartmentId == deptId && p.CategoryId == null),
            PolicyResolver.Resolve(vesselKey, chain,
                // A department line also falls back on this target's own "all departments" row.
                deptId is null ? inheritedRows : inheritedRows.Concat(own.Where(p => p.DepartmentId == null && p.CategoryId == null)).ToList(),
                cats, names, deptId, null, DateTime.UtcNow));
    }

    /// <summary>Sets one matrix cell. Field is "requisition", "direct", "limit" or "budget"; a null value clears it, and a row left empty is removed.</summary>
    public async Task SetMatrixCellAsync(Guid companyId, Guid targetId, Guid? departmentId, string field, bool? flag, decimal? amount, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var isVessel = await db.Vessels.AnyAsync(v => v.Id == targetId && v.CompanyId == companyId);
        if (!isVessel && !await db.VesselGroups.AnyAsync(g => g.Id == targetId && g.CompanyId == companyId))
            throw new InvalidOperationException("Group or vessel not found.");
        if (departmentId.HasValue && !await db.Departments.AnyAsync(d => d.Id == departmentId && d.CompanyId == companyId))
            throw new InvalidOperationException("Department not found.");
        if (amount < 0) throw new InvalidOperationException("Amounts cannot be negative.");

        var row = await db.VesselPolicies.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.DepartmentId == departmentId && p.CategoryId == null
            && (isVessel ? p.VesselId == targetId : p.GroupId == targetId));
        if (row is null)
        {
            if (flag is null && amount is null) return;
            row = new VesselPolicy { CompanyId = companyId, GroupId = isVessel ? null : targetId, VesselId = isVessel ? targetId : null, DepartmentId = departmentId };
            db.VesselPolicies.Add(row);
        }

        switch (field)
        {
            case "requisition": row.CanRequisition = flag; break;
            case "direct": row.CanPurchaseDirect = flag; break;
            case "limit": row.MasterApprovalLimitUsd = amount; break;
            case "budget": row.MonthlyBudgetUsd = amount; break;
            default: throw new InvalidOperationException("Unknown field.");
        }

        var dept = departmentId.HasValue ? (await db.Departments.FirstAsync(d => d.Id == departmentId)).Name : "all departments";
        var shown = field is "requisition" or "direct" ? Tri(flag) : amount?.ToString("N2") ?? "-";
        AuditLog.Add(db, companyId, AuditEntityTypes.VesselPolicy, targetId, actor, $"SET {field} for {dept} to {shown}");

        if (row.CanRequisition is null && row.CanPurchaseDirect is null && row.MasterApprovalLimitUsd is null && row.MonthlyBudgetUsd is null)
        {
            if (db.Entry(row).State != EntityState.Added) db.VesselPolicies.Remove(row);
            else db.Entry(row).State = EntityState.Detached;
        }
        await db.SaveChangesAsync();
    }

    // ---------- modules ----------

    public async Task<List<VesselModuleSwitch>> GetModuleSwitchesAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.VesselModuleSwitches.Where(m => m.CompanyId == companyId).ToListAsync();
    }

    /// <summary>Sets a module on, off, or back to inherited (enabled = null) for a group or a vessel.</summary>
    public async Task SetModuleAsync(Guid companyId, Guid? groupId, Guid? vesselId, string module, bool? enabled, Actor actor)
    {
        if (groupId.HasValue == vesselId.HasValue) throw new InvalidOperationException("Choose either a group or a vessel.");
        if (!PolicyModules.All.Contains(module)) throw new InvalidOperationException("Unknown module.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var row = await db.VesselModuleSwitches.FirstOrDefaultAsync(m => m.CompanyId == companyId && m.GroupId == groupId && m.VesselId == vesselId && m.Module == module);
        if (enabled is null)
        {
            if (row is not null) db.VesselModuleSwitches.Remove(row);
        }
        else if (row is null)
            db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = companyId, GroupId = groupId, VesselId = vesselId, Module = module, Enabled = enabled.Value });
        else
            row.Enabled = enabled.Value;

        AuditLog.Add(db, companyId, AuditEntityTypes.VesselPolicy, (groupId ?? vesselId)!.Value, actor,
            $"MODULE {module} set to {(enabled is null ? "inherited" : enabled.Value ? "on" : "off")}");
        await db.SaveChangesAsync();
    }

    /// <summary>The categories the vessel may use for an action, or null when the company has no policy rules yet.</summary>
    public async Task<HashSet<Guid>?> AllowedCategoryIdsAsync(Guid companyId, Guid vesselId, Guid? departmentId, PolicyAction action)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await PolicyGuard.AllowedCategoriesAsync(db, companyId, vesselId, departmentId, action);
    }

    // ---------- effective policy ----------

    public async Task<EffectiveView> GetEffectiveAsync(Guid companyId, Guid vesselId, Guid? departmentId, Guid? categoryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        await EnsureDefaultAsync(db, companyId);

        var vessel = await db.Vessels.FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId) ?? throw new InvalidOperationException("Vessel not found.");
        var groups = await db.VesselGroups.Where(g => g.CompanyId == companyId).ToListAsync();
        var chain = PolicyResolver.GroupChain(groups, vessel.GroupId);
        var names = groups.ToDictionary(g => g.Id, g => g.Name);
        var rows = await db.VesselPolicies.Where(p => p.CompanyId == companyId).ToListAsync();
        var switches = await db.VesselModuleSwitches.Where(m => m.CompanyId == companyId).ToListAsync();
        var cats = await db.Categories.Where(c => c.CompanyId == companyId).ToListAsync();

        var policy = PolicyResolver.Resolve(vesselId, chain, rows, cats, names, departmentId, categoryId, DateTime.UtcNow);
        var modules = PolicyModules.All.Select(m =>
        {
            var (enabled, source) = PolicyResolver.ResolveModule(vesselId, chain, switches, names, m);
            return (m, enabled, source);
        }).ToList();
        var path = VesselGroup.BuildTree(groups).FirstOrDefault(t => t.Group.Id == vessel.GroupId).Path ?? "";
        return new EffectiveView(vessel.Name, path, policy, modules);
    }
}
