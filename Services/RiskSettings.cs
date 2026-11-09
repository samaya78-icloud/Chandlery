using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>The risk assessment settings the office keeps: who may sign ashore and whether the writer may sign their own work.</summary>
public partial class RiskLibraryService
{
    public async Task<bool> GetSignOffByAnotherPersonAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.RiskSignOffByAnotherPerson).FirstOrDefaultAsync();
    }

    /// <summary>When on, the writer of an assessment cannot sign it if another reviewer is available on that side.</summary>
    public async Task SetSignOffByAnotherPersonAsync(Guid companyId, bool on)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId) ?? throw new InvalidOperationException("Company not found.");
        company.RiskSignOffByAnotherPerson = on;
        await db.SaveChangesAsync();
    }

    /// <summary>Names (or removes) an office user as a risk reviewer. A company admin is always one, so the flag changes nothing for them.</summary>
    public async Task SetRiskReviewerAsync(Guid companyId, Guid userId, bool isReviewer)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.CompanyId == companyId && (u.Role == UserRole.CompanyAdmin || u.Role == UserRole.CompanyUser))
            ?? throw new InvalidOperationException("User not found.");
        user.IsRiskReviewer = isReviewer;
        await db.SaveChangesAsync();
    }
}
