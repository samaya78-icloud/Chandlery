using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Chandlery.Services;

public enum ConversionLevel { Info, Check, Skipped }

/// <summary>One thing the converter wants a person to know: what it did, what it guessed (check it), or what it left out.</summary>
public record ConversionNote(ConversionLevel Level, string Where, string Message);

/// <summary>A legacy form read into a draft definition, the answers it held, and a report. Nothing here is live until a person has checked and published it.</summary>
public record FormConversion(FormDef Definition, JsonObject Values, List<ConversionNote> Notes, int Inputs, int Converted)
{
    public string Json => Definition.ToPrettyJson();
}

/// <summary>
/// Reads a legacy HTML form (tables of inputs, each input named by a <c>data-ititle</c> path such as <c>Engine type [Vessel]</c>) into a draft <see cref="FormDef"/>, and the
/// answers the page holds into values keyed the same way. A table of single inputs becomes a section of fields; a table whose rows repeat the same inputs becomes a grid, its
/// header rows naming the columns (a header that repeats, Bearing 1 / Bearing 2, becomes one repeated column group). The page is parsed, never run. Anything it cannot read,
/// or can only guess, goes in the report: this is a first draft, never a publish.
/// </summary>
public static partial class FormHtmlConverter
{
    public const int MaxHtmlChars = 2_000_000;

