using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record NewAssessment(Guid VesselId, string JobDescription, DateTime AssessedOn, Guid? CategoryId = null, Guid? TemplateId = null);

public record RaTeamInput(string Name, string Rank = "", Guid? UserId = null);

public record RaHazardInput(Guid? Id, string Hazard, string Effect, int ConsequenceBefore, int ProbabilityBefore, string ExistingControls, string AdditionalControls,
    int ConsequenceAfter, int ProbabilityAfter, string ResidualReason = "", string ResponsibleName = "", DateTime? DueDate = null, string Contingency = "");

public record RaActivityInput(Guid? Id, string Title, IReadOnlyList<RaHazardInput> Hazards);

public record AssessmentEdit(string JobDescription, Guid? CategoryId, DateTime AssessedOn, string Contingency, IReadOnlyList<RaTeamInput> Team, IReadOnlyList<RaActivityInput> Activities);

public record AssessmentDetail(RiskAssessment Assessment, List<RaSignOff> SignOffs, RaStatus Status, bool Superseded, DateTime? SignedAt, DateTime? NextReview, RiskMatrix Matrix);

public record AssessmentRow(RiskAssessment Assessment, RaStatus Status, bool Superseded, DateTime? NextReview);

/// <summary>
/// Risk assessments: written by one side, submitted, and signed off through sign-off records. A draft or a returned assessment can be changed by the side that made it;
/// a submitted or signed one cannot. Changing a signed assessment is a new version. The status is always worked out, never stored.
/// </summary>
public class RiskAssessmentService(IDbContextFactory<ChandleryDbContext> factory)
{
    public const int MaxActivities = 25;
    public const int MaxHazardsPerActivity = 20;

    private static DateTime Day(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.RiskAssessment);

    public static async Task<RiskMatrix> MatrixAsync(ChandleryDbContext db, Guid companyId)
    {
        var bands = await db.RaMatrixBands.AsNoTracking().Where(b => b.CompanyId == companyId).OrderBy(b => b.MinFactor).Select(b => new { b.Name, b.MinFactor }).ToListAsync();
        return bands.Count == 0 ? RiskMatrix.Default : new RiskMatrix(bands.Select(b => (b.Name, b.MinFactor)));
    }

    private static async Task<RaStatus> StatusOfAsync(ChandleryDbContext db, RiskAssessment a)
        => RaStatusRules.Of(a.SubmissionCount, await db.RaSignOffs.AsNoTracking().Where(s => s.AssessmentId == a.Id).ToListAsync());

    private static IQueryable<RiskAssessment> WithChildren(ChandleryDbContext db) => db.RiskAssessments.Include(a => a.Team).Include(a => a.Activities).Include(a => a.Hazards);

    // ---- starting and writing ----

