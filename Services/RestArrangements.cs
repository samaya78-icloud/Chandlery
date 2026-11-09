using System.Text.Json;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>One row of the table of shipboard working arrangements (appendix 3): the position, and when it works at sea and in port, as periods like "04:00-08:00".</summary>
public record ArrangementRow(string Position, string SeaWatchkeeping, string SeaOther, string PortWatchkeeping, string PortOther, string Comments)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int SeaWorkMinutes => RestSpans.Minutes(RestSpans.Normalize(RestSpans.Parse(SeaWatchkeeping).Concat(RestSpans.Parse(SeaOther))));

    [System.Text.Json.Serialization.JsonIgnore]
    public int PortWorkMinutes => RestSpans.Minutes(RestSpans.Normalize(RestSpans.Parse(PortWatchkeeping).Concat(RestSpans.Parse(PortOther))));
}

public record ArrangementInput(DateTime EffectiveFrom, string LawReference, string LimitsText, string OtherRequirements, List<ArrangementRow> Rows);

/// <summary>One kind of day (at sea or in port) in one row of the table: the hours worked and rested, and what the limits say about that day repeated.</summary>
public sealed record ArrangementDayCheck(int WorkMinutes, int RestMinutes, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

public sealed record ArrangementRowCheck(ArrangementRow Row, ArrangementDayCheck Sea, ArrangementDayCheck Port)
{
    public bool Ok => Sea.Ok && Port.Ok;
}

/// <summary>
/// Checks a row of the table against the rule set. A day of the table is imagined repeated for a fortnight and the same engine that checks real hours
/// is run over it, so a table that could never be worked without a breach shows it before it is posted.
/// </summary>
public static class ArrangementRules
{
    private static readonly DateTime Origin = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static ArrangementDayCheck CheckDay(string watchkeeping, string other, RestLimits limits)
    {
        List<WorkSpan> spans;
        try
        {
            var parsed = RestSpans.Parse(watchkeeping).Concat(RestSpans.Parse(other)).ToList();
            spans = RestSpans.Normalize(parsed);
            if (RestSpans.Validate(spans) is { } bad) return new ArrangementDayCheck(0, RestSpans.DayMinutes, [bad]);
        }
        catch (FormatException ex) { return new ArrangementDayCheck(0, RestSpans.DayMinutes, [ex.Message]); }

        var work = RestSpans.Minutes(spans);
        var days = Enumerable.Range(0, 15).Select(i => new RestDayInput(Origin.AddDays(i), spans)).ToList();
        var last = RestRules.Evaluate(limits, days, Origin.AddDays(8), Origin.AddDays(14)).LastOrDefault();
        var problems = last is null ? [] : last.Findings.Select(Words).Distinct().ToList();
        return new ArrangementDayCheck(work, RestSpans.DayMinutes - work, problems);
    }

    /// <summary>The finding's own message names a date and time, which means nothing on a table that stands for every day; these say it without.</summary>
    private static string Words(RestFinding f) => f.Rule switch
    {
        RestRule.Rest24 => $"Rest in 24 hours is {RestRules.Hm(f.Actual)}; the minimum is {RestRules.Hm(f.Limit)}.",
        RestRule.Rest7 => $"Rest in 7 days is {RestRules.Hm(f.Actual)}; the minimum is {RestRules.Hm(f.Limit)}.",
        RestRule.Interval => $"The time between rest periods is {RestRules.Hm(f.Actual)}; the most allowed is {RestRules.Hm(f.Limit)}.",
        _ => f.Message,
    };

    public static ArrangementRowCheck Check(ArrangementRow row, RestLimits limits)
        => new(row, CheckDay(row.SeaWatchkeeping, row.SeaOther, limits), CheckDay(row.PortWatchkeeping, row.PortOther, limits));
}

/// <summary>Everything the printed table (appendix 3) shows: the header, the rows with their daily totals, and whether the Master has signed.</summary>
public sealed record ArrangementDocument(
    string CompanyName, string VesselName, string? ImoNumber, string FlagState, string RuleSetName,
    WorkArrangementTable Table, List<ArrangementRowCheck> Rows, WorkArrangementSignOff? Signature)
{
    public bool Signed => Signature is not null;
    public int Problems => Rows.Sum(r => r.Sea.Problems.Count + r.Port.Problems.Count);
}

/// <summary>The working-arrangements table: the office publishes a version, a vessel receives it and cannot change it; the Master signs it onboard.</summary>
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
        if (input.Rows.Select(r => r.Position.Trim().ToLowerInvariant()).Distinct().Count() != input.Rows.Count) throw new InvalidOperationException("A position appears twice in the table.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await db.Vessels.AnyAsync(v => v.Id == vesselId && v.CompanyId == companyId)) throw new InvalidOperationException("That vessel was not found.");
        if (!await RestHoursService.IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Rest hours are not switched on for this vessel.");
        var version = (await db.WorkArrangementTables.Where(t => t.VesselId == vesselId).MaxAsync(t => (int?)t.VersionNo) ?? 0) + 1;
        var table = new WorkArrangementTable
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, VersionNo = version, EffectiveFrom = DateTime.SpecifyKind(input.EffectiveFrom.Date, DateTimeKind.Utc),
            LawReference = input.LawReference.Trim(), LimitsText = input.LimitsText.Trim(), OtherRequirements = input.OtherRequirements.Trim(),
            RowsJson = JsonSerializer.Serialize(input.Rows.Select(r => r with { Position = r.Position.Trim(), Comments = r.Comments.Trim() }), Json), CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
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

    /// <summary>The limits that apply to a vessel, for checking a table as it is written, and the same limits in words for the table's header.</summary>
    public async Task<(RestLimits Limits, string Words, string FlagState)> LimitsAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (set, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        return (limits, Describe(limits), set?.FlagState ?? "");
    }

    public static string Describe(RestLimits l)
        => $"At least {RestRules.Hm(l.MinRest24)} of rest in any 24 hours and {RestRules.Hm(l.MinRest7)} in any 7 days; rest in no more than {l.MaxPeriods} period(s), one of at least {RestRules.Hm(l.MinLongest)}; no more than {RestRules.Hm(l.MaxInterval)} between rest periods.";

    /// <summary>The Master's signatures on a vessel's versions, newest first.</summary>
    public async Task<List<WorkArrangementSignOff>> SignaturesAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.WorkArrangementSignOffs.AsNoTracking().Where(s => s.VesselId == vesselId).OrderByDescending(s => s.SignedAt).ToListAsync();
    }

    /// <summary>
    /// One version of the table ready to show or print (the one in force today when no version is named, else the newest), with each row checked against
    /// the vessel's rule set. Null when the vessel has no table.
    /// </summary>
    public async Task<ArrangementDocument?> DocumentAsync(Guid companyId, Guid vesselId, int? versionNo = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var vessel = await db.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId);
        if (vessel is null) return null;
        var tables = (await db.WorkArrangementTables.AsNoTracking().Where(t => t.VesselId == vesselId).ToListAsync()).OrderBy(t => t.VersionNo).ToList();
        var today = DateTime.UtcNow.Date;
        var table = versionNo is { } n ? tables.FirstOrDefault(t => t.VersionNo == n) : tables.LastOrDefault(t => t.EffectiveFrom <= today) ?? tables.LastOrDefault();
        if (table is null) return null;
        var (set, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        var company = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        var signs = await db.WorkArrangementSignOffs.AsNoTracking().Where(s => s.TableId == table.Id).ToListAsync();
        return new ArrangementDocument(company, vessel.Name, vessel.ImoNumber, set?.FlagState ?? "", set?.Name ?? "ILO 180 / STCW defaults", table,
            RowsOf(table).Select(r => ArrangementRules.Check(r, limits)).ToList(), signs.OrderBy(s => s.SignedAt).LastOrDefault());
    }

    /// <summary>The Master signs a version of the table onboard. Only the Master of the vessel, and only the newest version; signing again does nothing.</summary>
    public async Task<WorkArrangementSignOff> SignAsync(Guid companyId, Guid vesselId, Actor actor, Guid tableId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var me = actor.UserId is null ? null : await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.UserId == actor.UserId && c.VesselId != null && c.IsActive);
        if (me is null) throw new InvalidOperationException("The Master signs the table onboard.");
        if (me.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        if (!me.IsMaster) throw new InvalidOperationException("Only the Master signs the working-arrangements table.");
        if (!await RestHoursService.IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Rest hours are not switched on for this vessel.");
        var table = await db.WorkArrangementTables.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tableId && t.VesselId == vesselId) ?? throw new InvalidOperationException("That version of the table was not found on this vessel.");
        var newest = await db.WorkArrangementTables.Where(t => t.VesselId == vesselId).MaxAsync(t => t.VersionNo);
        if (table.VersionNo != newest) throw new InvalidOperationException($"Version {table.VersionNo} has been replaced by version {newest}. Sign the newest version.");
        var already = await db.WorkArrangementSignOffs.FirstOrDefaultAsync(s => s.TableId == tableId);
        if (already is not null) return already;

        var sign = new WorkArrangementSignOff
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, TableId = tableId, VersionNo = table.VersionNo,
            SignedByName = me.FullName, SignedByCrewId = me.Id, SignedAt = DateTime.UtcNow,
        };
        db.WorkArrangementSignOffs.Add(sign);
        await db.SaveChangesAsync();
        return sign;
    }
}
