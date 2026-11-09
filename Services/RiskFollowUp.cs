using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>What submitting an assessment set in motion elsewhere: the worklist tasks raised for control actions, and anything that could not be done.</summary>
public record RaSubmitOutcome(List<WorklistTask> TasksRaised, List<string> Notes);

public record RiskCounts(int Waiting, int DueForReview, int Overdue, int HighResidual, int OldDrafts);

/// <summary>Worklist tasks for control actions and review dates, the counts for tiles and menu, and the assessments in force.</summary>
public partial class RiskAssessmentService
{
    public const string ReminderTitle = "Review risk assessment";

    /// <summary>A draft or returned assessment not touched for this many days counts as an old draft.</summary>
    public const int OldDraftDays = 14;

    /// <summary>A control action is an additional control with someone responsible and a date.</summary>
    public static bool IsControlAction(RaHazard h) => !string.IsNullOrWhiteSpace(h.AdditionalControls) && !string.IsNullOrWhiteSpace(h.ResponsibleName) && h.DueDate is not null;

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // ---- control actions: raised when the assessment is submitted ----

    /// <summary>
    /// After a submission: a worklist task for each control action that has none yet (urgent when the risk left is serious), linked to the assessment. The submission is already
    /// saved, so a problem here is reported and never undoes it; a resubmission, or a new version that carries the link over, never raises the same action twice.
    /// </summary>
    private async Task<RaSubmitOutcome> FollowUpAsync(ChandleryDbContext db, Guid companyId, RiskAssessment a, Actor actor)
    {
        var raised = new List<WorklistTask>();
        var notes = new List<string>();
        var pending = a.Hazards.Where(h => h.WorklistTaskId is null && IsControlAction(h)).OrderBy(h => h.SortOrder).ToList();
        if (pending.Count == 0 || worklist is null) return new RaSubmitOutcome(raised, notes);
        if (!await WorklistService.IsEnabledForVesselAsync(db, companyId, a.VesselId))
        {
            notes.Add("The worklist is off for this vessel, so no tasks were raised for the control actions.");
            return new RaSubmitOutcome(raised, notes);
        }

        var matrix = await MatrixAsync(db, companyId);
        foreach (var h in pending)
        {
            try
            {
                var urgent = h.FactorAfter > 0 && matrix.IsSerious(h.BandAfter);
                var description = $"From risk assessment {a.Code} version {a.Version} ({Shorten(a.JobDescription, 120)}). Hazard: {h.Hazard}. Risk after controls: {h.BandAfter} ({h.FactorAfter}). Control: {h.AdditionalControls}";
                var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(a.VesselId, $"{a.Code}: {Shorten(h.AdditionalControls, 80)}", description,
                    Priority: urgent ? WorklistPriority.Urgent : WorklistPriority.Normal, Target: h.DueDate, Assignee: h.ResponsibleName), actor);
                await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.RiskAssessment, LinkTarget: a.Id, LinkLabel: $"Risk assessment {a.Code}"), actor);
                h.WorklistTaskId = task.Id;
                raised.Add(task);
            }
            catch (InvalidOperationException ex) { notes.Add($"{Shorten(h.Hazard, 40)}: no task raised ({ex.Message})"); }
        }
        if (raised.Count > 0) await db.SaveChangesAsync();
        return new RaSubmitOutcome(raised, notes);
    }

    // ---- review reminders: raised when the assessment is signed ----

    /// <summary>
    /// A reminder task for the review date (planned {SoonDays} days before it), linked to the assessment. Reminders of earlier versions of the same assessment are cancelled,
    /// because this version now sets the date. Best effort: a worklist that is off, or a task the other side raised and this side may not cancel, never stops the signing.
    /// </summary>
    private async Task RemindAsync(ChandleryDbContext db, Guid companyId, RiskAssessment a, DateTime signedAt, Actor actor)
    {
        if (worklist is null) return;
        try
        {
            if (!await WorklistService.IsEnabledForVesselAsync(db, companyId, a.VesselId)) return;
            var earlier = await db.RiskAssessments.AsNoTracking().Where(x => x.CompanyId == companyId && x.Code == a.Code && x.Id != a.Id).Select(x => new { x.Id, x.Version }).ToListAsync();
            foreach (var prior in earlier)
                foreach (var old in await worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.RiskAssessment, prior.Id))
                {
                    if (!old.Title.StartsWith(ReminderTitle, StringComparison.Ordinal) || old.Status is WorklistStatus.Completed or WorklistStatus.Cancelled) continue;
                    try { await worklist.AddEntryAsync(companyId, old.Id, new WorklistEntryInput(WorklistEntryType.Status, $"Replaced: version {a.Version} was signed.", WorklistStatus.Cancelled), actor); }
                    catch (InvalidOperationException) { }
                }

            var due = RaStatusRules.NextReview(signedAt, a.ReviewPeriodMonths);
            var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(a.VesselId, $"{ReminderTitle} {a.Code} version {a.Version}",
                $"{Shorten(a.JobDescription, 160)}. Signed {signedAt:dd MMM yyyy}; due for review {due:dd MMM yyyy}. Make a new version (annual review) and have it signed.",
                Planned: due.AddDays(-RaReview.SoonDays), Target: due), actor);
            await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.RiskAssessment, LinkTarget: a.Id, LinkLabel: $"Risk assessment {a.Code}"), actor);
        }
        catch (InvalidOperationException) { }
    }

    // ---- what is in force, and the counts ----

    /// <summary>Is risk assessment switched on for this vessel?</summary>
    public async Task<bool> IsEnabledAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await IsEnabledForVesselAsync(db, companyId, vesselId);
    }

    /// <summary>Does the company ask for a signed risk assessment before a critical maintenance job is put off (and is the module on for this vessel)?</summary>
    public static async Task<bool> RequiredForCriticalDeferralAsync(ChandleryDbContext db, Guid companyId, Guid vesselId)
        => await db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && c.RiskRequiredForCriticalDeferral) && await IsEnabledForVesselAsync(db, companyId, vesselId);

    public async Task<bool> RequiredForCriticalDeferralAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await RequiredForCriticalDeferralAsync(db, companyId, vesselId);
    }

    /// <summary>The assessment, if it is for this vessel, signed, and not replaced by a later signed version; otherwise null.</summary>
    public static async Task<RiskAssessment?> InForceByIdAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, Guid id)
    {
        var a = await db.RiskAssessments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && x.VesselId == vesselId);
        if (a is null) return null;
        var chain = await db.RiskAssessments.AsNoTracking().Where(x => x.CompanyId == companyId && x.Code == a.Code).ToListAsync();
        var signOffs = (await db.RaSignOffs.AsNoTracking().Where(s => chain.Select(c => c.Id).Contains(s.AssessmentId)).ToListAsync()).ToLookup(s => s.AssessmentId);
        var states = chain.Select(c => (c.Version, Signed: RaStatusRules.Of(c.SubmissionCount, signOffs[c.Id]) == RaStatus.Signed)).ToList();
        var signed = RaStatusRules.Of(a.SubmissionCount, signOffs[a.Id]) == RaStatus.Signed;
        return signed && !RaStatusRules.IsSuperseded(a.Version, states) ? a : null;
    }

    /// <summary>The signed assessments of a vessel that have not been replaced, for linking from elsewhere (the Critical equipment category first).</summary>
    public async Task<List<AssessmentRow>> InForceAsync(Guid companyId, Guid vesselId)
        => (await ListAsync(companyId, vesselId)).Where(r => r.Status == RaStatus.Signed && !r.Superseded)
            .OrderByDescending(r => r.Assessment.CategoryCode == "CRE").ThenBy(r => r.Assessment.Code).ToList();

    /// <summary>For tiles and menu: what waits for sign-off, what is due or overdue for review, signed ones that still carry a serious risk, and old drafts. Null when the module is off everywhere.</summary>
    public async Task<RiskCounts?> CountsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = (await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.RiskAssessment)).Select(v => v.Id).ToHashSet();
        if (enabled.Count == 0) return null;
        var matrix = await MatrixAsync(db, companyId);
        var rows = (await ListAsync(companyId)).Where(r => enabled.Contains(r.Assessment.VesselId)).ToList();
        var today = DateTime.UtcNow.Date;
        var live = rows.Where(r => r.Status == RaStatus.Signed && !r.Superseded).ToList();
        var states = live.Select(r => RaReview.StateOf(r.NextReview, today)).ToList();
        return new RiskCounts(
            Waiting: rows.Count(r => r.Status is RaStatus.Submitted or RaStatus.InReview),
            DueForReview: states.Count(s => s is RaReviewState.DueSoon or RaReviewState.Overdue),
            Overdue: states.Count(s => s == RaReviewState.Overdue),
            HighResidual: live.Count(r => r.Assessment.HighestResidualFactor > 0 && matrix.IsSerious(r.Assessment.HighestResidualBand)),
            OldDrafts: rows.Count(r => r.Status is RaStatus.Draft or RaStatus.Returned && (today - r.Assessment.CreatedAt.Date).TotalDays > OldDraftDays));
    }
}
