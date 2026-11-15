using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chandlery.Services;

/// <summary>The kinds of answer a form field can take. A field with a calculation is read-only and its answer is always worked out.</summary>
public static class FormFieldTypes
{
    public const string Text = "text", LongText = "longtext", Number = "number", Date = "date", Time = "time", Moment = "datetime", Choice = "choice",
        MultiChoice = "multichoice", YesNoNa = "yesnona", Rating = "rating", Tick = "tick", Photo = "photo", Signature = "signature", Lookup = "lookup", Calculated = "calculated";

    public static readonly string[] All = [Text, LongText, Number, Date, Time, Moment, Choice, MultiChoice, YesNoNa, Rating, Tick, Photo, Signature, Lookup, Calculated];

    public static readonly string[] YesNoNaValues = ["Yes", "No", "NA"];
    public static readonly string[] DefaultRating = ["Good", "Fair", "Bad"];
    public static readonly string[] LookupSources = ["machinery", "tank", "crew", "port"];
}

/// <summary>What is shared by a field and a grid cell: a type and the limits on its answer.</summary>
public abstract class FormInput
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = FormFieldTypes.Text;
    public bool Required { get; set; }
    public string? Unit { get; set; }
    public int? Decimals { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>The choices of a choice, multi choice or rating (a rating without any has Good, Fair, Bad); the source of a lookup is named in <see cref="Source"/>.</summary>
    public List<string>? Options { get; set; }
    public string? Source { get; set; }
}

public class FormValidate
{
    public bool NotFuture { get; set; }
    public bool NotPast { get; set; }
}

public class FormField : FormInput
{
    /// <summary>A starting answer: text or a number, or <c>today</c> for a date.</summary>
    public string? Default { get; set; }

    /// <summary>Where the first answer comes from: <c>today</c>, <c>vessel.name</c>, <c>vessel.imo</c>, <c>vessel.code</c> or <c>previous.{fieldId}</c> (the same answer on the previous entry).</summary>
    public string? Prefill { get; set; }
    public string? Calc { get; set; }
    public string? ShowIf { get; set; }

    /// <summary>An expression that is true when the answer is an exception (for example <c>pressure &gt; 12</c>).</summary>
    public string? Flag { get; set; }

    /// <summary>Answers (of a choice, yes/no/NA or rating) that make an exception.</summary>
    public List<string>? FlagOn { get; set; }
    public FormValidate? Validate { get; set; }
}

/// <summary>A column of a grid. A column with children is a group; with <see cref="Repeat"/> the group is drawn that many times and its cells are numbered (<c>b1.f</c>, <c>b2.f</c>).</summary>
public class FormColumn : FormInput
{
    public int? Repeat { get; set; }
    public List<FormColumn>? Children { get; set; }
}

public class FormRow
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>How a grid gets its rows: <c>fixed</c> (declared here), <c>free</c> (the person adds them) or <c>list</c> (taken from a list of the vessel at fill time: the tanks, the cylinders).</summary>
public class FormRows
{
    public string Mode { get; set; } = "fixed";
    public List<FormRow>? Items { get; set; }
    public string? Source { get; set; }
    public int? Max { get; set; }
}

public class FormSection
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }

    /// <summary><c>fields</c> or <c>grid</c>.</summary>
    public string Layout { get; set; } = "fields";
    public string? ShowIf { get; set; }
    public List<FormField>? Fields { get; set; }
    public List<FormColumn>? Columns { get; set; }
    public FormRows? Rows { get; set; }
}

public class FormDef
{
    public int Schema { get; set; } = 1;
    public string Title { get; set; } = "";
    public List<FormSection> Sections { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,   // the vocabulary is closed: a misspelt rule is an error, not a silent no-op
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a definition. Returns null and says why when the text is not JSON of the right shape.</summary>
    public static FormDef? TryRead(string? json, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(json)) { error = "The definition is empty."; return null; }
        if (json.Length > FormDefinitionRules.MaxDefinitionChars) { error = $"The definition is larger than {FormDefinitionRules.MaxDefinitionChars / 1000} KB."; return null; }
        try { return JsonSerializer.Deserialize<FormDef>(json, Json) ?? throw new JsonException("The definition is empty."); }
        catch (JsonException ex) { error = ex.Message; return null; }
    }

    private static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The same JSON, laid out for a person to read and edit.</summary>
    public string ToPrettyJson() => JsonSerializer.Serialize(this, Pretty);

    /// <summary>
    /// Tidies what a screen leaves behind: a box left empty means "not set", not an empty text or list. Called by the builder after each change, so the rules see
    /// what the author meant.
    /// </summary>
    public FormDef Tidy()
    {
        static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        static List<string>? NoneIfEmpty(List<string>? list) => list is null || list.Count == 0 ? null : list;

        void Input(FormInput input)
        {
            (input.Unit, input.Source, input.Options) = (Blank(input.Unit), Blank(input.Source), NoneIfEmpty(input.Options));
        }

        void Columns(List<FormColumn>? columns)
        {
            foreach (var column in columns ?? [])
            {
                Input(column);
                if (column.Children is { Count: > 0 }) { column.Repeat = column.Repeat is null or < 1 ? null : column.Repeat; Columns(column.Children); }
            }
        }

        foreach (var section in Sections)
        {
            (section.Title, section.ShowIf) = (Blank(section.Title), Blank(section.ShowIf));
            foreach (var field in section.Fields ?? [])
            {
                Input(field);
                (field.Default, field.Prefill, field.Calc, field.ShowIf, field.Flag, field.FlagOn) = (Blank(field.Default), Blank(field.Prefill), Blank(field.Calc), Blank(field.ShowIf), Blank(field.Flag), NoneIfEmpty(field.FlagOn));
                if (field.Validate is { NotFuture: false, NotPast: false }) field.Validate = null;
            }
            Columns(section.Columns);
            if (section.Rows is { } rows) rows.Source = Blank(rows.Source);
        }
        return this;
    }

    // ---- walking it ----

    public IEnumerable<FormSection> Grids => Sections.Where(s => s.Layout == "grid");

    /// <summary>The cells of a grid, groups expanded: each with its full id (<c>b1.f</c>) and the column it came from.</summary>
    public static IEnumerable<(string Id, FormColumn Column)> Cells(FormSection grid)
    {
        IEnumerable<(string, FormColumn)> Walk(IEnumerable<FormColumn> columns, string prefix)
        {
            foreach (var column in columns)
            {
                if (column.Children is { Count: > 0 } children)
                {
                    for (var i = 1; i <= Math.Max(1, column.Repeat ?? 1); i++)
                    {
                        var name = column.Repeat is null ? column.Id : column.Id + i;
                        foreach (var cell in Walk(children, prefix + name + ".")) yield return cell;
                    }
                }
                else yield return (prefix + column.Id, column);
            }
        }
        return Walk(grid.Columns ?? [], "");
    }
}