    public async Task<RiskAssessment> StartAsync(Guid companyId, NewAssessment input, Actor actor)
    {
        if (input.AssessedOn.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("An assessment cannot be dated in the future.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await db.Vessels.AnyAsync(v => v.Id == input.VesselId && v.CompanyId == companyId)) throw new InvalidOperationException("Vessel not found.");
        if (!await IsEnabledForVesselAsync(db, companyId, input.VesselId)) throw new InvalidOperationException("Risk assessment is not switched on for this vessel.");

        RaTemplate? template = null;
        if (input.TemplateId is { } templateId)
        {
            template = await db.RaTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Template not found.");
            if (!template.IsActive) throw new InvalidOperationException("That template is no longer in use.");
        }
        var categoryId = input.CategoryId ?? template?.CategoryId;
        RaCategory? category = null;
        if (categoryId is { } cid) category = await db.RaCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId) ?? throw new InvalidOperationException("Category not found.");

        var description = (string.IsNullOrWhiteSpace(input.JobDescription) ? template?.JobDescription : input.JobDescription)?.Trim() ?? "";
        var assessment = new RiskAssessment
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = input.VesselId, Code = await DocumentNumbers.NextAsync(db, companyId, input.VesselId, DocType.RiskAssessment),
            Version = 1, Kind = RaKind.Initial, CategoryId = category?.Id, CategoryName = category?.Name ?? "", CategoryCode = category?.Code ?? "",
            TemplateId = template?.Id, TemplateVersion = template?.Version ?? 0, JobDescription = description, AssessedOn = Day(input.AssessedOn),
            Contingency = template?.Contingency ?? "", ReviewPeriodMonths = RaStandard.DefaultReviewMonths, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        if (template is not null) await CopyTemplateAsync(db, template, assessment);
        db.RiskAssessments.Add(assessment);
        await db.SaveChangesAsync();
        return assessment;
    }

    private static async Task CopyTemplateAsync(ChandleryDbContext db, RaTemplate template, RiskAssessment assessment)
    {
        var matrix = await MatrixAsync(db, template.CompanyId);
        var activities = await db.RaTemplateActivities.AsNoTracking().Where(a => a.TemplateId == template.Id).OrderBy(a => a.SortOrder).ThenBy(a => a.Title).ToListAsync();
        var hazards = await db.RaTemplateHazards.AsNoTracking().Where(h => h.TemplateId == template.Id).OrderBy(h => h.SortOrder).ToListAsync();
        var order = 0;
        foreach (var act in activities)
        {
            var copy = new RaActivity { Id = Guid.CreateVersion7(), Title = act.Title, SortOrder = order++ };
            assessment.Activities.Add(copy);
            var h = 0;
            foreach (var th in hazards.Where(x => x.ActivityId == act.Id))
            {
                var hazard = new RaHazard
                {
                    Id = Guid.CreateVersion7(), ActivityId = copy.Id, Hazard = th.Hazard, Effect = th.Effect, ExistingControls = th.ExistingControls, AdditionalControls = th.AdditionalControls,
                    ConsequenceBefore = th.ConsequenceBefore, ProbabilityBefore = th.ProbabilityBefore, ConsequenceAfter = th.ConsequenceAfter, ProbabilityAfter = th.ProbabilityAfter,
                    Contingency = th.Contingency, SortOrder = h++,
                };
                RaChecks.Score(hazard, matrix);
                assessment.Hazards.Add(hazard);
            }
        }
    }

    private static async Task<RiskAssessment> LoadEditableAsync(ChandleryDbContext db, Guid companyId, Guid id)
    {
        var assessment = await WithChildren(db).FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId) ?? throw new InvalidOperationException("Assessment not found.");
        var status = await StatusOfAsync(db, assessment);
        if (!RaStatusRules.IsEditable(status))
            throw new InvalidOperationException(status == RaStatus.Signed
                ? "This assessment has been signed and is locked. Make a new version to change it."
                : $"This assessment has been submitted ({status.ToString().ToLowerInvariant()}) and cannot be changed until it is returned.");
        return assessment;
    }

