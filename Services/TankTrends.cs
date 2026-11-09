using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>How an answer is shown on the trend matrix and chart: green, amber, red, or grey for an answer that says nothing (not inspected, not applicable).</summary>
public enum CellTone { None, Good, Fair, Bad }

/// <summary>How serious an exception is. A Bad rating is its own class; Fair is the other rating exception; anything else (a "No", a low percentage) is Other.</summary>
public enum ExceptionSeverity { Bad, Fair, Other }

public record TrendColumn(Guid InspectionId, string Code, DateTime InspectedOn, InspectionStatus Status, int ExceptionCount, bool HasBadRating);

public record TrendCell(string Value, string Comment, bool IsException, CellTone Tone);

/// <summary>One criterion across the inspections. Cells line up with the columns; null means the criterion was not on that inspection's checklist.</summary>
public record TrendRow(CriterionGroup Group, string Name, ScaleKind Scale, IReadOnlyList<TrendCell?> Cells)
{
    public bool HasScores => Cells.Any(c => c is not null && TankScales.Score(Scale, c.Value) is not null);
}

/// <param name="TotalInspections">Submitted inspections of the tank; the columns may be only the latest of them.</param>
public record TankTrendData(Tank Tank, List<TrendColumn> Columns, List<TrendRow> Rows, int TotalInspections);

public record TrendPoint(DateTime On, string Code, string Value, int Score, CellTone Tone);

public record ExceptionRow(Guid InspectionId, string InspectionCode, DateTime InspectedOn, InspectionStatus Status, Guid VesselId, string VesselName, Guid TankId, string TankCode,
    string TankName, TankType TankType, CriterionGroup Group, string Criterion, string Value, ExceptionSeverity Severity, string Comment, Guid? WorklistTaskId);

/// <param name="CurrentOnly">True: only what each tank's latest submitted inspection found (what is wrong now). False: every submitted inspection, optionally since a date.</param>
public record ExceptionFilter(IReadOnlyCollection<Guid>? VesselIds = null, IReadOnlyCollection<TankType>? Types = null, IReadOnlyCollection<ExceptionSeverity>? Severities = null,
    bool CurrentOnly = true, DateTime? Since = null);

/// <summary>The numbers behind the tiles and the menu.</summary>
public record TankCounts(int WaitingReview, int WaitingBad, int WithVessel, int CurrentExceptions, int TanksWithBad, int NeverInspected);

/// <summary>Pure rules for the trend matrix, the chart and the exception list, so both sides and the tests read them the same way.</summary>
public static class TankTrend
{
    public static CellTone Tone(ScaleKind scale, string? value, string? exceptionValues) => (scale, value) switch
    {
        (_, null or "" or "NI" or "NA") => CellTone.None,
        (ScaleKind.Rating, "Good") => CellTone.Good,
        (ScaleKind.Rating, "Fair") => CellTone.Fair,
        (ScaleKind.Rating, "Bad") => CellTone.Bad,
        (ScaleKind.PercentBucket, "76-100") => CellTone.Good,
        (ScaleKind.PercentBucket, "0-25") => CellTone.Bad,
        (ScaleKind.PercentBucket, _) => CellTone.Fair,
        _ => TankScales.IsException(scale, exceptionValues, value) ? CellTone.Fair : CellTone.Good,
    };

    public static string Colour(CellTone tone) => tone switch
    {
        CellTone.Good => "#10b981", CellTone.Fair => "#f59e0b", CellTone.Bad => "#f43f5e", _ => "#94a3b8",
    };

    public static ExceptionSeverity SeverityOf(ScaleKind scale, string? value)
        => scale == ScaleKind.Rating && value == "Bad" ? ExceptionSeverity.Bad : scale == ScaleKind.Rating && value == "Fair" ? ExceptionSeverity.Fair : ExceptionSeverity.Other;

    public static string Label(ExceptionSeverity severity) => severity switch { ExceptionSeverity.Bad => "Bad rating", ExceptionSeverity.Fair => "Fair rating", _ => "Other" };

    /// <summary>The scored answers of a scale from worst to best, for the chart's axis.</summary>
    public static List<(int Score, string Label)> Levels(ScaleKind scale)
        => TankScales.Values(scale).Select(v => (Score: TankScales.Score(scale, v), Label: v)).Where(x => x.Score is not null).Select(x => (x.Score!.Value, x.Label)).OrderBy(x => x.Item1).ToList();

    private static string Key(CriterionGroup group, string name) => $"{(int)group}|{name.Trim().ToLowerInvariant()}";

