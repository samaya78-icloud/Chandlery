using System.Security.Cryptography;
using System.Text;
using Chandlery.Models;

namespace Chandlery.Services;

// The rules of seafarers' hours of rest as pure functions: no database, no clock. The vessel and the office run the same code on the
// same records, so they always show the same result. Spec: docs/specs/11-safety-rest-hours.md (sections 2 and 4).

/// <summary>The numbers a rule set gives, in minutes.</summary>
public sealed record RestLimits(
    int MinRest24, int MinRest7, int MaxPeriods, int MinLongest, int MaxInterval,
    bool ReductionAllowed, int ReductionMinConsecutive, int ReductionMaxDays, int ReductionMin7,
    bool MinorNightProtection, int MinorNightStart, int MinorNightLength)
{
    /// <summary>
    /// ILO Convention 180 art. 5 and STCW A-VIII/1. The shorter rest of A-VIII/1.4 is off: it exists only where the flag state allows it,
    /// so a rule set has to switch it on deliberately.
    /// </summary>
    public static RestLimits Default { get; } = new(600, 4620, 2, 360, 840, false, 360, 2, 4200, true, 1260, 540);

    public static RestLimits Of(RestRuleSet r) => new(r.MinRest24Minutes, r.MinRest7Minutes, r.MaxRestPeriods, r.MinLongestPeriodMinutes, r.MaxIntervalMinutes,
        r.ReductionAllowed, r.ReductionMinConsecutiveMinutes, r.ReductionMaxDays, r.ReductionMin7Minutes, r.MinorNightProtection, r.MinorNightStartMinute, r.MinorNightLengthMinutes);
}

/// <summary>A period of work within one day, in minutes from midnight (0 to 1440).</summary>
public readonly record struct WorkSpan(int Start, int End);

/// <summary>What was recorded for one date. A date with no input is not recorded, which is not the same as a day of rest.</summary>
public sealed record RestDayInput(DateTime Date, IReadOnlyList<WorkSpan> Work);

public enum RestRule { Rest24, Rest7, Periods, LongestPeriod, Interval, ReductionLimit, MinorNight }

public enum RestDayState { NotRecorded, Ok, OkByReduction, Breach }

/// <summary>One rule not met. <c>Actual</c> and <c>Limit</c> are minutes (or counts, for the number of periods and reduced days).</summary>
public sealed record RestFinding(RestRule Rule, DateTime Date, int Actual, int Limit, DateTime? WindowEnd, string Message);

/// <summary>
/// The result for one date. <c>Worst24</c> is the least rest found in any 24 hours ending on that date; <c>Worst7</c> the least in any 7 days ending on it
/// (null when the days they need are not all recorded). <c>WorkMinutes</c> and <c>RestMinutes</c> are for the calendar day.
/// </summary>
public sealed record RestDayResult(DateTime Date, RestDayState State, int WorkMinutes, int RestMinutes, int? Worst24, int? Worst7, bool ReducedDay, IReadOnlyList<RestFinding> Findings);

/// <summary>Work periods as text ("00:00-04:00,16:00-20:00"), checked and tidied.</summary>
public static class RestSpans
{
    public const int Resolution = 15;
    public const int DayMinutes = 1440;

