using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>Whether this person may sign off the assessment now, and if not, why.</summary>
public record SignOffRight(bool May, string Why = "");

public record SignOffRow(RiskAssessment Assessment, RaStatus Status, string VesselName);

public record ShareTarget(Guid VesselId, string VesselName, Guid? GroupId, string GroupName, bool AlreadyHasCopy);

/// <summary>Who may sign off, the queue of what is waiting, and sharing a signed assessment with other vessels.</summary>
public partial class RiskAssessmentService
{
    // ---- who may sign ----

    /// <summary>Onboard a crew member the office flagged as a risk reviewer; ashore a company admin, or a company user flagged as one.</summary>
    private static async Task<bool> IsReviewerAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, Guid? userId)
    {
        if (userId is not { } uid) return false;
        if (db.Node.IsVessel)
            return await db.CrewMembers.AsNoTracking().AnyAsync(c => c.UserId == uid && c.IsRiskReviewer && c.IsActive && c.LoginEnabled && c.VesselId == vesselId);
        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == uid && u.CompanyId == companyId && u.IsEnabled && (u.Role == UserRole.CompanyAdmin || u.IsRiskReviewer));
    }

    private static async Task<int> ReviewerCountAsync(ChandleryDbContext db, Guid companyId, Guid vesselId)
        => db.Node.IsVessel
            ? await db.CrewMembers.AsNoTracking().CountAsync(c => c.IsRiskReviewer && c.IsActive && c.LoginEnabled && c.VesselId == vesselId)
            : await db.Users.AsNoTracking().CountAsync(u => u.CompanyId == companyId && u.IsEnabled && (u.Role == UserRole.CompanyAdmin || u.IsRiskReviewer));

    private static async Task<SignOffRight> RightAsync(ChandleryDbContext db, Guid companyId, RiskAssessment a, RaDecision decision, Actor actor)
    {
        if (!await IsReviewerAsync(db, companyId, a.VesselId, actor.UserId))
            return new SignOffRight(false, db.Node.IsVessel ? "Only a risk reviewer of this vessel can sign off an assessment onboard." : "Only a risk reviewer can sign off an assessment.");
        if (decision == RaDecision.Signed && a.CreatedByUserId == actor.UserId
            && await db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && c.RiskSignOffByAnotherPerson)
            && await ReviewerCountAsync(db, companyId, a.VesselId) >= 2)
            return new SignOffRight(false, "The company asks for someone other than the writer to sign an assessment, and another reviewer is available.");
        return new SignOffRight(true);
    }

    /// <summary>For the screen: may this person start a review of, sign or return this assessment?</summary>
    public async Task<SignOffRight> MaySignOffAsync(Guid companyId, Guid assessmentId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var a = await db.RiskAssessments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assessmentId && x.CompanyId == companyId) ?? throw new InvalidOperationException("Assessment not found.");
        return await RightAsync(db, companyId, a, RaDecision.Signed, actor);
    }

    // ---- the queue ----

    /// <summary>What is waiting for sign-off (submitted, or review started): the highest risk left first, then the longest waiting.</summary>
    public async Task<List<SignOffRow>> SignOffQueueAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && a.SubmissionCount > 0);
        if (vesselId is { } v) query = query.Where(a => a.VesselId == v);
        var submitted = await query.ToListAsync();
        var ids = submitted.Select(a => a.Id).ToList();
        var signOffs = (await db.RaSignOffs.AsNoTracking().Where(s => ids.Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        var vessels = (await db.Vessels.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => new { x.Id, x.Name }).ToListAsync()).ToDictionary(x => x.Id, x => x.Name);
        return submitted.Select(a => new SignOffRow(a, RaStatusRules.Of(a.SubmissionCount, signOffs[a.Id]), vessels.GetValueOrDefault(a.VesselId, "")))
            .Where(r => r.Status is RaStatus.Submitted or RaStatus.InReview)
            .OrderByDescending(r => r.Assessment.HighestResidualFactor).ThenBy(r => r.Assessment.SubmittedAt).ToList();
    }

    // ---- sharing ----

    /// <summary>The vessels a signed assessment could be shared with: those with the module on, other than its own, and whether they already have a copy of this version.</summary>
    public async Task<List<ShareTarget>> ShareTargetsAsync(Guid companyId, Guid assessmentId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var source = await db.RiskAssessments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assessmentId && a.CompanyId == companyId) ?? throw new InvalidOperationException("Assessment not found.");
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.RiskAssessment)).Where(v => v.Id != source.VesselId).Select(v => v.Id).ToHashSet();
        var vessels = await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId && enabled.Contains(v.Id)).OrderBy(v => v.Name).ToListAsync();
        var groups = (await db.VesselGroups.AsNoTracking().Where(g => g.CompanyId == companyId).ToListAsync()).ToDictionary(g => g.Id, g => g.Name);
        var copies = (await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && a.SharedFromCode == source.Code && a.SharedFromVersion >= source.Version).Select(a => a.VesselId).ToListAsync()).ToHashSet();
        return vessels.Select(v => new ShareTarget(v.Id, v.Name, v.GroupId, v.GroupId is { } g && groups.TryGetValue(g, out var name) ? name : "", copies.Contains(v.Id))).ToList();
    }

    /// <summary>
    /// Shares a signed assessment that is in force with other vessels. Each vessel gets its own copy: a new assessment (with its own reference) that is already signed,
    /// keeps the original signing and date, says where it came from, and can be given new versions onboard. A vessel that already has a copy of this version is skipped.
    /// </summary>
    public async Task<List<RiskAssessment>> ShareAsync(Guid companyId, Guid assessmentId, IEnumerable<Guid> vesselIds, Actor actor)
    {
        var targets = vesselIds.Distinct().ToList();
        if (targets.Count == 0) throw new InvalidOperationException("Choose at least one vessel.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("Assessments are shared by the office.");
        var source = await WithChildren(db).AsNoTracking().FirstOrDefaultAsync(a => a.Id == assessmentId && a.CompanyId == companyId) ?? throw new InvalidOperationException("Assessment not found.");
        var chain = await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && a.Code == source.Code).ToListAsync();
        var chainSignOffs = (await db.RaSignOffs.AsNoTracking().Where(s => chain.Select(c => c.Id).Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        var states = chain.Select(c => (c.Version, Signed: RaStatusRules.Of(c.SubmissionCount, chainSignOffs[c.Id]) == RaStatus.Signed)).ToList();
        if (!states.Single(s => s.Version == source.Version).Signed) throw new InvalidOperationException("Only a signed assessment can be shared.");
        if (RaStatusRules.IsSuperseded(source.Version, states)) throw new InvalidOperationException("A newer version has been signed; share that one.");

        var signing = chainSignOffs[source.Id].Where(s => s.ForSubmission == source.SubmissionCount && s.Decision == RaDecision.Signed).OrderByDescending(s => s.At).First();
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.RiskAssessment)).Select(v => v.Id).ToHashSet();
        var held = await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && targets.Contains(a.VesselId)).ToListAsync();
        var heldSigned = (await db.RaSignOffs.AsNoTracking().Where(s => held.Select(h => h.Id).Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        var made = new List<RiskAssessment>();

        foreach (var vesselId in targets)
        {
            if (vesselId == source.VesselId) throw new InvalidOperationException("That vessel already has this assessment.");
            if (!await db.Vessels.AnyAsync(v => v.Id == vesselId && v.CompanyId == companyId)) throw new InvalidOperationException("Vessel not found.");
            if (!enabled.Contains(vesselId)) throw new InvalidOperationException("Risk assessment is not switched on for one of those vessels.");
            // A vessel that already holds a copy of this assessment (shared earlier) gets this one as the next version of that copy, not as a second assessment.
            var mine = held.Where(a => a.VesselId == vesselId).ToList();
            var chainCode = mine.Where(a => a.SharedFromCode == source.Code).Select(a => a.Code).FirstOrDefault();
            var vesselChain = chainCode is null ? [] : mine.Where(a => a.Code == chainCode).ToList();
            if (vesselChain.Any(a => a.SharedFromCode == source.Code && a.SharedFromVersion >= source.Version)) continue;

            var version = 1;
            Guid? previous = null;
            string code;
            if (vesselChain.Count > 0)
            {
                if (vesselChain.Any(a => RaStatusRules.Of(a.SubmissionCount, heldSigned[a.Id]) != RaStatus.Signed))
                    throw new InvalidOperationException("One of those vessels is working on a newer version of its copy; share again once that is signed.");
                version = vesselChain.Max(a => a.Version) + 1;
                previous = vesselChain.OrderByDescending(a => a.Version).First().Id;
                code = chainCode!;
            }
            else code = await DocumentNumbers.NextAsync(db, companyId, vesselId, DocType.RiskAssessment);

            var copy = new RiskAssessment
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, Code = code, Version = version, PreviousVersionId = previous, Kind = source.Kind,
                CategoryId = source.CategoryId, CategoryName = source.CategoryName, CategoryCode = source.CategoryCode, TemplateId = source.TemplateId, TemplateVersion = source.TemplateVersion,
                JobDescription = source.JobDescription, AssessedOn = source.AssessedOn, Contingency = source.Contingency, ReviewPeriodMonths = source.ReviewPeriodMonths,
                SubmissionCount = 1, SubmittedAt = DateTime.UtcNow, HighestResidualFactor = source.HighestResidualFactor, HighestResidualBand = source.HighestResidualBand,
                CreatedByName = actor.Name, CreatedByUserId = actor.UserId, CreatedAt = DateTime.UtcNow, SharedFromId = source.Id, SharedFromCode = source.Code, SharedFromVersion = source.Version,
            };
            foreach (var m in source.Team) copy.Team.Add(new RaTeamMember { Id = Guid.CreateVersion7(), Name = m.Name, Rank = m.Rank, UserId = m.UserId });
            var activityIds = new Dictionary<Guid, Guid>();
            foreach (var act in source.Activities.OrderBy(x => x.SortOrder))
            {
                var madeActivity = new RaActivity { Id = Guid.CreateVersion7(), Title = act.Title, SortOrder = act.SortOrder };
                activityIds[act.Id] = madeActivity.Id;
                copy.Activities.Add(madeActivity);
            }
            foreach (var h in source.Hazards.OrderBy(x => x.SortOrder))
                copy.Hazards.Add(new RaHazard
                {
                    Id = Guid.CreateVersion7(), ActivityId = activityIds[h.ActivityId], Hazard = h.Hazard, Effect = h.Effect, ConsequenceBefore = h.ConsequenceBefore, ProbabilityBefore = h.ProbabilityBefore,
                    ExistingControls = h.ExistingControls, AdditionalControls = h.AdditionalControls, ConsequenceAfter = h.ConsequenceAfter, ProbabilityAfter = h.ProbabilityAfter,
                    ResidualReason = h.ResidualReason, ResponsibleName = h.ResponsibleName, DueDate = h.DueDate, Contingency = h.Contingency, SortOrder = h.SortOrder,
                    FactorBefore = h.FactorBefore, BandBefore = h.BandBefore, FactorAfter = h.FactorAfter, BandAfter = h.BandAfter,
                });
            db.RiskAssessments.Add(copy);
            db.RaSignOffs.Add(new RaSignOff
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, AssessmentId = copy.Id, ForSubmission = 1, Decision = RaDecision.Signed,
                Comment = $"Shared from {source.Code} version {source.Version}.", ReviewerName = signing.ReviewerName, ReviewerRank = signing.ReviewerRank, AuthorUserId = signing.AuthorUserId, At = signing.At,
            });
            made.Add(copy);
        }
        await db.SaveChangesAsync();
        return made;
    }
}
