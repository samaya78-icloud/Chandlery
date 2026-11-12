using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

// Sign-off and breaches for the hours of rest (spec 11, S3).
//
// Two layers, like the rest of the module. RestReview is pure: given what was recorded, signed and said, it works out which breaches exist and where each
// stands, and which signatures still match the days they were given for. RestReviewService reads the records, checks who may do what and appends the new
// record. Nothing here edits or deletes anything.

/// <summary>Where a breach stands: unexplained, explained and waiting for the Master, acknowledged, excepted by the Master (emergency, drill...), or acknowledged/excepted but the days were changed afterwards.</summary>
public enum BreachStatus { Unexplained, Explained, Stale, Acknowledged, Excepted }

/// <summary>One rule not met on one date, with everything said about it so far.</summary>
public sealed record BreachView(Guid CrewId, string CrewName, string Rank, RestFinding Finding, BreachStatus Status,
    RestBreachNote? Explanation, RestBreachNote? Decision, RestBreachNote? Compensatory, int OwedMinutes, IReadOnlyList<RestBreachNote> Notes)
{
    /// <summary>The Master has dealt with it and nothing has changed since.</summary>
    public bool Resolved => Status is BreachStatus.Acknowledged or BreachStatus.Excepted;

    /// <summary>An exception was declared and the Master has not yet confirmed the compensatory rest was given.</summary>
    public bool CompensatoryOutstanding => Status == BreachStatus.Excepted && Compensatory is null;

    public string Key => RestReview.KeyOf(Finding.Date, Finding.Rule);
}

public sealed record SignView(RestSignOff Sign, bool Current);

/// <summary>One signing period of a seafarer's month: what was recorded, and who has signed it and whether the signature still matches.</summary>
public sealed record RestPeriod(DateTime From, DateTime To, int Days, int Recorded, List<DateTime> Gaps, bool Ended, SignView? Seafarer, SignView? Master, int Breaches, int BreachesUnresolved)
{
    public bool SeafarerSigned => Seafarer is { Current: true };
    public bool MasterSigned => Master is { Current: true };
    public bool Complete => SeafarerSigned && MasterSigned;

    /// <summary>A signature was given and the days have changed since.</summary>
    public bool OutOfDate => Seafarer is { Current: false } || Master is { Current: false };
}

/// <summary>What a seafarer's records were as far as this module can tell: the entries (all revisions), what was signed and what was said. Everything else is worked out from these.</summary>
public sealed record CrewBook(Guid CrewId, string Name, string Rank, DateTime? BornOn, DateTime? FirstRecorded, List<RestEntry> Entries, List<RestBreachNote> Notes, List<RestSignOff> Signs)
{
    public List<RestDayInput> Inputs => RestHoursService.Inputs(Entries);

    public Func<DateTime, bool>? IsMinor => BornOn is { } dob ? date => dob.Date.AddYears(18) > date : null;
}

public static class RestReview
{
    public static string KeyOf(DateTime date, RestRule rule) => $"{date:yyyy-MM-dd}|{rule}";

    private static DateTime Utc(DateTime date) => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

    // ---- signing periods ----

    /// <summary>The signing periods of a month: consecutive runs of <paramref name="intervalDays"/> days from the 1st (the last one shorter). With a long interval the month is one period.</summary>
    public static List<(DateTime From, DateTime To)> Periods(int year, int month, int intervalDays)
    {
        var step = Math.Clamp(intervalDays, 1, 31);
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = first.AddMonths(1).AddDays(-1);
        var list = new List<(DateTime, DateTime)>();
        for (var from = first; from <= last; from = from.AddDays(step))
        {
            var to = from.AddDays(step - 1);
            list.Add((from, to > last ? last : to));
        }
        return list;
    }

    /// <summary>Does the signature still describe the days it was given for? False once any of them was revised, or a gap in them filled.</summary>
    public static bool IsCurrent(RestSignOff sign, IEnumerable<RestDayInput> days) => sign.Fingerprint == RestRules.Fingerprint(days, sign.FromDate, sign.ToDate);

    /// <summary>The latest signature of a role that covers the whole period.</summary>
    public static RestSignOff? LatestCovering(IEnumerable<RestSignOff> signs, RestRole role, DateTime from, DateTime to)
        => signs.Where(s => s.Role == role && s.FromDate.Date <= from.Date && s.ToDate.Date >= to.Date).OrderBy(s => s.SignedAt).ThenBy(s => s.Id).LastOrDefault();

