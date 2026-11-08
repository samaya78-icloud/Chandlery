using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>A spreadsheet as the wizard sees it: a header row and text cells (dates already turned into text).</summary>
public record ImportTable(List<string> Headers, List<List<string>> Rows);

public enum ImportField { Location, System, SubSystem, Equipment, JobCode, JobTitle, Interval, LastDoneDate, LastDoneHours, Critical, Department, Instructions }

/// <summary>Which column holds each field (-1 or absent: not in the file).</summary>
public class ImportMapping
{
    public Dictionary<ImportField, int> Columns { get; set; } = [];
    public int Of(ImportField f) => Columns.TryGetValue(f, out var c) ? c : -1;
    public ImportMapping With(ImportField f, int column) { var copy = new ImportMapping { Columns = new(Columns) }; copy.Columns[f] = column; return copy; }
}

/// <summary>Reads a CSV or Excel file into an <see cref="ImportTable"/>.</summary>
public static class PmsImportFile
{
    public const int MaxRows = 20000;

    public static ImportTable ReadCsv(string text)
    {
        text = text.TrimStart('﻿');
        var delimiter = Delimiter(text);
        var records = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(cell.ToString().Trim()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString().Trim()); cell.Clear();
                if (row.Any(x => x != "")) records.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString().Trim());
        if (row.Any(x => x != "")) records.Add(row);
        return Table(records);
    }

    /// <summary>The first sheet of an Excel workbook. Dates come out as yyyy-MM-dd.</summary>
    public static ImportTable ReadXlsx(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed();
        if (used is null) return new ImportTable([], []);
        var records = new List<List<string>>();
        var columns = used.ColumnCount();
        foreach (var r in used.Rows().Take(MaxRows + 1))
        {
            var row = new List<string>(columns);
            for (var c = 1; c <= columns; c++)
            {
                var cell = r.Cell(c);
                row.Add(cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var d) ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : cell.GetFormattedString().Trim());
            }
            if (row.Any(x => x != "")) records.Add(row);
        }
        return Table(records);
    }

    private static ImportTable Table(List<List<string>> records)
    {
        if (records.Count == 0) return new ImportTable([], []);
        var headers = records[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h).ToList();
        var rows = records.Skip(1).Take(MaxRows).Select(r => { var padded = new List<string>(r); while (padded.Count < headers.Count) padded.Add(""); return padded; }).ToList();
        return new ImportTable(headers, rows);
    }

    private static char Delimiter(string text)
    {
        var first = text.Split('\n', 2)[0];
        return new[] { ',', ';', '\t' }.OrderByDescending(d => first.Count(ch => ch == d)).First();
    }
}

/// <summary>Guesses which column is which from the headers.</summary>
public static class PmsImportColumns
{
    // Most specific fields first, so "Last done hours" is not taken for the interval or the date.
    private static readonly (ImportField Field, string[] Words)[] Synonyms =
    [
        (ImportField.LastDoneHours, ["last done hour", "hours at", "last hours", "hrs at", "running hours at", "rh at", "last rh", "hours when"]),
        (ImportField.LastDoneDate, ["last done", "last completed", "date done", "done date", "last date", "completed", "last carried"]),
        (ImportField.Interval, ["interval", "frequency", "periodicity", "period", "freq", "every"]),
        (ImportField.JobCode, ["job code", "task code", "task no", "task number", "job no", "job number", "work order", "wo no", "code", "ref", "id"]),
        (ImportField.SubSystem, ["sub system", "subsystem", "sub-system", "sub group", "subgroup"]),
        (ImportField.Instructions, ["instruction", "checklist", "procedure", "remark", "note"]),
        (ImportField.Critical, ["critical", "safety", "class"]),
        (ImportField.Department, ["department", "dept", "responsible", "discipline", "rank"]),
        (ImportField.Location, ["location", "space", "area", "compartment", "deck"]),
        (ImportField.System, ["system", "section"]),
        (ImportField.Equipment, ["equipment", "machinery", "component", "asset", "item"]),
        (ImportField.JobTitle, ["job title", "job description", "job", "task", "title", "description", "maintenance", "work"]),
    ];