    public static List<WorkSpan> Parse(string? text)
    {
        var spans = new List<WorkSpan>();
        if (string.IsNullOrWhiteSpace(text)) return spans;
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            if (ends.Length != 2) throw new FormatException($"'{part}' is not a period; write it like 06:00-12:00.");
            spans.Add(new WorkSpan(Time(ends[0]), Time(ends[1])));
        }
        return spans;
    }

    private static int Time(string text)
    {
        var t = text.Replace(":", "");
        if (t.Length is < 3 or > 4 || !int.TryParse(t, out var n)) throw new FormatException($"'{text}' is not a time; write it like 06:30.");
        var (h, m) = (n / 100, n % 100);
        if (m > 59 || h > 24 || (h == 24 && m != 0)) throw new FormatException($"'{text}' is not a time of day.");
        return h * 60 + m;
    }

    public static string Format(IEnumerable<WorkSpan> spans)
        => string.Join(",", spans.Select(s => $"{Clock(s.Start)}-{Clock(s.End)}"));

    private static string Clock(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    /// <summary>Sorted, with overlapping or touching periods joined.</summary>
    public static List<WorkSpan> Normalize(IEnumerable<WorkSpan> spans)
    {
        var result = new List<WorkSpan>();
        foreach (var s in spans.OrderBy(x => x.Start).ThenBy(x => x.End))
        {
            if (result.Count > 0 && s.Start <= result[^1].End) result[^1] = new WorkSpan(result[^1].Start, Math.Max(result[^1].End, s.End));
            else result.Add(s);
        }
        return result;
    }

    /// <summary>Null when the periods are acceptable, otherwise what is wrong.</summary>
    public static string? Validate(IEnumerable<WorkSpan> spans)
    {
        var list = spans.OrderBy(s => s.Start).ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.Start < 0 || s.End > DayMinutes || s.End <= s.Start) return $"{Format([s])} is not a period within one day. A period over midnight is entered on each of the two days.";
            if (s.Start % Resolution != 0 || s.End % Resolution != 0) return $"{Format([s])}: times must be on the quarter hour.";
            if (i > 0 && s.Start < list[i - 1].End) return $"{Format([list[i - 1]])} and {Format([s])} overlap.";
        }
        return null;
    }

    public static int Minutes(IEnumerable<WorkSpan> spans) => spans.Sum(s => s.End - s.Start);
}

public static class RestRules
{
    public const int SlotMinutes = RestSpans.Resolution;
    private const int SlotsPerDay = RestSpans.DayMinutes / SlotMinutes;       // 96
    private const int Slots7 = 7 * SlotsPerDay;                               // 672

    private sealed record Run(int Start, int End, bool StartKnown, bool EndKnown, int BlockStart);

    /// <summary>
    /// Checks every date from <paramref name="from"/> to <paramref name="to"/>. Pass the days recorded from a week before <paramref name="from"/>, so the
    /// 7-day rule has what it needs. The rules, as this engine reads them:
    /// <list type="bullet">
    /// <item>Rest in any 24 hours, and in any 7 days, is tested on every window ending that day (rolling, not only calendar days). A window that includes a day that was not recorded is not tested.</item>
    /// <item>A rest period belongs to the day it starts on, and may run over midnight. The number of periods and the gap before a period are tested for the day it starts; the longest period for every day it covers.</item>
    /// <item>The shorter rest of STCW A-VIII/1.4 counts as a reduced day when the worst 24 hours still hold the minimum consecutive rest; no more reduced days than allowed in any 7 days, and the 7-day rest then falls to the lower figure.</item>
    /// <item>For a seafarer under 18 no work is allowed in the protected night hours.</item>
    /// </list>
    /// </summary>
    public static List<RestDayResult> Evaluate(RestLimits rules, IEnumerable<RestDayInput> days, DateTime from, DateTime to, Func<DateTime, bool>? isMinor = null)
    {
        var first = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var last = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc);
        if (last < first) return [];
        var origin = first.AddDays(-7);
        var count = (last - origin).Days + 1;

        // ---- the timeline: one slot per quarter hour, true where the seafarer worked ----
        var work = new bool[count * SlotsPerDay];
        var recorded = new bool[count];
        foreach (var day in days)
        {
            var i = (day.Date.Date - origin).Days;
            if (i < 0 || i >= count) continue;
            recorded[i] = true;
            Array.Clear(work, i * SlotsPerDay, SlotsPerDay);
            foreach (var span in RestSpans.Normalize(day.Work))
            {
                if (RestSpans.Validate([span]) is { } problem) throw new ArgumentException($"{day.Date:yyyy-MM-dd}: {problem}");
                for (var s = span.Start / SlotMinutes; s < span.End / SlotMinutes; s++) work[i * SlotsPerDay + s] = true;
            }
        }

        var prefixWork = new int[work.Length + 1];
        for (var s = 0; s < work.Length; s++) prefixWork[s + 1] = prefixWork[s] + (work[s] ? 1 : 0);
        var missing = new int[count + 1];
        for (var d = 0; d < count; d++) missing[d + 1] = missing[d] + (recorded[d] ? 0 : 1);
        bool Known(int a, int b) => missing[(b - 1) / SlotsPerDay + 1] - missing[a / SlotsPerDay] == 0;      // every day the window touches is recorded

