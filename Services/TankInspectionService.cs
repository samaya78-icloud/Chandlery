using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record NewInspection(Guid TankId, Guid TemplateId, DateTime InspectedOn, string Place, string WorkDone = "", Guid? PlannedJobId = null);

public record InspectionAnswer(Guid ResultId, string Value, string Comment = "");

public record InspectionEdit(DateTime InspectedOn, string Place, string WorkDone, string GeneralComments, string InspectorName, IReadOnlyList<InspectionAnswer> Answers);

public record InspectionDetail(TankInspection Inspection, Tank Tank, List<TankInspectionReview> Reviews, InspectionStatus Status);

public record InspectionRow(TankInspection Inspection, string TankName, string TankCode, TankType TankType, InspectionStatus Status);

/// <summary>What submitting an inspection set in motion elsewhere: the worklist tasks raised for exceptions, whether the maintenance job was completed, and anything that could not be done.</summary>
public record SubmitOutcome(List<WorklistTask> TasksRaised, bool JobCompleted, List<string> Notes);

/// <summary>
/// Tank inspections: started and filled in by one side (the vessel), submitted, and looked at by the office through review records. A draft or a returned
/// inspection can be changed by the side that made it; a submitted or reviewed one is locked. The status is always worked out, never stored.
/// </summary>
public class TankInspectionService(IDbContextFactory<ChandleryDbContext> factory, WorklistService? worklist = null, PmsService? pms = null)
{
    private static DateTime Day(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    private static async Task<InspectionStatus> StatusOfAsync(ChandleryDbContext db, TankInspection inspection)
        => TankStatus.Of(inspection.SubmissionCount, await db.TankInspectionReviews.AsNoTracking().Where(r => r.InspectionId == inspection.Id).ToListAsync());

    // ---- starting and filling in ----

    public async Task<TankInspection> StartAsync(Guid companyId, NewInspection input, Actor actor)
    {
        if (string.IsNullOrWhiteSpace(input.Place)) throw new InvalidOperationException("Say where the inspection is made (at sea, or the port).");
        if (input.InspectedOn.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("An inspection cannot be dated in the future.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var tank = await db.Tanks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.TankId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Tank not found.");
        if (!await TankService.IsEnabledForVesselAsync(db, companyId, tank.VesselId)) throw new InvalidOperationException("Tank inspection is not switched on for this vessel.");
        if (!tank.IsActive) throw new InvalidOperationException("This tank is no longer in use.");
        var template = await db.TankChecklistTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.TemplateId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Checklist not found.");
        if (!template.IsActive) throw new InvalidOperationException("That checklist is no longer in use.");
        if (template.TankType != tank.Type) throw new InvalidOperationException($"That checklist is for {template.TankType} tanks, and {tank.Name} is a {tank.Type} tank.");
        if (input.PlannedJobId is { } jobId)
        {
            var job = await db.PlannedJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId) ?? throw new InvalidOperationException("Maintenance job not found.");
            if (job.TankId != tank.Id) throw new InvalidOperationException("That maintenance job is not an inspection of this tank.");
        }
        var criteria = await db.TankCriteria.AsNoTracking().Where(c => c.TemplateId == template.Id && c.IsActive).OrderBy(c => c.Group).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        if (criteria.Count == 0) throw new InvalidOperationException("That checklist has nothing to look at yet.");

        var inspection = new TankInspection
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = tank.VesselId, TankId = tank.Id, Code = await DocumentNumbers.NextAsync(db, companyId, tank.VesselId, DocType.TankInspection),
            TemplateId = template.Id, TemplateName = template.Name, TemplateVersion = template.Version, InspectedOn = Day(input.InspectedOn), Place = input.Place.Trim(), WorkDone = (input.WorkDone ?? "").Trim(),
            PlannedJobId = input.PlannedJobId, InspectorName = actor.Name, CreatedByName = actor.Name, CreatedAt = DateTime.UtcNow,
        };
        var order = 0;
        foreach (var c in criteria)
            inspection.Results.Add(new TankInspectionResult { Id = Guid.CreateVersion7(), CriterionId = c.Id, Group = c.Group, CriterionName = c.Name, Scale = c.Scale, ExceptionValues = c.ExceptionValues, SortOrder = order++ });
        db.TankInspections.Add(inspection);
        await db.SaveChangesAsync();
        return inspection;
    }

