using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>A policy row with the names a person needs to read it.</summary>
public record PolicyRow(VesselPolicy Policy, string Target, string Department, string Category);

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