        // ---- the rest periods, within each stretch of recorded days ----
        var runs = new List<Run>();
        for (var d = 0; d < count;)
        {
            if (!recorded[d]) { d++; continue; }
            var end = d;
            while (end < count && recorded[end]) end++;
            int blockStart = d * SlotsPerDay, blockEnd = end * SlotsPerDay;
            for (var s = blockStart; s < blockEnd;)
            {
                if (work[s]) { s++; continue; }
                var e = s;
                while (e < blockEnd && !work[e]) e++;
                runs.Add(new Run(s, e, s > blockStart, e < blockEnd, blockStart));
                s = e;
            }
            d = end;
        }

        // ---- the worst window of each day, and whether the day needed the reduction ----
        var worst24 = new int?[count]; var worst24End = new int[count]; var reduced = new bool[count];
        var worst7 = new int?[count]; var worst7End = new int[count];
        for (var d = 0; d < count; d++)
        {
            if (!recorded[d]) continue;
            // Several windows can share the least rest; the one that holds the rest in the fewest pieces is the one that shows whether a long unbroken rest was kept
            // (a window that cuts a night's rest in two would hide it).
            int bestRest = int.MaxValue, bestEnd = -1, bestLongest = 0;
            for (var e = d * SlotsPerDay + 1; e <= (d + 1) * SlotsPerDay; e++)
            {
                var a = e - SlotsPerDay;
                if (a < 0 || !Known(a, e)) continue;
                var rest = SlotsPerDay - (prefixWork[e] - prefixWork[a]);
                if (rest < bestRest) { (bestRest, bestEnd) = (rest, e); bestLongest = LongestRest(work, a, e); }
                else if (rest == bestRest) bestLongest = Math.Max(bestLongest, LongestRest(work, a, e));
            }
            if (bestEnd >= 0)
            {
                worst24[d] = bestRest * SlotMinutes; worst24End[d] = bestEnd;
                reduced[d] = rules.ReductionAllowed && worst24[d] < rules.MinRest24 && bestLongest * SlotMinutes >= rules.ReductionMinConsecutive;
            }

            bestRest = int.MaxValue; bestEnd = -1;
            for (var e = d * SlotsPerDay + 1; e <= (d + 1) * SlotsPerDay; e++)
            {
                var a = e - Slots7;
                if (a < 0 || !Known(a, e)) continue;
                var rest = Slots7 - (prefixWork[e] - prefixWork[a]);
                if (rest < bestRest) (bestRest, bestEnd) = (rest, e);
            }
            if (bestEnd >= 0) { worst7[d] = bestRest * SlotMinutes; worst7End[d] = bestEnd; }
        }
        int ReducedIn(int d) { var n = 0; for (var k = Math.Max(0, d - 6); k <= d; k++) if (reduced[k]) n++; return n; }
        DateTime At(int slot) => origin.AddMinutes((long)slot * SlotMinutes);

