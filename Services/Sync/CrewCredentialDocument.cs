using System.Text.Json;
using Chandlery.Models;

namespace Chandlery.Services.Sync;

/// <summary>
/// A crew member's login as changed onboard (a crew admin reset the password, or disabled the login, or the person chose a new
/// password), on its way to the office. The office keeps whichever change is latest. The version is the change time, so a newer
/// change always outranks an older one.
/// </summary>
public sealed record CrewCredentialDocument(Guid CrewId, Guid CompanyId, string PasswordHash, bool MustChangePassword, bool LoginEnabled,
    DateTime ChangedAt, string ChangedBy)
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CrewCredentialDocument From(CrewMember c)
        => new(c.Id, c.CompanyId, c.PasswordHash, c.MustChangePassword, c.LoginEnabled, DateTime.SpecifyKind(c.CredentialsChangedAt, DateTimeKind.Utc), c.CredentialsChangedBy);

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static CrewCredentialDocument FromJson(string json) => JsonSerializer.Deserialize<CrewCredentialDocument>(json, Json) ?? throw new JsonException("Empty credential document.");

    public Marine.Sync.SyncDocument ToSyncDocument(long hlc, string originNode)
        => new(SyncDocTypes.CrewCredential, CrewId, ChangedAt.Ticks, originNode, hlc, SyncStages.Vessel, SchemaVersion, ToJson());
}
