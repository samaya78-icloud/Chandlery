using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>The company's risk matrix: consequence by probability gives a factor, and the factor falls in a band. Bands are data, not code.</summary>
public sealed class RiskMatrix
{
    public const int Levels = 5;

    public IReadOnlyList<(string Name, int MinFactor)> Bands { get; }

    public RiskMatrix(IEnumerable<(string Name, int MinFactor)> bands)
    {
        Bands = bands.OrderBy(b => b.MinFactor).ToList();
        if (Bands.Count == 0) throw new ArgumentException("A risk matrix needs at least one band.", nameof(bands));
    }

    public static readonly RiskMatrix Default = new([("Low", 1), ("Medium", 5), ("Medium High", 10), ("High", 15), ("Extreme", 20)]);

    public static bool IsLevel(int level) => level is >= 1 and <= Levels;

    public static int Factor(int consequence, int probability) => consequence * probability;

    /// <summary>The band a factor falls in: the highest band whose minimum it reaches. Zero (not scored) has none.</summary>
    public string BandOf(int factor) => factor <= 0 ? "" : Bands.LastOrDefault(b => factor >= b.MinFactor).Name ?? Bands[0].Name;

    /// <summary>Position of a band from lowest (0) up, to compare "High or worse".</summary>
    public int RankOf(string band) => Bands.ToList().FindIndex(b => b.Name == band);

    /// <summary>True when the band is one of the top two, which need a named control, owner and date.</summary>
    public bool IsSerious(string band) => Bands.Count >= 2 && RankOf(band) >= Bands.Count - 2;
}

/// <summary>Where an assessment stands, worked out from its submission count and its sign-offs (the same on both sides).</summary>
public static class RaStatusRules
{
    /// <summary>
    /// Draft until first submitted. Then the latest sign-off of the current submission decides: signed means Signed, returned means Returned, started means InReview;
    /// with none it is Submitted. A decision on an earlier submission is ignored.
    /// </summary>
    public static RaStatus Of(int submissionCount, IEnumerable<RaSignOff> signOffs)
    {
        if (submissionCount <= 0) return RaStatus.Draft;
        var latest = signOffs.Where(s => s.ForSubmission == submissionCount).OrderByDescending(s => s.At).ThenByDescending(s => s.Id).FirstOrDefault();
        return latest?.Decision switch
        {
            RaDecision.Signed => RaStatus.Signed,
            RaDecision.Returned => RaStatus.Returned,
            RaDecision.Started => RaStatus.InReview,
            _ => RaStatus.Submitted,
        };
    }

    /// <summary>Only a draft or a returned assessment can be changed; anything submitted or signed is as it was left.</summary>
    public static bool IsEditable(RaStatus status) => status is RaStatus.Draft or RaStatus.Returned;

    /// <summary>When it was signed: the time of the signing decision on the current submission.</summary>
    public static DateTime? SignedAt(int submissionCount, IEnumerable<RaSignOff> signOffs)
        => Of(submissionCount, signOffs) == RaStatus.Signed
            ? signOffs.Where(s => s.ForSubmission == submissionCount && s.Decision == RaDecision.Signed).Max(s => s.At)
            : null;

    /// <summary>The date the assessment is next due to be reviewed: the day it was signed plus the review period.</summary>
    public static DateTime NextReview(DateTime signedAt, int periodMonths) => signedAt.Date.AddMonths(Math.Max(1, periodMonths));

    /// <summary>
    /// A version is Superseded once a later version of the same assessment has been signed. (A later version still being written does not replace it: the signed one
    /// stays the one in force until its successor is signed.) Worked out from the chain, so both sides agree once they hold the same versions.
    /// </summary>
    public static bool IsSuperseded(int version, IEnumerable<(int Version, bool Signed)> chain) => chain.Any(c => c.Signed && c.Version > version);

    /// <summary>The version in force: the highest one that has been signed, if any.</summary>
    public static int? InForce(IEnumerable<(int Version, bool Signed)> chain) => chain.Where(c => c.Signed).Select(c => (int?)c.Version).Max();
}

