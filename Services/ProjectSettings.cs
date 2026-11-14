using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public partial class ProjectService
{
    public async Task<bool> GetSuperintendentSignOffAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.ProjectSuperintendentSignOff).FirstOrDefaultAsync();
    }

    /// <summary>When on, a project that has a superintendent needs that person's own sign-off on every budget, variation and extension, as well as the matrix steps.</summary>
    public async Task SetSuperintendentSignOffAsync(Guid companyId, bool on)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId) ?? throw new InvalidOperationException("Company not found.");
        company.ProjectSuperintendentSignOff = on;
        await db.SaveChangesAsync();
    }
}
