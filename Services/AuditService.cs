using System.Security.Claims;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>Who did something, for the audit history. Pages build one from the signed-in user's claims.</summary>
public record Actor(int? UserId, string Name)
{
    public static Actor From(ClaimsPrincipal user)
    {
        var id = int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsed) ? parsed : (int?)null;
        return new Actor(id, user.Identity?.Name ?? "");
    }

    public static readonly Actor System = new(null, "System");
}

public static class AuditEntityTypes
{
    public const string Requisition = "Requisition";
    public const string Rfq = "Rfq";
    public const string PurchaseOrder = "PurchaseOrder";
}

public static class AuditLog
{
    /// <summary>Adds an entry to the caller's context so it is saved in the same transaction as the change itself.</summary>
    public static void Add(ChandleryDbContext db, int companyId, string entityType, int entityId, Actor actor, string text)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            CompanyId = companyId,
            EntityType = entityType,
            EntityId = entityId,
            UserId = actor.UserId,
            UserName = actor.Name,
            At = DateTime.UtcNow,
            Text = text
        });
    }

    /// <summary>"CHANGED Remarks from [a] to [b]" — or null when nothing changed.</summary>
    public static string? Changed(string field, string? from, string? to)
    {
        from ??= "";
        to ??= "";
        return from == to ? null : $"CHANGED {field} from [{from}] to [{to}]";
    }
}

/// <summary>Reads the audit history for the detail pages.</summary>
public class AuditService(IDbContextFactory<ChandleryDbContext> factory)
{
    public async Task<List<AuditEntry>> GetAsync(int companyId, string entityType, int entityId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.AuditEntries
            .Where(a => a.CompanyId == companyId && a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .ToListAsync();
    }
}