    /// <summary>The days of the period that have to be recorded before it can be signed: all of them, except those before the seafarer's first record (they had not joined).</summary>
    public static List<DateTime> Gaps(DateTime from, DateTime to, DateTime? firstRecorded, IEnumerable<RestDayInput> days, DateTime today)
    {
        var have = days.Select(d => d.Date.Date).ToHashSet();
        var start = firstRecorded is { } first && first.Date > from.Date ? first.Date : from.Date;
        var end = to.Date > today.Date ? today.Date : to.Date;
        var gaps = new List<DateTime>();
        for (var d = start; d <= end; d = d.AddDays(1))
            if (!have.Contains(d)) gaps.Add(Utc(d));
        return gaps;
    }

    public static RestPeriod PeriodOf(CrewBook book, DateTime from, DateTime to, DateTime today, IReadOnlyList<BreachView> breaches)
    {
        var inputs = book.Inputs;
        var days = (to.Date - from.Date).Days + 1;
        var recorded = inputs.Count(d => d.Date.Date >= from.Date && d.Date.Date <= to.Date);
        SignView? View(RestRole role) => LatestCovering(book.Signs, role, from, to) is { } s ? new SignView(s, IsCurrent(s, inputs)) : null;
        var inPeriod = breaches.Where(b => b.Finding.Date.Date >= from.Date && b.Finding.Date.Date <= to.Date).ToList();
        return new RestPeriod(Utc(from), Utc(to), days, recorded, Gaps(from, to, book.FirstRecorded, inputs, today), to.Date <= today.Date, View(RestRole.Seafarer), View(RestRole.Master),
            inPeriod.Count, inPeriod.Count(b => !b.Resolved));
    }

    // ---- breaches ----

    /// <summary>
    /// Every rule not met in <paramref name="days"/>, with what has been said about it. A Master's acknowledgement or declared exception counts only while the
    /// days it was about are unchanged: if one is revised afterwards, the breach goes back to the Master as <see cref="BreachStatus.Stale"/>.
    /// </summary>
    public static List<BreachView> Breaches(CrewBook book, IEnumerable<RestDayResult> days)
    {
        var list = new List<BreachView>();
        foreach (var finding in days.SelectMany(d => d.Findings).OrderBy(f => f.Date).ThenBy(f => f.Rule))
        {
            var notes = book.Notes.Where(n => n.Date.Date == finding.Date.Date && n.Rule == finding.Rule.ToString()).OrderBy(n => n.At).ThenBy(n => n.Id).ToList();
            var explanation = notes.LastOrDefault(n => n.Kind == RestNoteKind.Explanation);
            var decision = notes.LastOrDefault(n => n.Kind is RestNoteKind.MasterAcknowledged or RestNoteKind.Exception);
            if (decision is { Kind: RestNoteKind.Exception, ExceptionKind: RestExceptionKind.None }) decision = null;      // the exception was withdrawn
            var compensatory = notes.LastOrDefault(n => n.Kind == RestNoteKind.CompensatoryRestGiven);

            BreachStatus status;
            if (decision is not null)
                status = ChangedSince(book.Entries, finding, decision.At) ? BreachStatus.Stale : decision.Kind == RestNoteKind.Exception ? BreachStatus.Excepted : BreachStatus.Acknowledged;
            else status = explanation is null ? BreachStatus.Unexplained : BreachStatus.Explained;

            if (compensatory is not null && decision is not null && compensatory.At < decision.At) compensatory = null;      // a new decision starts the question again
            var owed = finding.Rule is RestRule.Rest24 or RestRule.Rest7 ? Math.Max(0, finding.Limit - finding.Actual) : 0;
            list.Add(new BreachView(book.CrewId, book.Name, book.Rank, finding, status, explanation, decision, compensatory, owed, notes));
        }
        return list;
    }

    /// <summary>Was a day the finding rests on revised after <paramref name="since"/>? A 7-day finding rests on the week before it, the others on the day and the one before.</summary>
    public static bool ChangedSince(IEnumerable<RestEntry> entries, RestFinding finding, DateTime since)
    {
        var back = finding.Rule == RestRule.Rest7 ? 7 : 1;
        return entries.Any(e => e.EnteredAt > since && e.Date.Date >= finding.Date.Date.AddDays(-back) && e.Date.Date <= finding.Date.Date);
    }

