using System.Text.Json.Nodes;
using Chandlery.Models;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>A definition used by the form tests: header fields with a calculation, flags and a hidden section, a grid of repeated columns, and a section shown only when the grid has a measurement.</summary>
internal static class FormSamples
{
    public const string Crankpin = """
    {
      "schema": 1, "title": "Crankpin Bearings",
      "sections": [
        { "id": "header", "layout": "fields", "fields": [
          { "id": "engineType", "label": "Engine type", "type": "text", "required": true },
          { "id": "date", "label": "Date", "type": "date", "default": "today", "validate": { "notFuture": true } },
          { "id": "draftFwd", "label": "Draft fwd", "type": "number", "unit": "m", "decimals": 1 },
          { "id": "draftAft", "label": "Draft aft", "type": "number", "unit": "m", "decimals": 1 },
          { "id": "trim", "label": "Trim", "type": "number", "decimals": 1, "calc": "draftAft - draftFwd" },
          { "id": "condition", "label": "Condition", "type": "rating", "flagOn": ["Bad"] },
          { "id": "pressure", "label": "Pressure", "type": "number", "decimals": 1, "min": 0, "max": 50, "flag": "pressure > 12" }
        ]},
        { "id": "measured", "title": "Measured", "layout": "grid",
          "columns": [ { "id": "b", "label": "Bearing", "repeat": 2, "children": [
            { "id": "f", "label": "F", "type": "number", "decimals": 2 },
            { "id": "a", "label": "A", "type": "number", "decimals": 2 } ] } ],
          "rows": { "mode": "fixed", "items": [ { "id": "orig", "label": "Original" }, { "id": "meas", "label": "Measured" } ] } },
        { "id": "remarks", "title": "Remarks", "layout": "fields", "showIf": "any(measured.meas.*)", "fields": [
          { "id": "note", "label": "Chief engineer", "type": "longtext", "required": true } ] }
      ]
    }
    """;

    public static FormDef Def(string json = Crankpin) => FormDef.TryRead(json, out var error) ?? throw new InvalidOperationException(error);
    public static readonly DateOnly Today = new(2026, 11, 15);
    public static JsonObject Values(string json) => (JsonObject)JsonNode.Parse(json)!;
}

public class FormExpressionTests
{
    private sealed class Dict(Dictionary<string, object?> values) : IFormScope
    {
        public object? Resolve(FormRef reference) => values.GetValueOrDefault(reference.Text);
    }

