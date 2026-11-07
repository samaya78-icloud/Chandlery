using Chandlery.Data;
using Chandlery.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record CrewInput(Guid? Id, string CrewCode, string FullName, string Rank, string Nationality, DateTime? DateOfBirth,
    string IdType, string? IdNumber, DateTime? IdExpiry, string Phone, string Email, Guid? VesselId, DateTime? SignOnDate,
    bool IsActive, bool IsCrewAdmin);

public record CrewRow(CrewMember Crew, string? VesselName);

/// <summary>
/// Crew: kept by the office, held (for the vessel they serve on) onboard, where they sign in with their crew code. Everything about the
/// person is the office's to change; the login (password, enabled) can also be changed onboard by a designated crew admin, and the
/// latest change wins. Each call is checked here, whatever the page lets through.
/// </summary>
public class CrewService(IDbContextFactory<ChandleryDbContext> factory, CrewCrypto crypto)
{
    private static readonly PasswordHasher<AppUser> Hasher = new();
    public const int MinPasswordLength = 8;

    // ---- reading ----

    public async Task<List<CrewRow>> ListAsync(Guid companyId, string? search = null, Guid? vesselId = null, bool? signedOn = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.CrewMembers.AsQueryable();
        if (vesselId is { } v) query = query.Where(c => c.VesselId == v);
        if (signedOn == true) query = query.Where(c => c.VesselId != null);
        if (signedOn == false) query = query.Where(c => c.VesselId == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(c => c.CrewCode.ToLower().Contains(s) || c.FullName.ToLower().Contains(s) || c.Rank.ToLower().Contains(s));
        }
        var crew = await query.OrderBy(c => c.FullName).ToListAsync();
        var vessels = await db.Vessels.ToDictionaryAsync(x => x.Id, x => x.Name);
        return crew.Select(c => new CrewRow(c, c.VesselId is { } id ? vessels.GetValueOrDefault(id) : null)).ToList();
    }

