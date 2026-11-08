using Chandlery.Data;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>Which vessels a module is switched on for: its own switch, else its group's, up the tree; none means off. One reading of the vessels, groups and switches serves them all.</summary>
public static class ModuleGate
{
    public static async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(ChandleryDbContext db, Guid companyId, string module)
    {
        var vessels = await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).OrderBy(v => v.Name).ToListAsync();
        if (vessels.Count == 0) return [];
        var groups = await db.VesselGroups.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync();
        var switches = await db.VesselModuleSwitches.AsNoTracking().Where(m => m.CompanyId == companyId).ToListAsync();
        var names = groups.ToDictionary(g => g.Id, g => g.Name);
        return vessels.Where(v => PolicyResolver.ResolveModule(v.Id, PolicyResolver.GroupChain(groups, v.GroupId), switches, names, module).Enabled)
            .Select(v => (v.Id, v.Name)).ToList();
    }

    public static async Task<bool> IsEnabledAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, string module)
    {
        var vessel = await db.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId);
        if (vessel is null) return false;
        var groups = await db.VesselGroups.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync();
        var switches = await db.VesselModuleSwitches.AsNoTracking().Where(m => m.CompanyId == companyId).ToListAsync();
        return PolicyResolver.ResolveModule(vessel.Id, PolicyResolver.GroupChain(groups, vessel.GroupId), switches, groups.ToDictionary(g => g.Id, g => g.Name), module).Enabled;
    }
}
