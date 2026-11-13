using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>A yard's question with its yard, for the office list.</summary>
public record TenderQuestionView(ProjectTenderQuestion Question, string YardName);

/// <summary>What the office is told about yards: questions to answer, revisions made, quotes in. Office only; a vessel never sees any of it.</summary>
public partial class ProjectPlanningService
{
    /// <summary>Adds a line to the office's notices. The caller saves.</summary>
    internal static void Notify(ChandleryDbContext db, Guid companyId, Guid projectId, Guid? tenderId, string text)
        => db.ProjectNotices.Add(new ProjectNotice { Id = Guid.CreateVersion7(), CompanyId = companyId, ProjectId = projectId, TenderId = tenderId, Text = text.Length > 500 ? text[..500] : text, At = DateTime.UtcNow });

    public async Task<List<ProjectNotice>> NoticesAsync(Guid companyId, Guid? projectId = null, bool unreadOnly = true, int take = 20)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return [];
        var query = db.ProjectNotices.AsNoTracking().Where(n => n.CompanyId == companyId);
        if (projectId is { } pid) query = query.Where(n => n.ProjectId == pid);
        if (unreadOnly) query = query.Where(n => n.ReadAt == null);
        return await query.OrderByDescending(n => n.At).Take(take).ToListAsync();
    }

    /// <summary>Marks the notices of one project (or all of them) as read.</summary>
    public async Task MarkNoticesReadAsync(Guid companyId, Guid? projectId = null)
    {
        await using var db = await OfficeAsync(companyId);
        var query = db.ProjectNotices.Where(n => n.CompanyId == companyId && n.ReadAt == null);
        if (projectId is { } pid) query = query.Where(n => n.ProjectId == pid);
        var now = DateTime.UtcNow;
        foreach (var n in await query.ToListAsync()) n.ReadAt = now;
        await db.SaveChangesAsync();
    }

    public async Task<List<TenderQuestionView>> QuestionsOfAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return [];
        var questions = await db.ProjectTenderQuestions.AsNoTracking().Where(q => q.ProjectId == projectId).OrderBy(q => q.AskedAt).ToListAsync();
        var yards = await (from t in db.ProjectTenders.AsNoTracking() join y in db.ProjectYards.AsNoTracking() on t.YardId equals y.Id where t.ProjectId == projectId select new { t.Id, y.Name }).ToDictionaryAsync(x => x.Id, x => x.Name);
        return questions.Select(q => new TenderQuestionView(q, yards.GetValueOrDefault(q.TenderId) ?? "")).ToList();
    }

    /// <summary>The office answers a yard's question. Only the yard that asked sees the answer.</summary>
    public async Task AnswerQuestionAsync(Guid companyId, Guid questionId, string answer, Actor actor)
    {
        answer = (answer ?? "").Trim();
        if (answer == "") throw new InvalidOperationException("Write the answer.");
        if (answer.Length > 2000) throw new InvalidOperationException("The answer can be at most 2000 characters.");
        await using var db = await OfficeAsync(companyId, actor);
        var question = await db.ProjectTenderQuestions.FirstOrDefaultAsync(q => q.Id == questionId && q.CompanyId == companyId) ?? throw new InvalidOperationException("Question not found.");
        await ProjectService.LoadEditableAsync(db, companyId, question.ProjectId);
        (question.Answer, question.AnsweredAt, question.AnsweredByName) = (answer, DateTime.UtcNow, actor.Name);
        await db.SaveChangesAsync();
    }

    /// <summary>Every version of each yard's quote on a project, oldest first, by tender.</summary>
    public async Task<ILookup<Guid, ProjectTenderRevision>> RevisionsOfAsync(Guid companyId, Guid projectId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) return Array.Empty<ProjectTenderRevision>().ToLookup(r => r.TenderId);
        var tenderIds = await db.ProjectTenders.AsNoTracking().Where(t => t.ProjectId == projectId).Select(t => t.Id).ToListAsync();
        return (await db.ProjectTenderRevisions.AsNoTracking().Where(r => tenderIds.Contains(r.TenderId)).OrderBy(r => r.Number).ToListAsync()).ToLookup(r => r.TenderId);
    }
}
