using System.Text.Json;
using Chandlery.Models;

namespace Chandlery.Services;

public enum PermitState { Requested, Issued, Active, Suspended, Completed, Cancelled, Withdrawn, Expired }

/// <summary>Where the gas test stands on a permit that needs one.</summary>
public enum PermitGasStatus { NotRequired, Missing, OutOfLimits, Overdue, Ok }

public sealed record PermitValidity(bool Valid, string Reason, DateTime Until);

/// <summary>What the issue check reads. Everything is passed in, so the rule needs no database.</summary>
public sealed record PermitIssueFacts(
    PermitTerms Terms, IReadOnlyList<PermitAnswer> Answers, IReadOnlyList<PermitGasReading> Readings, IReadOnlyList<PermitEvent> Events,
    Guid? RequesterId, Guid? IssuerId, bool IssuerMayIssue, Guid? AttendantId, IReadOnlyList<Guid?> PersonIds,
    bool RiskLinked, bool RiskSigned, DateTime ValidFrom, DateTime ValidUntil, DateTime Now);

/// <summary>An active permit, as the conflict check sees it.</summary>
public sealed record PermitOnBoard(Guid Id, Guid TypeId, string Location);

public sealed record PermitConflict(Guid PermitId, string Message);

/// <summary>
/// The rules of permit to work, worked out from what is written and the clock. Pure, so they are tested without a database. Nothing here decides what is safe: the limits,
/// intervals and sign-offs come from the type's terms, which the company sets.
/// </summary>
public static class PermitRules
{
    public static PermitTerms TermsOf(PermitType t) => new(t.MaxValidityHours, t.GasTestRequired, t.GasRepeatMinutes, t.OxygenMin, t.OxygenMax, t.FlammableMaxPercentLel, t.H2sMaxPpm, t.CoMaxPpm,
        t.AttendantRequired, t.EntryLogRequired, t.RiskAssessmentRequired, t.MasterApprovalRequired, t.SameRequesterAndIssuer);

    public static string Serialize(PermitTerms terms) => JsonSerializer.Serialize(terms);

    public static PermitTerms ReadTerms(string json) => JsonSerializer.Deserialize<PermitTerms>(json) ?? throw new InvalidOperationException("The permit has no terms.");

    // ---- window and state ----

    /// <summary>The end of the window now: the latest extension, else the period the permit was issued for.</summary>
    public static DateTime Until(DateTime validUntil, IEnumerable<PermitEvent> events)
        => events.Where(e => e.Kind == PermitEventKind.Extended && e.Until is not null).OrderBy(e => e.At).Select(e => e.Until!.Value).DefaultIfEmpty(validUntil).Last();

    private static IEnumerable<PermitEvent> InOrder(IEnumerable<PermitEvent> events) => events.OrderBy(e => e.At);

    public static PermitState State(IReadOnlyList<PermitEvent> events, DateTime validUntil, DateTime now)
    {
        bool issued = false, started = false, suspended = false;
        var until = Until(validUntil, events);
        foreach (var e in InOrder(events))
        {
            switch (e.Kind)
            {
                case PermitEventKind.Withdrawn: return PermitState.Withdrawn;
                case PermitEventKind.Cancelled: return PermitState.Cancelled;
                case PermitEventKind.Completed: return PermitState.Completed;
                case PermitEventKind.Issued: issued = true; break;
                case PermitEventKind.Started: started = true; suspended = false; break;
                case PermitEventKind.Suspended: suspended = true; break;
                case PermitEventKind.Resumed: suspended = false; break;
            }
        }
        if (issued && now > until) return PermitState.Expired;
        if (suspended) return PermitState.Suspended;
        if (started) return PermitState.Active;
        return issued ? PermitState.Issued : PermitState.Requested;
    }

    public static bool IsOpen(PermitState s) => s is PermitState.Issued or PermitState.Active or PermitState.Suspended or PermitState.Expired;

    // ---- gas ----

