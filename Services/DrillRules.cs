using Chandlery.Models;

namespace Chandlery.Services;

/// <summary>How often a drill is due on a vessel: the type's rule, or the vessel's own where the office has changed it.</summary>
public sealed record DrillRule(DrillIntervalKind Kind, int Count, int WarningDays, bool EveryCrewMember);

public enum DrillStatus { NotDue, DueSoon, Due, Overdue, NeverHeld }

/// <summary>The crew who have not taken part in a drill of the ship in the previous month, against the quarter that triggers the "drills within 24 hours of leaving port" rule.</summary>
public sealed record DrillQuarter(int CrewCount, int NotTakenPart, bool Triggered)
{
    public int Percent => CrewCount == 0 ? 0 : (int)Math.Round(NotTakenPart * 100.0 / CrewCount);
}

/// <summary>
/// When a drill is due, and where each ship and each person stands, as pure functions of the drills held and the day, so a vessel and the office read the same schedule from the
/// same records. The intervals themselves are data on the drill types (the company's, with "to verify" on every default), never constants here.
/// </summary>
public static class DrillRules
{
    /// <summary>A person who joins is not late on the first day: they have this many days to take part in a drill of a type everyone must attend.</summary>
    public const int JoinerGraceDays = 14;

    /// <summary>Two drills of one type this close are shown as a possible duplicate (the same drill entered twice).</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromHours(1);

    public static DrillRule Of(DrillType type) => new(type.IntervalKind, Math.Max(1, type.IntervalCount), Math.Max(0, type.WarningDays), type.EveryCrewMember);

    /// <summary>The type's rule with the vessel's own interval where the office changed it for that vessel.</summary>
    public static DrillRule Effective(DrillType type, DrillOverride? vessel)
        => vessel is { IntervalKind: { } kind, IntervalCount: { } count and >= 1 } ? Of(type) with { Kind = kind, Count = count } : Of(type);

    public static DateTime EndOfMonth(DateTime date) => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1).AddDays(-1);

    /// <summary>
    /// The day by which the next drill is due: a number of days after the last one, or, counted in calendar months, the end of the month that many months after the month of
    /// the last drill (so a monthly drill held on 5 October is due by 30 November). Nothing is due until one has been held.
    /// </summary>
    public static DateTime? DueDate(DrillRule rule, DateTime? lastHeld)
    {
        if (lastHeld is not { } last) return null;
        return rule.Kind == DrillIntervalKind.Days ? last.Date.AddDays(rule.Count) : EndOfMonth(new DateTime(last.Year, last.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(rule.Count));
    }

    /// <summary>Not due, due soon (inside the warning days), due (the day itself), overdue (after it), or never held.</summary>
    public static DrillStatus StatusOf(DrillRule rule, DateTime? lastHeld, DateTime today)
        => DueDate(rule, lastHeld) is not { } due ? DrillStatus.NeverHeld : StatusOn(due, rule.WarningDays, today);

    private static DrillStatus StatusOn(DateTime due, int warningDays, DateTime today)
        => today.Date > due.Date ? DrillStatus.Overdue
         : today.Date == due.Date ? DrillStatus.Due
         : today.Date >= due.Date.AddDays(-warningDays) ? DrillStatus.DueSoon
         : DrillStatus.NotDue;

    /// <summary>
    /// A person's due date for a type everyone must attend: from their own last drill; or, for someone who has never taken part, from the day they signed on plus the joiner's
    /// grace. Null when they have no drill and no sign-on date to count from.
    /// </summary>
    public static DateTime? PersonDue(DrillRule rule, DateTime? lastTookPart, DateTime? signedOn)
        => lastTookPart is not null ? DueDate(rule, lastTookPart) : signedOn?.Date.AddDays(JoinerGraceDays);

    public static DrillStatus PersonStatus(DrillRule rule, DateTime? lastTookPart, DateTime? signedOn, DateTime today)
        => PersonDue(rule, lastTookPart, signedOn) is not { } due ? DrillStatus.NeverHeld : StatusOn(due, rule.WarningDays, today);

    /// <summary>The worst of several statuses, for a vessel's one-word standing. Overdue is worse than never held, which is worse than due.</summary>
    public static DrillStatus Worst(IEnumerable<DrillStatus> statuses)
    {
        var order = new[] { DrillStatus.Overdue, DrillStatus.NeverHeld, DrillStatus.Due, DrillStatus.DueSoon, DrillStatus.NotDue };
        var list = statuses.ToList();
        return order.FirstOrDefault(list.Contains, DrillStatus.NotDue);
    }

    /// <summary>
    /// More than a quarter of the crew have taken part in no drill of the ship in the previous calendar month: the drills are then held within 24 hours of leaving port. Worked out
    /// from the crew now aboard and who took part. <b>(To verify against the flag state; the number is the SOLAS III/19 one.)</b>
    /// </summary>
    public static DrillQuarter QuarterRule(IReadOnlyCollection<Guid> crewAboard, IEnumerable<Guid> tookPartLastMonth)
    {
        var took = tookPartLastMonth.ToHashSet();
        var not = crewAboard.Count(c => !took.Contains(c));
        return new DrillQuarter(crewAboard.Count, not, crewAboard.Count > 0 && not * 4 > crewAboard.Count);
    }

    /// <summary>The first and last day of the calendar month before <paramref name="today"/>.</summary>
    public static (DateTime From, DateTime To) PreviousMonth(DateTime today)
    {
        var first = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        return (first, EndOfMonth(first));
    }

    /// <summary>The same kind of drill started within the window of another one: very likely entered twice.</summary>
    public static bool PossibleDuplicate(DateTime a, DateTime b) => (a - b).Duration() <= DuplicateWindow;

    /// <summary>The checks on a drill's times: it cannot start in the future, end before it starts, or run for more than half a day.</summary>
    public static string? TimeProblem(DateTime startedAt, DateTime? endedAt, DateTime now)
    {
        if (startedAt > now.AddDays(1)) return "A drill cannot have started in the future.";
        if (endedAt is { } end)
        {
            if (end < startedAt) return "The drill cannot end before it starts.";
            if (end - startedAt > TimeSpan.FromHours(12)) return "A drill does not last more than 12 hours: check the times.";
        }
        return null;
    }
}