    [GeneratedRegex(@"^\s*(?<l>.*?)\s*\[(?<g>[^\[\]]*)\]\s*$", RegexOptions.Singleline)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"num|amount|qty|quantity|decimal|digit|integer|float", RegexOptions.IgnoreCase)]
    private static partial Regex NumberHint();

    [GeneratedRegex(@"isnumber|numeric|digit|onlynum|allownum", RegexOptions.IgnoreCase)]
    private static partial Regex NumberScript();

    private static readonly string[] DateFormats = ["dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd.MM.yyyy", "dd MMM yyyy", "d MMM yyyy"];

    // ---- naming ----

    private sealed class Names
    {
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A valid, unused id from some text: <c>Engine type</c> becomes <c>engineType</c>; a second one gets a number.</summary>
        public string Make(string? text, string fallback)
        {
            var words = Regex.Split(text ?? "", @"[^A-Za-z0-9]+").Where(w => w != "").ToList();
            var sb = new StringBuilder();
            for (var i = 0; i < words.Count; i++) sb.Append(i == 0 ? char.ToLowerInvariant(words[i][0]) + words[i][1..] : char.ToUpperInvariant(words[i][0]) + words[i][1..]);
            var id = sb.ToString();
            if (id == "") id = fallback;
            if (!char.IsLetter(id[0])) id = "f" + id;
            if (id.Length > 36) id = id[..36];
            if (!FormDefinitionRules.IsValidId(id)) id += "Value";
            var unique = id;
            for (var n = 2; !_used.Add(unique); n++) unique = id + n;
            return unique;
        }
    }

    private static string Clean(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

    // ---- what each input is ----

    private sealed class Item
    {
        public IElement El = null!;
        public string Path = "", Label = "", Group = "", Type = FormFieldTypes.Text, Value = "";
        public bool Guessed, ReadOnly;
        public List<string> Options = [];
        public string Key = "";
        public string Where => Path != "" ? Path : El.GetAttribute("id") ?? El.GetAttribute("name") ?? "an input";
    }

    private static bool Numeric(string value, out decimal number)
        => decimal.TryParse(value.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out number);

    private static bool Date(string value, out DateOnly date)
        => DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static Item Read(IElement el)
    {
        var item = new Item { El = el, Path = Clean(el.GetAttribute("data-ititle")), ReadOnly = el.HasAttribute("readonly") || el.HasAttribute("disabled") };
        var match = PathPattern().Match(item.Path);
        (item.Label, item.Group) = match.Success ? (Clean(match.Groups["l"].Value), Clean(match.Groups["g"].Value)) : (item.Path, "");
        var cls = el.GetAttribute("class") ?? "";

        switch (el)
        {
            case IHtmlSelectElement select:
                item.Options = select.Options.Select(o => Clean(o.Value != "" ? o.Value : o.Text)).Where(o => o != "").Distinct().ToList();
                item.Value = Clean(select.Options.FirstOrDefault(o => o.IsSelected && o.Value != "")?.Value ?? "");
                item.Type = item.Options.Count > 0 ? FormFieldTypes.Choice : FormFieldTypes.Text;
                break;
            case IHtmlTextAreaElement area:
                (item.Value, item.Type) = (area.Value.Trim(), FormFieldTypes.LongText);
                break;
            default:
                var input = (IHtmlInputElement)el;
                item.Value = input.GetAttribute("value")?.Trim() ?? "";
                if (input.Type == "checkbox") { item.Type = FormFieldTypes.Tick; item.Value = input.HasAttribute("checked") ? "true" : ""; }
                else if (cls.Contains("cDatePicker", StringComparison.OrdinalIgnoreCase) || cls.Contains("hasDatepicker", StringComparison.OrdinalIgnoreCase) || input.HasAttribute("data-date-format") || input.Type == "date")
                    item.Type = FormFieldTypes.Date;
                else if (input.Type == "number" || NumberHint().IsMatch(cls) || NumberScript().IsMatch((el.GetAttribute("onkeypress") ?? "") + (el.GetAttribute("onkeyup") ?? "") + (el.GetAttribute("onkeydown") ?? "")))
                    item.Type = FormFieldTypes.Number;
                else if (item.Value != "" && Numeric(item.Value, out _) && !Regex.IsMatch(item.Value, @"^0\d"))
                    (item.Type, item.Guessed) = (FormFieldTypes.Number, true);
                break;
        }
        return item;
    }

    /// <summary>The cell's label when an input carries no path: the text in the cell before it, or its id.</summary>
    private static string NearbyLabel(Item item)
    {
        for (var cell = item.El.Closest("td,th")?.PreviousElementSibling; cell is not null; cell = cell.PreviousElementSibling)
            if (Clean(cell.TextContent) is { Length: > 0 } text && cell.QuerySelector("input,select,textarea") is null) return Cut(text, 80);
        return Clean((item.El.GetAttribute("id") ?? item.El.GetAttribute("name") ?? "").Replace('_', ' '));
    }

    // ---- the conversion ----

    public static FormConversion Convert(string html, string title = "")
    {
        var notes = new List<ConversionNote>();
        void Note(ConversionLevel level, string where, string message) => notes.Add(new ConversionNote(level, where, message));

        var def = new FormDef { Title = title };
        var values = new JsonObject();
        if (string.IsNullOrWhiteSpace(html)) { Note(ConversionLevel.Skipped, "file", "The file is empty."); return new FormConversion(def, values, notes, 0, 0); }
        if (html.Length > MaxHtmlChars) { Note(ConversionLevel.Skipped, "file", $"The file is larger than {MaxHtmlChars / 1_000_000} MB."); return new FormConversion(def, values, notes, 0, 0); }

        var doc = new HtmlParser().ParseDocument(html);
        if (def.Title == "") def.Title = Clean(doc.Title is { Length: > 0 } t ? t : doc.QuerySelector("h1,h2")?.TextContent);
        var scripts = doc.QuerySelectorAll("script").Length;
        if (scripts > 0) Note(ConversionLevel.Check, "scripts", $"The page has {scripts} script(s). They are not run and not converted: any calculation or rule they hold has to be added by hand.");

        var all = doc.QuerySelectorAll("input,select,textarea").Where(e => e is not IHtmlInputElement i || i.Type is not ("hidden" or "button" or "submit" or "reset" or "image" or "file")).ToList();
        var items = new List<Item>();
        foreach (var el in all)
        {
            var item = Read(el);
            if (item.Path == "")
            {
                var named = NearbyLabel(item);
                if (named == "") { Note(ConversionLevel.Skipped, item.Where, "An input with no data-ititle, id or label was left out."); continue; }
                (item.Label, item.Path) = (named, "");
                Note(ConversionLevel.Check, named, "No data-ititle: named from the text beside it.");
            }
            if (el is IHtmlInputElement { Type: "radio" }) { Note(ConversionLevel.Skipped, item.Where, "A radio button was left out; add it as a choice by hand."); continue; }
            items.Add(item);
        }
        var handlers = all.Count(e => e.Attributes.Any(a => a.Name is "onchange" or "onkeyup" or "onblur" or "onclick" && Clean(a.Value) != ""));
        if (handlers > 0) Note(ConversionLevel.Check, "scripts", $"{handlers} input(s) have a script handler (a calculation or a rule?). It was not converted.");
        foreach (var ro in items.Where(i => i.ReadOnly)) Note(ConversionLevel.Check, ro.Where, "A read-only box: probably a calculation. It is a plain field now; add the calculation by hand.");

        // The table an input belongs to: the nearest one with an id, else the nearest one.
        IElement? TableOf(IElement el) { IElement? first = null; for (var t = el.Closest("table"); t is not null; t = t.ParentElement?.Closest("table")) { first ??= t; if (!string.IsNullOrEmpty(t.Id)) return t; } return first; }
        var byTable = items.GroupBy(i => TableOf(i.El)).ToList();

        var names = new Names();
        var sectionNo = 0;
        foreach (var group in byTable)
        {
            sectionNo++;
            var table = group.Key;
            var members = group.ToList();
            var rows = members.GroupBy(i => i.El.Closest("tr")).ToList();
            var perRow = rows.Select(r => r.Count()).ToList();
            var mode = perRow.GroupBy(n => n).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
            var isGrid = table is not null && rows.All(r => r.Key is not null) && rows.Count >= 2 && mode >= 2 && perRow.Count(n => n == mode) * 10 >= rows.Count * 6;
            if (isGrid)
            {
                // Two fields side by side on each row is a form laid out in columns, not a grid: a grid has header rows, or repeats the same labels down its columns.
                var trs = table!.QuerySelectorAll("tr").ToList();
                var hasHeader = trs.Take(Math.Max(0, trs.IndexOf(rows[0].Key!))).Any(tr => tr.QuerySelector("input,select,textarea") is null && tr.Children.Count(c => Clean(c.TextContent) != "") >= 2);
                isGrid = hasHeader || members.GroupBy(i => i.Label).Any(g => g.Key != "" && g.Count() >= 2);
            }

            var groupName = members.GroupBy(i => i.Group).Where(g => g.Key != "").OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            var heading = Clean(table?.QuerySelector("caption")?.TextContent);
            var section = new FormSection { Id = names.Make(!string.IsNullOrEmpty(table?.Id) ? table!.Id : groupName, "section" + sectionNo), Title = Cut(groupName != "" ? groupName : heading != "" ? heading : table?.Id ?? "", 80), Layout = isGrid ? "grid" : "fields" };
            if (isGrid) Grid(section, table!, rows.Select(r => (r.Key!, r.ToList())).ToList(), names, values, notes);
            else Fields(section, members, names, values, notes);
            def.Sections.Add(section);
            Note(ConversionLevel.Info, section.Title == "" ? section.Id : section.Title, isGrid ? $"A grid of {section.Rows?.Items?.Count ?? 0} row(s) and {FormDef.Cells(section).Count()} column(s)." : $"{section.Fields?.Count ?? 0} field(s).");
        }

        def.Tidy();
        foreach (var problem in FormDefinitionRules.Validate(def)) Note(ConversionLevel.Check, problem.Where, "The draft breaks a rule: " + problem.Message);
        var check = FormValueRules.Check(def, values, DateOnly.FromDateTime(DateTime.UtcNow), forSubmit: false);
        foreach (var problem in check.Problems) Note(ConversionLevel.Check, problem.Where, "An answer in the file was not kept: " + problem.Message);

        var converted = def.Sections.Sum(s => s.Layout == "fields" ? s.Fields?.Count ?? 0 : (s.Rows?.Items?.Count ?? 0) * FormDef.Cells(s).Count());
        Note(ConversionLevel.Info, "file", $"{items.Count} input(s) read, {converted} field(s) and cell(s) in the draft, {check.Clean.Count} answered.");
        return new FormConversion(def, check.Clean, notes, all.Count, converted);
    }

    private static void SetValue(JsonObject values, string[] path, Item item, ICollection<ConversionNote> notes)
    {
        if (item.Value == "") return;
        JsonNode? node = item.Type switch
        {
            FormFieldTypes.Number when Numeric(item.Value, out var n) => JsonValue.Create(n),
            FormFieldTypes.Date when Date(item.Value, out var d) => JsonValue.Create(d.ToString("yyyy-MM-dd")),
            FormFieldTypes.Tick => JsonValue.Create(true),
            FormFieldTypes.Number or FormFieldTypes.Date => null,
            _ => JsonValue.Create(item.Value),
        };
        if (node is null) { notes.Add(new ConversionNote(ConversionLevel.Check, item.Where, $"The value \"{Cut(item.Value, 30)}\" is not a valid {item.Type} and was not kept.")); return; }
        var target = values;
        foreach (var part in path[..^1]) target = target[part] as JsonObject ?? (JsonObject)(target[part] = new JsonObject());
        target[path[^1]] = node;
    }

    private static void Fields(FormSection section, List<Item> members, Names names, JsonObject values, List<ConversionNote> notes)
    {
        section.Fields = [];
        foreach (var item in members)
        {
            var field = new FormField { Id = names.Make(item.Label, "field"), Label = Cut(item.Label == "" ? item.Path : item.Label, 120), Type = item.Type };
            if (item.Type == FormFieldTypes.Choice) field.Options = item.Value != "" && !item.Options.Contains(item.Value) ? [.. item.Options, item.Value] : item.Options;
            section.Fields.Add(field);
            if (item.Guessed) notes.Add(new ConversionNote(ConversionLevel.Check, item.Where, "Made a number because the sample holds a number. Check that it is not text."));
            SetValue(values, [field.Id], item, notes);
        }
        if (section.Fields.Count > FormDefinitionRules.MaxFieldsPerSection) notes.Add(new ConversionNote(ConversionLevel.Check, section.Title ?? section.Id, $"{section.Fields.Count} fields in one section; the limit is {FormDefinitionRules.MaxFieldsPerSection}. Split it in the builder."));
    }

    private sealed class Col
    {
        public int Index;
        public List<string> Chain = [];
        public List<Item> Cells = [];
        public string Top => Chain.Count >= 2 ? Chain[0] : "";
        public string Sub => string.Join(" ", Chain.Skip(Chain.Count >= 2 ? 1 : 0));
        public string Id = "";
    }

    private static string Stem(string heading) => Regex.Replace(heading, @"[\s_\-]*\d+$", "").Trim();

    private static void Grid(FormSection section, IElement table, List<(IElement Tr, List<Item> Inputs)> rows, Names names, JsonObject values, List<ConversionNote> notes)
    {
        // The column each cell starts at, counting colspans and the rowspans of cells above it.
        var layout = new Dictionary<IElement, (int Start, int Span)>();
        var pending = new Dictionary<int, int>();   // column -> rows still covered by a cell above
        foreach (var tr in table.QuerySelectorAll("tr"))
        {
            var at = 0;
            foreach (var cell in tr.Children.Where(c => c.LocalName is "td" or "th"))
            {
                while (pending.GetValueOrDefault(at) > 0) at++;
                var span = int.TryParse(cell.GetAttribute("colspan"), out var s) && s > 0 ? Math.Min(s, 50) : 1;
                var down = int.TryParse(cell.GetAttribute("rowspan"), out var d) && d > 1 ? Math.Min(d, 50) : 1;
                layout[cell] = (at, span);
                if (down > 1) for (var i = 0; i < span; i++) pending[at + i] = down;
                at += span;
            }
            foreach (var key in pending.Keys.ToList()) pending[key]--;
        }
        List<(IElement Cell, int Start, int Span)> Cells(IElement tr) => tr.Children.Where(c => layout.ContainsKey(c)).Select(c => (c, layout[c].Start, layout[c].Span)).ToList();
        var firstInputRow = table.QuerySelectorAll("tr").ToList().IndexOf(rows[0].Tr);
        var headerRows = table.QuerySelectorAll("tr").Take(Math.Max(0, firstInputRow)).Where(tr => tr.QuerySelector("input,select,textarea") is null).ToList();
        var headerText = headerRows.Select(tr => { var map = new Dictionary<int, string>(); foreach (var (cell, start, span) in Cells(tr)) for (var i = 0; i < span; i++) map[start + i] = Clean(cell.TextContent); return map; }).ToList();

        var columns = new SortedDictionary<int, Col>();
        var labels = new List<string>();
        foreach (var (tr, inputs) in rows)
        {
            var cells = Cells(tr);
            var label = "";
            foreach (var item in inputs)
            {
                var start = cells.FirstOrDefault(c => c.Cell == item.El.Closest("td,th")).Start;
                if (!columns.TryGetValue(start, out var col)) columns[start] = col = new Col { Index = start };
                col.Cells.Add(item);
            }
            label = Clean(string.Join(" ", cells.Where(c => c.Cell.QuerySelector("input,select,textarea") is null && c.Start < columns.Keys.DefaultIfEmpty(int.MaxValue).Min()).Select(c => c.Cell.TextContent)));
            if (label == "") label = Clean(cells.FirstOrDefault(c => c.Cell.QuerySelector("input,select,textarea") is null).Cell?.TextContent);
            labels.Add(Cut(label, 80));
        }
        foreach (var col in columns.Values)
        {
            var chain = new List<string>();
            foreach (var header in headerText) if (header.TryGetValue(col.Index, out var text) && text != "" && (chain.Count == 0 || chain[^1] != text)) chain.Add(text);
            col.Chain = chain.Count == 0 ? [Cut(col.Cells[0].Label != "" ? col.Cells[0].Label : "Column " + (col.Index + 1), 60)] : chain;
        }

        // Rows.
        section.Rows = new FormRows { Mode = "fixed", Items = [] };
        var rowNames = new Names();
        var rowIds = new List<string>();
        for (var r = 0; r < rows.Count; r++)
        {
            var id = rowNames.Make(labels[r], "r" + (r + 1));
            rowIds.Add(id);
            section.Rows.Items.Add(new FormRow { Id = id, Label = labels[r] == "" ? "Row " + (r + 1) : labels[r] });
            if (labels[r] == "") notes.Add(new ConversionNote(ConversionLevel.Check, section.Title ?? section.Id, $"Row {r + 1} has no label: called \"Row {r + 1}\"."));
        }

        // Column groups: columns under the same top heading go together; groups with the same sub headings and the same stem are one repeated group.
        var colNames = new Names();
        section.Columns = [];
        var ordered = columns.Values.ToList();
        var blocks = new List<List<Col>>();
        foreach (var col in ordered)
        {
            if (col.Top != "" && blocks.Count > 0 && blocks[^1][0].Top == col.Top) blocks[^1].Add(col);
            else blocks.Add([col]);
        }
        var keyOf = new Dictionary<Col, string[]>();   // the cell path under a row: (column id[, child id])
        for (var i = 0; i < blocks.Count;)
        {
            var block = blocks[i];
            if (block[0].Top == "")
            {
                var col = block[0];
                var column = ColumnFor(col, colNames.Make(col.Sub, "col"), notes, section);
                section.Columns.Add(column);
                keyOf[col] = [column.Id];
                i++;
                continue;
            }
            // consecutive blocks with the same stem and the same sub headings: one repeated group
            var stem = Stem(block[0].Top);
            var run = new List<List<Col>> { block };
            while (i + run.Count < blocks.Count)
            {
                var next = blocks[i + run.Count];
                if (next[0].Top == "" || !Stem(next[0].Top).Equals(stem, StringComparison.OrdinalIgnoreCase) || !next.Select(c => c.Sub.ToLowerInvariant()).SequenceEqual(block.Select(c => c.Sub.ToLowerInvariant()))) break;
                run.Add(next);
            }

            var parent = new FormColumn { Id = colNames.Make(run.Count > 1 ? stem : block[0].Top, "group"), Label = Cut(run.Count > 1 ? stem : block[0].Top, 60), Repeat = run.Count > 1 ? run.Count : null, Children = [] };
            var childNames = new Names();
            foreach (var col in block)
            {
                var child = ColumnFor(col, childNames.Make(col.Sub, "col"), notes, section);
                parent.Children.Add(child);
            }
            for (var n = 0; n < run.Count; n++)
                for (var c = 0; c < run[n].Count; c++)
                    keyOf[run[n][c]] = [(run.Count > 1 ? parent.Id + (n + 1) : parent.Id) + "." + parent.Children[c].Id];
            section.Columns.Add(parent);
            if (run.Count > 1) notes.Add(new ConversionNote(ConversionLevel.Info, section.Title ?? section.Id, $"\"{stem}\" repeats {run.Count} times: one column group with a repeat of {run.Count}."));
            i += run.Count;
        }

        // Answers: values[grid][row][cell].
        foreach (var col in ordered)
            foreach (var item in col.Cells)
            {
                var r = rows.FindIndex(x => x.Inputs.Contains(item));
                item.Type = ColumnType(col);
                SetValue(values, [section.Id, rowIds[r], keyOf[col][0]], item, notes);
            }
    }

    /// <summary>A column is a number when every answer in it is one (or the page says so); one text answer makes it text. Empty boxes do not count.</summary>
    private static string ColumnType(Col col)
    {
        var filled = col.Cells.Where(c => c.Value != "").ToList();
        var types = (filled.Count > 0 ? filled : col.Cells).Select(c => c.Type).Distinct().ToList();
        if (types.Count == 1) return types[0];
        if (types.All(t => t is FormFieldTypes.Number or FormFieldTypes.Text) && filled.All(c => Numeric(c.Value, out _))) return FormFieldTypes.Number;
        return FormFieldTypes.Text;
    }
    private static FormColumn ColumnFor(Col col, string id, ICollection<ConversionNote> notes, FormSection section)
    {
        var type = ColumnType(col);
        if (type == FormFieldTypes.Number && col.Cells.Where(c => c.Value != "").All(c => c.Guessed))
            notes.Add(new ConversionNote(ConversionLevel.Check, $"{section.Title ?? section.Id} / {col.Sub}", "Made a number because every answer in the column is a number. Check that it is not text."));
        var column = new FormColumn { Id = id, Label = Cut(col.Sub == "" ? col.Chain[^1] : col.Sub, 80), Type = type };
        if (type == FormFieldTypes.Choice) column.Options = col.Cells.SelectMany(c => c.Options.Append(c.Value)).Where(o => o != "").Distinct().ToList();
        return column;
    }
}