    public async Task<CrewMember?> GetAsync(Guid companyId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Is this signed-in user a designated crew admin (on a vessel)?</summary>
    public async Task<bool> IsCrewAdminAsync(Guid userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.CrewMembers.AnyAsync(c => c.UserId == userId && c.IsCrewAdmin && c.IsActive && c.LoginEnabled && c.VesselId != null);
    }

    /// <summary>The crew of the vessel a crew admin serves on.</summary>
    public async Task<List<CrewMember>> ListOnboardAsync(Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        var admin = await RequireCrewAdminAsync(db, actor);
        return await db.CrewMembers.AsNoTracking().Where(c => c.VesselId == admin.VesselId).OrderBy(c => c.FullName).ToListAsync();
    }

    // ---- the office maintains the people ----

    public async Task<CrewMember> SaveAsync(Guid companyId, CrewInput input, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        RequireOffice(db);
        db.CurrentCompanyId = companyId;

        var code = (input.CrewCode ?? "").Trim().ToUpperInvariant();
        if (code.Length < 2 || code.Length > 30 || code.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new InvalidOperationException("The crew code needs 2 to 30 letters, digits, dots, dashes or underscores.");
        if (string.IsNullOrWhiteSpace(input.FullName)) throw new InvalidOperationException("The name is required.");
        if (await db.CrewMembers.AnyAsync(c => c.CrewCode == code && c.Id != input.Id))
            throw new InvalidOperationException($"The crew code {code} is already used.");
        if (input.VesselId is { } vessel && !await db.Vessels.AnyAsync(x => x.Id == vessel))
            throw new InvalidOperationException("That vessel does not exist.");

        var isNew = input.Id is null;
        var crew = isNew
            ? new CrewMember { Id = Guid.CreateVersion7(), CompanyId = companyId, UserId = Guid.CreateVersion7(), CredentialsChangedAt = DateTime.UtcNow, CredentialsChangedBy = actor.Name }
            : await db.CrewMembers.FirstOrDefaultAsync(c => c.Id == input.Id) ?? throw new InvalidOperationException("Crew member not found.");

        var before = isNew ? "" : $"{crew.FullName}/{crew.Rank}/{crew.VesselId}/{crew.IsActive}/{crew.IsCrewAdmin}";
        crew.CrewCode = code;
        crew.FullName = input.FullName.Trim();
        crew.Rank = (input.Rank ?? "").Trim();
        crew.Nationality = (input.Nationality ?? "").Trim();
        crew.DateOfBirth = Utc(input.DateOfBirth);
        crew.IdType = (input.IdType ?? "").Trim();
        crew.IdExpiry = Utc(input.IdExpiry);
        crew.Phone = (input.Phone ?? "").Trim();
        crew.Email = (input.Email ?? "").Trim();
        if (input.IdNumber is not null)    // null leaves the stored number alone
        {
            var number = input.IdNumber.Trim();
            crew.IdNumberEnc = crypto.Encrypt(number);
            crew.IdLast4 = CrewCrypto.Last4(number);
        }
        if (crew.VesselId != input.VesselId && input.VesselId is not null) crew.SignOnDate = Utc(input.SignOnDate) ?? DateTime.UtcNow.Date;
        if (crew.VesselId is not null && input.VesselId is null) crew.SignOffDate = DateTime.UtcNow.Date;
        crew.VesselId = input.VesselId;
        crew.IsActive = input.IsActive;
        crew.IsCrewAdmin = input.IsCrewAdmin;
        if (isNew) db.CrewMembers.Add(crew);

        var after = $"{crew.FullName}/{crew.Rank}/{crew.VesselId}/{crew.IsActive}/{crew.IsCrewAdmin}";
        AuditLog.Add(db, companyId, AuditEntityTypes.CrewMember, crew.Id, actor, isNew ? $"ADDED crew member {crew.CrewCode} {crew.FullName}" : before == after ? $"UPDATED details of {crew.CrewCode}" : $"CHANGED {crew.CrewCode}: [{before}] to [{after}]");
        await db.SaveChangesAsync();
        return crew;
    }

    /// <summary>Takes the person off their vessel. The vessel drops the record and the login stops working there.</summary>
    public async Task SignOffAsync(Guid companyId, Guid crewId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        RequireOffice(db);
        db.CurrentCompanyId = companyId;
        var crew = await db.CrewMembers.FirstOrDefaultAsync(c => c.Id == crewId) ?? throw new InvalidOperationException("Crew member not found.");
        if (crew.VesselId is null) return;
        crew.VesselId = null;
        crew.SignOffDate = DateTime.UtcNow.Date;
        crew.IsCrewAdmin = false;
        AuditLog.Add(db, companyId, AuditEntityTypes.CrewMember, crew.Id, actor, $"SIGNED OFF {crew.CrewCode}");
        await db.SaveChangesAsync();
    }

    /// <summary>The ID number in full. Only for people allowed to see it, and every look is recorded.</summary>
    public async Task<string> RevealIdAsync(Guid companyId, Guid crewId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) await RequireCrewAdminAsync(db, actor, crewId);   // office callers are checked by the page (company admin)
        var crew = await db.CrewMembers.FirstOrDefaultAsync(c => c.Id == crewId) ?? throw new InvalidOperationException("Crew member not found.");
        var number = crypto.Decrypt(crew.IdNumberEnc);
        AuditLog.Add(db, companyId, AuditEntityTypes.CrewMember, crew.Id, actor, $"REVEALED the {(crew.IdType == "" ? "ID" : crew.IdType)} number of {crew.CrewCode}");
        await db.SaveChangesAsync();
        return number;
    }

    // ---- logins: the office, or a crew admin onboard ----

    public async Task SetPasswordAsync(Guid companyId, Guid crewId, string password, bool mustChange, Actor actor)
    {
        if ((password ?? "").Length < MinPasswordLength) throw new InvalidOperationException($"The password needs at least {MinPasswordLength} characters.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var crew = await LoadForLoginChangeAsync(db, crewId, actor);
        crew.PasswordHash = Hasher.HashPassword(new AppUser(), password);
        crew.MustChangePassword = mustChange;
        Stamp(crew, actor.Name);
        AuditLog.Add(db, companyId, AuditEntityTypes.CrewMember, crew.Id, actor, $"RESET the password of {crew.CrewCode}");
        await AfterLoginChangeAsync(db, crew);
    }

    public async Task SetLoginEnabledAsync(Guid companyId, Guid crewId, bool enabled, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var crew = await LoadForLoginChangeAsync(db, crewId, actor);
        crew.LoginEnabled = enabled;
        Stamp(crew, actor.Name);
        AuditLog.Add(db, companyId, AuditEntityTypes.CrewMember, crew.Id, actor, $"{(enabled ? "ENABLED" : "DISABLED")} the login of {crew.CrewCode}");
        await AfterLoginChangeAsync(db, crew);
    }

    /// <summary>A crew member choosing their own password (after a temporary one, or any time). Returns false when the current password is wrong.</summary>
    public async Task<bool> ChangeOwnPasswordAsync(Guid userId, string current, string next)
    {
        if ((next ?? "").Length < MinPasswordLength) throw new InvalidOperationException($"The new password needs at least {MinPasswordLength} characters.");
        await using var db = await factory.CreateDbContextAsync();
        var crew = await db.CrewMembers.FirstOrDefaultAsync(c => c.UserId == userId) ?? throw new InvalidOperationException("This account is not a crew account.");
        if (Hasher.VerifyHashedPassword(new AppUser(), crew.PasswordHash, current) == PasswordVerificationResult.Failed) return false;
        crew.PasswordHash = Hasher.HashPassword(new AppUser(), next);
        crew.MustChangePassword = false;
        Stamp(crew, crew.FullName);
        await AfterLoginChangeAsync(db, crew);
        return true;
    }

    // ---- internals ----

    private static void Stamp(CrewMember crew, string by)
    {
        crew.CredentialsChangedAt = DateTime.UtcNow;
        crew.CredentialsChangedBy = by;
    }

    private async Task<CrewMember> LoadForLoginChangeAsync(ChandleryDbContext db, Guid crewId, Actor actor)
    {
        var crew = await db.CrewMembers.FirstOrDefaultAsync(c => c.Id == crewId) ?? throw new InvalidOperationException("Crew member not found.");
        if (db.Node.IsVessel) await RequireCrewAdminAsync(db, actor, crewId);
        return crew;
    }

    private static async Task AfterLoginChangeAsync(ChandleryDbContext db, CrewMember crew)
    {
        if (db.Node.IsVessel) await CrewAccounts.ApplyAsync(db, crew);   // the local sign-in account follows at once
        await db.SaveChangesAsync();
    }

    /// <summary>Onboard: the actor must be a designated crew admin, and (when a target is given) serve on the same vessel as that person.</summary>
    private static async Task<CrewMember> RequireCrewAdminAsync(ChandleryDbContext db, Actor actor, Guid? targetId = null)
    {
        var admin = actor.UserId is { } uid
            ? await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == uid && c.IsCrewAdmin && c.IsActive && c.LoginEnabled && c.VesselId != null)
            : null;
        if (admin is null) throw new UnauthorizedAccessException("Only a crew admin can do this.");
        if (targetId is { } id && !await db.CrewMembers.AnyAsync(c => c.Id == id && c.VesselId == admin.VesselId))
            throw new UnauthorizedAccessException("That person does not serve on your vessel.");
        return admin;
    }

    private static void RequireOffice(ChandleryDbContext db)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException("Crew records are maintained by the office.");
    }

