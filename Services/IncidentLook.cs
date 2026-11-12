using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>The words and chip tones for incident reports, in one place so every screen says the same thing.</summary>
public static class IncidentLook
{
    public static string Kind(IncidentKind kind) => kind switch
    {
        IncidentKind.NearMiss => "Near miss",
        IncidentKind.UnsafeAct => "Unsafe act",
        IncidentKind.UnsafeCondition => "Unsafe condition",
        IncidentKind.Observation => "Observation or suggestion",
        IncidentKind.InjuryIllness => "Injury or illness",
        IncidentKind.HazardousOccurrence => "Hazardous occurrence",
        IncidentKind.Pollution => "Pollution",
        IncidentKind.EquipmentDamage => "Damage to equipment or property",
        IncidentKind.Security => "Security",
        _ => "Non-conformity",
    };

    /// <summary>One line on what each kind is for, so the reporter picks the right one.</summary>
    public static string KindHelp(IncidentKind kind) => kind switch
    {
        IncidentKind.NearMiss => "Nobody was hurt and nothing was damaged, but it could have been.",
        IncidentKind.UnsafeAct => "Someone did something that put themselves or others at risk.",
        IncidentKind.UnsafeCondition => "Something onboard is not safe as it is.",
        IncidentKind.Observation => "Good practice seen, or an idea to make the ship safer.",
        IncidentKind.InjuryIllness => "Someone was hurt or fell ill.",
        IncidentKind.HazardousOccurrence => "Something dangerous happened, whether or not anyone was hurt.",
        IncidentKind.Pollution => "Any release of oil, chemicals, garbage or emissions beyond the rules.",
        IncidentKind.EquipmentDamage => "Equipment or property was damaged.",
        IncidentKind.Security => "A security breach or threat.",
        _ => "Something that does not meet the company's procedures or a requirement.",
    };

    public static string ShipStatus(IncidentShipStatus status) => status switch
    {
        IncidentShipStatus.AtSea => "At sea",
        IncidentShipStatus.InPort => "In port",
        IncidentShipStatus.Anchored => "At anchor",
        _ => "In the shipyard",
    };

    public static string Role(IncidentPersonRole role) => role switch
    {
        IncidentPersonRole.Injured => "Injured or ill",
        IncidentPersonRole.Witness => "Witness",
        _ => "Involved",
    };

    public static string Treatment(IncidentTreatment treatment) => treatment switch
    {
        IncidentTreatment.None => "None",
        IncidentTreatment.FirstAid => "First aid",
        IncidentTreatment.MedicalTreatment => "Medical treatment",
        IncidentTreatment.Hospital => "Hospital",
        _ => "Evacuated",
    };

    public static string Assessment(IncidentAssessmentFinding finding) => finding switch
    {
        IncidentAssessmentFinding.Adequate => "The risk assessment was adequate",
        IncidentAssessmentFinding.Inadequate => "The risk assessment was not adequate",
        IncidentAssessmentFinding.NoneExisted => "There was no risk assessment",
        _ => "Not looked at",
    };

    public static (string Label, string Tone) Status(IncidentStatus status) => status switch
    {
        IncidentStatus.Draft => (IncidentStatusRules.Label(status), "neutral"),
        IncidentStatus.Reported => (IncidentStatusRules.Label(status), "warning"),
        IncidentStatus.Reviewed => (IncidentStatusRules.Label(status), "info"),
        IncidentStatus.Investigating => (IncidentStatusRules.Label(status), "info"),
        IncidentStatus.ActionsOpen => (IncidentStatusRules.Label(status), "warning"),
        IncidentStatus.ReadyToClose => (IncidentStatusRules.Label(status), "success"),
        IncidentStatus.Closed => (IncidentStatusRules.Label(status), "success"),
        _ => (IncidentStatusRules.Label(status), "neutral"),
    };

    /// <summary>How an event reads on the timeline.</summary>
    public static string Event(IncidentEventKind kind) => kind switch
    {
        IncidentEventKind.Reviewed => "Reviewed by the Master and sent to the office",
        IncidentEventKind.InvestigationOpened => "Investigation opened",
        IncidentEventKind.NoInvestigationNeeded => "No investigation needed",
        IncidentEventKind.VesselFindings => "The vessel's findings",
        IncidentEventKind.NotificationRecorded => "Notification recorded",
        IncidentEventKind.Closed => "Closed",
        IncidentEventKind.Reopened => "Reopened",
        _ => "Withdrawn",
    };

    public static string When(DateTime at) => at.ToString("d MMM yyyy HH:mm");
}
