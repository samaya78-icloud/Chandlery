using System.Text.RegularExpressions;

namespace Chandlery.Services;

/// <summary>One thing wrong with a definition or an answer: where, and what.</summary>
public record FormProblem(string Where, string Message)
{
    public override string ToString() => Where == "" ? Message : $"{Where}: {Message}";
}

/// <summary>
/// Whether a form definition can be published. Pure: the builder, the publish step and the tests all ask it. A definition that fails any rule cannot be published, so a
/// vessel is never handed one that could loop, hang or point at something that is not there.
/// </summary>
public static partial class FormDefinitionRules
{
    public const int MaxDefinitionChars = 400_000;
    public const int MaxSections = 40;
    public const int MaxFields = 400;
    public const int MaxFieldsPerSection = 100;
    public const int MaxColumns = 100;
    public const int MaxRows = 100;
    public const int MaxCells = 2000;
    public const int MaxFreeRows = 200;
    public const int DefaultFreeRows = 50;
    public const int MaxOptions = 50;
    public const int MaxRepeat = 30;

    public static readonly string[] RowSources = ["tanks", "machinery", "crew"];
    private static readonly string[] PrefillFixed = ["today", "vessel.name", "vessel.imo", "vessel.code"];
    private static readonly string[] Reserved = ["and", "or", "not", "true", "false"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,39}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id) && !Reserved.Contains(id.ToLowerInvariant());

    /// <summary>Reads and checks a definition given as JSON.</summary>
    public static List<FormProblem> Validate(string? json)
    {
        var def = FormDef.TryRead(json, out var error);
        return def is null ? [new FormProblem("", error)] : Validate(def);
    }

    public static List<FormProblem> Validate(FormDef def)
    {
        var problems = new List<FormProblem>();
        void Add(string where, string message) => problems.Add(new FormProblem(where, message));

        if (def.Schema != 1) Add("", $"Schema {def.Schema} is not known (it must be 1).");
        if (string.IsNullOrWhiteSpace(def.Title)) Add("", "The form needs a title.");
        else if (def.Title.Length > 120) Add("", "The title is longer than 120 characters.");
        if (def.Sections.Count == 0) Add("", "The form has no sections.");
        if (def.Sections.Count > MaxSections) Add("", $"A form can have up to {MaxSections} sections.");

        // ---- ids ----
        var sectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fieldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fieldSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var grids = new Dictionary<string, (FormSection Section, HashSet<string> Cells)>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in def.Sections)
        {
            var at = $"section {section.Id}";
            if (!IsValidId(section.Id)) { Add(at, "The id must start with a letter and be letters, digits and underscores (up to 40), and not a reserved word."); continue; }
            if (!sectionIds.Add(section.Id)) { Add(at, "Another section has this id."); continue; }
            if (section.Layout is not ("fields" or "grid")) { Add(at, $"The layout \"{section.Layout}\" is not known (fields or grid)."); continue; }
            if (section.Title is { Length: > 120 }) Add(at, "The title is longer than 120 characters.");

            if (section.Layout == "fields")
            {
                if (section.Columns is not null || section.Rows is not null) Add(at, "A section of fields has no columns or rows.");
                if (section.Fields is null or { Count: 0 }) Add(at, "The section has no fields.");
                else if (section.Fields.Count > MaxFieldsPerSection) Add(at, $"A section can have up to {MaxFieldsPerSection} fields.");
            }
            else
            {
                if (section.Fields is not null) Add(at, "A grid has columns and rows, not fields.");
                var cells = CheckGrid(section, at, Add);
                grids[section.Id] = (section, cells);
            }
        }

        foreach (var section in def.Sections.Where(s => s.Layout == "fields" && IsValidId(s.Id)))
            foreach (var field in section.Fields ?? [])
            {
                var at = $"{section.Id}/{field.Id}";
                if (!IsValidId(field.Id)) { Add(at, "The id must start with a letter and be letters, digits and underscores (up to 40), and not a reserved word."); continue; }
                if (sectionIds.Contains(field.Id) || !fieldIds.Add(field.Id)) { Add(at, "Another field or section has this id (ids are unique across the whole form)."); continue; }
                fieldSection[field.Id] = section.Id;
            }
        if (fieldIds.Count > MaxFields) Add("", $"A form can have up to {MaxFields} fields.");

        // ---- fields ----
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Edge(string from, string to) { if (!edges.TryGetValue(from, out var list)) edges[from] = list = []; list.Add(to); }

        // Checks one expression and returns the nodes it depends on.
        void Expression(string at, string what, string? text, string owner, bool dependency)
        {
            if (text is null) return;
            if (!FormExpression.TryParse(text, out var expression, out var error)) { Add($"{at}.{what}", error); return; }
            foreach (var reference in expression!.References)
            {
                if (fieldIds.Contains(reference.Head))
                {
                    if (reference.Rest.Count > 0) Add($"{at}.{what}", $"{reference.Text}: {reference.Head} is a field, not a grid.");
                    else if (dependency) Edge(owner, reference.Head);
                }
                else if (grids.TryGetValue(reference.Head, out var grid))
                {
                    var mode = grid.Section.Rows?.Mode;
                    if (reference.Row != "*" && mode == "fixed" && !(grid.Section.Rows!.Items ?? []).Any(r => string.Equals(r.Id, reference.Row, StringComparison.OrdinalIgnoreCase)))
                        Add($"{at}.{what}", $"{reference.Text}: the grid {reference.Head} has no row {reference.Row}.");
                    else if (reference.Cell != "*" && !grid.Cells.Contains(reference.Cell))
                        Add($"{at}.{what}", $"{reference.Text}: the grid {reference.Head} has no column {reference.Cell}.");
                    else if (dependency) Edge(owner, reference.Head);
                }
                else Add($"{at}.{what}", $"{reference.Text}: there is no field or grid called {reference.Head}.");
            }
        }

        foreach (var section in def.Sections.Where(s => IsValidId(s.Id)))
        {
            Expression($"section {section.Id}", "showIf", section.ShowIf, section.Id, true);
            if (section.Layout != "fields") continue;
            foreach (var field in section.Fields ?? [])
            {
                if (!IsValidId(field.Id)) continue;
                var at = $"{section.Id}/{field.Id}";
                CheckInput(field, at, isCell: false, Add);
                CheckField(field, at, Add);
                Edge(field.Id, section.Id);                              // a field is hidden when its section is
                Expression(at, "calc", field.Calc, field.Id, true);
                Expression(at, "showIf", field.ShowIf, field.Id, true);
                Expression(at, "flag", field.Flag, field.Id, false);
            }
        }

        // ---- loops ----
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reported = false;
        void Visit(string node, List<string> path)
        {
            if (reported) return;
            if (state.GetValueOrDefault(node) == 2) return;
            if (state.GetValueOrDefault(node) == 1)
            {
                var loop = path.SkipWhile(p => !string.Equals(p, node, StringComparison.OrdinalIgnoreCase)).Append(node);
                Add("", $"A calculation or a show-if loops back on itself: {string.Join(" → ", loop)}.");
                reported = true;
                return;
            }
            state[node] = 1;
            path.Add(node);
            foreach (var next in edges.GetValueOrDefault(node) ?? []) Visit(next, path);
            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }
        foreach (var node in edges.Keys.ToList()) Visit(node, []);

        return problems;
    }

    // ---- one input (a field or a grid cell) ----

    private static void CheckInput(FormInput input, string at, bool isCell, Action<string, string> add)
    {
        if (string.IsNullOrWhiteSpace(input.Label)) add(at, "The label is empty.");
        else if (input.Label.Length > 160) add(at, "The label is longer than 160 characters.");
        if (!FormFieldTypes.All.Contains(input.Type)) { add(at, $"The type \"{input.Type}\" is not known ({string.Join(", ", FormFieldTypes.All)})."); return; }
        if (isCell && input.Type is FormFieldTypes.Photo or FormFieldTypes.Signature or FormFieldTypes.Calculated or FormFieldTypes.LongText) add(at, $"A grid cell cannot be of type {input.Type}.");

        if (input.Decimals is < 0 or > 6) add(at, "Decimals must be 0 to 6.");
        if (input.Min is { } min && input.Max is { } max && min > max) add(at, "The minimum is above the maximum.");
        if (input.MaxLength is < 1 or > 4000) add(at, "The maximum length must be 1 to 4000.");
        if ((input.Min is not null || input.Max is not null || input.Decimals is not null) && input.Type is not (FormFieldTypes.Number or FormFieldTypes.Calculated))
            add(at, "Minimum, maximum and decimals are for numbers.");

        switch (input.Type)
        {
            case FormFieldTypes.Choice or FormFieldTypes.MultiChoice:
                var options = input.Options ?? [];
                if (options.Count == 0) add(at, "A choice needs its options.");
                if (options.Count > MaxOptions) add(at, $"A choice can have up to {MaxOptions} options.");
                if (options.Any(string.IsNullOrWhiteSpace)) add(at, "An option is empty.");
                if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count) add(at, "An option appears twice.");
                break;
            case FormFieldTypes.Rating:
                if (input.Options is { Count: > MaxOptions }) add(at, $"A rating can have up to {MaxOptions} options.");
                break;
            case FormFieldTypes.Lookup:
                if (input.Source is null || !FormFieldTypes.LookupSources.Contains(input.Source)) add(at, $"A lookup needs a source ({string.Join(", ", FormFieldTypes.LookupSources)}).");
                break;
        }
    }

    private static void CheckField(FormField field, string at, Action<string, string> add)
    {
        var calculated = field.Calc is not null || field.Type == FormFieldTypes.Calculated;
        if (field.Type == FormFieldTypes.Calculated && field.Calc is null) add(at, "A calculated field needs its calculation.");
        if (calculated)
        {
            if (field.Required) add(at, "A calculated field is worked out, so it cannot be required.");
            if (field.Default is not null || field.Prefill is not null) add(at, "A calculated field has no default or prefill.");
        }

        if (field.Prefill is { } prefill && !(PrefillFixed.Contains(prefill) || (prefill.StartsWith("previous.", StringComparison.Ordinal) && IsValidId(prefill["previous.".Length..]))))
            add(at, $"The prefill \"{prefill}\" is not known ({string.Join(", ", PrefillFixed)}, previous.<field>).");

        if (field.Default is { } value)
        {
            var ok = field.Type switch
            {
                FormFieldTypes.Date or FormFieldTypes.Moment => value == "today" || DateOnly.TryParseExact(value, "yyyy-MM-dd", out _),
                FormFieldTypes.Number => decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
                FormFieldTypes.Tick => value is "true" or "false",
                FormFieldTypes.Choice => (field.Options ?? []).Contains(value),
                FormFieldTypes.YesNoNa => FormFieldTypes.YesNoNaValues.Contains(value),
                FormFieldTypes.Rating => RatingOptions(field).Contains(value),
                FormFieldTypes.Text or FormFieldTypes.LongText => true,
                _ => false,
            };
            if (!ok) add(at, $"The default \"{value}\" does not suit a field of type {field.Type}.");
        }

        if (field.FlagOn is { Count: > 0 } flagOn)
        {
            var allowed = field.Type switch
            {
                FormFieldTypes.Choice or FormFieldTypes.MultiChoice => field.Options ?? [],
                FormFieldTypes.YesNoNa => FormFieldTypes.YesNoNaValues.ToList(),
                FormFieldTypes.Rating => RatingOptions(field),
                _ => null,
            };
            if (allowed is null) add(at, "flagOn is for choices, yes/no/NA and ratings.");
            else foreach (var answer in flagOn.Where(a => !allowed.Contains(a))) add(at, $"flagOn names \"{answer}\", which is not one of the answers.");
        }
        if (field.Validate is { NotFuture: true, NotPast: true }) add(at, "A date cannot be both not-future and not-past.");
        if (field.Validate is not null && field.Type is not (FormFieldTypes.Date or FormFieldTypes.Moment)) add(at, "validate is for dates.");
    }

    public static List<string> RatingOptions(FormInput input) => input.Options is { Count: > 0 } ? input.Options : [.. FormFieldTypes.DefaultRating];

    // ---- a grid: its columns, its rows, and the ids of its cells ----

    private static HashSet<string> CheckGrid(FormSection grid, string at, Action<string, string> add)
    {
        var cells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (grid.Columns is null or { Count: 0 }) add(at, "A grid needs columns.");
        else
        {
            void CheckColumns(List<FormColumn> columns, string prefix, int level)
            {
                var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var column in columns)
                {
                    var here = $"{at}/{prefix}{column.Id}";
                    if (!IsValidId(column.Id)) { add(here, "The id must start with a letter and be letters, digits and underscores (up to 40)."); continue; }
                    if (!siblings.Add(column.Id)) { add(here, "Another column here has this id."); continue; }
                    if (column.Children is { Count: > 0 } children)
                    {
                        if (level >= 3) { add(here, "Columns can be grouped up to three levels."); continue; }
                        if (column.Repeat is < 1 or > MaxRepeat) add(here, $"Repeat must be 1 to {MaxRepeat}.");
                        if (string.IsNullOrWhiteSpace(column.Label)) add(here, "The label is empty.");
                        for (var i = 1; i <= Math.Clamp(column.Repeat ?? 1, 1, MaxRepeat); i++)
                            CheckColumns(children, prefix + (column.Repeat is null ? column.Id : column.Id + i) + ".", level + 1);
                    }
                    else
                    {
                        CheckInput(column, here, isCell: true, add);
                        if (column.Repeat is not null) add(here, "Repeat is for a group of columns.");
                        if (!cells.Add(prefix + column.Id)) add(here, "The cell ids overlap (a repeated group and a column are named alike).");
                    }
                }
            }
            if (grid.Columns is { } columnList) CheckColumns(columnList, "", 1);
            if (cells.Count > MaxColumns) add(at, $"A grid can have up to {MaxColumns} columns once groups are repeated.");
        }

        var rows = grid.Rows;
        if (rows is null) { add(at, "A grid needs its rows."); return cells; }
        switch (rows.Mode)
        {
            case "fixed":
                var items = rows.Items ?? [];
                if (items.Count == 0) add(at, "A grid of fixed rows needs its rows.");
                if (items.Count > MaxRows) add(at, $"A grid can have up to {MaxRows} fixed rows.");
                if (items.Any(r => !IsValidId(r.Id))) add(at, "A row id must start with a letter and be letters, digits and underscores.");
                if (items.Any(r => string.IsNullOrWhiteSpace(r.Label))) add(at, "A row has no label.");
                if (items.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count) add(at, "Two rows have the same id.");
                if (items.Count * cells.Count > MaxCells) add(at, $"A grid can have up to {MaxCells} cells.");
                if (rows.Source is not null || rows.Max is not null) add(at, "Source and max are not for fixed rows.");
                break;
            case "free":
                if (rows.Max is < 1 or > MaxFreeRows) add(at, $"The most rows must be 1 to {MaxFreeRows}.");
                if (rows.Items is not null || rows.Source is not null) add(at, "Free rows have no items or source.");
                if ((rows.Max ?? DefaultFreeRows) * cells.Count > MaxCells * 2) add(at, "Too many cells for the rows allowed.");
                break;
            case "list":
                if (rows.Source is null || !RowSources.Contains(rows.Source)) add(at, $"Rows from a list need a source ({string.Join(", ", RowSources)}).");
                if (rows.Items is not null) add(at, "Rows from a list have no items.");
                if (rows.Max is < 1 or > MaxFreeRows) add(at, $"The most rows must be 1 to {MaxFreeRows}.");
                break;
            default:
                add(at, $"The row mode \"{rows.Mode}\" is not known (fixed, free or list).");
                break;
        }
        return cells;
    }
}