    public static ImportMapping Suggest(IReadOnlyList<string> headers)
    {
        var mapping = new ImportMapping();
        var used = new HashSet<int>();
        foreach (var (field, words) in Synonyms)
        {
            foreach (var word in words)
            {
                var index = headers.Select((h, i) => (h: h.ToLowerInvariant(), i)).FirstOrDefault(x => !used.Contains(x.i) && x.h.Contains(word));
                if (index.h is null) continue;
                mapping.Columns[field] = index.i;
                used.Add(index.i);
                break;
            }
        }
        return mapping;
    }
}

/// <summary>Turns the words people write for "how often" into numbers.</summary>
public static class PmsIntervals
{
    public readonly record struct Parsed(bool Ok, int? Calendar, PmsIntervalUnit Unit, int? Hours);

    private static readonly Regex Number = new(@"^(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[a-z\.\s]+)$", RegexOptions.Compiled);
    private static readonly Regex EveryWord = new(@"^(?<n>\d+)\s*[- ]?\s*(?<u>yearly|monthly|weekly|daily|annually)$", RegexOptions.Compiled);

    private static readonly Dictionary<string, (int N, PmsIntervalUnit Unit)> Words = new()
    {
        ["daily"] = (1, PmsIntervalUnit.Days), ["weekly"] = (1, PmsIntervalUnit.Weeks), ["fortnightly"] = (2, PmsIntervalUnit.Weeks), ["monthly"] = (1, PmsIntervalUnit.Months),
        ["quarterly"] = (3, PmsIntervalUnit.Months), ["half yearly"] = (6, PmsIntervalUnit.Months), ["half-yearly"] = (6, PmsIntervalUnit.Months), ["six monthly"] = (6, PmsIntervalUnit.Months),
        ["six-monthly"] = (6, PmsIntervalUnit.Months), ["semi annual"] = (6, PmsIntervalUnit.Months), ["semi-annual"] = (6, PmsIntervalUnit.Months), ["biannual"] = (6, PmsIntervalUnit.Months),
        ["annual"] = (1, PmsIntervalUnit.Years), ["annually"] = (1, PmsIntervalUnit.Years), ["yearly"] = (1, PmsIntervalUnit.Years), ["biennial"] = (2, PmsIntervalUnit.Years),
    };

    public static Parsed Parse(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t == "") return new Parsed(false, null, PmsIntervalUnit.Months, null);
        int? calendar = null, hours = null;
        var unit = PmsIntervalUnit.Months;
        foreach (var raw in Regex.Split(t, @"\s*(?:/|,|;|\||&|\+|\bor\b|\band\b|\bwhichever\b.*)\s*").Where(x => x.Trim() != ""))
        {
            var token = raw.Trim();
            if (Words.TryGetValue(token, out var word)) { if (calendar is not null) return Bad(); (calendar, unit) = (word.N, word.Unit); continue; }
            if (EveryWord.Match(token) is { Success: true } every)
            {
                if (calendar is not null) return Bad();
                var n = int.Parse(every.Groups["n"].Value, CultureInfo.InvariantCulture);
                (calendar, unit) = (n, every.Groups["u"].Value switch { "daily" => PmsIntervalUnit.Days, "weekly" => PmsIntervalUnit.Weeks, "monthly" => PmsIntervalUnit.Months, _ => PmsIntervalUnit.Years });
                continue;
            }
            if (Number.Match(token) is not { Success: true } m) return Bad();
            if (!decimal.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > 1_000_000) return Bad();
            var u = Regex.Replace(m.Groups["u"].Value.Trim(), @"[\.\s]+", " ").Trim();
            switch (u)
            {
                case "h" or "hr" or "hrs" or "hour" or "hours" or "rh" or "running hours" or "running hour" or "run hrs" or "r h":
                    if (hours is not null) return Bad();
                    hours = (int)Math.Round(value);
                    break;
                case "d" or "day" or "days": if (calendar is not null) return Bad(); (calendar, unit) = ((int)Math.Round(value), PmsIntervalUnit.Days); break;
                case "w" or "wk" or "wks" or "week" or "weeks": if (calendar is not null) return Bad(); (calendar, unit) = ((int)Math.Round(value), PmsIntervalUnit.Weeks); break;
                case "m" or "mo" or "mos" or "mth" or "mths" or "month" or "months": if (calendar is not null) return Bad(); (calendar, unit) = ((int)Math.Round(value), PmsIntervalUnit.Months); break;
                case "y" or "yr" or "yrs" or "year" or "years": if (calendar is not null) return Bad(); (calendar, unit) = ((int)Math.Round(value), PmsIntervalUnit.Years); break;
                default: return Bad();
            }
        }
        if (calendar is not > 0 && hours is not > 0) return Bad();
        if (calendar is > 0 && PmsScheduler.ApproxDays(calendar.Value, unit) > 365 * 50) return Bad();
        return new Parsed(true, calendar is > 0 ? calendar : null, unit, hours is > 0 ? hours : null);

