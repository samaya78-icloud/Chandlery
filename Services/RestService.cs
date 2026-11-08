using System.Text.Json;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>The checked rest of one seafarer over a period, with the rule set it was checked against.</summary>
public record RestSheet(Guid CrewMemberId, string CrewName, string Rank, List<RestDayResult> Days, RestLimits Limits);

/// <summary>
/// Seafarers' hours: recording a day (always as a new revision), the vessel's day status, and checking a period against the rules.
/// Hours are recorded onboard; the office reads them. Each call is checked here, whatever the page lets through.
/// </summary>
public class RestHoursService(IDbContextFactory<ChandleryDbContext> factory)
{
    // ---- the module switch ----

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.RestHours);

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.RestHours);
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    // ---- rule sets ----

    /// <summary>The rule set that applies to a vessel: its own, else the company default, else the ILO/STCW numbers.</summary>
    public static async Task<(RestRuleSet? Set, RestLimits Limits)> RulesForAsync(ChandleryDbContext db, Guid companyId, Guid vesselId)
    {
        var sets = await db.RestRuleSets.AsNoTracking().Where(r => r.CompanyId == companyId && r.IsActive && (r.VesselId == vesselId || r.VesselId == null)).ToListAsync();
        var chosen = sets.FirstOrDefault(r => r.VesselId == vesselId) ?? sets.FirstOrDefault(r => r.IsDefault) ?? sets.FirstOrDefault();
        return (chosen, chosen is null ? RestLimits.Default : RestLimits.Of(chosen));
    }

    public async Task<List<RestRuleSet>> ListRuleSetsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.RestRuleSets.AsNoTracking().OrderByDescending(r => r.IsDefault).ThenBy(r => r.Name).ToListAsync();
    }

    /// <summary>Creates or changes a rule set (the office's). Exactly one active set per company can be the default.</summary>
    public async Task<RestRuleSet> SaveRuleSetAsync(Guid companyId, RestRuleSet input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new InvalidOperationException("Give the rule set a name.");
        if (input.MinRest24Minutes is < 1 or > 1440 || input.MinRest7Minutes is < 1 or > 10080) throw new InvalidOperationException("The minimum rest must be within 24 hours and within 7 days.");
        if (input.MaxRestPeriods < 1 || input.MinLongestPeriodMinutes < 0 || input.MaxIntervalMinutes < 1) throw new InvalidOperationException("Check the period limits.");
        if (input.SignIntervalDays is < 1 or > 31) throw new InvalidOperationException("Records are signed every 1 to 31 days.");
        if (input.MinorNightLengthMinutes is < 0 or > 1440 || input.MinorNightStartMinute is < 0 or > 1439) throw new InvalidOperationException("Check the protected night hours.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (input.VesselId is { } vesselId && !await db.Vessels.AnyAsync(v => v.Id == vesselId && v.CompanyId == companyId)) throw new InvalidOperationException("That vessel is not in this company.");

        var set = input.Id == Guid.Empty ? null : await db.RestRuleSets.FirstOrDefaultAsync(r => r.Id == input.Id);
        if (set is null)
        {
            set = new RestRuleSet { Id = Guid.CreateVersion7(), CompanyId = companyId };
            db.RestRuleSets.Add(set);
        }
        Copy(input, set);
        if (set.VesselId is not null) set.IsDefault = false;      // the default is the company's; a vessel's set overrides it
        if (set.IsDefault)
            foreach (var other in await db.RestRuleSets.Where(r => r.IsDefault && r.Id != set.Id && r.VesselId == null).ToListAsync())
                other.IsDefault = false;
        await db.SaveChangesAsync();
        return set;
    }

    private static void Copy(RestRuleSet from, RestRuleSet to)
    {
        (to.VesselId, to.Name, to.FlagState) = (from.VesselId, from.Name.Trim(), from.FlagState.Trim());
        (to.MinRest24Minutes, to.MinRest7Minutes, to.MaxRestPeriods, to.MinLongestPeriodMinutes, to.MaxIntervalMinutes) =
            (from.MinRest24Minutes, from.MinRest7Minutes, from.MaxRestPeriods, from.MinLongestPeriodMinutes, from.MaxIntervalMinutes);
        (to.ReductionAllowed, to.ReductionMinConsecutiveMinutes, to.ReductionMaxDays, to.ReductionMin7Minutes) =
            (from.ReductionAllowed, from.ReductionMinConsecutiveMinutes, from.ReductionMaxDays, from.ReductionMin7Minutes);
        (to.MinorNightProtection, to.MinorNightStartMinute, to.MinorNightLengthMinutes) = (from.MinorNightProtection, from.MinorNightStartMinute, from.MinorNightLengthMinutes);
        (to.SignIntervalDays, to.RetentionMonths, to.IsDefault, to.IsActive) = (from.SignIntervalDays, from.RetentionMonths, from.IsDefault, from.IsActive);
    }

    // ---- recording ----

    /// <summary>
    /// Records what a seafarer worked on a date. The first record of a day needs no reason; a change to an earlier record does, and is kept
    /// beside it (nothing is overwritten). Saving what is already recorded does nothing.
    /// </summary>
    public async Task<RestEntry> RecordDayAsync(Guid companyId, Guid vesselId, Guid crewMemberId, DateTime date, string workSpans, Actor actor,
        Guid? enteredByCrewId = null, string reason = "")
    {
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        if (day > DateTime.UtcNow.Date.AddDays(1)) throw new InvalidOperationException("Hours cannot be recorded for a day that has not started.");
        List<WorkSpan> spans;
        try { spans = RestSpans.Normalize(RestSpans.Parse(workSpans)); }
        catch (FormatException ex) { throw new InvalidOperationException(ex.Message); }
        if (RestSpans.Validate(RestSpans.Parse(workSpans)) is { } problem) throw new InvalidOperationException(problem);
        var text = RestSpans.Format(spans);

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Rest hours are not switched on for this vessel.");
        var crew = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewMemberId && c.CompanyId == companyId)
            ?? throw new InvalidOperationException("That seafarer was not found.");
        if (crew.VesselId != vesselId || !crew.IsActive) throw new InvalidOperationException("That seafarer is not signed on to this vessel.");

        var latest = (await db.RestEntries.AsNoTracking().Where(e => e.CrewMemberId == crewMemberId && e.Date == day).ToListAsync()).OrderBy(e => e.EnteredAt).ThenBy(e => e.Id).LastOrDefault();
        if (latest is not null)
        {
            if (latest.WorkSpans == text) return latest;
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Give a reason for changing a day that was already recorded.");
        }

        var now = DateTime.UtcNow;
        if (latest is not null && now <= latest.EnteredAt) now = latest.EnteredAt.AddMilliseconds(1);      // revisions stay in order even if the clock moved back
        var entry = new RestEntry
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CrewMemberId = crewMemberId, CrewName = crew.FullName, Rank = crew.Rank,
            Date = day, WorkSpans = text, Reason = reason.Trim(), EnteredByName = actor.Name, EnteredByCrewId = enteredByCrewId,
            OnBehalf = enteredByCrewId is not null && enteredByCrewId != crewMemberId, EnteredAt = now,
        };
        db.RestEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    /// <summary>Sets what the vessel was doing on a date (at sea, in port, at anchor). A later setting for the same date replaces the earlier one in effect; both are kept.</summary>
    public async Task<VesselDayStatus> SetDayStatusAsync(Guid companyId, Guid vesselId, DateTime date, VesselDayKind kind, string portName, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Rest hours are not switched on for this vessel.");
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var latest = (await db.VesselDayStatuses.AsNoTracking().Where(s => s.VesselId == vesselId && s.Date == day).ToListAsync()).OrderBy(s => s.At).ThenBy(s => s.Id).LastOrDefault();
        var port = kind == VesselDayKind.InPort ? portName.Trim() : "";
        if (latest is not null && latest.Kind == kind && latest.PortName == port) return latest;
        var now = DateTime.UtcNow;
        if (latest is not null && now <= latest.At) now = latest.At.AddMilliseconds(1);
        var status = new VesselDayStatus { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, Date = day, Kind = kind, PortName = port, SetByName = actor.Name, At = now };
        db.VesselDayStatuses.Add(status);
        await db.SaveChangesAsync();
        return status;
    }

    // ---- reading ----

    /// <summary>The latest revision of each date: the days as they stand.</summary>
    public static Dictionary<DateTime, RestEntry> Latest(IEnumerable<RestEntry> entries)
        => entries.GroupBy(e => e.Date.Date).ToDictionary(g => g.Key, g => g.OrderBy(e => e.EnteredAt).ThenBy(e => e.Id).Last());

    public static List<RestDayInput> Inputs(IEnumerable<RestEntry> entries)
        => Latest(entries).Values.Select(e => new RestDayInput(e.Date, RestSpans.Parse(e.WorkSpans))).ToList();

    /// <summary>Every revision of a seafarer's days in a period, oldest first: the history behind the sheet.</summary>
    public async Task<List<RestEntry>> HistoryAsync(Guid companyId, Guid crewMemberId, DateTime from, DateTime to)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (f, t) = (DateTime.SpecifyKind(from.Date, DateTimeKind.Utc), DateTime.SpecifyKind(to.Date, DateTimeKind.Utc));
        return (await db.RestEntries.AsNoTracking().Where(e => e.CrewMemberId == crewMemberId && e.Date >= f && e.Date <= t).ToListAsync())
            .OrderBy(e => e.Date).ThenBy(e => e.EnteredAt).ThenBy(e => e.Id).ToList();
    }

    public async Task<VesselDayStatus?> DayStatusAsync(Guid companyId, Guid vesselId, DateTime date)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        return (await db.VesselDayStatuses.AsNoTracking().Where(s => s.VesselId == vesselId && s.Date == day).ToListAsync()).OrderBy(s => s.At).ThenBy(s => s.Id).LastOrDefault();
    }

    /// <summary>Checks a seafarer's days from <paramref name="from"/> to <paramref name="to"/> against the rules that apply to the vessel.</summary>
    public async Task<RestSheet> CheckAsync(Guid companyId, Guid vesselId, Guid crewMemberId, DateTime from, DateTime to)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var crew = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == crewMemberId && c.CompanyId == companyId);
        var (_, limits) = await RulesForAsync(db, companyId, vesselId);
        var start = DateTime.SpecifyKind(from.Date.AddDays(-7), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc);
        var entries = await db.RestEntries.AsNoTracking().Where(e => e.CrewMemberId == crewMemberId && e.Date >= start && e.Date <= end).ToListAsync();
        var born = crew?.DateOfBirth;
        var days = RestRules.Evaluate(limits, Inputs(entries), from, to, born is { } dob ? date => dob.Date.AddYears(18) > date : null);
        var name = crew?.FullName ?? entries.OrderBy(e => e.EnteredAt).LastOrDefault()?.CrewName ?? "";
        var rank = crew?.Rank ?? entries.OrderBy(e => e.EnteredAt).LastOrDefault()?.Rank ?? "";
        return new RestSheet(crewMemberId, name, rank, days, limits);
    }
}

