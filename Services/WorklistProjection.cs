using System.Globalization;
using System.Text.Json.Nodes;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>What a task looks like after its timeline has been replayed.</summary>
public record WorklistState(
    string Title, string Description, WorklistPriority Priority, WorklistStatus Status, DateTime? Planned, DateTime? Target,
    string Assignee, int Percent, int Deferrals, DateTime? CompletedAt, DateTime? VerifiedAt, DateTime? LastEntryAt);

/// <summary>
/// Works out a task from its entries. Pure: the same entries always give the same answer, whatever order they arrived in, because
/// they are replayed by time (then by id). So the vessel and the office, which receive each other's entries at different times, still
/// end up with identical tasks. Where two entries set the same thing, the later one wins and both stay in the history.
/// </summary>
public static class WorklistProjection
{
    public static WorklistState Replay(IEnumerable<WorklistEntry> entries)
    {
        var state = new WorklistState("", "", WorklistPriority.Normal, WorklistStatus.Open, null, null, "", 0, 0, null, null, null);
        foreach (var entry in entries.OrderBy(e => e.At).ThenBy(e => e.Id))
        {
            var p = Parse(entry.Payload);
            state = state with { LastEntryAt = entry.At };
            switch (entry.Type)
            {
                case WorklistEntryType.Created:
                    state = state with
                    {
                        Title = Text(p, "title") ?? state.Title,
                        Description = Text(p, "description") ?? state.Description,
                        Priority = ParseEnum(p, "priority", state.Priority),
                        Planned = Date(p, "planned"),
                        Target = Date(p, "target"),
                        Assignee = Text(p, "assignee") ?? "",
                        Status = WorklistStatus.Open,
                    };
                    break;

                case WorklistEntryType.Status:
                    state = WithStatus(state, ParseEnum(p, "status", state.Status), entry.At);
                    break;

                case WorklistEntryType.Percent:
                {
                    var percent = Math.Clamp(Int(p, "percent") ?? state.Percent, 0, 100);
                    state = state with { Percent = percent };
                    if (percent > 0 && state.Status == WorklistStatus.Open) state = state with { Status = WorklistStatus.InProgress };
                    if (percent < 100 && state.Status == WorklistStatus.Completed) state = WithStatus(state, WorklistStatus.InProgress, entry.At);
                    break;
                }

                case WorklistEntryType.Priority:
                    state = state with { Priority = ParseEnum(p, "priority", state.Priority) };
                    break;

                case WorklistEntryType.Target:
                    state = state with { Target = Date(p, "target") ?? state.Target };
                    break;

                case WorklistEntryType.Assignee:
                    state = state with { Assignee = Text(p, "assignee") ?? state.Assignee };
                    break;

                case WorklistEntryType.Details:
                    state = state with { Title = Text(p, "title") ?? state.Title, Description = Text(p, "description") ?? state.Description };
                    break;

                case WorklistEntryType.Defer:
                    state = state with { Target = Date(p, "target") ?? state.Target, Deferrals = state.Deferrals + 1 };
                    break;

                case WorklistEntryType.Completed:
                    state = WithStatus(state, WorklistStatus.Completed, entry.At);
                    break;

                case WorklistEntryType.Verified:
                    // Only a completed task can be verified; a verification that precedes the completion has nothing to confirm.
                    if (state.Status == WorklistStatus.Completed) state = state with { VerifiedAt = entry.At };
                    break;

                case WorklistEntryType.Comment:
                case WorklistEntryType.Link:
                    break;
            }
        }
        return state;
    }

    private static WorklistState WithStatus(WorklistState state, WorklistStatus status, DateTime at) => status switch
    {
        WorklistStatus.Completed => state with { Status = status, Percent = 100, CompletedAt = at, VerifiedAt = null },
        _ => state with { Status = status, CompletedAt = null, VerifiedAt = null },
    };

    /// <summary>Writes a replayed state onto the task's projected columns.</summary>
    public static void Apply(WorklistTask task, WorklistState state)
    {
        task.Title = state.Title;
        task.Description = state.Description;
        task.Priority = state.Priority;
        task.Status = state.Status;
        task.PlannedDate = state.Planned;
        task.TargetDate = state.Target;
        task.AssigneeName = state.Assignee;
        task.PercentComplete = state.Percent;
        task.DeferralCount = state.Deferrals;
        task.CompletedAt = state.CompletedAt;
        task.VerifiedAt = state.VerifiedAt;
        task.LastEntryAt = state.LastEntryAt;
    }

    /// <summary>Re-reads a task's entries and rewrites its projected columns. Used after an entry arrives from the other side.</summary>
    public static async Task RefreshAsync(ChandleryDbContext db, Guid taskId, CancellationToken ct = default)
    {
        var task = await db.WorklistTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;
        var entries = await db.WorklistEntries.AsNoTracking().Where(e => e.TaskId == taskId).ToListAsync(ct);
        Apply(task, Replay(entries));
        await db.SaveChangesAsync(ct);
    }

    // ---- payload helpers ----

    public static string Json(params (string Key, object? Value)[] values)
    {
        var o = new JsonObject();
        foreach (var (key, value) in values)
            if (value is not null)
                o[key] = System.Text.Json.JsonSerializer.SerializeToNode(
                    value is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : value is Enum e ? e.ToString() : value);
        return o.ToJsonString();
    }

    private static JsonObject Parse(string payload)
    {
        try { return JsonNode.Parse(payload) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    private static string? Text(JsonObject p, string key) => p[key]?.GetValue<string>();

    private static int? Int(JsonObject p, string key)
        => p[key] is { } node && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTime? Date(JsonObject p, string key)
        => Text(p, key) is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;

    private static T ParseEnum<T>(JsonObject p, string key, T fallback) where T : struct, Enum
        => Text(p, key) is { } text && Enum.TryParse<T>(text, true, out var v) ? v : fallback;
}