    /// <summary>The Master's signature that still holds for the day, if any: while it does, only the Master may change that day.</summary>
    public static async Task<RestSignOff?> MasterLockAsync(ChandleryDbContext db, Guid crewMemberId, DateTime date)
    {
        var day = Utc(date);
        var signs = await db.RestSignOffs.AsNoTracking().Where(s => s.CrewMemberId == crewMemberId && s.Role == RestRole.Master && s.FromDate <= day && s.ToDate >= day).ToListAsync();
        foreach (var sign in signs.OrderByDescending(s => s.SignedAt))
        {
            var entries = await db.RestEntries.AsNoTracking().Where(e => e.CrewMemberId == crewMemberId && e.Date >= sign.FromDate && e.Date <= sign.ToDate).ToListAsync();
            if (IsCurrent(sign, RestHoursService.Inputs(entries))) return sign;
        }
        return null;
    }
}

/// <summary>What the signed-in seafarer (or the Master) has waiting, for the vessel's home page.</summary>
public sealed record RestAttention(bool IsMaster, int MissingDays, int ToSign, int MyUnexplained, int ToAcknowledge, int ToEndorse, bool IsDeputy = false)
{
    public bool Any => MissingDays + ToSign + MyUnexplained + ToAcknowledge + ToEndorse > 0;
}

/// <summary>The result of adding a note to a breach; <c>Task</c> is the follow-up raised in the worklist, <c>TaskProblem</c> says why none was.</summary>
public sealed record BreachNoteResult(RestBreachNote Note, WorklistTask? Task, string? TaskProblem);

public sealed record RestMonthReview(DateTime First, DateTime Last, string CrewName, string Rank, RestLimits Limits, int SignIntervalDays, List<RestPeriod> Periods, List<BreachView> Breaches);

/// <summary>Signing the hours and dealing with breaches. Onboard only: the office reads.</summary>
public class RestReviewService(IDbContextFactory<ChandleryDbContext> factory, RestHoursService rest, WorklistService worklist)
{
    // ---- loading ----