    private static object? Eval(string text, Dictionary<string, object?>? values = null) => FormExpression.Parse(text).Evaluate(new Dict(values ?? []));

    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("10 / 4", "2.5")]
    [InlineData("-3 + 5", "2")]
    [InlineData("round(2.345, 2)", "2.35")]
    [InlineData("round(2.5)", "3")]
    public void Arithmetic(string text, string expected) => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Eval(text));

    [Fact]
    public void AMissingAnswerOrADivisionByZeroGivesNothing_NeverZero()
    {
        Assert.Null(Eval("a + 1"));
        Assert.Null(Eval("10 / 0"));
        Assert.Null(Eval("a * b", new() { ["a"] = 2m }));
    }

    [Fact]
    public void ComparisonsAndLogic()
    {
        Assert.Equal(true, Eval("a > 5 and not b", new() { ["a"] = 6m, ["b"] = false }));
        Assert.Equal(false, Eval("a > 5 and b", new() { ["a"] = 6m, ["b"] = false }));
        Assert.Equal(true, Eval("a > 5 or b", new() { ["a"] = 1m, ["b"] = true }));
        Assert.Equal(true, Eval("'Yes' = 'Yes'"));
        Assert.Equal(true, Eval("'b' > 'a'"));
        Assert.Equal(true, Eval("'2026-11-02' < '2026-11-15'"));            // dates are ISO text
        Assert.Equal(false, Eval("a > 5", new()));                          // nothing is not greater than anything
        Assert.Equal(true, Eval("a = b", new()));                           // but nothing equals nothing
        Assert.Equal(true, Eval("not (1 = 2)"));
        Assert.Equal(true, Eval("1 != 2"));
    }

    [Fact]
    public void FunctionsOverAListLeaveOutWhatIsMissing()
    {
        var scope = new Dictionary<string, object?> { ["g.*.x"] = new List<object?> { 1m, 2m, null, 3m, "" } };
        Assert.Equal(6m, Eval("sum(g.*.x)", scope));
        Assert.Equal(3m, Eval("count(g.*.x)", scope));
        Assert.Equal(true, Eval("any(g.*.x)", scope));
        Assert.Equal(1m, Eval("min(g.*.x)", scope));
        Assert.Equal(3m, Eval("max(g.*.x)", scope));
        Assert.Equal(false, Eval("any(g.*.y)", new() { ["g.*.y"] = new List<object?>() }));
        Assert.Null(Eval("max(g.*.y)", new() { ["g.*.y"] = new List<object?>() }));
        Assert.Equal(0m, Eval("sum(g.*.y)", new() { ["g.*.y"] = new List<object?>() }));
    }

    [Fact]
    public void ANameCanReachIntoAGrid()
    {
        var expression = FormExpression.Parse("measured.meas.b1.f - measured.orig.b1.f");
        Assert.Equal(["measured.meas.b1.f", "measured.orig.b1.f"], expression.References.Select(r => r.Text));
        var reference = expression.References[0];
        Assert.Equal(("measured", "meas", "b1.f"), (reference.Head, reference.Row, reference.Cell));
        var column = FormExpression.Parse("sum(measured.*.b1.f)").References[0];
        Assert.Equal(("*", "b1.f"), (column.Row, column.Cell));
        var whole = FormExpression.Parse("any(measured)").References[0];
        Assert.Equal(("*", "*"), (whole.Row, whole.Cell));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("a b")]
    [InlineData("foo(1)")]
    [InlineData("round()")]
    [InlineData("round(1, 2, 3)")]
    [InlineData("'open")]
    [InlineData("a.")]
    [InlineData("and + 1")]
    [InlineData("1 = = 2")]
    [InlineData("a $ b")]
    public void BrokenExpressionsAreRefusedWithAReason(string text)
    {
        Assert.False(FormExpression.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ALongDeepOrHugeExpressionIsRefused_SoADefinitionCanNeverHangAVessel()
    {
        Assert.False(FormExpression.TryParse(string.Join("+", Enumerable.Repeat("1", 200)), out _, out var tooLong));
        Assert.Contains("longer", tooLong);
        Assert.False(FormExpression.TryParse(new string('(', 25) + "1" + new string(')', 25), out _, out var tooDeep));
        Assert.Contains("deeper", tooDeep);
        Assert.False(FormExpression.TryParse(string.Join("+", Enumerable.Repeat("1", 120)), out _, out var tooMany));
        Assert.Contains("too large", tooMany);
        Assert.True(FormExpression.TryParse(new string('(', 15) + "1" + new string(')', 15), out _, out _));
    }

    [Fact]
    public void ThereIsNoWayToCallAnything_OnlyTheFunctionsOfTheLanguage()
    {
        foreach (var text in new[] { "System.Diagnostics.Process.Start('x')", "eval('1')", "a.b.c(1)", "Math.Max(1, 2)", "new X()" })
            Assert.False(FormExpression.TryParse(text, out _, out _), text);
    }
}

public class FormDefinitionRulesTests
{
    private static List<FormProblem> Problems(Action<FormDef> change)
    {
        var def = FormSamples.Def();
        change(def);
        return FormDefinitionRules.Validate(def);
    }

    private static FormField Field(FormDef def, string id) => def.Sections.SelectMany(s => s.Fields ?? []).Single(f => f.Id == id);

    [Fact]
    public void TheSampleIsAValidDefinition()
    {
        Assert.Empty(FormDefinitionRules.Validate(FormSamples.Crankpin));
        Assert.Equal(0, FormDefinitionRules.Validate(FormSamples.Def().ToJson()).Count);   // and survives being written and read again
    }

    [Fact]
    public void TextThatIsNotAFormIsRefused()
    {
        Assert.Contains("empty", FormDefinitionRules.Validate("").Single().Message);
        Assert.Single(FormDefinitionRules.Validate("{ not json"));
        Assert.Single(FormDefinitionRules.Validate("""{ "schema": 1, "title": "X", "sections": [ { "id": "a", "layout": "fields", "colour": "red", "fields": [] } ] }"""));   // the vocabulary is closed
        Assert.Contains(FormDefinitionRules.Validate("""{ "schema": 2, "title": "X", "sections": [] }"""), p => p.Message.Contains("Schema 2"));
    }

    [Fact]
    public void IdsAreUnique_AndFieldsAcrossTheWholeForm()
    {
        Assert.Contains(Problems(d => Field(d, "trim").Id = "draftFwd"), p => p.Message.Contains("Another field or section has this id"));
        Assert.Contains(Problems(d => d.Sections[2].Id = "header"), p => p.Message.Contains("Another section has this id"));
        Assert.Contains(Problems(d => Field(d, "note").Id = "measured"), p => p.Message.Contains("Another field or section"));
        Assert.Contains(Problems(d => Field(d, "trim").Id = "a.b"), p => p.Message.Contains("must start with a letter"));
        Assert.Contains(Problems(d => Field(d, "trim").Id = "and"), p => p.Message.Contains("reserved"));
    }

    [Fact]
    public void ATypeThatIsNotKnown_OrOptionsThatAreMissing_AreRefused()
    {
        Assert.Contains(Problems(d => Field(d, "trim").Type = "money"), p => p.Message.Contains("not known"));
        Assert.Contains(Problems(d => { Field(d, "engineType").Type = "choice"; }), p => p.Message.Contains("needs its options"));
        Assert.Contains(Problems(d => { Field(d, "engineType").Type = "lookup"; }), p => p.Message.Contains("needs a source"));
        Assert.Contains(Problems(d => Field(d, "pressure").Min = 100), p => p.Message.Contains("minimum is above"));
        Assert.Contains(Problems(d => Field(d, "engineType").Min = 1), p => p.Message.Contains("are for numbers"));
        Assert.Contains(Problems(d => Field(d, "condition").FlagOn = ["Awful"]), p => p.Message.Contains("not one of the answers"));
        Assert.Contains(Problems(d => Field(d, "draftFwd").Default = "lots"), p => p.Message.Contains("does not suit"));
        Assert.Contains(Problems(d => Field(d, "engineType").Prefill = "vessel.captain"), p => p.Message.Contains("not known"));
        Assert.Contains(Problems(d => Field(d, "trim").Required = true), p => p.Message.Contains("cannot be required"));
    }

    [Fact]
    public void ACalculationOrAShowIfThatNamesSomethingMissing_IsRefused()
    {
        Assert.Contains(Problems(d => Field(d, "trim").Calc = "nothing + 1"), p => p.Message.Contains("no field or grid called nothing"));
        Assert.Contains(Problems(d => Field(d, "trim").Calc = "measured.nope.b1.f"), p => p.Message.Contains("has no row nope"));
        Assert.Contains(Problems(d => Field(d, "trim").Calc = "measured.orig.b9.f"), p => p.Message.Contains("has no column b9.f"));
        Assert.Contains(Problems(d => Field(d, "trim").Calc = "draftAft.x"), p => p.Message.Contains("is a field, not a grid"));
        Assert.Contains(Problems(d => d.Sections[2].ShowIf = "1 +"), p => p.Where == "section remarks.showIf");
        Assert.Contains(Problems(d => Field(d, "pressure").Flag = "wat > 1"), p => p.Where.EndsWith(".flag"));
        Assert.Empty(Problems(d => Field(d, "trim").Calc = "sum(measured.*.b1.f) + measured.meas.b2.a"));
    }

    [Fact]
    public void ALoopIsRefused_WhetherInCalculationsOrInAShowIf()
    {
        var loop = FormDefinitionRules.Validate("""
        { "schema": 1, "title": "Loop", "sections": [ { "id": "s", "layout": "fields", "fields": [
          { "id": "a", "label": "A", "type": "number", "calc": "b + 1" },
          { "id": "b", "label": "B", "type": "number", "calc": "a + 1" } ] } ] }
        """);
        Assert.Contains(loop, p => p.Message.Contains("loops back on itself"));
        Assert.Contains(Problems(d => Field(d, "trim").Calc = "trim + 1"), p => p.Message.Contains("loops"));
        Assert.Contains(Problems(d => d.Sections[2].ShowIf = "note"), p => p.Message.Contains("loops"));                      // a section shown by an answer inside itself
        Assert.Contains(Problems(d => Field(d, "note").ShowIf = "note = 'x'"), p => p.Message.Contains("loops"));
        Assert.Empty(Problems(d => Field(d, "note").ShowIf = "trim > 1"));                                                    // an answer elsewhere is fine
    }

    [Fact]
    public void GridsAreBoundedAndWellFormed()
    {
        Assert.Contains(Problems(d => d.Sections[1].Columns![0].Repeat = 31), p => p.Message.Contains("Repeat must be 1 to 30"));
        Assert.Contains(Problems(d => d.Sections[1].Rows!.Mode = "wander"), p => p.Message.Contains("not known"));
        Assert.Contains(Problems(d => d.Sections[1].Rows!.Items![1].Id = "orig"), p => p.Message.Contains("same id"));
        Assert.Contains(Problems(d => d.Sections[1].Rows = new FormRows { Mode = "list", Source = "sprockets" }), p => p.Message.Contains("need a source"));
        Assert.Contains(Problems(d => d.Sections[1].Columns![0].Children![0].Type = "photo"), p => p.Message.Contains("cannot be of type photo"));
        Assert.Contains(Problems(d => d.Sections[1].Fields = []), p => p.Message.Contains("not fields"));
        Assert.Contains(Problems(d => d.Sections[0].Rows = new FormRows()), p => p.Message.Contains("no columns or rows"));
        Assert.Contains(Problems(d =>
        {
            d.Sections[1].Columns![0].Repeat = 30;
            d.Sections[1].Rows = new FormRows { Items = Enumerable.Range(1, 40).Select(i => new FormRow { Id = "r" + i, Label = "Row " + i }).ToList() };
        }), p => p.Message.Contains("up to 2000 cells") || p.Message.Contains("up to 100 columns"));
        Assert.Contains(Problems(d => d.Sections[1].Columns!.Add(new FormColumn { Id = "b", Label = "x", Type = "text" })), p => p.Message.Contains("Another column here has this id"));
    }

    [Fact]
    public void RowsFromAListOrAddedByThePersonNeedNoDeclaredRows()
    {
        Assert.Empty(Problems(d => d.Sections[1].Rows = new FormRows { Mode = "free", Max = 20 }));
        Assert.Empty(Problems(d => d.Sections[1].Rows = new FormRows { Mode = "list", Source = "tanks", Max = 30 }));
        Assert.Contains(Problems(d => d.Sections[1].Rows = new FormRows { Mode = "free", Max = 999 }), p => p.Message.Contains("1 to 200"));
    }

    [Fact]
    public void LimitsOnSize()
    {
        Assert.Contains(Problems(d => d.Sections = []), p => p.Message.Contains("no sections"));
        Assert.Contains(Problems(d => d.Title = ""), p => p.Message.Contains("needs a title"));
        Assert.Contains(FormDefinitionRules.Validate(new string('x', FormDefinitionRules.MaxDefinitionChars + 1)), p => p.Message.Contains("larger than"));
    }
}

public class FormValueRulesTests
{
    private static FormCheck Check(string values, bool submit = false, string definition = FormSamples.Crankpin) => FormValueRules.Check(FormSamples.Def(definition), FormSamples.Values(values), FormSamples.Today, submit);

    [Fact]
    public void ACalculationIsRecomputed_AndATypedAnswerForItIsIgnored()
    {
        var check = Check("""{ "engineType": "7G60", "draftFwd": 6.5, "draftAft": 9 }""");
        Assert.True(check.Ok);
        Assert.Equal(2.5m, check.Clean["trim"]!.GetValue<decimal>());
        Assert.Equal(2.5m, Check("""{ "draftFwd": 6.5, "draftAft": 9, "trim": 99 }""").Clean["trim"]!.GetValue<decimal>());
        Assert.Null(Check("""{ "draftAft": 9 }""").Clean["trim"]);                             // one input missing: nothing, never a wrong number
    }

    [Fact]
    public void RequiredAnswersAreOnlyInsistedOnWhenSubmitting()
    {
        Assert.True(Check("{}").Ok);
        var submit = Check("{}", submit: true);
        Assert.Equal(["engineType"], submit.Problems.Select(p => p.Where));
        Assert.Equal("Engine type is required.", submit.Problems[0].Message);
        Assert.True(Check("""{ "engineType": "   " }""", submit: false).Ok);
        Assert.Single(Check("""{ "engineType": "   " }""", submit: true).Problems);
    }

    [Fact]
    public void AHiddenFieldIsNeitherRequiredNorKept()
    {
        var hidden = Check("""{ "engineType": "x", "note": "left over" }""", submit: true);
        Assert.True(hidden.Ok);                                                                    // the remarks section is hidden: its required note is not asked for
        Assert.Null(hidden.Clean["note"]);                                                         // and an answer left in it is not kept

        var shown = Check("""{ "engineType": "x", "measured": { "meas": { "b1.f": 0.55 } } }""", submit: true);
        Assert.Equal(["note"], shown.Problems.Select(p => p.Where));                              // a measurement shows the section, and then its required note is asked for
        Assert.True(Check("""{ "engineType": "x", "note": "Fine", "measured": { "meas": { "b1.f": 0.55 } } }""", submit: true).Ok);
    }

    [Fact]
    public void ADependentShowIfFollowsACalculatedValue()
    {
        const string definition = """
        { "schema": 1, "title": "T", "sections": [ { "id": "s", "layout": "fields", "fields": [
          { "id": "a", "label": "A", "type": "number" },
          { "id": "double", "label": "Double", "type": "number", "calc": "a * 2" },
          { "id": "why", "label": "Why", "type": "text", "required": true, "showIf": "double > 10" } ] } ] }
        """;
        Assert.True(Check("""{ "a": 4 }""", true, definition).Ok);
        Assert.Equal(["why"], Check("""{ "a": 6 }""", true, definition).Problems.Select(p => p.Where));
        Assert.Equal("because", Check("""{ "a": 6, "why": "because" }""", true, definition).Clean["why"]!.GetValue<string>());
        Assert.Null(Check("""{ "a": 4, "why": "stale" }""", true, definition).Clean["why"]);
    }

    [Fact]
    public void TypesAndLimitsAreChecked_AndWhatIsWrongIsNotKept()
    {
        var check = Check("""{ "engineType": "x", "draftFwd": "abc", "pressure": 60, "date": "2026-12-01", "condition": "Awful" }""");
        Assert.Equal(["date", "draftFwd", "condition", "pressure"], check.Problems.Select(p => p.Where));      // in the order of the form
        Assert.Contains("cannot be in the future", check.Problems[0].Message);
        Assert.Contains("is not a number", check.Problems[1].Message);
        Assert.Contains("is not one of Good, Fair, Bad", check.Problems[2].Message);
        Assert.Contains("above 50", check.Problems[3].Message);
        Assert.Null(check.Clean["draftFwd"]);
        Assert.Null(check.Clean["pressure"]);
        Assert.Equal("x", check.Clean["engineType"]!.GetValue<string>());
        Assert.Single(Check("""{ "mystery": 1 }""").Problems);                                       // an answer the form does not ask for
    }

    [Fact]
    public void NumbersAreRoundedToTheFieldsDecimals_AndKeptExact()
    {
        Assert.Equal(6.6m, Check("""{ "draftFwd": 6.55 }""").Clean["draftFwd"]!.GetValue<decimal>());
        Assert.Equal(6.5m, Check("""{ "draftFwd": "6.5" }""").Clean["draftFwd"]!.GetValue<decimal>());     // a number typed as text is accepted
        Assert.Equal("0.1", Check("""{ "draftFwd": 0.1 }""").Clean["draftFwd"]!.ToJsonString());           // and no float noise
    }

    [Fact]
    public void ExceptionsAreFoundByAnswerAndByRule()
    {
        Assert.Empty(Check("""{ "condition": "Good", "pressure": 11 }""").Exceptions);
        var bad = Check("""{ "condition": "Bad", "pressure": 13 }""");
        Assert.Equal(["condition", "pressure"], bad.Exceptions.Select(e => e.FieldId));
        Assert.Equal("Condition: Bad", bad.Exceptions[0].Message);
        Assert.Empty(Check("""{ "pressure": 60 }""").Exceptions);                                   // an answer that is not kept is not flagged
    }

    [Fact]
    public void GridCellsAreCheckedLikeFields()
    {
        var check = Check("""{ "measured": { "orig": { "b1.f": 0.555, "b2.a": "x" }, "zzz": { "b1.f": 1 }, "meas": { "b9.f": 1 } } }""");
        Assert.Equal(["measured.orig.b2.a", "measured.zzz", "measured.meas.b9.f"], check.Problems.Select(p => p.Where));
        Assert.Equal(0.56m, check.Clean["measured"]!["orig"]!["b1.f"]!.GetValue<decimal>());
        Assert.Null(check.Clean["measured"]!["zzz"]);
    }

    [Fact]
    public void ACalculationCanReadAGridColumn()
    {
        const string definition = """
        { "schema": 1, "title": "T", "sections": [
          { "id": "g", "layout": "grid", "columns": [ { "id": "w", "label": "Weight", "type": "number" } ],
            "rows": { "mode": "free", "max": 10 } },
          { "id": "s", "layout": "fields", "fields": [ { "id": "total", "label": "Total", "type": "number", "calc": "sum(g.*.w)" } ] } ] }
        """;
        Assert.Equal(7.5m, Check("""{ "g": { "r1": { "w": 2.5 }, "r2": { "w": 5 } } }""", false, definition).Clean["total"]!.GetValue<decimal>());
        Assert.True(Check("""{ "g": { "r1": { "w": 2.5 } } }""", false, definition).Ok);
        Assert.Contains(Check("""{ "g": { "bad id": { "w": 2.5 } } }""", false, definition).Problems, p => p.Message.Contains("not valid"));
    }

    [Fact]
    public void RowsFromAFreeGridAreLimited()
    {
        const string definition = """
        { "schema": 1, "title": "T", "sections": [ { "id": "g", "layout": "grid", "columns": [ { "id": "w", "label": "W", "type": "text" } ], "rows": { "mode": "free", "max": 2 } } ] }
        """;
        Assert.Contains(Check("""{ "g": { "a": { "w": "1" }, "b": { "w": "2" }, "c": { "w": "3" } } }""", false, definition).Problems, p => p.Message.Contains("At most 2 rows"));
    }

    [Fact]
    public void TheCheckIsRepeatable_SoBothSidesAgree()
    {
        var first = Check("""{ "engineType": "x", "draftFwd": 6.5, "draftAft": 9, "condition": "Bad", "measured": { "meas": { "b1.f": 0.55 } }, "note": "ok" }""", true);
        var second = FormValueRules.Check(FormSamples.Def(), first.Clean, FormSamples.Today, true);
        Assert.True(first.Ok && second.Ok);
        Assert.Equal(first.Clean.ToJsonString(), second.Clean.ToJsonString());                     // what is kept passes as it is, and keeps as it is
        Assert.Equal(first.Exceptions, second.Exceptions);
    }

    [Fact]
    public void ChoiceSignatureTickAndPhotoAnswers()
    {
        const string definition = """
        { "schema": 1, "title": "T", "sections": [ { "id": "s", "layout": "fields", "fields": [
          { "id": "pick", "label": "Pick", "type": "choice", "options": ["A", "B"] },
          { "id": "many", "label": "Many", "type": "multichoice", "options": ["A", "B", "C"] },
          { "id": "ok", "label": "Ok", "type": "tick" },
          { "id": "yn", "label": "Yn", "type": "yesnona" },
          { "id": "shot", "label": "Shot", "type": "photo" },
          { "id": "sign", "label": "Sign", "type": "signature" },
          { "id": "when", "label": "When", "type": "time" },
          { "id": "stamp", "label": "Stamp", "type": "datetime" } ] } ] }
        """;
        var id = Guid.NewGuid().ToString();
        var good = Check($$"""{ "pick": "A", "many": ["A", "C", "A"], "ok": true, "yn": "NA", "shot": ["{{id}}"], "sign": { "name": "A. Master", "at": "2026-11-15T08:00" }, "when": "07:30", "stamp": "2026-11-15T07:30" }""", false, definition);
        Assert.True(good.Ok);
        Assert.Equal(["A", "C"], good.Clean["many"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("A. Master", good.Clean["sign"]!["name"]!.GetValue<string>());
        var bad = Check("""{ "pick": "Z", "many": ["Q"], "ok": "maybe", "yn": "Maybe", "shot": ["not-a-file"], "sign": { "at": "x" }, "when": "25:99", "stamp": "yesterday" }""", false, definition);
        Assert.Equal(8, bad.Problems.Count);
        Assert.Contains(Check($$"""{ "shot": ["{{id}}", "{{id}}", "{{id}}", "{{id}}"] }""", false, definition).Problems, p => p.Message.Contains("up to 3"));
    }

    [Fact]
    public void ANewEntryStartsFromPrefillAndDefaults()
    {
        const string definition = """
        { "schema": 1, "title": "T", "sections": [ { "id": "s", "layout": "fields", "fields": [
          { "id": "vessel", "label": "Vessel", "type": "text", "prefill": "vessel.name" },
          { "id": "imo", "label": "Imo", "type": "text", "prefill": "vessel.imo" },
          { "id": "date", "label": "Date", "type": "date", "default": "today" },
          { "id": "lastReading", "label": "Last", "type": "number", "prefill": "previous.reading" },
          { "id": "reading", "label": "Reading", "type": "number", "default": "5" },
          { "id": "sailing", "label": "Sailing", "type": "tick", "default": "true" },
          { "id": "calc", "label": "Calc", "type": "number", "calc": "reading * 2" } ] } ] }
        """;
        var initial = FormValueRules.Initial(FormSamples.Def(definition), new FormPrefillContext("MV Sea Fox", "9123456", "SF", FormSamples.Today, FormSamples.Values("""{ "reading": 41.5 }""")));
        Assert.Equal("MV Sea Fox", initial["vessel"]!.GetValue<string>());
        Assert.Equal("9123456", initial["imo"]!.GetValue<string>());
        Assert.Equal("2026-11-15", initial["date"]!.GetValue<string>());
        Assert.Equal(5m, initial["reading"]!.GetValue<decimal>());
        Assert.True(initial["sailing"]!.GetValue<bool>());
        Assert.Equal(41.5m, initial["lastReading"]!.GetValue<decimal>());                           // taken from the same answer on the previous entry
        Assert.Null(initial["calc"]);
        var again = FormValueRules.Initial(FormSamples.Def(definition), new FormPrefillContext("MV Sea Fox", "", "SF", FormSamples.Today, FormSamples.Values("""{ "reading": 41.5 }""")));
        Assert.Null(again["imo"]);
        Assert.True(FormValueRules.Check(FormSamples.Def(definition), initial, FormSamples.Today, false).Ok);
    }
}

public class FormBuilderSupportTests
{
    [Fact]
    public void TidyTurnsBoxesLeftEmptyIntoNotSet_SoTheRulesSeeWhatTheAuthorMeant()
    {
        var def = FormSamples.Def();
        var trim = def.Sections[0].Fields!.Single(f => f.Id == "trim");
        (trim.Unit, trim.Prefill, trim.ShowIf, trim.Flag, trim.FlagOn, trim.Options) = ("  ", "", "   ", "", [], []);
        def.Sections[2].ShowIf = "";
        def.Sections[0].Title = " ";
        def.Sections[0].Fields![0].Validate = new FormValidate();

        Assert.NotEmpty(FormDefinitionRules.Validate(def));                                 // an empty show-if is an error until it is tidied
        def.Tidy();
        Assert.Empty(FormDefinitionRules.Validate(def));
        Assert.Null(trim.Unit);
        Assert.Null(trim.FlagOn);
        Assert.Null(def.Sections[2].ShowIf);
        Assert.Null(def.Sections[0].Fields![0].Validate);
    }

    [Fact]
    public void ThePrettyJsonReadsBackAsTheSameDefinition()
    {
        var def = FormSamples.Def();
        var again = FormDef.TryRead(def.ToPrettyJson(), out var error);
        Assert.True(again is not null, error);
        Assert.Equal(def.ToJson(), again!.ToJson());
        Assert.Contains("\n", def.ToPrettyJson());
        Assert.DoesNotContain("\n", def.ToJson());
    }

    [Fact]
    public void TheCheckSaysWhatIsHiddenSoAScreenDrawsOnlyWhatIsShown()
    {
        var empty = FormValueRules.Check(FormSamples.Def(), FormSamples.Values("{}"), FormSamples.Today, false);
        Assert.True(empty.IsSectionHidden("remarks"));
        Assert.True(empty.IsHidden("note"));
        Assert.False(empty.IsHidden("trim"));
        Assert.False(empty.IsSectionHidden("header"));

        var measured = FormValueRules.Check(FormSamples.Def(), FormSamples.Values("""{ "measured": { "meas": { "b1.f": 0.55 } } }"""), FormSamples.Today, false);
        Assert.False(measured.IsSectionHidden("remarks"));
        Assert.False(measured.IsHidden("note"));
    }
}

public class FormStatusRulesTests
{
    private static FormReview Review(int forSubmission, ReviewDecision decision, int minute) => new() { Id = Guid.NewGuid(), ForSubmission = forSubmission, Decision = decision, At = new DateTime(2026, 11, 15, 8, minute, 0, DateTimeKind.Utc) };

    [Fact]
    public void TheStatusIsWorkedOutFromTheSubmissionsAndTheLatestReviewOfTheCurrentOne()
    {
        Assert.Equal(FormStatus.Draft, FormStatusRules.Of(0, []));
        Assert.Equal(FormStatus.Submitted, FormStatusRules.Of(1, []));
        Assert.Equal(FormStatus.InReview, FormStatusRules.Of(1, [Review(1, ReviewDecision.Started, 1)]));
        Assert.Equal(FormStatus.Returned, FormStatusRules.Of(1, [Review(1, ReviewDecision.Started, 1), Review(1, ReviewDecision.Returned, 2)]));
        Assert.Equal(FormStatus.Reviewed, FormStatusRules.Of(1, [Review(1, ReviewDecision.Accepted, 3), Review(1, ReviewDecision.Started, 1)]));   // by time, not by the order they arrive
        Assert.Equal(FormStatus.Submitted, FormStatusRules.Of(2, [Review(1, ReviewDecision.Returned, 2)]));                                         // a review of an earlier submission no longer counts
    }

    [Fact]
    public void OnlyADraftOrAReturnedEntryCanBeChanged()
    {
        Assert.True(FormStatusRules.IsEditable(FormStatus.Draft));
        Assert.True(FormStatusRules.IsEditable(FormStatus.Returned));
        Assert.False(FormStatusRules.IsEditable(FormStatus.Submitted));
        Assert.False(FormStatusRules.IsEditable(FormStatus.InReview));
        Assert.False(FormStatusRules.IsEditable(FormStatus.Reviewed));
    }
}

public class FormApplicabilityTests
{
    private static readonly Guid Fleet = Guid.NewGuid(), Tankers = Guid.NewGuid(), Bulkers = Guid.NewGuid(), Vessel = Guid.NewGuid(), Other = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> TankerChain = new HashSet<Guid> { Tankers, Fleet };

    [Fact]
    public void WithNothingNamedAFormAppliesToEveryVessel() => Assert.True(FormApplicability.AppliesTo("", "", Vessel, TankerChain));

    [Fact]
    public void AGroupAppliesToItsVesselsAndEverythingBeneathIt()
    {
        Assert.True(FormApplicability.AppliesTo(FormApplicability.Join([Tankers]), "", Vessel, TankerChain));
        Assert.True(FormApplicability.AppliesTo(FormApplicability.Join([Fleet]), "", Vessel, TankerChain));            // the vessel's group chain includes the ones above it
        Assert.False(FormApplicability.AppliesTo(FormApplicability.Join([Bulkers]), "", Vessel, TankerChain));
    }

    [Fact]
    public void ANamedVesselAppliesAlongsideTheGroups()
    {
        Assert.True(FormApplicability.AppliesTo(FormApplicability.Join([Bulkers]), FormApplicability.Join([Vessel]), Vessel, TankerChain));
        Assert.False(FormApplicability.AppliesTo("", FormApplicability.Join([Other]), Vessel, TankerChain));
        Assert.False(FormApplicability.AppliesTo("", FormApplicability.Join([Other]), Vessel, new HashSet<Guid>()));
    }

    [Fact]
    public void TheListsSurviveBeingWrittenAndRead()
    {
        Assert.Equal([Vessel, Other], FormApplicability.ParseIds(FormApplicability.Join([Vessel, Other, Vessel])));
        Assert.Empty(FormApplicability.ParseIds("not a guid, , ;"));
    }
}

public class FormScheduleTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);
    private static FormDue Monthly(DateOnly today, params DateOnly[] entries) => FormSchedule.Compute(FormPeriodicity.Monthly, FormScheduleBasis.Calendar, D(2026, 1, 1), entries, today);

    [Fact]
    public void OnTheCalendarAMonthlyFormIsDueOnTheLastDayOfEachMonth()
    {
        var open = Monthly(D(2026, 3, 10), D(2026, 1, 20), D(2026, 2, 10));
        Assert.Equal((FormDueStatus.Open, D(2026, 3, 31), D(2026, 3, 1), 0), (open.Status, open.DueOn, open.PeriodStart, open.MissedPeriods));
        Assert.Equal(FormDueStatus.DueSoon, Monthly(D(2026, 3, 24), D(2026, 1, 20), D(2026, 2, 10)).Status);               // within a week of the last day
        Assert.Equal(FormDueStatus.DueSoon, Monthly(D(2026, 3, 31), D(2026, 1, 20), D(2026, 2, 10)).Status);               // the last day itself is not yet late
        Assert.Equal(FormDueStatus.Done, Monthly(D(2026, 3, 31), D(2026, 1, 20), D(2026, 2, 10), D(2026, 3, 5)).Status);
    }

    [Fact]
    public void AMonthNobodyFilledMakesTheFormOverdue_UntilTheCurrentOneIsMet()
    {
        var late = Monthly(D(2026, 3, 10), D(2026, 1, 20));
        Assert.Equal((FormDueStatus.Overdue, 1), (late.Status, late.MissedPeriods));                                          // February was skipped
        Assert.Equal((FormDueStatus.Done, 1), (Monthly(D(2026, 3, 10), D(2026, 1, 20), D(2026, 3, 8)).Status, Monthly(D(2026, 3, 10), D(2026, 1, 20), D(2026, 3, 8)).MissedPeriods));
        Assert.Equal(3, Monthly(D(2026, 4, 2)).MissedPeriods);                                                                // January, February and March; April has only begun
    }

    [Fact]
    public void BeforeTheStartDateNothingIsDueYet()
    {
        var before = FormSchedule.Compute(FormPeriodicity.Weekly, FormScheduleBasis.Calendar, D(2026, 6, 1), [], D(2026, 5, 1));
        Assert.Equal((FormDueStatus.Open, D(2026, 6, 7)), (before.Status, before.DueOn));
    }

    [Fact]
    public void FromThePreviousEntryTheNextIsDueOnePeriodLater()
    {
        FormDue FromPrevious(DateOnly today) => FormSchedule.Compute(FormPeriodicity.Monthly, FormScheduleBasis.FromPrevious, null, [D(2026, 1, 10), D(2026, 2, 10)], today);
        Assert.Equal((FormDueStatus.Open, D(2026, 3, 10)), (FromPrevious(D(2026, 2, 15)).Status, FromPrevious(D(2026, 2, 15)).DueOn));
        Assert.Equal(FormDueStatus.DueSoon, FromPrevious(D(2026, 3, 5)).Status);
        Assert.Equal(FormDueStatus.Overdue, FromPrevious(D(2026, 3, 12)).Status);
        Assert.Equal(FormDueStatus.Overdue, FormSchedule.Compute(FormPeriodicity.Daily, FormScheduleBasis.FromPrevious, D(2026, 3, 1), [], D(2026, 3, 5)).Status);   // never done, started on the 1st
    }

    [Theory]
    [InlineData(FormPeriodicity.Voyage)]
    [InlineData(FormPeriodicity.Pms)]
    [InlineData(FormPeriodicity.Periodical)]
    [InlineData(FormPeriodicity.ThirdParty)]
    [InlineData(FormPeriodicity.None)]
    public void SomeFormsAreStartedByHandAndNeverComeDue(FormPeriodicity periodicity)
        => Assert.Equal(FormDueStatus.NotScheduled, FormSchedule.Compute(periodicity, FormScheduleBasis.Calendar, D(2026, 1, 1), [], D(2026, 11, 1)).Status);

    [Fact]
    public void EveryPeriodLengthStepsCorrectly()
    {
        FormDue On(FormPeriodicity p, DateOnly today) => FormSchedule.Compute(p, FormScheduleBasis.Calendar, D(2026, 1, 1), [], today);
        Assert.Equal(D(2026, 1, 1), On(FormPeriodicity.Daily, D(2026, 1, 1)).DueOn);
        Assert.Equal(D(2026, 1, 7), On(FormPeriodicity.Weekly, D(2026, 1, 3)).DueOn);
        Assert.Equal(D(2026, 1, 14), On(FormPeriodicity.Fortnight, D(2026, 1, 3)).DueOn);
        Assert.Equal(D(2026, 3, 31), On(FormPeriodicity.Quarterly, D(2026, 2, 3)).DueOn);
        Assert.Equal(D(2026, 6, 30), On(FormPeriodicity.HalfYearly, D(2026, 2, 3)).DueOn);
        Assert.Equal(D(2026, 12, 31), On(FormPeriodicity.Yearly, D(2026, 2, 3)).DueOn);
    }
}
