using System.Globalization;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public enum RestImportStatus
{
    /// <summary>A day not recorded yet: it will be added.</summary>
    New,

    /// <summary>The day is recorded already, the same: nothing to do.</summary>
    Same,

    /// <summary>The day is recorded already, differently: the import adds a correction beside it, with the reason "Imported history".</summary>
    Correction,

    /// <summary>The row cannot be imported; <see cref="RestImportRow.Problem"/> says why.</summary>
    Problem,
}

public sealed record RestImportRow(int Line, string Name, DateTime? Date, string Spans, Guid? CrewId, RestImportStatus Status, string Problem = "");

public sealed record RestImportPlan(IReadOnlyList<RestImportRow> Rows, string? FileProblem = null)
{
    public int Count(RestImportStatus status) => Rows.Count(r => r.Status == status);
    public IEnumerable<RestImportRow> ToRecord => Rows.Where(r => r.Status is RestImportStatus.New or RestImportStatus.Correction);
}

/// <summary>A seafarer aboard, as the import needs to know them.</summary>
public sealed record RestImportCrew(Guid Id, string Name);

/// <summary>
/// Reads hours of rest history from a CSV in the format the system itself exports (the daily CSV): columns "Seafarer" (or "Name"), "Date" and "Work periods", the rest ignored. Pure:
/// it works out what each row would do and never writes. Rows are matched to the crew aboard by name, and each day goes through the same checks as a day typed in.
/// </summary>
public static class RestImport
{
    public const int MaxRows = 20000;
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d/M/yyyy", "dd/MM/yyyy", "yyyy/MM/dd"];

    private static int Column(List<string> header, params string[] names)
        => header.FindIndex(h => names.Any(n => string.Equals(h.Trim(), n, StringComparison.OrdinalIgnoreCase)));

    /// <param name="existing">The spans last recorded for each seafarer and day, keyed by seafarer and date.</param>
    public static RestImportPlan Plan(string csv, IReadOnlyList<RestImportCrew> crew, IReadOnlyDictionary<(Guid CrewId, DateTime Date), string> existing, DateTime today)
    {
        var table = Csv.Read(csv ?? "").Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (table.Count == 0) return new RestImportPlan([], "The file is empty.");
        var header = table[0];
        var name = Column(header, "Seafarer", "Name");
        var date = Column(header, "Date");
        var spans = Column(header, "Work periods", "Work spans", "Spans");
        if (name < 0 || date < 0 || spans < 0) return new RestImportPlan([], "The first row must name the columns Seafarer (or Name), Date and Work periods, as the daily CSV export does.");
        if (table.Count - 1 > MaxRows) return new RestImportPlan([], $"The file has more than {MaxRows} rows; import it in parts.");

        var byName = crew.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(Guid, DateTime)>();
        var rows = new List<RestImportRow>();
        for (var i = 1; i < table.Count; i++)
        {
            var record = table[i];
            string Cell(int index) => index < record.Count ? record[index].Trim() : "";
            var line = i + 1;
            var who = Cell(name);
            var text = Cell(spans);

            if (!DateTime.TryParseExact(Cell(date), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { rows.Add(new(line, who, null, text, null, RestImportStatus.Problem, $"The date \"{Cell(date)}\" is not understood (use yyyy-MM-dd).")); continue; }
            var day = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            if (!byName.TryGetValue(who, out var matches))
            { rows.Add(new(line, who, day, text, null, RestImportStatus.Problem, "No seafarer of that name is signed on to this vessel.")); continue; }
            if (matches.Count > 1)
            { rows.Add(new(line, who, day, text, null, RestImportStatus.Problem, "Two seafarers aboard have that name; correct it by hand.")); continue; }
            var member = matches[0];
            if (day > today.Date) { rows.Add(new(line, who, day, text, member.Id, RestImportStatus.Problem, "That day has not happened yet.")); continue; }

            string normal;
            try
            {
                var parsedSpans = RestSpans.Parse(text);
                if (RestSpans.Validate(parsedSpans) is { } invalid) { rows.Add(new(line, who, day, text, member.Id, RestImportStatus.Problem, invalid)); continue; }
                normal = RestSpans.Format(RestSpans.Normalize(parsedSpans));
            }
            catch (FormatException ex) { rows.Add(new(line, who, day, text, member.Id, RestImportStatus.Problem, ex.Message)); continue; }

            if (!seen.Add((member.Id, day))) { rows.Add(new(line, who, day, normal, member.Id, RestImportStatus.Problem, "The file has this seafarer and day twice.")); continue; }
            var status = !existing.TryGetValue((member.Id, day), out var current) ? RestImportStatus.New : current == normal ? RestImportStatus.Same : RestImportStatus.Correction;
            rows.Add(new(line, who, day, normal, member.Id, status));
        }
        return new RestImportPlan(rows);
    }
}

/// <summary>Brings hours of rest history in from a CSV, onboard, by the Master or a crew admin. Each day is recorded through <see cref="RestHoursService.RecordAsync"/>, so every rule of typing a day in applies.</summary>
public class RestImportService(IDbContextFactory<ChandleryDbContext> factory, RestHoursService rest)
{
    public const string Reason = "Imported history";

    private async Task<RestImportPlan> PlanAsync(Guid companyId, Guid vesselId, Actor actor, string csv)
    {
        var me = await rest.CrewOfUserAsync(companyId, actor.UserId) ?? throw new InvalidOperationException("History is imported onboard, by the Master or a crew admin.");
        if (me.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        if (!me.IsMaster && !me.IsCrewAdmin) throw new InvalidOperationException("History is imported by the Master or a crew admin.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var crew = (await db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive).ToListAsync()).Select(c => new RestImportCrew(c.Id, c.FullName)).ToList();
        var ids = crew.Select(c => c.Id).ToList();
        var entries = await db.RestEntries.AsNoTracking().Where(e => ids.Contains(e.CrewMemberId)).ToListAsync();
        var existing = entries.GroupBy(e => (e.CrewMemberId, e.Date)).ToDictionary(g => g.Key, g => g.OrderBy(e => e.EnteredAt).ThenBy(e => e.Id).Last().WorkSpans);
        return RestImport.Plan(csv, crew, existing, DateTime.UtcNow);
    }

    /// <summary>What the file would do, without doing it.</summary>
    public Task<RestImportPlan> PreviewAsync(Guid companyId, Guid vesselId, Actor actor, string csv) => PlanAsync(companyId, vesselId, actor, csv);

    /// <summary>
    /// Records every day that is new or differs, and returns the plan with rows the recording refused (a period the Master signed, for a crew admin) turned into problems. Days
    /// already recorded the same are left alone, so running the same file twice changes nothing.
    /// </summary>
    public async Task<(RestImportPlan Plan, int Recorded)> ApplyAsync(Guid companyId, Guid vesselId, Actor actor, string csv)
    {
        var plan = await PlanAsync(companyId, vesselId, actor, csv);
        if (plan.FileProblem is not null) throw new InvalidOperationException(plan.FileProblem);
        var done = 0;
        var rows = new List<RestImportRow>();
        foreach (var row in plan.Rows)
        {
            if (row.Status is not (RestImportStatus.New or RestImportStatus.Correction)) { rows.Add(row); continue; }
            try
            {
                await rest.RecordAsync(companyId, vesselId, actor, row.CrewId!.Value, row.Date!.Value, row.Spans, Reason);
                done++;
                rows.Add(row);
            }
            catch (InvalidOperationException ex) { rows.Add(row with { Status = RestImportStatus.Problem, Problem = ex.Message }); }
        }
        return (new RestImportPlan(rows), done);
    }
}