        // ---- the findings for each date asked for ----
        var results = new List<RestDayResult>();
        for (var d = (first - origin).Days; d < count; d++)
        {
            var date = origin.AddDays(d);
            if (!recorded[d]) { results.Add(new RestDayResult(date, RestDayState.NotRecorded, 0, 0, null, null, false, [])); continue; }

            var findings = new List<RestFinding>();
            var usedReduction = false;
            int dayStart = d * SlotsPerDay, dayEnd = dayStart + SlotsPerDay;
            var workMinutes = (prefixWork[dayEnd] - prefixWork[dayStart]) * SlotMinutes;

            if (worst24[d] is { } w24 && w24 < rules.MinRest24)
            {
                if (reduced[d])
                {
                    var n = ReducedIn(d);
                    if (n > rules.ReductionMaxDays)
                        findings.Add(new RestFinding(RestRule.ReductionLimit, date, n, rules.ReductionMaxDays, At(worst24End[d]),
                            $"The shorter rest was used on {n} days in 7; at most {rules.ReductionMaxDays} are allowed."));
                    else usedReduction = true;
                }
                else
                    findings.Add(new RestFinding(RestRule.Rest24, date, w24, rules.MinRest24, At(worst24End[d]),
                        $"Rest in the 24 hours ending {At(worst24End[d]):dd MMM HH:mm} was {Hm(w24)}; the minimum is {Hm(rules.MinRest24)}."));
            }

            if (worst7[d] is { } w7)
            {
                var n = ReducedIn(d);
                var required = n > 0 && n <= rules.ReductionMaxDays ? rules.ReductionMin7 : rules.MinRest7;
                if (w7 < required)
                    findings.Add(new RestFinding(RestRule.Rest7, date, w7, required, At(worst7End[d]),
                        $"Rest in the 7 days ending {At(worst7End[d]):dd MMM HH:mm} was {Hm(w7)}; the minimum is {Hm(required)}."));
                else if (w7 < rules.MinRest7) usedReduction = true;
            }

            var starting = runs.Where(r => r.StartKnown && r.Start >= dayStart && r.Start < dayEnd).ToList();
            if (starting.Count > rules.MaxPeriods)
                findings.Add(new RestFinding(RestRule.Periods, date, starting.Count, rules.MaxPeriods, null,
                    $"Rest was taken in {starting.Count} periods; at most {rules.MaxPeriods} are allowed."));

            var covering = runs.Where(r => r.Start < dayEnd && r.End > dayStart).ToList();
            if (covering.Count > 0 && covering.All(r => r.StartKnown && r.EndKnown))
            {
                var longest = covering.Max(r => r.End - r.Start) * SlotMinutes;
                if (longest < rules.MinLongest)
                    findings.Add(new RestFinding(RestRule.LongestPeriod, date, longest, rules.MinLongest, null,
                        $"The longest rest period was {Hm(longest)}; one must be at least {Hm(rules.MinLongest)}."));
            }

            foreach (var run in starting)
            {
                var previous = runs.LastOrDefault(r => r.BlockStart == run.BlockStart && r.End <= run.Start && !ReferenceEquals(r, run));
                var stretch = (run.Start - (previous?.End ?? run.BlockStart)) * SlotMinutes;
                // On a day that uses the shorter rest, the gap before that rest is of course longer than 14 hours: that is what the reduction allows.
                var allowedByReduction = reduced[d] && stretch <= RestSpans.DayMinutes - rules.ReductionMinConsecutive;
                if (stretch > rules.MaxInterval && !allowedByReduction)
                    findings.Add(new RestFinding(RestRule.Interval, date, stretch, rules.MaxInterval, At(run.Start),
                        $"The time between rest periods before {At(run.Start):dd MMM HH:mm} was {Hm(stretch)}; the most allowed is {Hm(rules.MaxInterval)}."));
            }

            if (rules.MinorNightProtection && isMinor?.Invoke(date) == true)
            {
                var night = 0;
                for (var s = 0; s < SlotsPerDay; s++)
                    if (work[dayStart + s] && InNight(s * SlotMinutes, rules)) night += SlotMinutes;
                if (night > 0)
                    findings.Add(new RestFinding(RestRule.MinorNight, date, night, 0, null, $"A seafarer under 18 worked {Hm(night)} in the protected night hours."));
            }

            var state = findings.Count > 0 ? RestDayState.Breach : usedReduction || reduced[d] ? RestDayState.OkByReduction : RestDayState.Ok;
            results.Add(new RestDayResult(date, state, workMinutes, RestSpans.DayMinutes - workMinutes, worst24[d], worst7[d], reduced[d], findings));
        }
        return results;
    }

    private static bool InNight(int minuteOfDay, RestLimits r)
    {
        var end = r.MinorNightStart + r.MinorNightLength;
        return minuteOfDay >= r.MinorNightStart || (end > RestSpans.DayMinutes && minuteOfDay < end - RestSpans.DayMinutes);
    }

    private static int LongestRest(bool[] work, int from, int to)
    {
        int best = 0, run = 0;
        for (var s = from; s < to; s++) { run = work[s] ? 0 : run + 1; best = Math.Max(best, run); }
        return best;
    }

    /// <summary>"10h", "8h 30m".</summary>
    public static string Hm(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60}m";

    /// <summary>
    /// A short fingerprint of the days from <paramref name="from"/> to <paramref name="to"/> as recorded. A signature keeps it; if a day is revised
    /// afterwards the fingerprint differs and the signature is out of date. A date not recorded counts as such, so filling a gap changes it too.
    /// </summary>
    public static string Fingerprint(IEnumerable<RestDayInput> days, DateTime from, DateTime to)
    {
        var byDate = new Dictionary<DateTime, string>();
        foreach (var day in days) byDate[day.Date.Date] = RestSpans.Format(RestSpans.Normalize(day.Work));
        var text = new StringBuilder();
        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
            text.Append(date.ToString("yyyy-MM-dd")).Append('|').Append(byDate.TryGetValue(date, out var spans) ? "=" + spans : "-").Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }
}