        static Parsed Bad() => new(false, null, PmsIntervalUnit.Months, null);
    }
}

public static class PmsDates
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "d-MMM-yy",
        "dd MMMM yyyy", "d MMMM yyyy", "MMM d, yyyy", "yyyy/MM/dd", "yyyyMMdd",
    ];

    /// <summary>Day-first dates (as ships write them) and ISO dates; an Excel serial number also works. Null when it cannot be read.</summary>
    public static DateTime? Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t == "") return null;
        if (DateTime.TryParseExact(t, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
            return DateTime.SpecifyKind(DateTime.FromOADate(Math.Floor(serial)).Date, DateTimeKind.Utc);
        return null;
    }
}

public enum MatchKind { None, Code, Title, Similar }

public record ImportRow(
    int Line, string[] Path, string Code, string Title, string Instructions, int? CalendarInterval, PmsIntervalUnit CalendarUnit, int? HoursInterval, DateTime? LastDoneDate, decimal? LastDoneHours,
    bool Critical, string Department, JobTemplate? Match, MatchKind MatchKind, List<string> Errors, List<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public string PathText => string.Join(" › ", Path);
}

public record ImportPlan(List<ImportRow> Rows, int Valid, int Invalid, int Warnings, int NewMachinery, int AlreadyOnPlan, int WithLibraryMatch, int HoursJobs);

/// <summary>Turns a table and a column mapping into rows ready to import, with what is wrong with each, and suggests a library template for each job. Pure.</summary>
public static class PmsImportPlanner
{
    private static readonly HashSet<string> Stop = ["the", "of", "and", "or", "a", "an", "to", "for", "in", "on", "check", "checks", "inspect", "inspection", "test", "tests", "service", "maintenance"];

