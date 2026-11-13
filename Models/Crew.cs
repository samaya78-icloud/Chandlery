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

    /// <summary>May start a review of, sign or return a risk assessment onboard (the Master or Chief Engineer, named by the office).</summary>
    public bool IsRiskReviewer { get; set; }

    /// <summary>
    /// What else the office lets this person do onboard, as a comma-separated list of <see cref="CrewAuthority"/> names. A list rather than a
    /// column for each, so the next module that needs "who may do this" adds a name, not a column and a migration.
    /// </summary>
    public string Authorities { get; set; } = "";

    public bool Has(string authority) => CrewAuthority.Has(Authorities, authority);

    /// <summary>The Master, or a deputy the office has named for signing hours of rest. Only the Master holds the Master's own powers (exceptions, the lock).</summary>
    public bool ActsForMaster => IsMaster || Has(CrewAuthority.SignHoursAsDeputy);

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

/// <summary>
/// The things the office can allow a person to do onboard beyond their rank. Stored on <see cref="CrewMember.Authorities"/>.
/// <see cref="Available"/> lists the ones a screen can offer: a module adds its own name here when it starts to use it.
/// </summary>
public static class CrewAuthority
{
    /// <summary>May sign a seafarer's hours of rest for the Master and acknowledge breaches, never their own and never an exception.</summary>
    public const string SignHoursAsDeputy = "SignHoursAsDeputy";

    /// <summary>May record a drill, and a safety meeting, that they conduct. The Master always may; the Master signs.</summary>
    public const string ConductDrills = "ConductDrills";

    /// <summary>May issue permits to work (the kinds the type allows), and approve extensions. The Master always may.</summary>
    public const string IssuePermits = "IssuePermits";

    public sealed record Info(string Name, string Label, string Help);

    public static readonly IReadOnlyList<Info> Available =
    [
        new(SignHoursAsDeputy, "Deputy for the Master on hours of rest", "May sign a seafarer's hours as Master and acknowledge breaches, but not their own hours, and not declare exceptions."),
        new(ConductDrills, "Conducts drills and records meetings", "May record a drill or a safety meeting they conduct. The Master signs it."),
        new(IssuePermits, "Issues permits to work", "May issue a permit to work, check its gas readings and close it. Never for a permit they requested, unless the permit type allows it."),
    ];

    public static IReadOnlySet<string> Parse(string? list)
        => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    public static bool Has(string? list, string authority) => Parse(list).Contains(authority);

    /// <summary>The list as stored: only names this build knows, each once, in a fixed order, so the same set always gives the same text.</summary>
    public static string Normalise(IEnumerable<string>? names)
    {
        var wanted = (names ?? []).ToHashSet(StringComparer.Ordinal);
        return string.Join(',', Available.Select(a => a.Name).Where(wanted.Contains));
    }

    public static string Set(string? list, string authority, bool on)
    {
        var names = Parse(list).ToHashSet(StringComparer.Ordinal);
        if (on) names.Add(authority); else names.Remove(authority);
        return Normalise(names);
    }
}
