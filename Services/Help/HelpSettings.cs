using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>The company-level switch for the help assistant, kept on the company record like the other settings.</summary>
public partial class HelpService
{
    public async Task<bool> GetEnabledAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => (bool?)c.HelpAssistantEnabled).FirstOrDefaultAsync() ?? true;
    }

    /// <summary>Switches the assistant on or off for the whole company. Done by the office.</summary>
    public async Task SetEnabledAsync(Guid companyId, bool on)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The help assistant is switched on or off by the office.");
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId) ?? throw new InvalidOperationException("Company not found.");
        company.HelpAssistantEnabled = on;
        await db.SaveChangesAsync();
    }

    /// <summary>Whether this server can answer at all (a key is set, or Echo mode). The profile page says so beside the switch.</summary>
    public bool IsConfigured => options.IsConfigured;

    /// <summary>True when answers come from the AI; false when this server answers from the built-in help files (no key).</summary>
    public bool UsesAi => options.HasKey && !options.IsLocal && !options.IsEcho;
}