    public static ImportPlan Plan(ImportTable table, ImportMapping map, IReadOnlyList<JobTemplate> templates, ISet<string> existingJobKeys, ISet<string> existingPaths, DateTime today)
    {
        string Cell(List<string> r, ImportField f) { var c = map.Of(f); return c >= 0 && c < r.Count ? r[c].Trim() : ""; }
        var byCode = templates.Where(t => t.IsActive).GroupBy(t => t.Code.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var byTitle = templates.Where(t => t.IsActive).GroupBy(t => Normalize(t.Title)).Where(g => g.Key != "").ToDictionary(g => g.Key, g => g.First());
        var tokens = templates.Where(t => t.IsActive).Select(t => (t, Tokens: Tokens(t.Title))).Where(x => x.Tokens.Count >= 2).ToList();

        var seen = new HashSet<string>();
        var rows = new List<ImportRow>();
        var line = 1;
        foreach (var r in table.Rows)
        {
            line++;
            var errors = new List<string>();
            var warnings = new List<string>();
            var parts = new[] { Cell(r, ImportField.Location), Cell(r, ImportField.System), Cell(r, ImportField.SubSystem), Cell(r, ImportField.Equipment) }.Where(p => p != "").ToArray();
            var code = Cell(r, ImportField.JobCode);
            var title = Cell(r, ImportField.JobTitle);
            if (parts.Length == 0) errors.Add("No machinery named (location, system or equipment).");
            if (code == "") errors.Add("No job code.");
            if (title == "") errors.Add("No job title.");
            if (code.Length > 40) errors.Add("The job code is longer than 40 characters.");

            var interval = PmsIntervals.Parse(Cell(r, ImportField.Interval));
            if (!interval.Ok) errors.Add(Cell(r, ImportField.Interval) == "" ? "No interval." : $"The interval \"{Cell(r, ImportField.Interval)}\" could not be read (try \"3 months\", \"500 hrs\" or \"6 months / 4000 hrs\").");

            DateTime? lastDone = null;
            var lastText = Cell(r, ImportField.LastDoneDate);
            if (lastText != "")
            {
                lastDone = PmsDates.Parse(lastText);
                if (lastDone is null) errors.Add($"The last done date \"{lastText}\" could not be read.");
                else if (lastDone > today) errors.Add("The last done date is in the future.");
            }
            decimal? lastHours = null;
            var hoursText = Cell(r, ImportField.LastDoneHours);
            if (hoursText != "")
            {
                if (decimal.TryParse(hoursText.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h >= 0) lastHours = h;
                else errors.Add($"The running hours \"{hoursText}\" could not be read.");
            }
            if (lastDone is null && lastHours is not null) warnings.Add("Hours at the last completion are given without a date, so they are ignored.");
            if (interval.Ok && interval.Hours is > 0 && lastDone is not null && lastHours is null) warnings.Add("An hours job with no hours at the last completion: its hours date is unknown until readings come in.");

            var key = string.Join("/", parts).ToLowerInvariant() + "|" + code.ToLowerInvariant();
            if (errors.Count == 0 && !seen.Add(key)) errors.Add("The same job code appears twice for this machinery in the file.");
            if (errors.Count == 0 && existingJobKeys.Contains(key)) warnings.Add("Already on the plan: it will be skipped.");

            JobTemplate? match = null;
            var kind = MatchKind.None;
            if (errors.Count == 0)
            {
                if (byCode.TryGetValue(code.ToLowerInvariant(), out var c)) (match, kind) = (c, MatchKind.Code);
                else if (byTitle.TryGetValue(Normalize(title), out var t)) (match, kind) = (t, MatchKind.Title);
                else
                {
                    var mine = Tokens(title);
                    if (mine.Count >= 2)
                    {
                        var best = tokens.Select(x => (x.t, score: (double)x.Tokens.Intersect(mine).Count() / x.Tokens.Union(mine).Count())).OrderByDescending(x => x.score).FirstOrDefault();
                        if (best.t is not null && best.score >= 0.6) (match, kind) = (best.t, MatchKind.Similar);
                    }
                }
            }

            rows.Add(new ImportRow(line, parts, code, title, Cell(r, ImportField.Instructions), interval.Calendar, interval.Unit, interval.Hours, lastDone, lastHours,
                IsYes(Cell(r, ImportField.Critical)), Cell(r, ImportField.Department), match, kind, errors, warnings));
        }

        var valid = rows.Where(x => x.IsValid).ToList();
        var newPaths = valid.Select(x => x.PathText.ToLowerInvariant()).Distinct().Count(p => !existingPaths.Contains(p));
        var skipped = valid.Count(x => existingJobKeys.Contains(string.Join("/", x.Path).ToLowerInvariant() + "|" + x.Code.ToLowerInvariant()));
        return new ImportPlan(rows, valid.Count, rows.Count - valid.Count, rows.Sum(x => x.Warnings.Count), newPaths, skipped, valid.Count(x => x.Match is not null), valid.Count(x => x.HoursInterval is > 0));
    }

    public static string Normalize(string title) => string.Join(" ", Tokens(title).OrderBy(x => x));

    private static HashSet<string> Tokens(string title)
        => Regex.Split(title.ToLowerInvariant(), @"[^a-z0-9]+").Where(x => (x.Length > 1 || char.IsDigit(x[0])) && !Stop.Contains(x)).Select(Stem).ToHashSet();   // a number is part of a title: "pump 1" is not "pump 2"

    private static string Stem(string w) => w.Length > 4 && w.EndsWith("s") ? w[..^1] : w;

    private static bool IsYes(string s) => s.Trim().ToLowerInvariant() is "y" or "yes" or "true" or "1" or "x" or "critical" or "crit" or "safety critical";
}