    /// <summary>Saves what has been written so far, replacing the team, activities and hazards with those given. Allowed only while it is a draft or has been returned.</summary>
    public async Task SaveAsync(Guid companyId, Guid id, AssessmentEdit edit, Actor actor)
    {
        if (edit.AssessedOn.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("An assessment cannot be dated in the future.");
        if (edit.Activities.Count > MaxActivities) throw new InvalidOperationException($"An assessment can hold at most {MaxActivities} work activities; split it into two.");
        if (edit.Activities.Any(a => a.Hazards.Count > MaxHazardsPerActivity)) throw new InvalidOperationException($"A work activity can hold at most {MaxHazardsPerActivity} hazards.");

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var a = await LoadEditableAsync(db, companyId, id);
        var matrix = await MatrixAsync(db, companyId);

        RaCategory? category = null;
        if (edit.CategoryId is { } cid) category = await db.RaCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId) ?? throw new InvalidOperationException("Category not found.");
        (a.JobDescription, a.AssessedOn, a.Contingency) = ((edit.JobDescription ?? "").Trim(), Day(edit.AssessedOn), (edit.Contingency ?? "").Trim());
        (a.CategoryId, a.CategoryName, a.CategoryCode) = (category?.Id, category?.Name ?? "", category?.Code ?? "");

        // The team: those given replace those held (a person keeps the same row only by being typed again, which is fine for a list this short).
        foreach (var old in a.Team.ToList()) { a.Team.Remove(old); db.Remove(old); }
        foreach (var m in edit.Team.Where(m => !string.IsNullOrWhiteSpace(m.Name)))
            db.Add(new RaTeamMember { Id = Guid.CreateVersion7(), AssessmentId = a.Id, Name = m.Name.Trim(), Rank = (m.Rank ?? "").Trim(), UserId = m.UserId });

        // Activities and hazards keep their ids when given (so a task raised for a hazard stays attached to it) and are matched by id.
        var heldActivities = a.Activities.ToDictionary(x => x.Id);
        var heldHazards = a.Hazards.ToDictionary(x => x.Id);
        var keepActivities = new HashSet<Guid>();
        var keepHazards = new HashSet<Guid>();
        var order = 0;
        foreach (var input in edit.Activities)
        {
            if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("Give every work activity a title.");
            var activity = input.Id is { } aid && heldActivities.TryGetValue(aid, out var existing) ? existing : null;
            if (activity is null) { activity = new RaActivity { Id = input.Id ?? Guid.CreateVersion7(), AssessmentId = a.Id }; db.Add(activity); }
            (activity.Title, activity.SortOrder) = (input.Title.Trim(), order++);
            keepActivities.Add(activity.Id);

            var h = 0;
            foreach (var hi in input.Hazards)
            {
                foreach (var s in new[] { hi.ConsequenceBefore, hi.ProbabilityBefore, hi.ConsequenceAfter, hi.ProbabilityAfter })
                    if (s != 0 && !RiskMatrix.IsLevel(s)) throw new InvalidOperationException($"Scores run from 1 to {RiskMatrix.Levels}.");
                var hazard = hi.Id is { } hid && heldHazards.TryGetValue(hid, out var found) ? found : null;
                if (hazard is null) { hazard = new RaHazard { Id = hi.Id ?? Guid.CreateVersion7(), AssessmentId = a.Id }; db.Add(hazard); }
                (hazard.ActivityId, hazard.Hazard, hazard.Effect, hazard.SortOrder) = (activity.Id, (hi.Hazard ?? "").Trim(), (hi.Effect ?? "").Trim(), h++);
                (hazard.ConsequenceBefore, hazard.ProbabilityBefore, hazard.ConsequenceAfter, hazard.ProbabilityAfter) = (hi.ConsequenceBefore, hi.ProbabilityBefore, hi.ConsequenceAfter, hi.ProbabilityAfter);
                (hazard.ExistingControls, hazard.AdditionalControls, hazard.ResidualReason) = ((hi.ExistingControls ?? "").Trim(), (hi.AdditionalControls ?? "").Trim(), (hi.ResidualReason ?? "").Trim());
                (hazard.ResponsibleName, hazard.DueDate, hazard.Contingency) = ((hi.ResponsibleName ?? "").Trim(), hi.DueDate is { } due ? Day(due) : null, (hi.Contingency ?? "").Trim());
                RaChecks.Score(hazard, matrix);
                keepHazards.Add(hazard.Id);
            }
        }
        foreach (var gone in a.Hazards.Where(x => !keepHazards.Contains(x.Id)).ToList()) { a.Hazards.Remove(gone); db.Remove(gone); }
        foreach (var gone in a.Activities.Where(x => !keepActivities.Contains(x.Id)).ToList()) { a.Activities.Remove(gone); db.Remove(gone); }
        await db.SaveChangesAsync();
    }

    /// <summary>Submits the assessment for sign-off, once it passes the checks.</summary>
    public async Task SubmitAsync(Guid companyId, Guid id, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var a = await LoadEditableAsync(db, companyId, id);
        var matrix = await MatrixAsync(db, companyId);
        var problems = RaChecks.SubmissionProblems(a, matrix);
        if (problems.Count > 0) throw new InvalidOperationException($"{problems.Count} thing(s) to put right before submitting: {string.Join(" ", problems.Take(3))}{(problems.Count > 3 ? " …" : "")}");

        a.SubmissionCount++;
        a.SubmittedAt = DateTime.UtcNow;
        (a.HighestResidualFactor, a.HighestResidualBand) = RaChecks.HighestResidual(a.Hazards);
        await db.SaveChangesAsync();
    }

    // ---- sign-off ----

    /// <summary>
    /// The reviewer's response to the current submission: start reviewing, sign (the assessment is then locked) or return with comments. Written as a new record;
    /// the assessment itself is never edited by the reviewer. The person who wrote it may not be the only one to sign it, but that rule is set per company later.
    /// </summary>
    public async Task<RaSignOff> SignOffAsync(Guid companyId, Guid id, RaDecision decision, string comment, Actor actor, string rank = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var a = await db.RiskAssessments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId) ?? throw new InvalidOperationException("Assessment not found.");
        var status = await StatusOfAsync(db, a);
        if (status is RaStatus.Draft or RaStatus.Returned) throw new InvalidOperationException("This assessment has not been submitted (or is back with its writer).");
        if (status == RaStatus.Signed) throw new InvalidOperationException("This assessment has already been signed.");
        if (decision == RaDecision.Started && status == RaStatus.InReview) throw new InvalidOperationException("The review has already been started.");
        if (decision == RaDecision.Returned && string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException("Say what needs correcting.");

        var signOff = new RaSignOff
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = a.VesselId, AssessmentId = id, ForSubmission = a.SubmissionCount, Decision = decision,
            Comment = (comment ?? "").Trim(), ReviewerName = actor.Name, ReviewerRank = (rank ?? "").Trim(), AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.RaSignOffs.Add(signOff);
        await db.SaveChangesAsync();
        return signOff;
    }

