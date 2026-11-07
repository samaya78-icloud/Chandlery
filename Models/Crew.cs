namespace Chandlery.Models;

/// <summary>
/// A person who serves on the company's vessels. The office keeps the record (name, rank, passport/ID, which vessel); the vessel
/// they serve on receives it through sync and lets them sign in with their crew code. The credential columns are the one part a
/// vessel may change (a crew admin resets a password onboard); the latest change wins on either side.
/// </summary>
public class CrewMember
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    /// <summary>The login name onboard. Unique within the company.</summary>
    public string CrewCode { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Rank { get; set; } = "";
    public string Nationality { get; set; } = "";
    public DateTime? DateOfBirth { get; set; }

    /// <summary>"Passport", "NRIC", "Seaman's book" and so on.</summary>
    public string IdType { get; set; } = "";

    /// <summary>The ID number, encrypted (AES-GCM, see <c>CrewCrypto</c>). Travels through sync in this form only.</summary>
    public string IdNumberEnc { get; set; } = "";

    /// <summary>The last four characters of the ID number, so screens can show it masked without decrypting.</summary>
    public string IdLast4 { get; set; } = "";
    public DateTime? IdExpiry { get; set; }
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";

    /// <summary>The vessel they serve on now. Empty when signed off; the vessel loses the record when this changes.</summary>
    public Guid? VesselId { get; set; }
    public DateTime? SignOnDate { get; set; }
    public DateTime? SignOffDate { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>May reset passwords and enable or disable logins of the crew on their own vessel.</summary>
    public bool IsCrewAdmin { get; set; }

    /// <summary>The Master: approves purchase orders onboard within the limit the office sets for the vessel.</summary>
    public bool IsMaster { get; set; }

    /// <summary>The local sign-in account (<see cref="AppUser"/>) onboard. The id is fixed here so both sides agree.</summary>
    public Guid UserId { get; set; }

    // ---- credentials: changed by the office or by the vessel's crew admin; the latest change wins ----
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public bool LoginEnabled { get; set; } = true;
    public DateTime CredentialsChangedAt { get; set; }
    public string CredentialsChangedBy { get; set; } = "";

    /// <summary>The columns a vessel installation may change on a crew record.</summary>
    public static readonly string[] CredentialColumns =
        [nameof(PasswordHash), nameof(MustChangePassword), nameof(LoginEnabled), nameof(CredentialsChangedAt), nameof(CredentialsChangedBy)];

    public bool CanSignIn => LoginEnabled && IsActive && PasswordHash != "";
}