/// <summary>The starting rule set a company has.</summary>
public static class RestDefaults
{
    public const string DefaultName = "ILO 180 / STCW default";

    /// <summary>Adds (without saving) the default rule set when the company has none. Returns whether it did.</summary>
    public static async Task<bool> AddMissingAsync(ChandleryDbContext db, Guid companyId)
    {
        if (await db.RestRuleSets.IgnoreQueryFilters().AnyAsync(r => r.CompanyId == companyId)) return false;
        db.RestRuleSets.Add(new RestRuleSet { Id = Guid.CreateVersion7(), CompanyId = companyId, Name = DefaultName, IsDefault = true });
        return true;
    }

    public static Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId) => AddMissingAsync(db, companyId);
}

/// <summary>One row of the table of shipboard working arrangements (appendix 3): the position, and when it works at sea and in port, as periods like "04:00-08:00".</summary>
public record ArrangementRow(string Position, string SeaWatchkeeping, string SeaOther, string PortWatchkeeping, string PortOther, string Comments)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int SeaWorkMinutes => RestSpans.Minutes(RestSpans.Normalize(RestSpans.Parse(SeaWatchkeeping).Concat(RestSpans.Parse(SeaOther))));

    [System.Text.Json.Serialization.JsonIgnore]
    public int PortWorkMinutes => RestSpans.Minutes(RestSpans.Normalize(RestSpans.Parse(PortWatchkeeping).Concat(RestSpans.Parse(PortOther))));
}

