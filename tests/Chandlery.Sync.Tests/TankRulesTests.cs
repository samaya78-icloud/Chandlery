using Chandlery.Models;
using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

public class TankRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static TankInspectionReview Review(int forSubmission, ReviewDecision decision, int minutes = 0, Guid? id = null)
        => new() { Id = id ?? Guid.CreateVersion7(), ForSubmission = forSubmission, Decision = decision, At = T0.AddMinutes(minutes) };

    // ---- scales ----

    [Fact]
    public void EachScaleAllowsItsOwnAnswers_AndRatingsFlagFairAndBadUnlessToldOtherwise()
    {
        Assert.Equal(["Good", "Fair", "Bad", "NI", "NA"], TankScales.Values(ScaleKind.Rating));
        Assert.Equal(["Yes", "No", "NA"], TankScales.Values(ScaleKind.YesNoNa));
        Assert.Equal(["0-25", "26-50", "51-75", "76-100", "NA"], TankScales.Values(ScaleKind.PercentBucket));

        Assert.True(TankScales.IsValid(ScaleKind.Rating, "Fair"));
        Assert.False(TankScales.IsValid(ScaleKind.Rating, "Yes"));
        Assert.False(TankScales.IsValid(ScaleKind.PercentBucket, "30"));
        Assert.False(TankScales.IsValid(ScaleKind.Rating, null));

        Assert.Equal("Fair,Bad", TankScales.DefaultExceptionValues(ScaleKind.Rating));
        Assert.Equal("", TankScales.DefaultExceptionValues(ScaleKind.YesNoNa));
        Assert.True(TankScales.IsException(ScaleKind.Rating, "Fair,Bad", "Bad"));
        Assert.True(TankScales.IsException(ScaleKind.Rating, "Fair, Bad", "fair"));                                  // spaces and case do not matter
        Assert.False(TankScales.IsException(ScaleKind.Rating, "Fair,Bad", "Good"));
        Assert.False(TankScales.IsException(ScaleKind.Rating, "Fair,Bad", ""));
        Assert.True(TankScales.IsException(ScaleKind.PercentBucket, "0-25,26-50", "26-50"));                         // a criterion can add its own
        Assert.False(TankScales.IsException(ScaleKind.YesNoNa, "", "No"));
    }

    [Fact]
    public void ScoresGiveChartsANumber_AndNotInspectedHasNone()
    {
        Assert.Equal([3, 2, 1, null, null], new[] { "Good", "Fair", "Bad", "NI", "NA" }.Select(v => TankScales.Score(ScaleKind.Rating, v)));
        Assert.Equal([4, 3, 2, 1, null], new[] { "76-100", "51-75", "26-50", "0-25", "NA" }.Select(v => TankScales.Score(ScaleKind.PercentBucket, v)));
    }

    // ---- status ----

    [Fact]
    public void TheStatusIsWorkedOutFromTheSubmissionCountAndTheLatestReviewOfThatSubmission()
    {
        Assert.Equal(InspectionStatus.Draft, TankStatus.Of(0, []));
        Assert.Equal(InspectionStatus.Submitted, TankStatus.Of(1, []));
        Assert.Equal(InspectionStatus.InReview, TankStatus.Of(1, [Review(1, ReviewDecision.Started)]));
        Assert.Equal(InspectionStatus.Reviewed, TankStatus.Of(1, [Review(1, ReviewDecision.Started, 0), Review(1, ReviewDecision.Accepted, 5)]));
        Assert.Equal(InspectionStatus.Returned, TankStatus.Of(1, [Review(1, ReviewDecision.Started, 0), Review(1, ReviewDecision.Returned, 5)]));

        // Submitted again after a return: the earlier review no longer counts, and a review of the new submission does.
        Assert.Equal(InspectionStatus.Submitted, TankStatus.Of(2, [Review(1, ReviewDecision.Returned, 5)]));
        Assert.Equal(InspectionStatus.Reviewed, TankStatus.Of(2, [Review(1, ReviewDecision.Returned, 5), Review(2, ReviewDecision.Accepted, 50)]));
        Assert.Equal(InspectionStatus.Submitted, TankStatus.Of(2, [Review(1, ReviewDecision.Accepted, 5)]));          // an acceptance of an older submission does not lock the new one
    }

    [Fact]
    public void ReviewsAreOrderedByTimeThenId_SoTheOrderTheyArriveInChangesNothing()
    {
        var a = Review(1, ReviewDecision.Started, 0);
        var b = Review(1, ReviewDecision.Returned, 10);
        var c = Review(1, ReviewDecision.Accepted, 10, id: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));        // same minute: the later id wins
        var expected = TankStatus.Of(1, [a, b, c]);
        var random = new Random(3);
        for (var i = 0; i < 30; i++) Assert.Equal(expected, TankStatus.Of(1, new[] { a, b, c }.OrderBy(_ => random.Next())));
        Assert.Equal(InspectionStatus.Reviewed, expected);
    }

    [Fact]
    public void OnlyADraftOrAReturnedInspectionCanBeChanged()
    {
        Assert.Equal([true, false, false, true, false], new[] { InspectionStatus.Draft, InspectionStatus.Submitted, InspectionStatus.InReview, InspectionStatus.Returned, InspectionStatus.Reviewed }.Select(TankStatus.IsEditable));
    }

    // ---- results ----

    private static TankInspectionResult Result(string name, ScaleKind scale, string value, string exceptions = "", int order = 0)
        => new() { CriterionName = name, Scale = scale, Value = value, ExceptionValues = exceptions == "" ? TankScales.DefaultExceptionValues(scale) : exceptions, SortOrder = order };

    [Fact]
    public void AnInspectionCannotBeSubmittedUntilEveryCriterionIsAnswered_ButNotInspectedAndNotApplicableAreAnswers()
    {
        var results = new[]
        {
            Result("Condition of coating", ScaleKind.Rating, "Good", order: 1),
            Result("Ladders", ScaleKind.Rating, "", order: 2),
            Result("Bottom plate", ScaleKind.Rating, "NI", order: 3),
            Result("Cathodic condition", ScaleKind.PercentBucket, "Fair", order: 4),
            Result("Anode check", ScaleKind.YesNoNa, "NA", order: 5),
        };
        var problems = TankStatus.SubmissionProblems(results);
        Assert.Equal(2, problems.Count);
        Assert.Contains("Ladders: not answered", problems[0]);
        Assert.Contains("\"Fair\" is not an answer", problems[1]);
        Assert.Empty(TankStatus.SubmissionProblems(results.Take(1).Append(Result("Bottom plate", ScaleKind.Rating, "NI"))));
    }

    [Fact]
    public void ExceptionsAreTheAnswersOnTheirCriterionsExceptionList()
    {
        var results = new[]
        {
            Result("Coating", ScaleKind.Rating, "Fair"), Result("Pipes", ScaleKind.Rating, "Bad"), Result("Ladders", ScaleKind.Rating, "Good"),
            Result("Cathodic", ScaleKind.PercentBucket, "0-25", exceptions: "0-25,26-50"), Result("Cathodic 2", ScaleKind.PercentBucket, "0-25"), Result("Anodes fitted", ScaleKind.YesNoNa, "No"),
        };
        Assert.Equal(["Coating", "Pipes", "Cathodic"], TankStatus.Exceptions(results).Select(r => r.CriterionName));
    }

    // ---- the standard checklists ----

    [Theory]
    [InlineData(TankType.Cargo, 30)]
    [InlineData(TankType.Slops, 26)]
    [InlineData(TankType.Ballast, 23)]
    [InlineData(TankType.FreshWater, 21)]
    [InlineData(TankType.VoidSpace, 17)]
    [InlineData(TankType.Other, 19)]
    public void EveryTankTypeHasAStandardChecklistBuiltFromTheSpecsLists(TankType type, int count)
    {
        var criteria = TankStandardTemplates.CriteriaFor(type);
        Assert.Equal(count, criteria.Count);
        Assert.Equal(["Condition of coating", "Cathodic condition"], criteria.Where(c => c.Group == CriterionGroup.VariableData).Select(c => c.Name));
        Assert.Equal(ScaleKind.PercentBucket, criteria.Single(c => c.Name == "Cathodic condition").Scale);
        Assert.Equal(11, criteria.Count(c => c.Group == CriterionGroup.Structure));
        Assert.Equal(["Thickness loss", "Pitting", "Cracks"], criteria.Where(c => c.Group == CriterionGroup.Reports).Select(c => c.Name));
        Assert.Equal(criteria.Count, criteria.Select(c => c.Name).Distinct().Count());
        Assert.Equal(type == TankType.Cargo ? 4 : 0, criteria.Count(c => c.Group == CriterionGroup.TypeSpecific));
    }
}