    /// <summary>What is outside the limits in one reading. Oxygen and flammable gas must be recorded; toxic gases only when measured.</summary>
    public static List<string> GasFailures(PermitTerms t, decimal? oxygen, decimal? flammablePercentLel, decimal? h2sPpm, decimal? coPpm)
    {
        var f = new List<string>();
        if (oxygen is null) f.Add("Oxygen was not recorded");
        else if (oxygen < t.OxygenMin || oxygen > t.OxygenMax) f.Add($"Oxygen {oxygen:0.0}% is outside {t.OxygenMin:0.0} to {t.OxygenMax:0.0}%");
        if (flammablePercentLel is null) f.Add("Flammable gas was not recorded");
        else if (flammablePercentLel > t.FlammableMaxPercentLel) f.Add($"Flammable gas {flammablePercentLel:0.#}% LEL is above {t.FlammableMaxPercentLel:0.#}%");
        if (h2sPpm > t.H2sMaxPpm) f.Add($"Hydrogen sulphide {h2sPpm:0.#} ppm is above {t.H2sMaxPpm:0.#}");
        if (coPpm > t.CoMaxPpm) f.Add($"Carbon monoxide {coPpm:0.#} ppm is above {t.CoMaxPpm:0.#}");
        return f;
    }

    public static PermitGasReading? LastReading(IEnumerable<PermitGasReading> readings) => readings.OrderBy(r => r.At).LastOrDefault();

    public static PermitGasStatus GasStatus(PermitTerms t, IReadOnlyList<PermitGasReading> readings, DateTime now)
    {
        if (!t.GasTestRequired) return PermitGasStatus.NotRequired;
        var last = LastReading(readings);
        if (last is null) return PermitGasStatus.Missing;
        if (!last.WithinLimits) return PermitGasStatus.OutOfLimits;
        return now > NextGasDue(t, last)!.Value ? PermitGasStatus.Overdue : PermitGasStatus.Ok;
    }

    /// <summary>When the next reading is due, counted from the last one.</summary>
    public static DateTime? NextGasDue(PermitTerms t, PermitGasReading? last) => last is null || !t.GasTestRequired ? null : last.At.AddMinutes(t.GasRepeatMinutes);

    // ---- validity ----

    /// <summary>Whether the permit may be worked under at this moment: started, inside its window, and any required gas reading within limits and in date.</summary>
    public static PermitValidity Validity(PermitTerms t, IReadOnlyList<PermitEvent> events, IReadOnlyList<PermitGasReading> readings, DateTime validFrom, DateTime validUntil, DateTime now)
    {
        var until = Until(validUntil, events);
        var state = State(events, validUntil, now);
        if (state != PermitState.Active) return new(false, state switch
        {
            PermitState.Requested => "Not issued yet",
            PermitState.Issued => "Issued but not started",
            PermitState.Suspended => "Suspended",
            PermitState.Expired => "Expired and not closed",
            _ => state.ToString(),
        }, until);
        if (now < validFrom) return new(false, "Not valid until " + validFrom.ToString("d MMM HH:mm"), until);
        return GasStatus(t, readings, now) switch
        {
            PermitGasStatus.Missing => new(false, "No gas reading", until),
            PermitGasStatus.OutOfLimits => new(false, "Last gas reading outside limits", until),
            PermitGasStatus.Overdue => new(false, "Gas reading overdue", until),
            _ => new(true, "Valid", until),
        };
    }

    // ---- issuing ----

