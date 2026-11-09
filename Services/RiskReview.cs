namespace Chandlery.Services;

/// <summary>How close an assessment is to its review date.</summary>
public enum RaReviewState { None, Ok, DueSoon, Overdue }

/// <summary>Review dates: when a signed assessment needs looking at again, worked out and never stored.</summary>
public static class RaReview
{
    /// <summary>Days before the review date at which an assessment shows as "due soon".</summary>
    public const int SoonDays = 30;

    /// <summary>Overdue once the review date has passed, due soon within <see cref="SoonDays"/> days of it, otherwise fine. No date means no state.</summary>
    public static RaReviewState StateOf(DateTime? nextReview, DateTime today)
    {
        if (nextReview is null) return RaReviewState.None;
        var days = (nextReview.Value.Date - today.Date).TotalDays;
        return days < 0 ? RaReviewState.Overdue : days <= SoonDays ? RaReviewState.DueSoon : RaReviewState.Ok;
    }

    public static string Label(RaReviewState state) => state switch
    {
        RaReviewState.Overdue => "Review overdue",
        RaReviewState.DueSoon => "Review due soon",
        RaReviewState.Ok => "Review on schedule",
        _ => "",
    };
}