/// <summary>What stops a hazard or an assessment from being submitted, and the scores kept with each hazard.</summary>
public static class RaChecks
{
    /// <summary>Works out the factors and bands of a hazard from its four scores (zero when not scored yet).</summary>
    public static void Score(RaHazard hazard, RiskMatrix matrix)
    {
        hazard.FactorBefore = RiskMatrix.IsLevel(hazard.ConsequenceBefore) && RiskMatrix.IsLevel(hazard.ProbabilityBefore) ? RiskMatrix.Factor(hazard.ConsequenceBefore, hazard.ProbabilityBefore) : 0;
        hazard.FactorAfter = RiskMatrix.IsLevel(hazard.ConsequenceAfter) && RiskMatrix.IsLevel(hazard.ProbabilityAfter) ? RiskMatrix.Factor(hazard.ConsequenceAfter, hazard.ProbabilityAfter) : 0;
        hazard.BandBefore = matrix.BandOf(hazard.FactorBefore);
        hazard.BandAfter = matrix.BandOf(hazard.FactorAfter);
    }

    public static List<string> SubmissionProblems(RiskAssessment a, RiskMatrix matrix)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(a.JobDescription)) problems.Add("Describe the job or activity.");
        if (a.CategoryId is null) problems.Add("Choose a category.");
        if (a.Team.Count == 0) problems.Add("Add at least one member to the team.");
        if (a.Activities.Count == 0) problems.Add("Add at least one work activity.");

        foreach (var activity in a.Activities.OrderBy(x => x.SortOrder))
        {
            var hazards = a.Hazards.Where(h => h.ActivityId == activity.Id).OrderBy(h => h.SortOrder).ToList();
            if (hazards.Count == 0) problems.Add($"{activity.Title}: add at least one hazard.");
            foreach (var h in hazards)
            {
                var name = string.IsNullOrWhiteSpace(h.Hazard) ? $"{activity.Title}: a hazard with no name" : $"{activity.Title}: {h.Hazard}";
                if (string.IsNullOrWhiteSpace(h.Hazard)) problems.Add($"{name}.");
                var scores = new[] { h.ConsequenceBefore, h.ProbabilityBefore, h.ConsequenceAfter, h.ProbabilityAfter };
                if (scores.Any(s => !RiskMatrix.IsLevel(s))) { problems.Add($"{name}: score the risk before and after controls (1 to {RiskMatrix.Levels})."); continue; }

                var before = RiskMatrix.Factor(h.ConsequenceBefore, h.ProbabilityBefore);
                var after = RiskMatrix.Factor(h.ConsequenceAfter, h.ProbabilityAfter);
                if (after >= before && string.IsNullOrWhiteSpace(h.ResidualReason)) problems.Add($"{name}: the risk after controls is not lower than before; say why.");
                if (matrix.IsSerious(matrix.BandOf(after)))
                {
                    if (string.IsNullOrWhiteSpace(h.ExistingControls) && string.IsNullOrWhiteSpace(h.AdditionalControls)) problems.Add($"{name}: a {matrix.BandOf(after)} risk needs a control.");
                    if (string.IsNullOrWhiteSpace(h.ResponsibleName)) problems.Add($"{name}: a {matrix.BandOf(after)} risk needs a responsible person.");
                    if (h.DueDate is null) problems.Add($"{name}: a {matrix.BandOf(after)} risk needs a due date.");
                }
            }
        }
        return problems;
    }

    /// <summary>The highest risk left after controls, with its band; (0, "") when nothing is scored.</summary>
    public static (int Factor, string Band) HighestResidual(IEnumerable<RaHazard> hazards)
    {
        var top = hazards.OrderByDescending(h => h.FactorAfter).FirstOrDefault();
        return top is null ? (0, "") : (top.FactorAfter, top.BandAfter);
    }
}

/// <summary>The starting library a company gets: the categories and matrix wording from spec 06.</summary>
public static class RaStandard
{
    /// <summary>Months from signing to the next review unless a company sets another.</summary>
    public const int DefaultReviewMonths = 11;

    public static readonly IReadOnlyList<(string Name, string Code)> Categories =
    [
        ("Navigational", "NAV"), ("Technical", "TEC"), ("Operations", "OPS"), ("HSE&V", "HSE"),
        ("Critical equipment", "CRE"), ("Hot work", "HOT"), ("IT", "ITS"), ("Ship security", "SEC"),
    ];

    public static readonly IReadOnlyList<string> Consequence = ["Insignificant", "Minor", "Moderate", "Major", "Catastrophic"];
    public static readonly IReadOnlyList<string> Probability = ["Rare", "Unlikely", "Possible", "Likely", "Almost certain"];
}
