using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>
/// The starting list of permit types, with a generic checklist for each, and two conflict pairs. Everything is "to verify": the limits and intervals against the company's SMS,
/// the checklists against the company's own permit forms (the office replaces these with the real ones).
/// </summary>
public static class PermitDefaults
{
    private const string Verify = " (to verify against the company's SMS and permit forms)";

    private sealed record Def(string Name, string Code, PermitRole Requester, bool Gas, int GasRepeat, bool Attendant, bool EntryLog, bool Risk, bool Master, string Reference, string[] Items);

    private static readonly Def[] Types =
    [
        new("Hot work", "HOT", PermitRole.Officer, true, 60, true, false, true, true, "MCA Code of Safe Working Practices, permit to work" + Verify,
        [
            "Area|The work area has been inspected and the hot work cannot be done in a workshop",
            "Area|Flammable and combustible materials are removed or protected for at least 10 metres",
            "Area|Adjacent spaces, bulkheads, decks and pipework are checked and protected",
            "Area|Fire detection in the area is isolated only as the SMS allows, and the bridge or control room is told",
            "Gas|The space and adjacent spaces are tested and free of flammable gas",
            "Fire|A fire watch is posted for the work and for at least 30 minutes after it",
            "Fire|Fire extinguishers and a charged hose are at hand",
            "Equipment|Cutting and welding equipment, hoses and gauges are in good condition",
            "Equipment|Gas cylinders are secured and flashback arrestors fitted",
            "Close-out|The area has been inspected after the work and is cool and safe",
        ]),
        new("Enclosed space entry", "ENC", PermitRole.Officer, true, 30, true, true, true, true, "IMO A.1050(27), SOLAS XI-1/7" + Verify,
        [
            "Preparation|The space is isolated from pipelines, power and sources of gas",
            "Preparation|The space has been emptied, cleaned and ventilated",
            "Preparation|The entry has been planned and the people entering have been briefed",
            "Atmosphere|The atmosphere has been tested at several levels and is within the limits",
            "Atmosphere|Ventilation continues for as long as people are inside",
            "Rescue|An attendant is posted at the entrance and has a way to raise the alarm",
            "Rescue|Rescue and resuscitation equipment is ready and the rescue plan is known",
            "Rescue|Communication between those inside and the attendant is checked",
            "Equipment|Lighting and equipment used inside are suitable for the atmosphere",
            "Equipment|A personal gas detector is carried by each person entering",
            "Close-out|Everyone has left, the space is closed and the entry log is complete",
        ]),
        new("Working aloft", "ALO", PermitRole.Officer, false, 30, true, false, true, false, "MCA Code of Safe Working Practices" + Verify,
        [
            "Preparation|The weather and the ship's movement allow the work",
            "Preparation|The bridge and the engine room are told, and radar, horns and radio are isolated where needed",
            "Equipment|Harnesses, lanyards and lifelines are inspected and fitted",
            "Equipment|Tools and materials are secured against falling",
            "People|The person aloft is fit for the work and a second person is in attendance",
            "Close-out|The work is complete, systems are restored and the area is cleared",
        ]),
        new("Working overside", "OVS", PermitRole.Officer, false, 30, true, false, true, false, "MCA Code of Safe Working Practices" + Verify,
        [
            "Preparation|The weather, sea state and ship's movement allow the work",
            "Preparation|The engine room is told and the propeller and discharges are stopped or isolated",
            "Equipment|A stage, pilot ladder or boat is rigged and inspected",
            "Equipment|Lifejackets and safety lines are worn and a lifebuoy is ready",
            "People|A watch is kept on those overside and on the ship's movement",
            "Close-out|Everyone is back on board and the equipment is recovered",
        ]),
        new("Electrical isolation and live electrical work", "ELE", PermitRole.Officer, false, 30, true, false, true, false, "Company SMS electrical safety procedure" + Verify,
        [
            "Isolation|The equipment is isolated, locked and tagged at every point of supply",
            "Isolation|Stored energy has been discharged and the equipment tested dead",
            "Isolation|Isolation points are recorded on the permit",
            "People|The people doing the work are competent and briefed",
            "Equipment|Insulated tools and protective equipment are used",
            "Close-out|Tools are removed, tags and locks are taken off only by those who placed them, and the equipment is restored",
        ]),
        new("Pressure testing and opening of pressurised systems", "PRS", PermitRole.Officer, false, 30, false, false, true, false, "Company SMS" + Verify,
        [
            "Isolation|The system is isolated, drained and depressurised before it is opened",
            "Isolation|Valves are locked or tagged and the isolation points are recorded",
            "Test|The test pressure, medium and duration are agreed and the gauge is calibrated",
            "Test|The area is cleared and barriers or signs are posted during the test",
            "Close-out|The system is restored, valves are returned to their normal positions and the area is cleared",
        ]),
        new("Cold work in a hazardous area", "CWH", PermitRole.Officer, true, 60, false, false, false, false, "Company SMS" + Verify,
        [
            "Area|The area has been inspected and the hazard is known",
            "Gas|The atmosphere has been tested and is within the limits",
            "Equipment|Tools and equipment are suitable for the area and do not make sparks",
            "People|The people doing the work are briefed on the hazard and on the alarm",
            "Close-out|The area has been left clean and safe",
        ]),
    ];

    private static readonly (string A, string B, bool SameLocation, string Message)[] Pairs =
    [
        ("Hot work", "Enclosed space entry", false, "Hot work while an enclosed space is open: check for gas, fumes and the route to the open space"),
        ("Hot work", "Cold work in a hazardous area", true, "Hot work in the area where cold work in a hazardous area is going on"),
    ];

    public static async Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId)
    {
        if (await db.PermitTypes.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == companyId)) return;
        var order = 0;
        var ids = new Dictionary<string, Guid>();
        foreach (var d in Types)
        {
            var type = new PermitType
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, Name = d.Name, Code = d.Code, MaxValidityHours = 8, RequesterRole = d.Requester, GasTestRequired = d.Gas, GasRepeatMinutes = d.GasRepeat,
                AttendantRequired = d.Attendant, EntryLogRequired = d.EntryLog, RiskAssessmentRequired = d.Risk, MasterApprovalRequired = d.Master, Reference = d.Reference, SortOrder = ++order, ChecklistVersion = 1,
            };
            db.PermitTypes.Add(type);
            ids[d.Name] = type.Id;
            var position = 0;
            foreach (var line in d.Items)
            {
                var parts = line.Split('|', 2);
                db.PermitChecklistItems.Add(new PermitChecklistItem { Id = Guid.CreateVersion7(), CompanyId = companyId, PermitTypeId = type.Id, Version = 1, Position = ++position, Section = parts[0], Text = parts[1], Mandatory = true });
            }
        }
        foreach (var (a, b, same, message) in Pairs)
            db.PermitConflictPairs.Add(new PermitConflictPair { Id = Guid.CreateVersion7(), CompanyId = companyId, PermitTypeAId = ids[a], PermitTypeBId = ids[b], SameLocationOnly = same, Message = message });
    }
}
