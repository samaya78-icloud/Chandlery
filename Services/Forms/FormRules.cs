using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>The status of an entry, worked out from its submissions and the reviews of the current one. Pure, so the vessel, the office and the sync filter agree.</summary>
public static class FormStatusRules
{
    public static FormStatus Of(int submissionCount, IEnumerable<FormReview> reviews)
    {
        if (submissionCount <= 0) return FormStatus.Draft;
        var latest = reviews.Where(r => r.ForSubmission == submissionCount).OrderBy(r => r.At).ThenBy(r => r.Id).LastOrDefault();
        return latest?.Decision switch
        {
            ReviewDecision.Accepted => FormStatus.Reviewed,
            ReviewDecision.Returned => FormStatus.Returned,
            ReviewDecision.Started => FormStatus.InReview,
            _ => FormStatus.Submitted,
        };
    }

    /// <summary>Only a draft or a returned entry can be changed, and only by the side that made it.</summary>
    public static bool IsEditable(FormStatus status) => status is FormStatus.Draft or FormStatus.Returned;
}

/// <summary>Which vessels a form applies to: the groups (and everything beneath them) and the named vessels listed, or every vessel when both lists are empty.</summary>
public static class FormApplicability
{
    public static List<Guid> ParseIds(string? text)
        => (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();

    public static string Join(IEnumerable<Guid> ids) => string.Join(",", ids.Distinct());

    /// <param name="groupChain">The vessel's group and every group above it.</param>
    public static bool AppliesTo(string? groups, string? vessels, Guid vesselId, IReadOnlySet<Guid> groupChain)
    {
        var groupIds = ParseIds(groups);
        var vesselIds = ParseIds(vessels);
        if (groupIds.Count == 0 && vesselIds.Count == 0) return true;
        return vesselIds.Contains(vesselId) || groupIds.Any(groupChain.Contains);
    }
}

public enum FormDueStatus { NotScheduled, Open, DueSoon, Overdue, Done }

/// <summary>What is due for one vessel and one form right now.</summary>
public record FormDue(FormDueStatus Status, DateOnly? DueOn, DateOnly? PeriodStart, int MissedPeriods);

/// <summary>
/// What is due and late for a form with a periodicity. Due on the calendar: every period from a start date, a period being done when an entry is dated in it, and
/// due on its last day. Due from the previous entry: a period after the last one. Pure and shared by the vessel's list, the office's year view and the tests,
/// so a form the office sees as late is the form the vessel sees as overdue.
/// </summary>
public static class FormSchedule
{
    public const int DueSoonDays = 7;
    private const int MaxPeriods = 400;

    /// <summary>The length of a period as (months, days), or null for a periodicity that is not worked out from the calendar.</summary>
    public static (int Months, int Days)? PeriodOf(FormPeriodicity periodicity) => periodicity switch
    {
        FormPeriodicity.Daily => (0, 1),
        FormPeriodicity.Weekly => (0, 7),
        FormPeriodicity.Fortnight => (0, 14),
        FormPeriodicity.Monthly => (1, 0),
        FormPeriodicity.Quarterly => (3, 0),
        FormPeriodicity.HalfYearly => (6, 0),
        FormPeriodicity.Yearly => (12, 0),
        _ => null,
    };

    private static DateOnly Step(DateOnly start, (int Months, int Days) period, int count)
        => start.AddMonths(period.Months * count).AddDays(period.Days * count);

    public static FormDue Compute(FormPeriodicity periodicity, FormScheduleBasis basis, DateOnly? startsOn, IReadOnlyCollection<DateOnly> entryDates, DateOnly today)
    {
        if (PeriodOf(periodicity) is not { } period) return new FormDue(FormDueStatus.NotScheduled, null, null, 0);

        if (basis == FormScheduleBasis.FromPrevious)
        {
            var due = entryDates.Count == 0 ? startsOn ?? today : Step(entryDates.Max(), period, 1);
            return new FormDue(StatusFor(due, today), due, entryDates.Count == 0 ? startsOn : entryDates.Max(), 0);
        }

        var start = startsOn ?? today;
        if (today < start) return new FormDue(FormDueStatus.Open, Step(start, period, 1).AddDays(-1), start, 0);

        // The period that today falls in, and every earlier period that nobody filled.
        var current = 0;
        while (current < MaxPeriods && Step(start, period, current + 1) <= today) current++;
        var missed = 0;
        for (var k = Math.Max(0, current - MaxPeriods); k < current; k++)
            if (!entryDates.Any(d => d >= Step(start, period, k) && d < Step(start, period, k + 1))) missed++;

        var periodStart = Step(start, period, current);
        var dueOn = Step(start, period, current + 1).AddDays(-1);
        var done = entryDates.Any(d => d >= periodStart && d <= dueOn);
        if (done) return new FormDue(FormDueStatus.Done, dueOn, periodStart, missed);   // what was skipped earlier stays in the missed count, but this period is met
        // An unfilled current period is overdue only after its last day; earlier unfilled periods make it overdue already.
        return new FormDue(missed > 0 ? FormDueStatus.Overdue : StatusFor(dueOn, today), dueOn, periodStart, missed);
    }

    private static FormDueStatus StatusFor(DateOnly dueOn, DateOnly today)
        => today > dueOn ? FormDueStatus.Overdue : dueOn.DayNumber - today.DayNumber <= DueSoonDays ? FormDueStatus.DueSoon : FormDueStatus.Open;
}