    // ---- new versions ----

    /// <summary>
    /// A new version of a signed assessment, as a draft: the same reference with the next version number, everything copied (a hazard that already has its task keeps
    /// the link, so the task is not raised again). Made from the version in force only, and only while no other version is being written.
    /// </summary>
    public async Task<RiskAssessment> NewVersionAsync(Guid companyId, Guid id, RaKind kind, Actor actor)
    {
        if (kind == RaKind.Initial) throw new InvalidOperationException("A new version is an interim repeat, an annual review or a revision.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var source = await WithChildren(db).AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId) ?? throw new InvalidOperationException("Assessment not found.");
        if (!await IsEnabledForVesselAsync(db, companyId, source.VesselId)) throw new InvalidOperationException("Risk assessment is not switched on for this vessel.");
        var chain = await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && a.Code == source.Code).ToListAsync();
        var signOffs = (await db.RaSignOffs.AsNoTracking().Where(s => chain.Select(c => c.Id).Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        var states = chain.Select(c => (Assessment: c, Status: RaStatusRules.Of(c.SubmissionCount, signOffs[c.Id]))).ToList();

        if (states.Single(s => s.Assessment.Id == id).Status != RaStatus.Signed) throw new InvalidOperationException("Only a signed assessment can have a new version.");
        if (RaStatusRules.InForce(states.Select(s => (s.Assessment.Version, s.Status == RaStatus.Signed))) != source.Version)
            throw new InvalidOperationException("A newer version of this assessment has been signed; make the new version from that one.");
        if (states.Any(s => s.Status != RaStatus.Signed && s.Assessment.Version > source.Version))
            throw new InvalidOperationException("A newer version of this assessment is already being written.");

        var copy = new RiskAssessment
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = source.VesselId, Code = source.Code, Version = chain.Max(c => c.Version) + 1, PreviousVersionId = source.Id, Kind = kind,
            CategoryId = source.CategoryId, CategoryName = source.CategoryName, CategoryCode = source.CategoryCode, TemplateId = source.TemplateId, TemplateVersion = source.TemplateVersion,
            JobDescription = source.JobDescription, AssessedOn = Day(DateTime.UtcNow), Contingency = source.Contingency, ReviewPeriodMonths = source.ReviewPeriodMonths,
            CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        foreach (var m in source.Team) copy.Team.Add(new RaTeamMember { Id = Guid.CreateVersion7(), Name = m.Name, Rank = m.Rank, UserId = m.UserId });
        var activityIds = new Dictionary<Guid, Guid>();
        foreach (var act in source.Activities.OrderBy(x => x.SortOrder))
        {
            var made = new RaActivity { Id = Guid.CreateVersion7(), Title = act.Title, SortOrder = act.SortOrder };
            activityIds[act.Id] = made.Id;
            copy.Activities.Add(made);
        }
        foreach (var h in source.Hazards.OrderBy(x => x.SortOrder))
            copy.Hazards.Add(new RaHazard
            {
                Id = Guid.CreateVersion7(), ActivityId = activityIds[h.ActivityId], Hazard = h.Hazard, Effect = h.Effect, ConsequenceBefore = h.ConsequenceBefore, ProbabilityBefore = h.ProbabilityBefore,
                ExistingControls = h.ExistingControls, AdditionalControls = h.AdditionalControls, ConsequenceAfter = h.ConsequenceAfter, ProbabilityAfter = h.ProbabilityAfter,
                ResidualReason = h.ResidualReason, ResponsibleName = h.ResponsibleName, DueDate = h.DueDate, Contingency = h.Contingency, SortOrder = h.SortOrder,
                FactorBefore = h.FactorBefore, BandBefore = h.BandBefore, FactorAfter = h.FactorAfter, BandAfter = h.BandAfter, WorklistTaskId = h.WorklistTaskId,
            });
        db.RiskAssessments.Add(copy);
        await db.SaveChangesAsync();
        return copy;
    }

    // ---- reading ----

    public async Task<AssessmentDetail?> GetAsync(Guid companyId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var a = await WithChildren(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId);
        if (a is null) return null;
        var signOffs = await db.RaSignOffs.AsNoTracking().Where(s => s.AssessmentId == id).OrderBy(s => s.At).ThenBy(s => s.Id).ToListAsync();
        var chain = await ChainAsync(db, companyId, a.Code);
        var signedAt = RaStatusRules.SignedAt(a.SubmissionCount, signOffs);
        return new AssessmentDetail(a, signOffs, RaStatusRules.Of(a.SubmissionCount, signOffs), RaStatusRules.IsSuperseded(a.Version, chain),
            signedAt, signedAt is { } at ? RaStatusRules.NextReview(at, a.ReviewPeriodMonths) : null, await MatrixAsync(db, companyId));
    }

    /// <summary>The versions of one assessment (every version shares the reference), oldest first.</summary>
    public async Task<List<(RiskAssessment Assessment, RaStatus Status)>> VersionsAsync(Guid companyId, string code)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var versions = await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && a.Code == code).OrderBy(a => a.Version).ToListAsync();
        var signOffs = (await db.RaSignOffs.AsNoTracking().Where(s => versions.Select(v => v.Id).Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        return versions.Select(v => (v, RaStatusRules.Of(v.SubmissionCount, signOffs[v.Id]))).ToList();
    }

    private static async Task<List<(int Version, bool Signed)>> ChainAsync(ChandleryDbContext db, Guid companyId, string code)
    {
        var versions = await db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId && a.Code == code).ToListAsync();
        var signOffs = (await db.RaSignOffs.AsNoTracking().Where(s => versions.Select(v => v.Id).Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        return versions.Select(v => (v.Version, RaStatusRules.Of(v.SubmissionCount, signOffs[v.Id]) == RaStatus.Signed)).ToList();
    }

    public async Task<List<AssessmentRow>> ListAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.RiskAssessments.AsNoTracking().Where(a => a.CompanyId == companyId);
        if (vesselId is { } v) query = query.Where(a => a.VesselId == v);
        var assessments = await query.OrderByDescending(a => a.CreatedAt).ToListAsync();
        var ids = assessments.Select(a => a.Id).ToList();
        var signOffs = (await db.RaSignOffs.AsNoTracking().Where(s => ids.Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        var chains = assessments.GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.Select(x => (x.Version, RaStatusRules.Of(x.SubmissionCount, signOffs[x.Id]) == RaStatus.Signed)).ToList());
        return assessments.Select(a =>
        {
            var signedAt = RaStatusRules.SignedAt(a.SubmissionCount, signOffs[a.Id]);
            return new AssessmentRow(a, RaStatusRules.Of(a.SubmissionCount, signOffs[a.Id]), RaStatusRules.IsSuperseded(a.Version, chains[a.Code]),
                signedAt is { } at ? RaStatusRules.NextReview(at, a.ReviewPeriodMonths) : null);
        }).ToList();
    }
}

/// <summary>The starting library a company begins with: the categories, the matrix wording and the bands.</summary>
public static class RaDefaults
{
    /// <summary>Adds (without saving) whatever part of the starting library the company does not have yet. Returns how many records were added.</summary>
    public static async Task<int> AddMissingAsync(ChandleryDbContext db, Guid companyId)
    {
        var made = 0;
        if (!await db.RaCategories.IgnoreQueryFilters().AnyAsync(c => c.CompanyId == companyId))
        {
            var order = 0;
            foreach (var (name, code) in RaStandard.Categories) { db.RaCategories.Add(new RaCategory { Id = Guid.CreateVersion7(), CompanyId = companyId, Name = name, Code = code, SortOrder = order++ }); made++; }
        }
        if (!await db.RaMatrixLabels.IgnoreQueryFilters().AnyAsync(l => l.CompanyId == companyId))
        {
            for (var i = 0; i < RiskMatrix.Levels; i++)
            {
                db.RaMatrixLabels.Add(new RaMatrixLabel { Id = Guid.CreateVersion7(), CompanyId = companyId, Axis = RaAxis.Consequence, Level = i + 1, Label = RaStandard.Consequence[i] });
                db.RaMatrixLabels.Add(new RaMatrixLabel { Id = Guid.CreateVersion7(), CompanyId = companyId, Axis = RaAxis.Probability, Level = i + 1, Label = RaStandard.Probability[i] });
                made += 2;
            }
        }
        if (!await db.RaMatrixBands.IgnoreQueryFilters().AnyAsync(b => b.CompanyId == companyId))
        {
            var order = 0;
            foreach (var (name, min) in RiskMatrix.Default.Bands) { db.RaMatrixBands.Add(new RaMatrixBand { Id = Guid.CreateVersion7(), CompanyId = companyId, Name = name, MinFactor = min, SortOrder = order++ }); made++; }
        }
        return made;
    }

    public static async Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId)
    {
        await AddMissingAsync(db, companyId);
    }
}