public record ArrangementInput(DateTime EffectiveFrom, string LawReference, string LimitsText, string OtherRequirements, List<ArrangementRow> Rows);

/// <summary>The working-arrangements table: the office publishes a version, a vessel receives it and cannot change it.</summary>
public class WorkArrangementService(IDbContextFactory<ChandleryDbContext> factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<ArrangementRow> RowsOf(WorkArrangementTable table) => JsonSerializer.Deserialize<List<ArrangementRow>>(table.RowsJson, Json) ?? [];

    public async Task<WorkArrangementTable> PublishVersionAsync(Guid companyId, Guid vesselId, ArrangementInput input, Actor actor)
    {
        if (input.Rows.Count == 0) throw new InvalidOperationException("Add at least one position to the table.");
        foreach (var row in input.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Position)) throw new InvalidOperationException("Every row needs a position or rank.");
            foreach (var spans in new[] { row.SeaWatchkeeping, row.SeaOther, row.PortWatchkeeping, row.PortOther })
            {
                try { if (RestSpans.Validate(RestSpans.Parse(spans)) is { } problem) throw new InvalidOperationException($"{row.Position}: {problem}"); }
                catch (FormatException ex) { throw new InvalidOperationException($"{row.Position}: {ex.Message}"); }
            }
        }

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await db.Vessels.AnyAsync(v => v.Id == vesselId && v.CompanyId == companyId)) throw new InvalidOperationException("That vessel was not found.");
        if (!await RestHoursService.IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Rest hours are not switched on for this vessel.");
        var version = (await db.WorkArrangementTables.Where(t => t.VesselId == vesselId).MaxAsync(t => (int?)t.VersionNo) ?? 0) + 1;
        var table = new WorkArrangementTable
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, VersionNo = version, EffectiveFrom = DateTime.SpecifyKind(input.EffectiveFrom.Date, DateTimeKind.Utc),
            LawReference = input.LawReference.Trim(), LimitsText = input.LimitsText.Trim(), OtherRequirements = input.OtherRequirements.Trim(),
            RowsJson = JsonSerializer.Serialize(input.Rows, Json), CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        db.WorkArrangementTables.Add(table);
        await db.SaveChangesAsync();
        return table;
    }

    /// <summary>The version in force on a date: the latest one that takes effect on or before it.</summary>
    public async Task<WorkArrangementTable?> InForceAsync(Guid companyId, Guid vesselId, DateTime date)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        return (await db.WorkArrangementTables.AsNoTracking().Where(t => t.VesselId == vesselId && t.EffectiveFrom <= day).ToListAsync()).OrderBy(t => t.VersionNo).LastOrDefault();
    }

    public async Task<List<WorkArrangementTable>> VersionsAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.WorkArrangementTables.AsNoTracking().Where(t => t.VesselId == vesselId).OrderByDescending(t => t.VersionNo).ToListAsync();
    }
}
