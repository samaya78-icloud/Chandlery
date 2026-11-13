using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>
/// A project request can carry several groups of steps at once: the steps for the whole request (scope "") and, for a budget, an extra sign-off for each category the
/// matrix has its own rules for. Each scope goes in order, level by level, with any one approver at a level satisfying it, and the request is approved only when every
/// scope is complete. Pure, so the service, the screens and the tests agree.
/// </summary>
public static class ProjectApprovalScopes
{
    /// <summary>The level a scope is waiting on; null once it is complete.</summary>
    public static int? CurrentLevel(IEnumerable<ProjectApprovalSlot> slots, string scope)
        => ApprovalEngine.CurrentLevel(slots.Where(s => s.Category == scope).Select(s => (s.Level, s.Decision)));

    /// <summary>True when every scope of the request has been approved.</summary>
    public static bool IsComplete(IReadOnlyCollection<ProjectApprovalSlot> slots)
        => slots.Select(s => s.Category).Distinct().All(scope => CurrentLevel(slots, scope) is null);

    /// <summary>Whether a slot is one that can be answered now: still pending, and at the level its scope is waiting on.</summary>
    public static bool IsTurn(IReadOnlyCollection<ProjectApprovalSlot> slots, ProjectApprovalSlot slot)
        => slot.Decision == ApprovalDecision.Pending && CurrentLevel(slots, slot.Category) == slot.Level;

    /// <summary>The slot this person can answer now, if any.</summary>
    public static ProjectApprovalSlot? MyTurn(IReadOnlyCollection<ProjectApprovalSlot> slots, Guid userId)
        => slots.FirstOrDefault(s => s.ApproverUserId == userId && IsTurn(slots, s));
}
