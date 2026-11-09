using Chandlery.Services;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

internal static class RiskTestSupport
{
    /// <summary>Makes the test office user a risk reviewer, and adds a crew member onboard who is one too (they reach the vessel through sync). Returns the crew member as an actor.</summary>
    public static async Task<Actor> MakeReviewersAsync(TwoSites s, string crewCode = "RAREV1", string name = "Capt. Reviewer")
    {
        await using (var db = await s.ShoreFactory.CreateDbContextAsync())
        {
            var user = await db.Users.FirstAsync(u => u.Id == s.User.UserId);
            user.IsRiskReviewer = true;
            await db.SaveChangesAsync();
        }
        var crew = await s.ShoreCrew.SaveAsync(s.CompanyId, new CrewInput(null, crewCode, name, "Master", "", null, "", null, null, "", "", s.VesselId, null, true, false, IsRiskReviewer: true), s.User);
        await s.SettleUntilQuietAsync();
        return new Actor(crew.UserId, crew.FullName);
    }
}
