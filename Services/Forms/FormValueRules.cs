using System.Globalization;
using System.Text.Json.Nodes;

namespace Chandlery.Services;

/// <summary>An answer that the form's own rules flag: the review and the action items can point at it.</summary>
public record FormException(string FieldId, string Label, string Message);

/// <summary>What <see cref="FormValueRules.Check"/> found: the answers as they should be kept, what is wrong, and what is flagged.</summary>
public record FormCheck(JsonObject Clean, List<FormProblem> Problems, List<FormException> Exceptions)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>What a new entry can be filled in from: the vessel, today, and the previous entry of the same form.</summary>
public record FormPrefillContext(string VesselName, string VesselImo, string VesselCode, DateOnly Today, JsonObject? Previous);

/// <summary>
/// Checks a set of answers against the definition it was filled on. Pure: the vessel's editor, the submit step, the step that receives an entry from the other side
/// and the tests all call it, so the two sides can never disagree about an answer. It works out which fields are hidden (a hidden field is neither required nor kept),
/// recomputes every calculation, checks the types and limits, and finds the exceptions. A draft is checked for what is wrong with what has been typed; only a
/// submission also insists that required answers are there.
/// </summary>
public static class FormValueRules
{
    private const int MaxRounds = 40;

    public static FormCheck Check(FormDef def, JsonObject? values, DateOnly today, bool forSubmit)
    {
        var problems = new List<FormProblem>();
        var raw = values ?? [];
        var fieldIds = def.Sections.Where(s => s.Layout == "fields").SelectMany(s => s.Fields ?? []).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gridIds = def.Grids.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in raw.Select(p => p.Key).Where(k => !fieldIds.Contains(k) && !gridIds.Contains(k)))
            problems.Add(new FormProblem(key, "This answer is not part of the form."));