    /// <summary>Everything that stops the permit being issued, each as a line the screen can show. Empty means it can be issued.</summary>
    public static List<string> IssueCheck(PermitIssueFacts f)
    {
        var missing = new List<string>();
        foreach (var a in f.Answers.OrderBy(a => a.Position))
        {
            if (a.Mandatory && a.Answer == PermitAnswerKind.Unanswered) missing.Add($"Not answered: {a.Text}");
            else if (a.Mandatory && a.Answer == PermitAnswerKind.No) missing.Add($"Answered no: {a.Text}");
            else if (a.NoteRequired && a.Answer != PermitAnswerKind.Unanswered && string.IsNullOrWhiteSpace(a.Note)) missing.Add($"Needs a note: {a.Text}");
        }

        if (!f.IssuerMayIssue) missing.Add("The issuer is not authorised to issue this kind of permit");
        if (f.IssuerId is not null && f.IssuerId == f.RequesterId && !f.Terms.SameRequesterAndIssuer) missing.Add("The issuer cannot be the person who requested the permit");

        if (f.Terms.AttendantRequired && f.AttendantId is null) missing.Add("An attendant is not named");
        if (f.AttendantId is not null && f.PersonIds.Contains(f.AttendantId)) missing.Add("The attendant cannot be one of the people doing the work");

        switch (GasStatus(f.Terms, f.Readings, f.Now))
        {
            case PermitGasStatus.Missing: missing.Add("A gas reading is needed"); break;
            case PermitGasStatus.OutOfLimits: missing.Add("The last gas reading is outside the limits"); break;
            case PermitGasStatus.Overdue: missing.Add("The last gas reading is out of date"); break;
        }

        if (f.Terms.RiskAssessmentRequired)
        {
            if (!f.RiskLinked) missing.Add("A risk assessment is not linked");
            else if (!f.RiskSigned) missing.Add("The linked risk assessment is not signed");
        }

        if (f.Terms.MasterApprovalRequired && !f.Events.Any(e => e.Kind == PermitEventKind.MasterApproved)) missing.Add("The Master has not approved it");

        if (f.ValidUntil <= f.ValidFrom) missing.Add("The permit ends before it starts");
        else if (f.ValidUntil - f.ValidFrom > TimeSpan.FromHours(f.Terms.MaxValidityHours)) missing.Add($"The permit is longer than the {f.Terms.MaxValidityHours} hours this kind allows");
        return missing;
    }

    /// <summary>What an extension needs: a reading taken now, within limits, and a window that stays inside the type's maximum from the new start.</summary>
    public static List<string> ExtensionCheck(PermitTerms t, IReadOnlyList<PermitGasReading> readings, DateTime currentUntil, DateTime newUntil, DateTime now)
    {
        var missing = new List<string>();
        if (newUntil <= currentUntil) missing.Add("The new end must be after the current end");
        if (newUntil - now > TimeSpan.FromHours(t.MaxValidityHours)) missing.Add($"A period cannot run longer than {t.MaxValidityHours} hours");
        if (t.GasTestRequired)
        {
            var last = LastReading(readings);
            if (last is null || !last.WithinLimits) missing.Add("A new gas reading within limits is needed");
            else if (now > last.At.AddMinutes(t.GasRepeatMinutes)) missing.Add("The last gas reading is out of date: take a new one");
        }
        return missing;
    }

    // ---- entry log ----

    public static List<PermitEntry> Inside(IEnumerable<PermitEntry> entries) => entries.Where(e => e.Out is null).OrderBy(e => e.In).ToList();

    /// <summary>What stops a permit being closed: anyone still inside the space.</summary>
    public static List<string> CloseBlockers(IEnumerable<PermitEntry> entries) => Inside(entries).Select(e => $"{e.PersonName} is still inside").ToList();

    // ---- who may do what ----

    private static readonly string[] OfficerWords = ["master", "captain", "mate", "officer", "engineer", "eto", "electro"];

    /// <summary>Officers and engineers by rank text. A starting rule, to verify against the company's rank list.</summary>
    public static bool IsOfficer(string rank) => OfficerWords.Any(w => (rank ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));

    public static bool Meets(PermitRole role, bool isMaster, string rank) => role switch
    {
        PermitRole.AnyCrew => true,
        PermitRole.Officer => isMaster || IsOfficer(rank),
        _ => isMaster,
    };

    // ---- simultaneous operations ----

    /// <summary>The permits already on board that conflict with a new one, by the company's pairs.</summary>
    public static List<PermitConflict> Conflicts(Guid typeId, string location, IEnumerable<PermitOnBoard> active, IEnumerable<PermitConflictPair> pairs)
    {
        var found = new List<PermitConflict>();
        var pairList = pairs.ToList();
        foreach (var other in active)
        {
            foreach (var p in pairList)
            {
                var matches = (p.PermitTypeAId == typeId && p.PermitTypeBId == other.TypeId) || (p.PermitTypeBId == typeId && p.PermitTypeAId == other.TypeId);
                if (!matches) continue;
                if (p.SameLocationOnly && !string.Equals(location.Trim(), other.Location.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(new(other.Id, p.Message));
                break;
            }
        }
        return found;
    }
}
