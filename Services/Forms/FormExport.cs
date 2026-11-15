using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Chandlery.Models;
using ClosedXML.Excel;

namespace Chandlery.Services;

/// <summary>One column of an export: where its answer is found in the values, and what it is called.</summary>
public record FormExportColumn(string Key, string Heading, string Type);

/// <summary>One line of an export: the entry's own facts, then its answers in the column order.</summary>
public record FormExportRow(string Reference, string Vessel, DateTime FormDate, string Status, int Version, int Exceptions, List<string> Cells);

/// <summary>
/// A form's entries as a table, one column per field (a grid gives one column per row and cell), in the order of the definition. Pure: the CSV and Excel files and the
/// tests all read the same table. A value that a spreadsheet could take for a formula is kept as text.
/// </summary>
public static class FormExport
{
    public static readonly string[] FixedHeadings = ["Reference", "Vessel", "Date", "Status", "Version", "Exceptions"];

    /// <summary>The columns of a definition. Rows of a free or list grid are not known from the definition, so those found in the entries (<paramref name="seen"/>) are added.</summary>
    public static List<FormExportColumn> Columns(FormDef def, IEnumerable<JsonObject>? seen = null)
    {
        var all = (seen ?? []).ToList();
        var columns = new List<FormExportColumn>();
        foreach (var section in def.Sections)
        {
            var title = string.IsNullOrWhiteSpace(section.Title) ? section.Id : section.Title;
            if (section.Layout == "fields")
            {
                foreach (var field in section.Fields ?? []) columns.Add(new FormExportColumn(field.Id, field.Label, field.Type));
                continue;
            }
            var rows = section.Rows?.Mode == "fixed"
                ? (section.Rows.Items ?? []).Select(r => (r.Id, r.Label)).ToList()
                : all.Select(v => v[section.Id] as JsonObject).Where(g => g is not null).SelectMany(g => g!.Select(p => p.Key)).Distinct().OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => (k, k)).ToList();
            foreach (var (rowId, rowLabel) in rows)
                foreach (var (cellId, column) in FormDef.Cells(section))
                    columns.Add(new FormExportColumn($"{section.Id}.{rowId}.{cellId}", $"{title} / {rowLabel} / {CellHeading(cellId, column)}", column.Type));
        }
        return columns;
    }

    /// <summary>A grid cell's name as the form shows it: the repeated group first (B1 F), so two cells are never called the same.</summary>
    public static string CellHeading(string cellId, FormColumn column)
    {
        var dot = cellId.LastIndexOf('.');
        return dot < 0 ? column.Label : $"{cellId[..dot].ToUpperInvariant()} {column.Label}";
    }

    /// <summary>The answer under a column key (<c>field</c> or <c>grid.row.cell</c>, where a cell id may itself contain dots), or null.</summary>
    public static JsonNode? Find(JsonObject values, string key)
    {
        if (values.TryGetPropertyValue(key, out var direct)) return direct;
        var first = key.IndexOf('.');
        if (first < 0 || values[key[..first]] is not JsonObject grid) return null;
        var second = key.IndexOf('.', first + 1);
        if (second < 0 || grid[key[(first + 1)..second]] is not JsonObject row) return null;
        return row[key[(second + 1)..]];
    }

    /// <summary>An answer as text for a table or a print: numbers plain, yes/no for a tick, a list joined, pictures counted, a signature by name.</summary>
    public static string Text(JsonNode? node, string type)
    {
        switch (node)
        {
            case null: return "";
            case JsonArray array:
                return type == FormFieldTypes.Photo ? $"{array.Count} picture(s)" : string.Join("; ", array.Select(n => n?.ToString() ?? ""));
            case JsonObject obj:
                return obj["name"] is { } name ? $"{name}{(obj["at"] is { } at ? " " + at : "")}" : obj.ToJsonString();
            case JsonValue v when v.TryGetValue<bool>(out var b): return b ? "Yes" : "No";
            case JsonValue v when v.TryGetValue<decimal>(out var d): return d.ToString("0.######", CultureInfo.InvariantCulture);
            default: return node.ToString();
        }
    }

    public static FormExportRow Row(IReadOnlyList<FormExportColumn> columns, FormEntry entry, string vessel, FormStatus status)
    {
        var values = JsonNode.Parse(entry.Values) as JsonObject ?? [];
        return new FormExportRow(entry.Code, vessel, entry.FormDate, status.ToString(), entry.VersionNumber, entry.ExceptionCount,
            columns.Select(c => Text(Find(values, c.Key), c.Type)).ToList());
    }

    /// <summary>Text that a spreadsheet would run as a formula gets a leading apostrophe; a plain negative number is left alone.</summary>
    public static string Safe(string text)
    {
        if (text.Length == 0) return text;
        var c = text[0];
        if (c is '=' or '+' or '@' or '\t' or '\r') return "'" + text;
        if (c == '-' && !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return "'" + text;
        return text;
    }

    private static string Quote(string text)
    {
        var safe = Safe(text);
        return safe.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + safe.Replace("\"", "\"\"") + "\"" : safe;
    }

    public static string Csv(IReadOnlyList<FormExportColumn> columns, IReadOnlyList<FormExportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", FixedHeadings.Concat(columns.Select(c => c.Heading)).Select(Quote)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", new[] { r.Reference, r.Vessel, r.FormDate.ToString("yyyy-MM-dd"), r.Status, r.Version.ToString(), r.Exceptions.ToString() }.Concat(r.Cells).Select(Quote)));
        return sb.ToString();
    }

    public static byte[] Xlsx(string sheet, IReadOnlyList<FormExportColumn> columns, IReadOnlyList<FormExportRow> rows)
    {
        using var book = new XLWorkbook();
        var name = new string(sheet.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        var ws = book.Worksheets.Add(string.IsNullOrWhiteSpace(name) ? "Form" : name[..Math.Min(31, name.Length)]);
        var headings = FixedHeadings.Concat(columns.Select(c => c.Heading)).ToList();
        for (var i = 0; i < headings.Count; i++) { ws.Cell(1, i + 1).Value = headings[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; }
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            ws.Cell(r + 2, 1).Value = row.Reference;
            ws.Cell(r + 2, 2).Value = row.Vessel;
            ws.Cell(r + 2, 3).Value = row.FormDate.Date;
            ws.Cell(r + 2, 4).Value = row.Status;
            ws.Cell(r + 2, 5).Value = row.Version;
            ws.Cell(r + 2, 6).Value = row.Exceptions;
            for (var c = 0; c < row.Cells.Count; c++)
            {
                var cell = ws.Cell(r + 2, 7 + c);
                // numbers stay numbers; everything else is text, never a formula
                if (columns[c].Type is FormFieldTypes.Number or FormFieldTypes.Calculated && decimal.TryParse(row.Cells[c], NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) cell.Value = n;
                else cell.SetValue(row.Cells[c]);
            }
        }
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.LightGray;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, 1, 60);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }
}

/// <summary>One cell of the year view: how many entries a vessel made for a form in a month, and the least settled state among them.</summary>
public record FormYearCell(int Count, FormStatus? Worst);

/// <summary>The year at a glance: for each form, twelve months. Pure.</summary>
public static class FormYearView
{
    private static int Rank(FormStatus s) => s switch { FormStatus.Returned => 0, FormStatus.Draft => 1, FormStatus.Submitted => 2, FormStatus.InReview => 3, _ => 4 };

    public static FormYearCell[] Months(IEnumerable<(DateTime FormDate, FormStatus Status)> entries, int year)
    {
        var cells = new FormYearCell[12];
        for (var m = 1; m <= 12; m++)
        {
            var inMonth = entries.Where(e => e.FormDate.Year == year && e.FormDate.Month == m).ToList();
            cells[m - 1] = new FormYearCell(inMonth.Count, inMonth.Count == 0 ? null : inMonth.Select(e => e.Status).OrderBy(Rank).First());
        }
        return cells;
    }
}