    private static async Task<TankInspection> LoadEditableAsync(ChandleryDbContext db, Guid companyId, Guid inspectionId)
    {
        var inspection = await db.TankInspections.Include(i => i.Results).FirstOrDefaultAsync(i => i.Id == inspectionId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Inspection not found.");
        var status = await StatusOfAsync(db, inspection);
        if (!TankStatus.IsEditable(status))
            throw new InvalidOperationException(status == InspectionStatus.Reviewed
                ? "This inspection has been reviewed and is locked. Record a new inspection to correct it."
                : $"This inspection has been submitted ({status.ToString().ToLowerInvariant()}) and cannot be changed until it is returned.");
        return inspection;
    }

    /// <summary>Saves what has been filled in so far. Allowed only while it is a draft or has been returned.</summary>
    public async Task SaveAsync(Guid companyId, Guid inspectionId, InspectionEdit edit, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var inspection = await LoadEditableAsync(db, companyId, inspectionId);
        if (edit.InspectedOn.Date > DateTime.UtcNow.Date) throw new InvalidOperationException("An inspection cannot be dated in the future.");

        var byId = inspection.Results.ToDictionary(r => r.Id);
        foreach (var answer in edit.Answers)
        {
            var result = byId.GetValueOrDefault(answer.ResultId) ?? throw new InvalidOperationException("One of the answers does not belong to this inspection.");
            var value = (answer.Value ?? "").Trim();
            if (value != "" && !TankScales.IsValid(result.Scale, value)) throw new InvalidOperationException($"{result.CriterionName}: \"{value}\" is not an answer on this scale ({string.Join(", ", TankScales.Values(result.Scale))}).");
            (result.Value, result.Comment) = (value, (answer.Comment ?? "").Trim());
        }
        (inspection.InspectedOn, inspection.Place, inspection.WorkDone, inspection.GeneralComments) = (Day(edit.InspectedOn), (edit.Place ?? "").Trim(), (edit.WorkDone ?? "").Trim(), (edit.GeneralComments ?? "").Trim());
        inspection.InspectorName = string.IsNullOrWhiteSpace(edit.InspectorName) ? actor.Name : edit.InspectorName.Trim();
        await db.SaveChangesAsync();
    }

    /// <summary>Submits the inspection for review. Every criterion must be answered (NI and NA are answers) and the place and date given.</summary>
    public async Task<SubmitOutcome> SubmitAsync(Guid companyId, Guid inspectionId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var inspection = await LoadEditableAsync(db, companyId, inspectionId);
        if (string.IsNullOrWhiteSpace(inspection.Place)) throw new InvalidOperationException("Say where the inspection was made (at sea, or the port).");
        var problems = TankStatus.SubmissionProblems(inspection.Results);
        if (problems.Count > 0) throw new InvalidOperationException($"{problems.Count} criterion(s) still need an answer: {string.Join(" ", problems.Take(3))}{(problems.Count > 3 ? " …" : "")}");

        var exceptions = TankStatus.Exceptions(inspection.Results);
        var firstSubmission = inspection.SubmissionCount == 0;
        inspection.SubmissionCount++;
        inspection.SubmittedAt = DateTime.UtcNow;
        inspection.ExceptionCount = exceptions.Count;
        inspection.HasBadRating = exceptions.Any(r => r.Scale == ScaleKind.Rating && r.Value == "Bad");
        await ChatService.NoteAsync(db, companyId, ChatModules.TankInspection, inspection.Id, inspection.VesselId,
            $"{(firstSubmission ? "Submitted" : "Submitted again")} by {actor.Name}: {exceptions.Count} exception(s)" + (inspection.HasBadRating ? ", including a Bad rating" : ""));
        await db.SaveChangesAsync();
        return await FollowUpAsync(db, companyId, inspection, exceptions, firstSubmission, actor);
    }

    /// <summary>
    /// After a submission: a worklist task for each exception that has none yet (a Bad rating urgent), and, the first time, completion of the maintenance job the
    /// inspection was started from. The submission itself is already saved, so a problem here is reported and never undoes it; a resubmission raises nothing twice.
    /// </summary>
    private async Task<SubmitOutcome> FollowUpAsync(ChandleryDbContext db, Guid companyId, TankInspection inspection, List<TankInspectionResult> exceptions, bool firstSubmission, Actor actor)
    {
        var raised = new List<WorklistTask>();
        var notes = new List<string>();
        var jobCompleted = false;

        var pending = exceptions.Where(r => r.WorklistTaskId is null).ToList();
        if (pending.Count > 0 && worklist is not null)
        {
            if (!await WorklistService.IsEnabledForVesselAsync(db, companyId, inspection.VesselId))
                notes.Add("The worklist is off for this vessel, so no tasks were raised for the exceptions.");
            else
            {
                var tank = await db.Tanks.AsNoTracking().FirstAsync(t => t.Id == inspection.TankId);
                var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == "Vessel").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
                foreach (var r in pending)
                {
                    try
                    {
                        var urgent = r.Scale == ScaleKind.Rating && r.Value == "Bad";
                        var description = $"Found at the tank inspection {inspection.Code} on {inspection.InspectedOn:dd MMM yyyy}: {r.CriterionName} is {r.Value}." + (r.Comment == "" ? "" : $" {r.Comment}");
                        var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(inspection.VesselId, $"{tank.Code} {tank.Name}: {r.CriterionName} ({r.Value})", description, source,
                            Priority: urgent ? WorklistPriority.Urgent : WorklistPriority.Normal), actor);
                        await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.TankInspection, LinkTarget: inspection.Id, LinkLabel: $"Tank inspection {inspection.Code}"), actor);
                        r.WorklistTaskId = task.Id;
                        raised.Add(task);
                    }
                    catch (InvalidOperationException ex) { notes.Add($"{r.CriterionName}: no task raised ({ex.Message})"); }
                }
                if (raised.Count > 0) await db.SaveChangesAsync();
            }
        }

        if (firstSubmission && inspection.PlannedJobId is { } jobId && pms is not null)
        {
            try
            {
                jobCompleted = await pms.CompleteJobByInspectionAsync(companyId, jobId, inspection.InspectedOn, $"Tank inspection {inspection.Code}: {exceptions.Count} exception(s).", actor) is not null;
                if (!jobCompleted) notes.Add("Planned maintenance is off for this vessel, so the maintenance job was not completed.");
            }
            catch (InvalidOperationException ex) { notes.Add($"The maintenance job was not completed ({ex.Message})"); }
        }
        return new SubmitOutcome(raised, jobCompleted, notes);
    }

    // ---- review (the office) ----

    /// <summary>
    /// The office's response to the current submission: start reviewing, accept (the inspection is then locked) or return with comments (the vessel corrects it
    /// and submits again). A review is a new record; the inspection itself is never edited by the reviewer.
    /// </summary>
    public async Task<TankInspectionReview> ReviewAsync(Guid companyId, Guid inspectionId, ReviewDecision decision, string comment, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("Inspections are reviewed by the office.");
        var inspection = await db.TankInspections.AsNoTracking().FirstOrDefaultAsync(i => i.Id == inspectionId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Inspection not found.");
        var status = await StatusOfAsync(db, inspection);
        if (status is InspectionStatus.Draft or InspectionStatus.Returned) throw new InvalidOperationException("This inspection has not been submitted (or is back with the vessel).");
        if (status == InspectionStatus.Reviewed) throw new InvalidOperationException("This inspection has already been reviewed.");
        if (decision == ReviewDecision.Started && status == InspectionStatus.InReview) throw new InvalidOperationException("The review has already been started.");
        if (decision == ReviewDecision.Returned && string.IsNullOrWhiteSpace(comment)) throw new InvalidOperationException("Say what needs correcting.");

        var review = new TankInspectionReview
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = inspection.VesselId, InspectionId = inspectionId, ForSubmission = inspection.SubmissionCount, Decision = decision,
            Comment = (comment ?? "").Trim(), ReviewerName = actor.Name, AuthorUserId = actor.UserId, At = DateTime.UtcNow,
        };
        db.TankInspectionReviews.Add(review);
        await ChatService.NoteAsync(db, companyId, ChatModules.TankInspection, inspection.Id, inspection.VesselId,
            decision switch
            {
                ReviewDecision.Started => $"Review started by {actor.Name}",
                ReviewDecision.Accepted => $"Accepted by {actor.Name}" + (review.Comment == "" ? "" : $": {review.Comment}"),
                _ => $"Returned to the vessel by {actor.Name}: {review.Comment}",
            });
        await db.SaveChangesAsync();
        return review;
    }

    // ---- reading ----

    public async Task<InspectionDetail?> GetAsync(Guid companyId, Guid inspectionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var inspection = await db.TankInspections.AsNoTracking().Include(i => i.Results).FirstOrDefaultAsync(i => i.Id == inspectionId && i.CompanyId == companyId);
        if (inspection is null) return null;
        inspection.Results = inspection.Results.OrderBy(r => r.Group).ThenBy(r => r.SortOrder).ToList();
        var reviews = (await db.TankInspectionReviews.AsNoTracking().Where(r => r.InspectionId == inspectionId).ToListAsync()).OrderBy(r => r.At).ThenBy(r => r.Id).ToList();
        var tank = await db.Tanks.AsNoTracking().FirstAsync(t => t.Id == inspection.TankId);
        return new InspectionDetail(inspection, tank, reviews, TankStatus.Of(inspection.SubmissionCount, reviews));
    }

    /// <summary>
    /// What is waiting for the office: submitted inspections and those being reviewed, a Bad rating first, then the oldest submission. Only vessels the module is
    /// on for. Returned and reviewed inspections are not waiting for the office.
    /// </summary>
    public async Task<List<ReviewQueueRow>> ReviewQueueAsync(Guid companyId, Guid? vesselId = null)
    {
        var rows = (await ListAsync(companyId, vesselId)).Where(r => r.Status is InspectionStatus.Submitted or InspectionStatus.InReview).ToList();
        if (rows.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.TankInspection)).ToDictionary(v => v.Id, v => v.Name);
        return rows.Where(r => enabled.ContainsKey(r.Inspection.VesselId))
            .OrderByDescending(r => r.Inspection.HasBadRating).ThenBy(r => r.Inspection.SubmittedAt).ThenBy(r => r.Inspection.Code)
            .Select(r => new ReviewQueueRow(r, enabled[r.Inspection.VesselId])).ToList();
    }

    public async Task<ReviewWaiting> ReviewWaitingAsync(Guid companyId)
    {
        var queue = await ReviewQueueAsync(companyId);
        return new ReviewWaiting(queue.Count, queue.Count(r => r.Row.Inspection.HasBadRating));
    }

    public async Task<List<InspectionRow>> ListAsync(Guid companyId, Guid? vesselId = null, Guid? tankId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.TankInspections.AsNoTracking().Where(i => i.CompanyId == companyId);
        if (vesselId is { } v) query = query.Where(i => i.VesselId == v);
        if (tankId is { } t) query = query.Where(i => i.TankId == t);
        var inspections = await query.OrderByDescending(i => i.InspectedOn).ThenByDescending(i => i.CreatedAt).ToListAsync();
        if (inspections.Count == 0) return [];
        var ids = inspections.Select(i => i.Id).ToList();
        var reviews = (await db.TankInspectionReviews.AsNoTracking().Where(r => ids.Contains(r.InspectionId)).ToListAsync()).ToLookup(r => r.InspectionId);
        var tankIds = inspections.Select(i => i.TankId).Distinct().ToList();
        var tanks = await db.Tanks.AsNoTracking().Where(x => tankIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        return inspections.Select(i =>
        {
            var tank = tanks.GetValueOrDefault(i.TankId);
            return new InspectionRow(i, tank?.Name ?? "", tank?.Code ?? "", tank?.Type ?? TankType.Other, TankStatus.Of(i.SubmissionCount, reviews[i.Id]));
        }).ToList();
    }
}