    private static DateTime? Utc(DateTime? d) => d is null ? null : DateTime.SpecifyKind(d.Value.Date, DateTimeKind.Utc);
}

/// <summary>The local sign-in account of a crew member. Exists only onboard; the office keeps the credentials on the crew record itself.</summary>
public static class CrewAccounts
{
    public static async Task ApplyAsync(ChandleryDbContext db, CrewMember crew)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == crew.UserId);
        if (user is null)
        {
            user = new AppUser
            {
                Id = crew.UserId,
                Email = $"{crew.CrewCode.ToLowerInvariant()}.{crew.CompanyId.ToString("N")[..8]}@crew.invalid",
                Role = UserRole.CompanyUser,
                CompanyId = crew.CompanyId,
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
        }
        user.LoginName = crew.CrewCode;
        user.DisplayName = crew.FullName;
        user.PasswordHash = crew.PasswordHash;
        user.MustChangePassword = crew.MustChangePassword;
        user.IsEnabled = crew.CanSignIn && crew.VesselId is not null;
    }

    /// <summary>The person left this vessel: the account stays (documents they made still point at it) but can no longer sign in.</summary>
    public static async Task DisableAsync(ChandleryDbContext db, Guid userId)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return;
        user.IsEnabled = false;
        user.PasswordHash = "";
    }
}