    /// <summary>
    /// The criteria-by-date matrix of one tank. Only submitted inspections count; the newest <paramref name="take"/> are shown (0 for all), oldest on the left. Rows are matched
    /// by the name and group each inspection stored, so a renamed criterion starts a new row and history is never rewritten.
    /// </summary>
    public static TankTrendData Build(Tank tank, IEnumerable<(TankInspection Inspection, InspectionStatus Status)> inspections, int take = 12)
    {
        var all = inspections.Where(i => i.Inspection.SubmissionCount > 0).OrderBy(i => i.Inspection.InspectedOn).ThenBy(i => i.Inspection.CreatedAt).ThenBy(i => i.Inspection.Id).ToList();
        var shown = take > 0 && all.Count > take ? all.Skip(all.Count - take).ToList() : all;
        var columns = shown.Select(i => new TrendColumn(i.Inspection.Id, i.Inspection.Code, i.Inspection.InspectedOn, i.Status, i.Inspection.ExceptionCount, i.Inspection.HasBadRating)).ToList();

        var order = new Dictionary<string, (CriterionGroup Group, string Name, ScaleKind Scale, int Sort)>();
        foreach (var (inspection, _) in shown)                               // oldest first, so a later inspection decides the order and the scale
            foreach (var r in inspection.Results)
                order[Key(r.Group, r.CriterionName)] = (r.Group, r.CriterionName.Trim(), r.Scale, r.SortOrder);

        var rows = order.OrderBy(o => o.Value.Group).ThenBy(o => o.Value.Sort).ThenBy(o => o.Value.Name, StringComparer.OrdinalIgnoreCase).Select(o =>
        {
            var cells = shown.Select(i =>
            {
                var r = i.Inspection.Results.OrderBy(x => x.SortOrder).FirstOrDefault(x => Key(x.Group, x.CriterionName) == o.Key);
                return r is null ? null : new TrendCell(r.Value, r.Comment, TankScales.IsException(r.Scale, r.ExceptionValues, r.Value), Tone(r.Scale, r.Value, r.ExceptionValues));
            }).ToList();
            return new TrendRow(o.Value.Group, o.Value.Name, o.Value.Scale, cells);
        }).ToList();
        return new TankTrendData(tank, columns, rows, all.Count);
    }

    /// <summary>The coating-condition row, if the tank's checklists ask for it.</summary>
    public static TrendRow? CoatingRow(TankTrendData data)
        => data.Rows.FirstOrDefault(r => r.Group == CriterionGroup.VariableData && r.Name.Equals("Condition of coating", StringComparison.OrdinalIgnoreCase) && r.HasScores);

    /// <summary>The points of a row for the chart: one per inspection that gave a scored answer.</summary>
    public static List<TrendPoint> Series(TankTrendData data, TrendRow row)
    {
        var points = new List<TrendPoint>();
        for (var i = 0; i < data.Columns.Count; i++)
        {
            if (row.Cells[i] is not { } cell || TankScales.Score(row.Scale, cell.Value) is not { } score) continue;
            points.Add(new TrendPoint(data.Columns[i].InspectedOn, data.Columns[i].Code, cell.Value, score, cell.Tone));
        }
        return points;
    }

