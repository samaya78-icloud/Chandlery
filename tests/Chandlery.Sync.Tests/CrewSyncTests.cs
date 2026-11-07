using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Sync.Tests;

/// <summary>Crew: the office keeps the people, the vessel holds its own crew and lets them sign in with their crew code.</summary>
public class CrewSyncTests : IAsyncLifetime
{
    private const string Password = "Temp-pass-1";
    private readonly TwoSites _s = new();
    public Task InitializeAsync() => _s.InitializeAsync();
    public Task DisposeAsync() => _s.DisposeAsync();

    private static CrewInput Input(string code, Guid? vessel, bool admin = false, string? idNumber = "E1234567X", Guid? id = null)
        => new(id, code, $"Name of {code}", "Chief Officer", "Singaporean", null, "Passport", idNumber, null, "+65 1234", "", vessel, null, true, admin);

    private async Task<CrewMember> AddCrewAsync(string code, Guid? vessel, bool admin = false, string? password = Password)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, Input(code, vessel, admin), _s.User);
        if (password is not null) await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, crew.Id, password, mustChange: true, _s.User);
        return crew;
    }

    private async Task<T> OnVessel<T>(Func<ChandleryDbContext, Task<T>> read)
    {
        await using var db = await _s.VesselFactory.CreateDbContextAsync();
        return await read(db);
    }

    private async Task<T> OnShore<T>(Func<ChandleryDbContext, Task<T>> read)
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        return await read(db);
    }

    [Fact]
    public async Task ACrewMemberAddedAtTheOffice_ArrivesOnTheirVessel_AndCanSignInWithTheirCode()
    {
        var crew = await AddCrewAsync("c1024", _s.VesselId);
        await _s.SettleUntilQuietAsync();

        var onboard = await OnVessel(db => db.CrewMembers.SingleAsync(c => c.Id == crew.Id));
        Assert.Equal("C1024", onboard.CrewCode);                      // codes are kept in capitals
        Assert.Equal(_s.VesselId, onboard.VesselId);

        // The local sign-in account exists and the crew code works there, but not as an office login.
        var user = await _s.VesselLogin.VerifyLoginAsync("c1024", Password);
        Assert.NotNull(user);
        Assert.Equal(crew.UserId, user.Id);
        Assert.True(user.MustChangePassword);
        Assert.Equal(UserRole.CompanyUser, user.Role);
        Assert.NotNull(await _s.VesselLogin.VerifyLoginAsync("C1024", Password));       // any capitalisation
        Assert.Null(await _s.VesselLogin.VerifyLoginAsync("C1024", "wrong"));
        Assert.Null(await _s.ShoreLogin.VerifyLoginAsync("C1024", Password));           // crew are not office staff
    }

    [Fact]
    public async Task TheIdNumber_IsEncrypted_EverywhereItIsStored_AndOnlyRevealedWithAnAuditTrail()
    {
        var crew = await AddCrewAsync("C2001", _s.VesselId);
        await _s.SettleUntilQuietAsync();

        foreach (var factory in new[] { _s.ShoreFactory, _s.VesselFactory })
        {
            await using var db = await factory.CreateDbContextAsync();
            var row = await db.CrewMembers.SingleAsync(c => c.Id == crew.Id);
            Assert.DoesNotContain("E1234567X", row.IdNumberEnc);
            Assert.StartsWith("v1:", row.IdNumberEnc);
            Assert.Equal("567X", row.IdLast4);
            Assert.Equal("E1234567X", _s.Crypto.Decrypt(row.IdNumberEnc));
        }
        Assert.Equal("E1234567X", await _s.ShoreCrew.RevealIdAsync(_s.CompanyId, crew.Id, _s.User));
        Assert.Contains(await OnShore(db => db.AuditEntries.Where(a => a.EntityId == crew.Id).Select(a => a.Text).ToListAsync()), t => t.Contains("REVEALED"));
    }

    [Fact]
    public async Task ACrewAdminOnboard_ResetsAPassword_AndTheOfficeLearnsOfIt()
    {
        var admin = await AddCrewAsync("MASTER1", _s.VesselId, admin: true);
        var sailor = await AddCrewAsync("AB0007", _s.VesselId);
        await _s.SettleUntilQuietAsync();
        var adminActor = new Actor(admin.UserId, admin.FullName);

        await _s.VesselCrew.SetPasswordAsync(_s.CompanyId, sailor.Id, "New-secret-9", mustChange: true, adminActor);

        Assert.Null(await _s.VesselLogin.VerifyLoginAsync("AB0007", Password));          // the old one is gone at once, onboard
        Assert.NotNull(await _s.VesselLogin.VerifyLoginAsync("AB0007", "New-secret-9"));

        await _s.SettleUntilQuietAsync();
        var atOffice = await OnShore(db => db.CrewMembers.SingleAsync(c => c.Id == sailor.Id));
        Assert.Equal("Name of MASTER1", atOffice.CredentialsChangedBy);                   // who did it travels with the change
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>();
        Assert.NotEqual(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new AppUser(), atOffice.PasswordHash, "New-secret-9"));
        Assert.Equal(0, await _s.DirtyAsync(_s.ShoreFactory));                           // applying it was not announced back
        Assert.Empty(await _s.ShoreSyncStore.GetQuarantineAsync());
    }

    [Fact]
    public async Task OnlyACrewAdmin_CanChangeLogins_AndOnlyForTheirOwnVessel()
    {
        var sailor = await AddCrewAsync("AB0100", _s.VesselId);
        var other = await AddCrewAsync("AB0101", _s.VesselId);
        await _s.SettleUntilQuietAsync();

        // A crew member who is not a crew admin cannot.
        var asSailor = new Actor(sailor.UserId, sailor.FullName);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _s.VesselCrew.SetPasswordAsync(_s.CompanyId, other.Id, "Whatever-123", true, asSailor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _s.VesselCrew.SetLoginEnabledAsync(_s.CompanyId, other.Id, false, asSailor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _s.VesselCrew.RevealIdAsync(_s.CompanyId, other.Id, asSailor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _s.VesselCrew.ListOnboardAsync(asSailor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _s.VesselCrew.SetPasswordAsync(_s.CompanyId, other.Id, "Whatever-123", true, new Actor(null, "nobody")));

        // Nor can they edit people: the vessel holds a copy of the office's record.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.VesselCrew.SaveAsync(_s.CompanyId, Input("AB0100", _s.VesselId, id: sailor.Id), asSailor));
        await using (var db = await _s.VesselFactory.CreateDbContextAsync())
        {
            (await db.CrewMembers.SingleAsync(c => c.Id == sailor.Id)).Rank = "Captain";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.NotNull(await _s.VesselLogin.VerifyLoginAsync("AB0101", Password));   // untouched
    }

    [Fact]
    public async Task ACrewMemberOnAnotherVessel_NeverArrives()
    {
        var elsewhere = await AddCrewAsync("OTHER1", _s.OtherVesselId);
        var unassigned = await AddCrewAsync("SHORE1", null);
        var mine = await AddCrewAsync("MINE1", _s.VesselId);
        await _s.SettleUntilQuietAsync();

        var codes = await OnVessel(db => db.CrewMembers.Select(c => c.CrewCode).ToListAsync());
        Assert.Equal(["MINE1"], codes);
        Assert.Null(await _s.VesselLogin.VerifyLoginAsync("OTHER1", Password));
    }

    [Fact]
    public async Task SigningOff_RemovesThePersonFromTheVessel_AndTheirLoginStopsWorking()
    {
        var crew = await AddCrewAsync("LEAVER", _s.VesselId);
        await _s.SettleUntilQuietAsync();
        Assert.NotNull(await _s.VesselLogin.VerifyLoginAsync("LEAVER", Password));

        await _s.ShoreCrew.SignOffAsync(_s.CompanyId, crew.Id, _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.False(await OnVessel(db => db.CrewMembers.AnyAsync(c => c.Id == crew.Id)));
        Assert.Null(await _s.VesselLogin.VerifyLoginAsync("LEAVER", Password));
        var account = await OnVessel(db => db.Users.SingleAsync(u => u.Id == crew.UserId));      // kept, because documents they made point at it
        Assert.False(account.IsEnabled);
        Assert.Equal("", account.PasswordHash);

        // Joining another vessel later is just an assignment (here the same vessel again).
        await _s.ShoreCrew.SaveAsync(_s.CompanyId, Input("LEAVER", _s.VesselId, id: crew.Id), _s.User);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, crew.Id, "Rejoined-pass-1", true, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.NotNull(await _s.VesselLogin.VerifyLoginAsync("LEAVER", "Rejoined-pass-1"));
    }

    [Fact]
    public async Task WhenBothSidesChangeALogin_TheLatestChangeWins()
    {
        var crew = await AddCrewAsync("RACE1", _s.VesselId, admin: false);
        var admin = await AddCrewAsync("MASTER2", _s.VesselId, admin: true);
        await _s.SettleUntilQuietAsync();
        var adminActor = new Actor(admin.UserId, admin.FullName);

        // The office changes it first, the vessel's crew admin changes it later; both before they have spoken to each other.
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, crew.Id, "Office-first-1", true, _s.User);
        await Task.Delay(50);
        await _s.VesselCrew.SetPasswordAsync(_s.CompanyId, crew.Id, "Vessel-later-1", true, adminActor);
        await _s.SettleUntilQuietAsync();

        Assert.NotNull(await _s.VesselLogin.VerifyLoginAsync("RACE1", "Vessel-later-1"));
        Assert.Null(await _s.VesselLogin.VerifyLoginAsync("RACE1", "Office-first-1"));
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>();
        var atOffice = await OnShore(db => db.CrewMembers.SingleAsync(c => c.Id == crew.Id));
        Assert.NotEqual(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(new AppUser(), atOffice.PasswordHash, "Vessel-later-1"));

        // And the other way round: the vessel first, then the office.
        await _s.VesselCrew.SetLoginEnabledAsync(_s.CompanyId, crew.Id, false, adminActor);
        await Task.Delay(50);
        await _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, crew.Id, "Office-later-1", true, _s.User);
        await _s.ShoreCrew.SetLoginEnabledAsync(_s.CompanyId, crew.Id, true, _s.User);
        await _s.SettleUntilQuietAsync();

        Assert.NotNull(await _s.VesselLogin.VerifyLoginAsync("RACE1", "Office-later-1"));
        Assert.True((await OnShore(db => db.CrewMembers.SingleAsync(c => c.Id == crew.Id))).LoginEnabled);
    }

    [Fact]
    public async Task ADisabledLogin_CannotSignIn_AndAPersonChoosingTheirOwnPassword_ClearsTheTemporaryOne()
    {
        var crew = await AddCrewAsync("SELF1", _s.VesselId);
        await _s.SettleUntilQuietAsync();

        Assert.False(await _s.VesselCrew.ChangeOwnPasswordAsync(crew.UserId, "not-the-password", "My-own-pass-1"));
        Assert.True(await _s.VesselCrew.ChangeOwnPasswordAsync(crew.UserId, Password, "My-own-pass-1"));
        var user = await _s.VesselLogin.VerifyLoginAsync("SELF1", "My-own-pass-1");
        Assert.NotNull(user);
        Assert.False(user.MustChangePassword);
        await _s.SettleUntilQuietAsync();
        Assert.False((await OnShore(db => db.CrewMembers.SingleAsync(c => c.Id == crew.Id))).MustChangePassword);

        await _s.ShoreCrew.SetLoginEnabledAsync(_s.CompanyId, crew.Id, false, _s.User);
        await _s.SettleUntilQuietAsync();
        Assert.Null(await _s.VesselLogin.VerifyLoginAsync("SELF1", "My-own-pass-1"));
    }

    [Fact]
    public async Task CrewCodes_AreUniqueWithinTheCompany_AndDetailsAreValidated()
    {
        await AddCrewAsync("DUP1", _s.VesselId, password: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.ShoreCrew.SaveAsync(_s.CompanyId, Input("dup1", null), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.ShoreCrew.SaveAsync(_s.CompanyId, Input("a", null), _s.User));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.ShoreCrew.SaveAsync(_s.CompanyId, Input("has space", null), _s.User));
        var id = (await OnShore(db => db.CrewMembers.SingleAsync())).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _s.ShoreCrew.SetPasswordAsync(_s.CompanyId, id, "short", true, _s.User));
    }
}
