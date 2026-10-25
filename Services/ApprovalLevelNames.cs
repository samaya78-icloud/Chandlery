using Chandlery.Data;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>The company's names for approval steps 1 to 3, falling back to plain wording when a step has no custom name.</summary>
public sealed class ApprovalLevelNames(string?[] custom)
{
    private static readonly string[] Defaults = { "First approval", "Second approval", "Final approval" };

    public const int MaxLength = 40;

    public string Name(int level)
    {
        var index = Math.Clamp(level, 1, 3) - 1;
        return string.IsNullOrWhiteSpace(custom[index]) ? Defaults[index] : custom[index]!.Trim();
    }

    /// <summary>The raw saved values (blank where the default applies), for the settings form.</summary>
    public string[] Raw() => custom.Select(c => c ?? "").ToArray();

    public static string Default(int level) => Defaults[Math.Clamp(level, 1, 3) - 1];

    public static async Task<ApprovalLevelNames> LoadAsync(ChandleryDbContext db, int companyId)
    {
        var c = await db.Companies.Where(x => x.Id == companyId)
            .Select(x => new { x.ApprovalLevel1Name, x.ApprovalLevel2Name, x.ApprovalLevel3Name }).FirstOrDefaultAsync();
        return new ApprovalLevelNames(new[] { c?.ApprovalLevel1Name, c?.ApprovalLevel2Name, c?.ApprovalLevel3Name });
    }
}