    /// <summary>The exceptions of the given inspections, most serious first, then by vessel, tank and the newest inspection.</summary>
    public static List<ExceptionRow> Exceptions(IEnumerable<TankInspection> inspections, IReadOnlyDictionary<Guid, Tank> tanks, IReadOnlyDictionary<Guid, string> vesselNames,
        Func<TankInspection, InspectionStatus> statusOf, IReadOnlyCollection<ExceptionSeverity>? severities = null)
    {
        var rows = new List<ExceptionRow>();
        foreach (var inspection in inspections)
        {
            if (!tanks.TryGetValue(inspection.TankId, out var tank)) continue;
            var status = statusOf(inspection);
            foreach (var r in TankStatus.Exceptions(inspection.Results).OrderBy(r => r.Group).ThenBy(r => r.SortOrder))
            {
                var severity = SeverityOf(r.Scale, r.Value);
                if (severities is { Count: > 0 } && !severities.Contains(severity)) continue;
                rows.Add(new ExceptionRow(inspection.Id, inspection.Code, inspection.InspectedOn, status, inspection.VesselId, vesselNames.GetValueOrDefault(inspection.VesselId, ""), tank.Id, tank.Code, tank.Name,
                    tank.Type, r.Group, r.CriterionName, r.Value, severity, r.Comment, r.WorklistTaskId));
            }
        }
        return rows.OrderBy(r => r.Severity).ThenBy(r => r.VesselName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.TankCode, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.InspectedOn).ThenBy(r => r.Criterion, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Trends, the exception summary and the counts for tiles and menu: all read from the submitted inspections, nothing is stored.</summary>
public class TankTrendService(IDbContextFactory<ChandleryDbContext> factory, TankInspectionService inspections)
{
    /// <summary>The tank's matrix and chart data. Null when the tank is not found.</summary>
    public async Task<TankTrendData?> TrendAsync(Guid companyId, Guid tankId, int take = 12)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var tank = await db.Tanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tankId && t.CompanyId == companyId);
        if (tank is null) return null;
        var list = await db.TankInspections.AsNoTracking().Include(i => i.Results).Where(i => i.TankId == tankId && i.CompanyId == companyId && i.SubmissionCount > 0).ToListAsync();
        var ids = list.Select(i => i.Id).ToList();
        var reviews = (await db.TankInspectionReviews.AsNoTracking().Where(r => ids.Contains(r.InspectionId)).ToListAsync()).ToLookup(r => r.InspectionId);
        return TankTrend.Build(tank, list.Select(i => (i, TankStatus.Of(i.SubmissionCount, reviews[i.Id]))), take);
    }

    /// <summary>
    /// The exceptions found on submitted inspections of vessels the module is on for. By default what each tank's latest inspection found (tanks out of service are left out);
    /// with <see cref="ExceptionFilter.CurrentOnly"/> off, every submitted inspection (optionally since a date).
    /// </summary>
    public async Task<List<ExceptionRow>> ExceptionsAsync(Guid companyId, ExceptionFilter? filter = null)
    {
        filter ??= new ExceptionFilter();
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.TankInspection)).ToDictionary(v => v.Id, v => v.Name);
        var vesselIds = (filter.VesselIds is { Count: > 0 } ? enabled.Keys.Where(filter.VesselIds.Contains) : enabled.Keys).ToList();
        if (vesselIds.Count == 0) return [];

        var tanks = await db.Tanks.AsNoTracking().Where(t => t.CompanyId == companyId && vesselIds.Contains(t.VesselId)).ToDictionaryAsync(t => t.Id);
        var heads = await db.TankInspections.AsNoTracking().Where(i => i.CompanyId == companyId && i.SubmissionCount > 0 && vesselIds.Contains(i.VesselId))
            .Select(i => new { i.Id, i.TankId, i.InspectedOn, i.CreatedAt, i.ExceptionCount }).ToListAsync();
        var candidates = heads.Where(h => tanks.TryGetValue(h.TankId, out var tank) && (filter.Types is not { Count: > 0 } || filter.Types.Contains(tank.Type)));
        var chosen = filter.CurrentOnly
            ? candidates.Where(h => tanks[h.TankId].IsActive).GroupBy(h => h.TankId).Select(g => g.OrderByDescending(h => h.InspectedOn).ThenByDescending(h => h.CreatedAt).First())
            : candidates.Where(h => filter.Since is not { } since || h.InspectedOn >= since.Date);
        var ids = chosen.Where(h => h.ExceptionCount > 0).Select(h => h.Id).ToList();
        if (ids.Count == 0) return [];

        var found = await db.TankInspections.AsNoTracking().Include(i => i.Results).Where(i => ids.Contains(i.Id)).ToListAsync();
        var reviews = (await db.TankInspectionReviews.AsNoTracking().Where(r => ids.Contains(r.InspectionId)).ToListAsync()).ToLookup(r => r.InspectionId);
        return TankTrend.Exceptions(found, tanks, enabled, i => TankStatus.Of(i.SubmissionCount, reviews[i.Id]), filter.Severities);
    }

    /// <summary>Numbers for the tiles and the menu. Null when tank inspection is on for no vessel.</summary>
    public async Task<TankCounts?> CountsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.TankInspection)).Select(v => v.Id).ToList();
        if (enabled.Count == 0) return null;

        var waiting = await inspections.ReviewWaitingAsync(companyId);
        var rows = await inspections.ListAsync(companyId);
        var enabledSet = enabled.ToHashSet();
        var withVessel = rows.Count(r => enabledSet.Contains(r.Inspection.VesselId) && r.Status is InspectionStatus.Draft or InspectionStatus.Returned);
        var current = await ExceptionsAsync(companyId);
        var tanks = await db.Tanks.AsNoTracking().Where(t => t.CompanyId == companyId && t.IsActive && enabled.Contains(t.VesselId)).Select(t => t.Id).ToListAsync();
        var inspected = rows.Where(r => r.Inspection.SubmissionCount > 0).Select(r => r.Inspection.TankId).ToHashSet();
        return new TankCounts(waiting.Count, waiting.WithBadRating, withVessel, current.Count, current.Where(r => r.Severity == ExceptionSeverity.Bad).Select(r => r.TankId).Distinct().Count(), tanks.Count(id => !inspected.Contains(id)));
    }
}