    /// <summary>The records of the seafarers of a vessel over a span of dates (with the week before it, which the 7-day rule needs), as books.</summary>
    internal static async Task<List<CrewBook>> BooksAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, DateTime from, DateTime to, Guid? onlyCrew = null)
    {
        var start = DateTime.SpecifyKind(from.Date.AddDays(-8), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc);
        var f = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);

        var entries = db.RestEntries.AsNoTracking().Where(e => e.VesselId == vesselId && e.Date >= start && e.Date <= end);
        var notes = db.RestBreachNotes.AsNoTracking().Where(n => n.VesselId == vesselId && n.Date >= f && n.Date <= end);
        var signs = db.RestSignOffs.AsNoTracking().Where(s => s.VesselId == vesselId && s.FromDate <= end && s.ToDate >= f);
        var crewQuery = db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive);
        if (onlyCrew is { } one)
        {
            entries = entries.Where(e => e.CrewMemberId == one);
            notes = notes.Where(n => n.CrewMemberId == one);
            signs = signs.Where(s => s.CrewMemberId == one);
            crewQuery = db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.Id == one);
        }
        var entryList = await entries.ToListAsync();
        var noteList = await notes.ToListAsync();
        var signList = await signs.ToListAsync();
        var crew = await crewQuery.ToListAsync();
        var ids = crew.Select(c => c.Id).Concat(entryList.Select(e => e.CrewMemberId)).Distinct().ToList();
        var firsts = await db.RestEntries.AsNoTracking().Where(e => ids.Contains(e.CrewMemberId)).GroupBy(e => e.CrewMemberId).Select(g => new { Id = g.Key, First = g.Min(e => e.Date) }).ToDictionaryAsync(x => x.Id, x => x.First);

        var books = new List<CrewBook>();
        foreach (var id in ids)
        {
            var member = crew.FirstOrDefault(c => c.Id == id);
            var mine = entryList.Where(e => e.CrewMemberId == id).ToList();
            var sample = mine.OrderBy(e => e.EnteredAt).LastOrDefault();
            books.Add(new CrewBook(id, member?.FullName ?? sample?.CrewName ?? "", member?.Rank ?? sample?.Rank ?? "", member?.DateOfBirth, firsts.TryGetValue(id, out var first) ? first : null,
                mine, noteList.Where(n => n.CrewMemberId == id).ToList(), signList.Where(s => s.CrewMemberId == id).ToList()));
        }
        return books.OrderBy(b => b.Name).ToList();
    }

    internal static List<RestDayResult> DaysOf(CrewBook book, RestLimits limits, DateTime from, DateTime to) => RestRules.Evaluate(limits, book.Inputs, from, to, book.IsMinor);

    private async Task<(ChandleryDbContext Db, CrewMember Me)> ActingAsync(Guid companyId, Guid vesselId, Actor actor)
    {
        var me = await rest.CrewOfUserAsync(companyId, actor.UserId) ?? throw new InvalidOperationException("This is done onboard, by the seafarers of the vessel.");
        if (me.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await RestHoursService.IsEnabledForVesselAsync(db, companyId, vesselId)) { await db.DisposeAsync(); throw new InvalidOperationException("Rest hours are not switched on for this vessel."); }
        return (db, me);
    }

    // ---- reading ----

    /// <summary>A seafarer's month for signing: its signing periods, and the breaches in it.</summary>
    public async Task<RestMonthReview> MonthAsync(Guid companyId, Guid vesselId, Guid crewMemberId, int year, int month)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last = first.AddMonths(1).AddDays(-1);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (set, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        var book = (await BooksAsync(db, companyId, vesselId, first, last, crewMemberId)).FirstOrDefault()
            ?? new CrewBook(crewMemberId, "", "", null, null, [], [], []);
        var breaches = RestReview.Breaches(book, DaysOf(book, limits, first, last));
        var interval = set?.SignIntervalDays ?? 7;
        var periods = RestReview.Periods(year, month, interval).Select(p => RestReview.PeriodOf(book, p.From, p.To, DateTime.UtcNow, breaches)).ToList();
        return new RestMonthReview(first, last, book.Name, book.Rank, limits, interval, periods, breaches);
    }

    /// <summary>The breaches of a vessel's seafarers (or one of them) between two dates, oldest first.</summary>
    public async Task<List<BreachView>> BreachesAsync(Guid companyId, Guid vesselId, DateTime from, DateTime to, Guid? crewMemberId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (_, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        var result = new List<BreachView>();
        foreach (var book in await BooksAsync(db, companyId, vesselId, from, to, crewMemberId))
            result.AddRange(RestReview.Breaches(book, DaysOf(book, limits, from, to)));
        return result.OrderBy(b => b.Finding.Date).ThenBy(b => b.CrewName).ThenBy(b => b.Finding.Rule).ToList();
    }

    public async Task<RestBreachNote?> NoteAsync(Guid companyId, Guid noteId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.RestBreachNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId && n.CompanyId == companyId);
    }

    // ---- signing ----

    /// <summary>
    /// Signs the hours of a signing period as the signed-in person. A seafarer signs only their own; the Master then signs as Master (and, being a seafarer too,
    /// their own hours first). The Master can sign only when the seafarer's signature still matches and every breach in the period has been dealt with.
    /// A signature remembers the days it was given for, and shows as out of date as soon as one of them is revised.
    /// </summary>
    public async Task<RestSignOff> SignAsync(Guid companyId, Guid vesselId, Actor actor, Guid crewMemberId, DateTime from, DateTime to, RestRole role)
    {
        var (db, me) = await ActingAsync(companyId, vesselId, actor);
        await using var owned = db;
        var (f, t) = (DateTime.SpecifyKind(from.Date, DateTimeKind.Utc), DateTime.SpecifyKind(to.Date, DateTimeKind.Utc));
        var (set, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        if (!RestReview.Periods(f.Year, f.Month, set?.SignIntervalDays ?? 7).Any(p => p.From == f && p.To == t))
            throw new InvalidOperationException("Hours are signed for a whole signing period, as the sheet shows them.");
        if (t > DateTime.UtcNow.Date) throw new InvalidOperationException("A period can be signed once its last day has begun.");

        if (role == RestRole.Seafarer && crewMemberId != me.Id) throw new InvalidOperationException("Only the seafarer signs their own hours.");
        if (role == RestRole.Master && !me.ActsForMaster) throw new InvalidOperationException("Only the Master, or a deputy the office has named, signs as Master.");
        if (role == RestRole.Master && !me.IsMaster && crewMemberId == me.Id) throw new InvalidOperationException("A deputy cannot sign their own hours as Master. The Master or another deputy does.");

        var book = (await BooksAsync(db, companyId, vesselId, f, t, crewMemberId)).FirstOrDefault(b => b.CrewId == crewMemberId)
            ?? throw new InvalidOperationException("That seafarer was not found.");
        if (book.Entries.All(e => e.Date < f || e.Date > t)) throw new InvalidOperationException("Nothing is recorded in this period.");
        var gaps = RestReview.Gaps(f, t, book.FirstRecorded, book.Inputs, DateTime.UtcNow);
        if (gaps.Count > 0)
            throw new InvalidOperationException($"Record every day first. Not recorded: {string.Join(", ", gaps.Take(4).Select(g => g.ToString("d MMM")))}{(gaps.Count > 4 ? $" and {gaps.Count - 4} more" : "")}.");

        var inputs = book.Inputs;
        if (RestReview.LatestCovering(book.Signs, role, f, t) is { } existing && RestReview.IsCurrent(existing, inputs)) throw new InvalidOperationException("This period is already signed, and nothing has changed since.");

        if (role == RestRole.Master)
        {
            var seafarer = RestReview.LatestCovering(book.Signs, RestRole.Seafarer, f, t);
            if (seafarer is null || !RestReview.IsCurrent(seafarer, inputs))
                throw new InvalidOperationException(seafarer is null ? $"{book.Name} has not signed this period yet." : $"{book.Name}'s signature is out of date: the days were changed after it. They sign again first.");
            var open = RestReview.Breaches(book, RestRules.Evaluate(limits, inputs, f, t, book.IsMinor)).Where(b => !b.Resolved).ToList();
            if (open.Count > 0)
                throw new InvalidOperationException($"{open.Count} breach(es) in this period still need an explanation and your acknowledgement: {string.Join(", ", open.Take(3).Select(b => b.Finding.Date.ToString("d MMM")))}{(open.Count > 3 ? "…" : "")}.");
        }

        var sign = new RestSignOff
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CrewMemberId = crewMemberId, CrewName = book.Name, FromDate = f, ToDate = t, Role = role,
            Fingerprint = RestRules.Fingerprint(inputs, f, t), SignedByName = me.FullName, SignedByCrewId = me.Id, SignedAt = DateTime.UtcNow,
            AsDeputy = role == RestRole.Master && !me.IsMaster,
        };
        db.RestSignOffs.Add(sign);
        await db.SaveChangesAsync();
        return sign;
    }

    // ---- breaches ----

    /// <summary>The seafarer's (or, for them, the Master's or a crew admin's) account of a breach. It can be given again; the latest counts and the earlier ones stay.</summary>
    public Task<BreachNoteResult> ExplainAsync(Guid companyId, Guid vesselId, Actor actor, Guid crewMemberId, DateTime date, RestRule rule, string text)
        => AddNoteAsync(companyId, vesselId, actor, crewMemberId, date, rule, RestNoteKind.Explanation, RestExceptionKind.None, text);

    /// <summary>The Master's acknowledgement of an explained breach. Raises a follow-up in the worklist, once.</summary>
    public Task<BreachNoteResult> AcknowledgeAsync(Guid companyId, Guid vesselId, Actor actor, Guid crewMemberId, DateTime date, RestRule rule, string text = "")
        => AddNoteAsync(companyId, vesselId, actor, crewMemberId, date, rule, RestNoteKind.MasterAcknowledged, RestExceptionKind.None, text);

    /// <summary>The Master declares the breach an exception (emergency, drill or overriding operational need, art. 7 / A-VIII/1.3), with the reason. Compensatory rest is then owed. <c>None</c> withdraws it.</summary>
    public Task<BreachNoteResult> DeclareExceptionAsync(Guid companyId, Guid vesselId, Actor actor, Guid crewMemberId, DateTime date, RestRule rule, RestExceptionKind kind, string reason)
        => AddNoteAsync(companyId, vesselId, actor, crewMemberId, date, rule, RestNoteKind.Exception, kind, reason);

    /// <summary>The Master confirms the compensatory rest owed for an exception was given.</summary>
    public Task<BreachNoteResult> ConfirmCompensatoryAsync(Guid companyId, Guid vesselId, Actor actor, Guid crewMemberId, DateTime date, RestRule rule, string text)
        => AddNoteAsync(companyId, vesselId, actor, crewMemberId, date, rule, RestNoteKind.CompensatoryRestGiven, RestExceptionKind.None, text);

    private async Task<BreachNoteResult> AddNoteAsync(Guid companyId, Guid vesselId, Actor actor, Guid crewMemberId, DateTime date, RestRule rule, RestNoteKind kind, RestExceptionKind exception, string text)
    {
        text = (text ?? "").Trim();
        var (db, me) = await ActingAsync(companyId, vesselId, actor);
        await using var owned = db;
        switch (kind)
        {
            case RestNoteKind.Explanation:
                if (crewMemberId != me.Id && !me.IsMaster && !me.IsCrewAdmin) throw new InvalidOperationException("You can explain your own breaches. The Master or a crew admin can do it for someone else.");
                if (text.Length < 3) throw new InvalidOperationException("Say what happened.");
                break;
            case RestNoteKind.MasterAcknowledged:
                if (!me.ActsForMaster) throw new InvalidOperationException("Only the Master, or a deputy the office has named, does this.");
                if (!me.IsMaster && crewMemberId == me.Id) throw new InvalidOperationException("A deputy cannot acknowledge their own breaches. The Master or another deputy does.");
                break;
            default:
                if (!me.IsMaster) throw new InvalidOperationException("Only the Master does this.");
                break;
        }
        if (kind == RestNoteKind.Exception && exception != RestExceptionKind.None && text.Length < 3) throw new InvalidOperationException("Give the reason for the exception.");
        if (kind == RestNoteKind.Exception && exception == RestExceptionKind.None && text.Length < 3) throw new InvalidOperationException("Give the reason for withdrawing the exception.");
        if (kind == RestNoteKind.CompensatoryRestGiven && text.Length < 3) throw new InvalidOperationException("Say when and how the compensatory rest was given.");

        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var (_, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        var book = (await BooksAsync(db, companyId, vesselId, day, day, crewMemberId)).FirstOrDefault(b => b.CrewId == crewMemberId)
            ?? throw new InvalidOperationException("That seafarer was not found.");
        var breach = RestReview.Breaches(book, DaysOf(book, limits, day, day)).FirstOrDefault(b => b.Finding.Rule == rule)
            ?? throw new InvalidOperationException("There is no breach of that rule on that day (the day may have been corrected).");

        switch (kind)
        {
            case RestNoteKind.MasterAcknowledged:
                if (breach.Explanation is null) throw new InvalidOperationException("The breach has to be explained before the Master acknowledges it.");
                if (breach.Status is BreachStatus.Acknowledged) throw new InvalidOperationException("Already acknowledged, and nothing has changed since.");
                if (breach.Status is BreachStatus.Excepted) throw new InvalidOperationException("The Master has declared this an exception already.");
                break;
            case RestNoteKind.Exception when exception != RestExceptionKind.None:
                if (breach.Status == BreachStatus.Excepted && breach.Decision!.ExceptionKind == exception && breach.Decision.Text == text) throw new InvalidOperationException("That exception is already declared.");
                break;
            case RestNoteKind.Exception:
                if (breach.Status != BreachStatus.Excepted && breach.Status != BreachStatus.Stale) throw new InvalidOperationException("No exception is declared for this breach.");
                break;
            case RestNoteKind.CompensatoryRestGiven:
                if (!breach.CompensatoryOutstanding) throw new InvalidOperationException("No compensatory rest is outstanding for this breach.");
                break;
        }

        var now = DateTime.UtcNow;
        var latest = breach.Notes.LastOrDefault();
        if (latest is not null && now <= latest.At) now = latest.At.AddMilliseconds(1);
        if (book.Entries.Count > 0 && now <= book.Entries.Max(e => e.EnteredAt)) now = book.Entries.Max(e => e.EnteredAt).AddMilliseconds(1);      // a decision always postdates the days it is about

        var note = new RestBreachNote
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CrewMemberId = crewMemberId, CrewName = book.Name, Date = day, Rule = rule.ToString(),
            Kind = kind, ExceptionKind = kind == RestNoteKind.Exception ? exception : RestExceptionKind.None, Text = text, ByName = me.FullName, ByCrewId = me.Id, At = now,
            // One follow-up for a seafarer's day, however many rules it broke or times the Master looks at it: a task raised before stays the follow-up.
            WorklistTaskId = book.Notes.Where(n => n.Date.Date == day).OrderBy(n => n.At).Select(n => n.WorklistTaskId).LastOrDefault(id => id is not null),
        };

        WorklistTask? task = null;
        string? problem = null;
        var raises = kind == RestNoteKind.MasterAcknowledged || (kind == RestNoteKind.Exception && exception != RestExceptionKind.None);
        if (raises && note.WorklistTaskId is null)
        {
            if (!await WorklistService.IsEnabledForVesselAsync(db, companyId, vesselId)) problem = "The worklist is not switched on for this vessel, so no follow-up was raised.";
            else
            {
                try
                {
                    task = await RaiseTaskAsync(db, companyId, vesselId, me, breach, note, text);
                    note.WorklistTaskId = task.Id;
                }
                catch (InvalidOperationException ex) { problem = ex.Message; }
            }
        }

        db.RestBreachNotes.Add(note);
        await db.SaveChangesAsync();
        return new BreachNoteResult(note, task, problem);
    }

    private async Task<WorklistTask> RaiseTaskAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, CrewMember me, BreachView breach, RestBreachNote note, string masterText)
    {
        var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == "Vessel").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        var category = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Category && i.IsActive && i.ParentId == null && i.Name == "Safety").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        var what = note.Kind == RestNoteKind.Exception ? $"Exception declared by the Master ({note.ExceptionKind}): {masterText}" : "Acknowledged by the Master.";
        var description = $"{breach.CrewName} ({breach.Rank}), {breach.Finding.Date:d MMM yyyy}: {breach.Finding.Message}\n\nExplanation: {breach.Explanation?.Text ?? "none given"}\n{what}\n\nFor the office to review the working schedule and, if needed, report as the company's procedure requires.";
        var actor = new Actor(me.UserId, me.FullName);
        var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(vesselId, $"Hours of rest breach: {breach.CrewName}, {breach.Finding.Date:d MMM yyyy}", description, source, CategoryId: category), actor);
        await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.RestBreach, LinkTarget: note.Id, LinkLabel: $"Hours of rest breach, {breach.CrewName}, {breach.Finding.Date:d MMM yyyy}"), actor);
        return task;
    }

    // ---- what is waiting ----

    /// <summary>What waits for the signed-in seafarer (and, for the Master, for the vessel), for the home page. Null when the person is not a seafarer aboard or the module is off.</summary>
    public async Task<RestAttention?> AttentionAsync(Guid companyId, Actor actor)
    {
        var me = await rest.CrewOfUserAsync(companyId, actor.UserId);
        if (me?.VesselId is not { } vesselId) return null;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!await RestHoursService.IsEnabledForVesselAsync(db, companyId, vesselId)) return null;

        var today = DateTime.UtcNow.Date;
        var from = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var (set, limits) = await RestHoursService.RulesForAsync(db, companyId, vesselId);
        var books = await BooksAsync(db, companyId, vesselId, from, today, me.ActsForMaster ? null : me.Id);

        int missing = 0, toSign = 0, unexplained = 0, toAck = 0, toEndorse = 0;
        foreach (var book in books)
        {
            var breaches = RestReview.Breaches(book, DaysOf(book, limits, from, today));
            var periods = new[] { from, from.AddMonths(1) }.SelectMany(m => RestReview.Periods(m.Year, m.Month, set?.SignIntervalDays ?? 7))
                .Where(p => p.From <= today).Select(p => RestReview.PeriodOf(book, p.From, p.To, DateTime.UtcNow, breaches)).ToList();
            if (book.CrewId == me.Id)
            {
                var start = new[] { today.AddDays(-14), book.FirstRecorded?.Date ?? today }.Max();
                var have = book.Inputs.Select(d => d.Date.Date).ToHashSet();
                for (var d = start; d < today; d = d.AddDays(1)) if (!have.Contains(d)) missing++;
                toSign += periods.Count(p => p.Ended && p.Recorded > 0 && p.Gaps.Count == 0 && !p.SeafarerSigned);
                unexplained += breaches.Count(b => b.Status == BreachStatus.Unexplained);
            }
            if (me.ActsForMaster && (me.IsMaster || book.CrewId != me.Id))
            {
                toAck += breaches.Count(b => b.Status is BreachStatus.Explained or BreachStatus.Stale);
                toEndorse += periods.Count(p => p.Ended && p.SeafarerSigned && !p.MasterSigned);
            }
        }
        return new RestAttention(me.IsMaster, missing, toSign, unexplained, toAck, toEndorse, !me.IsMaster && me.ActsForMaster);
    }
}