        // ---- read every answer into its type ----
        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in def.Sections.Where(s => s.Layout == "fields"))
            foreach (var field in section.Fields ?? [])
            {
                if (field.Calc is not null || field.Type == FormFieldTypes.Calculated)
                {
                    if (raw.ContainsKey(field.Id)) { /* a typed answer for a calculated field is simply ignored: it is worked out */ }
                    continue;
                }
                fields[field.Id] = Coerce(field, field.Label, field.Id, raw[field.Id], today, field.Validate, problems);
            }

        var grids = new Dictionary<string, Grid>(StringComparer.OrdinalIgnoreCase);
        foreach (var grid in def.Grids) grids[grid.Id] = ReadGrid(grid, raw[grid.Id], today, problems);

        // ---- which parts show, and what the calculations give: repeated until nothing moves (a definition has no loops, so this ends) ----
        var calcs = def.Sections.SelectMany(s => s.Fields ?? []).Where(f => f.Calc is not null).ToDictionary(f => f.Id, f => FormExpression.Parse(f.Calc!), StringComparer.OrdinalIgnoreCase);
        var showIfs = new Dictionary<string, FormExpression>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in def.Sections)
        {
            if (section.ShowIf is not null) showIfs["section:" + section.Id] = FormExpression.Parse(section.ShowIf);
            foreach (var field in section.Fields ?? []) if (field.ShowIf is not null) showIfs["field:" + field.Id] = FormExpression.Parse(field.ShowIf);
        }

        var hiddenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hiddenFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var calculated = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; round < MaxRounds; round++)
        {
            var scope = new Scope(fields, calculated, grids, hiddenSections, hiddenFields);
            var nextSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in def.Sections)
            {
                var sectionHidden = showIfs.TryGetValue("section:" + section.Id, out var sectionRule) && !sectionRule.EvaluateBool(scope);
                if (sectionHidden) nextSections.Add(section.Id);
                foreach (var field in section.Fields ?? [])
                    if (sectionHidden || (showIfs.TryGetValue("field:" + field.Id, out var fieldRule) && !fieldRule.EvaluateBool(scope))) nextFields.Add(field.Id);
            }
            var nextCalcs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in def.Sections.SelectMany(s => s.Fields ?? []).Where(f => calcs.ContainsKey(f.Id) && !nextFields.Contains(f.Id)))
                nextCalcs[field.Id] = Shape(field, calcs[field.Id].Evaluate(scope));

            var settled = nextSections.SetEquals(hiddenSections) && nextFields.SetEquals(hiddenFields) && SameValues(nextCalcs, calculated);
            (hiddenSections, hiddenFields, calculated) = (nextSections, nextFields, nextCalcs);
            if (settled) break;
        }

        // ---- required answers, the exceptions, and the answers to keep ----
        var clean = new JsonObject();
        var exceptions = new List<FormException>();
        var finalScope = new Scope(fields, calculated, grids, hiddenSections, hiddenFields);
        foreach (var section in def.Sections.Where(s => s.Layout == "fields" && !hiddenSections.Contains(s.Id)))
            foreach (var field in section.Fields ?? [])
            {
                if (hiddenFields.Contains(field.Id)) continue;
                var isCalc = calcs.ContainsKey(field.Id);
                var value = isCalc ? calculated.GetValueOrDefault(field.Id) : fields.GetValueOrDefault(field.Id);
                if (IsEmpty(value))
                {
                    if (field.Required && forSubmit) problems.Add(new FormProblem(field.Id, $"{field.Label} is required."));
                    continue;
                }
                clean[field.Id] = ToNode(value);
                if (field.FlagOn is { Count: > 0 } flagOn && Answers(value).Any(a => flagOn.Contains(a)))
                    exceptions.Add(new FormException(field.Id, field.Label, $"{field.Label}: {string.Join(", ", Answers(value).Where(a => flagOn.Contains(a)))}"));
                else if (field.Flag is not null && FormExpression.Parse(field.Flag).EvaluateBool(finalScope))
                    exceptions.Add(new FormException(field.Id, field.Label, $"{field.Label}: {Display(value)} is outside what is expected"));
            }
        foreach (var section in def.Grids.Where(s => !hiddenSections.Contains(s.Id)))
        {
            var grid = grids[section.Id];
            if (forSubmit && section.Rows?.Mode == "fixed")
                foreach (var row in section.Rows.Items ?? [])
                    foreach (var (cellId, column) in FormDef.Cells(section).Where(c => c.Column.Required))
                        if (IsEmpty(grid.Cell(row.Id, cellId))) problems.Add(new FormProblem($"{section.Id}.{row.Id}.{cellId}", $"{row.Label}, {column.Label} is required."));
            if (grid.Rows.Count > 0) clean[section.Id] = grid.ToNode();
        }

        return new FormCheck(clean, problems, exceptions);
    }

    // ---- a grid's answers ----

    private sealed class Grid
    {
        public Dictionary<string, Dictionary<string, object?>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object? Cell(string row, string cell) => Rows.TryGetValue(row, out var cells) ? cells.GetValueOrDefault(cell) : null;

        public JsonObject ToNode()
        {
            var node = new JsonObject();
            foreach (var (row, cells) in Rows)
            {
                var item = new JsonObject();
                foreach (var (cell, value) in cells) if (!IsEmpty(value)) item[cell] = FormValueRules.ToNode(value);
                if (item.Count > 0) node[row] = item;
            }
            return node;
        }
    }

    private static Grid ReadGrid(FormSection section, JsonNode? raw, DateOnly today, List<FormProblem> problems)
    {
        var grid = new Grid();
        if (raw is null) return grid;
        if (raw is not JsonObject rows) { problems.Add(new FormProblem(section.Id, "The grid's answers are not in the right shape.")); return grid; }
        var cells = FormDef.Cells(section).ToDictionary(c => c.Id, c => c.Column, StringComparer.OrdinalIgnoreCase);
        var mode = section.Rows?.Mode ?? "fixed";
        var known = mode == "fixed" ? (section.Rows?.Items ?? []).ToDictionary(r => r.Id, r => r.Label, StringComparer.OrdinalIgnoreCase) : null;
        var limit = section.Rows?.Max ?? FormDefinitionRules.DefaultFreeRows;
        if (mode != "fixed" && rows.Count > limit) problems.Add(new FormProblem(section.Id, $"At most {limit} rows can be added."));

        foreach (var (rowId, rowNode) in rows)
        {
            if (known is not null && !known.ContainsKey(rowId)) { problems.Add(new FormProblem($"{section.Id}.{rowId}", "This row is not part of the grid.")); continue; }
            if (mode != "fixed" && !FormDefinitionRules.IsValidId(rowId)) { problems.Add(new FormProblem($"{section.Id}.{rowId}", "This row id is not valid.")); continue; }
            if (rowNode is not JsonObject cellsNode) { problems.Add(new FormProblem($"{section.Id}.{rowId}", "The row's answers are not in the right shape.")); continue; }
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cellId, cellNode) in cellsNode)
            {
                if (!cells.TryGetValue(cellId, out var column)) { problems.Add(new FormProblem($"{section.Id}.{rowId}.{cellId}", "This column is not part of the grid.")); continue; }
                var label = known is not null ? $"{known[rowId]}, {column.Label}" : $"{rowId}, {column.Label}";
                row[cellId] = Coerce(column, label, $"{section.Id}.{rowId}.{cellId}", cellNode, today, null, problems);
            }
            grid.Rows[rowId] = row;
        }
        return grid;
    }

    // ---- what the expressions see ----

    private sealed class Scope(Dictionary<string, object?> fields, Dictionary<string, object?> calculated, Dictionary<string, Grid> grids,
        HashSet<string> hiddenSections, HashSet<string> hiddenFields) : IFormScope
    {
        public object? Resolve(FormRef reference)
        {
            if (grids.TryGetValue(reference.Head, out var grid))
            {
                if (hiddenSections.Contains(reference.Head)) return reference.Row == "*" || reference.Cell == "*" ? new List<object?>() : null;
                if (reference.Row != "*" && reference.Cell != "*") return Simple(grid.Cell(reference.Row, reference.Cell));
                var found = new List<object?>();
                foreach (var (rowId, cells) in grid.Rows)
                {
                    if (reference.Row != "*" && !string.Equals(rowId, reference.Row, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var (cellId, value) in cells)
                        if (reference.Cell == "*" || string.Equals(cellId, reference.Cell, StringComparison.OrdinalIgnoreCase)) found.Add(Simple(value));
                }
                return found;
            }
            if (hiddenFields.Contains(reference.Head)) return null;
            return Simple(calculated.TryGetValue(reference.Head, out var calc) ? calc : fields.GetValueOrDefault(reference.Head));
        }

        private static object? Simple(object? value) => value switch
        {
            List<string> list => list.Cast<object?>().ToList(),
            JsonObject signature => signature["name"]?.GetValue<string>(),
            _ => value,
        };
    }

    // ---- reading one answer ----

    private static object? Coerce(FormInput input, string label, string at, JsonNode? node, DateOnly today, FormValidate? validate, List<FormProblem> problems)
    {
        void Bad(string message) => problems.Add(new FormProblem(at, $"{label}: {message}"));
        if (node is null) return null;
        if (node is JsonValue textOnly && textOnly.TryGetValue<string>(out var emptyCheck) && string.IsNullOrWhiteSpace(emptyCheck)) return null;

        string? Text() => node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s.Trim() : v.ToString()) : null;

        switch (input.Type)
        {
            case FormFieldTypes.Text or FormFieldTypes.LongText:
            {
                var text = Text();
                var limit = input.MaxLength ?? (input.Type == FormFieldTypes.LongText ? 4000 : 500);
                if (text is null) { Bad("this answer should be text."); return null; }
                if (text.Length > limit) { Bad($"is longer than {limit} characters."); return null; }
                return text;
            }
            case FormFieldTypes.Number:
            {
                if (node is not JsonValue v) { Bad("this answer should be a number."); return null; }
                decimal number;
                if (v.TryGetValue<decimal>(out var d)) number = d;
                else if (v.TryGetValue<double>(out var dbl) && double.IsFinite(dbl) && Math.Abs(dbl) < 1e15) number = (decimal)dbl;
                else if (v.TryGetValue<string>(out var s) && decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) number = parsed;
                else { Bad("is not a number."); return null; }
                if (input.Decimals is { } places) number = Math.Round(number, places, MidpointRounding.AwayFromZero);
                if (input.Min is { } min && number < min) { Bad($"{number} is below {min}."); return null; }
                if (input.Max is { } max && number > max) { Bad($"{number} is above {max}."); return null; }
                return number;
            }
            case FormFieldTypes.Date:
            {
                var text = Text();
                if (text is null || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { Bad("is not a date (year-month-day)."); return null; }
                if (validate?.NotFuture == true && date > today) { Bad("cannot be in the future."); return null; }
                if (validate?.NotPast == true && date < today) { Bad("cannot be in the past."); return null; }
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            case FormFieldTypes.Time:
            {
                var text = Text();
                if (text is null || !TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) { Bad("is not a time (hours:minutes)."); return null; }
                return time.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            case FormFieldTypes.Moment:
            {
                var text = Text();
                if (text is null || !DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)) { Bad("is not a date and time."); return null; }
                if (validate?.NotFuture == true && DateOnly.FromDateTime(moment) > today) { Bad("cannot be in the future."); return null; }
                if (validate?.NotPast == true && DateOnly.FromDateTime(moment) < today) { Bad("cannot be in the past."); return null; }
                return moment.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
            }
            case FormFieldTypes.Choice or FormFieldTypes.YesNoNa or FormFieldTypes.Rating:
            {
                var text = Text();
                var options = input.Type switch
                {
                    FormFieldTypes.Choice => input.Options ?? [],
                    FormFieldTypes.YesNoNa => FormFieldTypes.YesNoNaValues.ToList(),
                    _ => FormDefinitionRules.RatingOptions(input),
                };
                if (text is null || !options.Contains(text)) { Bad($"\"{text}\" is not one of {string.Join(", ", options)}."); return null; }
                return text;
            }
            case FormFieldTypes.MultiChoice:
            {
                if (node is not JsonArray array) { Bad("should be a list of choices."); return null; }
                var chosen = new List<string>();
                foreach (var item in array)
                {
                    var text = item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (text is null || !(input.Options ?? []).Contains(text)) { Bad($"\"{text}\" is not one of {string.Join(", ", input.Options ?? [])}."); return null; }
                    if (!chosen.Contains(text)) chosen.Add(text);
                }
                return chosen.Count == 0 ? null : chosen;
            }
            case FormFieldTypes.Tick:
            {
                if (node is JsonValue v && v.TryGetValue<bool>(out var ticked)) return ticked;
                if (Text() is "true" or "false") return Text() == "true";
                Bad("should be ticked or not.");
                return null;
            }
            case FormFieldTypes.Photo:
            {
                if (node is not JsonArray array) { Bad("should be a list of pictures."); return null; }
                var ids = new List<string>();
                foreach (var item in array)
                {
                    var text = item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (!Guid.TryParse(text, out var id)) { Bad("a picture is not a valid file."); return null; }
                    ids.Add(id.ToString());
                }
                if (ids.Count > FileRules.MaxPerOwner) { Bad($"can have up to {FileRules.MaxPerOwner} pictures."); return null; }
                return ids.Count == 0 ? null : ids;
            }
            case FormFieldTypes.Signature:
            {
                if (node is not JsonObject signature || signature["name"] is not JsonValue nameNode || !nameNode.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name)) { Bad("should be signed with a name."); return null; }
                var at2 = signature["at"] is JsonValue atNode && atNode.TryGetValue<string>(out var stamp) ? stamp : "";
                return new JsonObject { ["name"] = name.Trim(), ["at"] = at2 };
            }
            case FormFieldTypes.Lookup:
            {
                var text = Text();
                if (text is null) { Bad("should be one of the list."); return null; }
                if (text.Length > 100) { Bad("is too long."); return null; }
                return text;
            }
            default:
                return null;
        }
    }

    /// <summary>A calculation's result in the shape of its field: a number rounded to the field's decimals, text, or a yes/no.</summary>
    private static object? Shape(FormField field, object? result) => result switch
    {
        decimal d => field.Decimals is { } places ? Math.Round(d, places, MidpointRounding.AwayFromZero) : d,
        string s => s,
        bool b => b,
        _ => null,
    };

    private static bool SameValues(Dictionary<string, object?> a, Dictionary<string, object?> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && Equals(p.Value, other));

    private static bool IsEmpty(object? value) => value is null || value is "" || value is List<string> { Count: 0 };

    private static IEnumerable<string> Answers(object? value) => value switch
    {
        string s => [s],
        List<string> list => list,
        _ => [],
    };

    private static string Display(object? value) => value switch
    {
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        JsonObject o => o["name"]?.ToString() ?? "",
        List<string> l => string.Join(", ", l),
        _ => value?.ToString() ?? "",
    };

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        decimal d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        List<string> list => new JsonArray(list.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        JsonObject o => JsonNode.Parse(o.ToJsonString()),
        _ => JsonValue.Create(value.ToString()),
    };

    // ---- first answers ----

    /// <summary>The answers a new entry starts with: a field's prefill where it can be had, else its default. Calculated fields are left for the checker.</summary>
    public static JsonObject Initial(FormDef def, FormPrefillContext context)
    {
        var values = new JsonObject();
        foreach (var field in def.Sections.Where(s => s.Layout == "fields").SelectMany(s => s.Fields ?? []).Where(f => f.Calc is null && f.Type != FormFieldTypes.Calculated))
        {
            var text = field.Prefill switch
            {
                "today" => context.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "vessel.name" => context.VesselName,
                "vessel.imo" => context.VesselImo,
                "vessel.code" => context.VesselCode,
                { } p when p.StartsWith("previous.", StringComparison.Ordinal) => context.Previous?[p["previous.".Length..]]?.ToJsonString().Trim('"'),
                _ => null,
            };
            if (field.Prefill is { } prefill && prefill.StartsWith("previous.", StringComparison.Ordinal) && context.Previous?[prefill["previous.".Length..]] is { } previous)
            {
                values[field.Id] = JsonNode.Parse(previous.ToJsonString());
                continue;
            }
            text ??= field.Default == "today" ? context.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : field.Default;
            if (field.Type == FormFieldTypes.Moment && text is { Length: 10 }) text += "T00:00";
            if (string.IsNullOrEmpty(text)) continue;
            values[field.Id] = field.Type switch
            {
                FormFieldTypes.Number when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
                FormFieldTypes.Tick => JsonValue.Create(text == "true"),
                _ => JsonValue.Create(text),
            };
        }
        return values;
    }
}
