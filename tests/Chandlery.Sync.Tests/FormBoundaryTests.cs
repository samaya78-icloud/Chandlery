using System.Text.Json.Nodes;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The edges of the pure rules, one by one: exactly at a limit, one past it, and the comparisons that a slip from &lt; to &lt;= would change. Added after a deliberate-mutation
/// check showed these edges were not held by any test.
/// </summary>
public class FormBoundaryTests
{
    private static bool Parses(string text) => FormExpression.TryParse(text, out _, out _);

    private static string Nested(int parens) => new string('(', parens) + "1" + new string(')', parens);

    private static object? Eval(string text) => FormExpression.Parse(text).Evaluate(new EmptyScope());

    private sealed class EmptyScope : IFormScope
    {
        public object? Resolve(FormRef reference) => null;
    }

    [Fact]
    public void AnExpressionOfExactlyTheMaximumLengthIsFine_AndOneCharacterMoreIsNot()
    {
        Assert.True(Parses("1" + new string(' ', FormExpression.MaxLength - 1)));
        Assert.False(Parses("1" + new string(' ', FormExpression.MaxLength)));
    }

    [Fact]
    public void TheDepthLimitIsExact()
    {
        var deepest = Enumerable.Range(1, 60).Last(n => Parses(Nested(n)));
        Assert.Equal(FormExpression.MaxDepth, deepest);
    }

    [Fact]
    public void TheSizeLimitIsExact()
    {
        var most = Enumerable.Range(1, 150).Last(n => Parses(string.Join("+", Enumerable.Repeat("1", n))));
        Assert.Equal(100, most);                                                   // 100 numbers and 99 plus signs are 199 parts; 101 numbers would be 201, over the limit of 200
    }

    [Fact]
    public void TextIsComparedAsWritten_NotAsPronounced()
    {
        Assert.Equal(false, Eval("'Bad' = 'bad'"));                          // case matters
        Assert.Equal(true, Eval("'Bad' != 'bad'"));
        Assert.Equal(true, Eval("'Bad' = 'Bad'"));
    }

    private static string Fields(string field) => $$"""{ "schema": 1, "title": "T", "sections": [ { "id": "s", "layout": "fields", "fields": [ {{field}} ] } ] }""";

    private static string Grid(string columns) => $$"""
        { "schema": 1, "title": "T", "sections": [ { "id": "g", "layout": "grid", "columns": [ {{columns}} ],
          "rows": { "mode": "fixed", "items": [ { "id": "r1", "label": "R1" } ] } } ] }
        """;

    [Fact]
    public void ColumnsCanBeGroupedTwiceOver_NotThreeTimes()
    {
        string Level(int n) => n == 0 ? """{ "id": "leaf", "label": "L", "type": "number" }""" : $$"""{ "id": "g{{n}}", "label": "G", "children": [ {{Level(n - 1)}} ] }""";
        Assert.Empty(FormDefinitionRules.Validate(Grid(Level(2))));                                                     // a group in a group, then the cells: three levels
        Assert.Contains(FormDefinitionRules.Validate(Grid(Level(3))), p => p.Message.Contains("three levels"));
    }

    [Fact]
    public void ChoicesThatDifferOnlyInCaseAreTheSameChoice()
    {
        Assert.Contains(FormDefinitionRules.Validate(Fields("""{ "id": "c", "label": "C", "type": "choice", "options": ["Good", "good"] }""")), p => p.Message.Contains("appears twice"));
        Assert.Empty(FormDefinitionRules.Validate(Fields("""{ "id": "c", "label": "C", "type": "choice", "options": ["Good", "Fair"] }""")));
    }

    [Fact]
    public void ANumberEqualToItsMinimumOrMaximumIsAllowed_ButNotBeyond()
    {
        var def = FormSamples.Def();                                                                                     // pressure: 0 to 50
        FormCheck Check(decimal pressure) => FormValueRules.Check(def, FormSamples.Values($$"""{ "pressure": {{pressure}} }"""), FormSamples.Today, false);
        Assert.Empty(Check(0).Problems);
        Assert.Empty(Check(50).Problems);
        Assert.Contains(Check(-0.1m).Problems, p => p.Where == "pressure");
        Assert.Contains(Check(50.1m).Problems, p => p.Where == "pressure");
    }

    [Fact]
    public void TodayIsNotInTheFuture_TomorrowIs()
    {
        var def = FormSamples.Def();                                                                                     // date: not in the future
        FormCheck Check(DateOnly date) => FormValueRules.Check(def, FormSamples.Values($$"""{ "date": "{{date:yyyy-MM-dd}}" }"""), FormSamples.Today, false);
        Assert.Empty(Check(FormSamples.Today).Problems);
        Assert.Contains(Check(FormSamples.Today.AddDays(1)).Problems, p => p.Where == "date");
    }

    [Fact]
    public void AnyOneFlaggedAnswerOfSeveralMakesTheException()
    {
        var def = FormDef.TryRead(Fields("""{ "id": "m", "label": "M", "type": "multichoice", "options": ["Good", "Bad", "Worn"], "flagOn": ["Bad", "Worn"] }"""), out var error)!;
        Assert.True(def is not null, error);
        Assert.Empty(FormDefinitionRules.Validate(def));
        Assert.Single(FormValueRules.Check(def, FormSamples.Values("""{ "m": ["Good", "Bad"] }"""), FormSamples.Today, false).Exceptions);
        Assert.Empty(FormValueRules.Check(def, FormSamples.Values("""{ "m": ["Good"] }"""), FormSamples.Today, false).Exceptions);
    }
}
