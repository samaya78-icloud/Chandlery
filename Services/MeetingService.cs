using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public sealed record MeetingAttendeeInput(Guid? CrewMemberId, string Name, string Rank, DrillAttendance Attendance, DrillAbsence Absence = DrillAbsence.None);

public sealed record MeetingItemInput(MeetingItemKind Kind, string Title, string Notes = "", string RaisedBy = "");

public sealed record MeetingInput(Guid MeetingTypeId, DateTime HeldAt, string Place, Guid? ChairCrewId, string SecretaryName, string Summary,
    IReadOnlyList<MeetingAttendeeInput> Attendees, IReadOnlyList<MeetingItemInput> Items);

public sealed record NewMeetingAction(string Title, string Description, DateTime? Target = null, string Assignee = "", WorklistPriority Priority = WorklistPriority.Normal);

public sealed record MeetingCan(bool IsOffice, bool Edit, bool Sign, bool Note, bool AddAction, bool Comment);

public sealed record MeetingRow(Meeting Meeting, string VesselName, int Present, int Items);

/// <summary>An action that came out of an earlier meeting of the same kind and is still open: it is put in front of the room at the next one.</summary>
public sealed record MeetingCarriedAction(WorklistTask Task, Guid FromMeetingId, string FromCode, DateTime FromHeldAt);

public sealed record MeetingDetail(Meeting Meeting, string VesselName, IReadOnlyList<MeetingComment> Comments, IReadOnlyList<WorklistTask> Actions, IReadOnlyList<MeetingCarriedAction> Carried, MeetingCan Can);

/// <summary>One meeting type on one vessel: when one was last held, when the next is due, and where that stands (for the types that are scheduled).</summary>
public sealed record MeetingScheduleRow(MeetingType Type, DrillRule? Rule, DateTime? LastHeld, DateTime? Due, DrillStatus Status);

/// <summary>
/// Safety meetings. Like a drill, a meeting is the vessel's document: written by the Master or crew the office gave the authority, signed by the Master, then only dated notes.
/// Actions that come out of a meeting are Worklist tasks linked to it, and the ones still open are carried to the next meeting of the same kind.
/// </summary>
public class MeetingService(IDbContextFactory<ChandleryDbContext> factory, WorklistService worklist)
{
    private sealed record Who(bool IsOffice, Guid? UserId, string Name, CrewMember? Crew)
    {
        public bool IsMaster => Crew?.IsMaster == true;
        public bool MayConduct => IsMaster || Crew?.Has(CrewAuthority.ConductDrills) == true;
    }

    // ---- the module (the same switch as drills) ----

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Drills);
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    private static async Task<Who> WhoAsync(ChandleryDbContext db, Guid companyId, Actor actor)
    {
        if (!db.Node.IsVessel) return new(true, actor.UserId, actor.Name, null);
        var crew = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.UserId == actor.UserId && c.VesselId != null && c.IsActive)
            ?? throw new InvalidOperationException("Meetings are recorded onboard by the crew of the vessel.");
        return new(false, actor.UserId, crew.FullName, crew);
    }

    // ---- types (the office's) ----

    public async Task<List<MeetingType>> TypesAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.MeetingTypes.AsNoTracking().Where(t => t.CompanyId == companyId);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        return await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
    }

    public async Task<MeetingType> SaveTypeAsync(Guid companyId, MeetingType input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The list of meeting types is the office's.");
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new InvalidOperationException("Give the meeting type a name.");
        if (await db.MeetingTypes.AnyAsync(t => t.CompanyId == companyId && t.Name == name && t.Id != input.Id)) throw new InvalidOperationException($"{name} is already in the list.");
        if (input.Scheduled)
        {
            var (min, max) = input.IntervalKind == DrillIntervalKind.Days ? (1, 730) : (1, 24);
            if (input.IntervalCount < min || input.IntervalCount > max) throw new InvalidOperationException(input.IntervalKind == DrillIntervalKind.Days ? "The interval is 1 to 730 days." : "The interval is 1 to 24 months.");
            if (input.WarningDays is < 0 or > 90) throw new InvalidOperationException("The warning is 0 to 90 days before the due date.");
        }
        var type = input.Id == Guid.Empty ? null : await db.MeetingTypes.FirstOrDefaultAsync(t => t.Id == input.Id && t.CompanyId == companyId) ?? throw new InvalidOperationException("That meeting type is not in the list.");
        if (type is null)
        {
            type = new MeetingType { Id = Guid.CreateVersion7(), CompanyId = companyId, SortOrder = (await db.MeetingTypes.Where(t => t.CompanyId == companyId).MaxAsync(t => (int?)t.SortOrder) ?? 0) + 1 };
            db.MeetingTypes.Add(type);
        }
        (type.Name, type.Scheduled, type.IntervalKind, type.IntervalCount, type.WarningDays) = (name, input.Scheduled, input.IntervalKind, Math.Max(1, input.IntervalCount), input.WarningDays);
        (type.StandardAgenda, type.Reference, type.IsActive) = ((input.StandardAgenda ?? "").Trim(), (input.Reference ?? "").Trim(), input.IsActive);
        await db.SaveChangesAsync();
        return type;
    }

    // ---- recording a meeting ----

    public async Task<Meeting> RecordAsync(Guid companyId, Guid vesselId, Actor actor, MeetingInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (who.IsOffice) throw new InvalidOperationException("Meetings are recorded onboard.");
        if (who.Crew!.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        if (!who.MayConduct) throw new InvalidOperationException("Only the Master, or crew the office has given the authority to conduct drills and record meetings, record a meeting.");
        if (!await ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Drills)) throw new InvalidOperationException("Drills and meetings are not switched on for this vessel.");

        var (type, crew, chair) = await CheckAsync(db, companyId, vesselId, who, input);
        var meeting = new Meeting
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CreatedAt = DateTime.UtcNow, CreatedByName = who.Name, OwnerStage = SyncStages.Vessel,
            Code = await DocumentNumbers.NextAsync(db, companyId, vesselId, DocType.Meeting),
        };
        db.Meetings.Add(meeting);
        Apply(meeting, type, chair, input);
        AddParts(db, meeting, crew, input);
        await db.SaveChangesAsync();
        return meeting;
    }

    public async Task<Meeting> UpdateAsync(Guid companyId, Guid meetingId, Actor actor, MeetingInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var meeting = await db.Meetings.Include(m => m.Attendees).Include(m => m.Items).Include(m => m.Notes).FirstOrDefaultAsync(m => m.Id == meetingId && m.CompanyId == companyId) ?? throw new InvalidOperationException("Meeting not found.");
        if (who.IsOffice) throw new InvalidOperationException("Meetings are changed onboard.");
        if (meeting.IsSigned) throw new InvalidOperationException("The Master has signed this meeting. Add a note instead.");
        if (!Owns(who, meeting)) throw new InvalidOperationException("Only the person who chaired it, or the Master, changes a meeting.");

        var (type, crew, chair) = await CheckAsync(db, companyId, meeting.VesselId, who, input);
        db.MeetingAttendees.RemoveRange(meeting.Attendees);
        db.MeetingItems.RemoveRange(meeting.Items);
        meeting.Attendees.Clear();
        meeting.Items.Clear();
        Apply(meeting, type, chair, input);
        AddParts(db, meeting, crew, input);
        await db.SaveChangesAsync();
        return meeting;
    }

    private static bool Owns(Who who, Meeting meeting) => who.Crew!.VesselId == meeting.VesselId && (who.IsMaster || (who.MayConduct && meeting.ChairCrewId == who.Crew.Id));

    private static async Task<(MeetingType Type, List<CrewMember> Crew, CrewMember Chair)> CheckAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, Who who, MeetingInput input)
    {
        var type = await db.MeetingTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.MeetingTypeId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Choose the kind of meeting.");
        if (!type.IsActive) throw new InvalidOperationException($"{type.Name} is no longer in use.");
        if (input.HeldAt > DateTime.UtcNow.AddDays(1)) throw new InvalidOperationException("A meeting cannot have been held in the future.");

        var crew = await db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive).ToListAsync();
        var chair = input.ChairCrewId is { } named ? crew.FirstOrDefault(c => c.Id == named) ?? throw new InvalidOperationException("The chair is not one of the vessel's crew.") : who.Crew!;
        if (chair.Id != who.Crew!.Id && !who.IsMaster) throw new InvalidOperationException("Only the Master names someone else as the chair.");

        var people = input.Attendees ?? [];
        if (people.Count(p => p.Attendance == DrillAttendance.Present) == 0) throw new InvalidOperationException("A meeting needs at least one person who attended.");
        if (people.Where(p => p.CrewMemberId is not null).GroupBy(p => p.CrewMemberId).Any(g => g.Count() > 1)) throw new InvalidOperationException("Someone is listed twice.");
        foreach (var p in people)
        {
            if (p.CrewMemberId is { } id && crew.All(c => c.Id != id)) throw new InvalidOperationException("A person listed is not one of the vessel's crew.");
            if (p.CrewMemberId is null && (p.Attendance != DrillAttendance.Observer || string.IsNullOrWhiteSpace(p.Name))) throw new InvalidOperationException("Someone who is not crew can be added as an observer, with a name.");
            if (p.Attendance != DrillAttendance.Absent && p.Absence != DrillAbsence.None) throw new InvalidOperationException("A reason for absence belongs to someone who was absent.");
        }
        var items = input.Items ?? [];
        if (items.Count == 0) throw new InvalidOperationException("A meeting needs at least one item: an agenda item or a suggestion.");
        if (items.Any(i => (i.Title ?? "").Trim().Length < 2)) throw new InvalidOperationException("Every item needs a title.");
        return (type, crew, chair);
    }

    private static void Apply(Meeting m, MeetingType type, CrewMember chair, MeetingInput input)
    {
        (m.MeetingTypeId, m.TypeName) = (type.Id, type.Name);
        m.HeldAt = DateTime.SpecifyKind(input.HeldAt, DateTimeKind.Utc);
        (m.Place, m.SecretaryName, m.Summary) = ((input.Place ?? "").Trim(), (input.SecretaryName ?? "").Trim(), (input.Summary ?? "").Trim());
        (m.ChairCrewId, m.ChairName) = (chair.Id, chair.FullName);
    }

    private static void AddParts(ChandleryDbContext db, Meeting meeting, List<CrewMember> crew, MeetingInput input)
    {
        foreach (var p in input.Attendees)
        {
            var member = p.CrewMemberId is { } id ? crew.First(c => c.Id == id) : null;
            db.MeetingAttendees.Add(new MeetingAttendee
            {
                Id = Guid.CreateVersion7(), MeetingId = meeting.Id, CrewMemberId = member?.Id, Name = member?.FullName ?? p.Name.Trim(), Rank = member?.Rank ?? (p.Rank ?? "").Trim(),
                Attendance = p.Attendance, Absence = p.Attendance == DrillAttendance.Absent && p.Absence == DrillAbsence.None ? DrillAbsence.Other : p.Absence,
            });
        }
        var position = 0;
        foreach (var i in input.Items)
            db.MeetingItems.Add(new MeetingItem { Id = Guid.CreateVersion7(), MeetingId = meeting.Id, Position = ++position, Kind = i.Kind, Title = i.Title.Trim(), Notes = (i.Notes ?? "").Trim(), RaisedBy = (i.RaisedBy ?? "").Trim() });
    }

    public async Task<Meeting> SignAsync(Guid companyId, Guid meetingId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var meeting = await db.Meetings.Include(m => m.Attendees).Include(m => m.Items).Include(m => m.Notes).FirstOrDefaultAsync(m => m.Id == meetingId && m.CompanyId == companyId) ?? throw new InvalidOperationException("Meeting not found.");
        if (who.IsOffice || !who.IsMaster || who.Crew!.VesselId != meeting.VesselId) throw new InvalidOperationException("Only the Master signs the minutes.");
        if (meeting.IsSigned) throw new InvalidOperationException("These minutes are signed already.");
        (meeting.SignedByName, meeting.SignedByCrewId, meeting.SignedAt) = (who.Name, who.Crew.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return meeting;
    }

    public async Task<MeetingNote> AddNoteAsync(Guid companyId, Guid meetingId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var meeting = await db.Meetings.Include(m => m.Attendees).Include(m => m.Items).Include(m => m.Notes).FirstOrDefaultAsync(m => m.Id == meetingId && m.CompanyId == companyId) ?? throw new InvalidOperationException("Meeting not found.");
        if (who.IsOffice) throw new InvalidOperationException("Notes are added onboard; the office comments.");
        if (!meeting.IsSigned) throw new InvalidOperationException("Change unsigned minutes directly; notes are for signed ones.");
        if (!Owns(who, meeting)) throw new InvalidOperationException("Only the chair or the Master adds a note.");
        text = (text ?? "").Trim();
        if (text.Length < 3) throw new InvalidOperationException("Write the note.");
        var note = new MeetingNote { Id = Guid.CreateVersion7(), MeetingId = meeting.Id, Text = text, ByName = who.Name, ByCrewId = who.Crew!.Id, At = DateTime.UtcNow };
        db.MeetingNotes.Add(note);
        await db.SaveChangesAsync();
        return note;
    }

    public async Task<MeetingComment> CommentAsync(Guid companyId, Guid meetingId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (!who.IsOffice) throw new InvalidOperationException("The office comments on a meeting.");
        var meeting = await db.Meetings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meetingId && m.CompanyId == companyId) ?? throw new InvalidOperationException("Meeting not found.");
        text = (text ?? "").Trim();
        if (text.Length < 3) throw new InvalidOperationException("Write the comment.");
        var comment = new MeetingComment { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = meeting.VesselId, MeetingId = meeting.Id, Text = text, ByName = who.Name, ByUserId = who.UserId, At = DateTime.UtcNow };
        db.MeetingComments.Add(comment);
        await db.SaveChangesAsync();
        return comment;
    }

    /// <summary>An action decided at a meeting, as a Worklist task on the vessel linked to the meeting. By the Master, the chair, or the office.</summary>
    public async Task<WorklistTask> AddActionAsync(Guid companyId, Guid meetingId, Actor actor, NewMeetingAction action)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var meeting = await db.Meetings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meetingId && m.CompanyId == companyId) ?? throw new InvalidOperationException("Meeting not found.");
        if (!who.IsOffice && (who.Crew!.VesselId != meeting.VesselId || !(who.IsMaster || (who.MayConduct && meeting.ChairCrewId == who.Crew.Id)))) throw new InvalidOperationException("Actions are added by the office, the Master or the chair.");
        if ((action.Title ?? "").Trim().Length < 3) throw new InvalidOperationException("Give the action a title.");
        var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == (who.IsOffice ? "Office" : "Vessel")).Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        var category = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Category && i.IsActive && i.ParentId == null && i.Name == "Safety").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        var acting = new Actor(who.UserId, who.Name);
        var body = $"{action.Description?.Trim()}\n\nFrom meeting {meeting.Code} ({meeting.TypeName}, {meeting.HeldAt:d MMM yyyy}).".TrimStart();
        var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(meeting.VesselId, action.Title.Trim(), body, source, CategoryId: category, Priority: action.Priority, Target: action.Target, Assignee: action.Assignee ?? ""), acting);
        await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Meeting, LinkTarget: meeting.Id, LinkLabel: $"Meeting {meeting.Code}"), acting);
        return task;
    }

    // ---- reading ----

    public async Task<MeetingDetail?> GetAsync(Guid companyId, Guid meetingId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var meeting = await db.Meetings.AsNoTracking().Include(m => m.Attendees).Include(m => m.Items).Include(m => m.Notes).FirstOrDefaultAsync(m => m.Id == meetingId && m.CompanyId == companyId);
        if (meeting is null || (!who.IsOffice && who.Crew!.VesselId != meeting.VesselId)) return null;
        var comments = await db.MeetingComments.AsNoTracking().Where(c => c.MeetingId == meetingId).OrderBy(c => c.At).ThenBy(c => c.Id).ToListAsync();
        var tasks = await worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.Meeting, meetingId);
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == meeting.VesselId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";
        var carried = await CarriedAsync(db, companyId, meeting.VesselId, meeting.MeetingTypeId, meeting.HeldAt, meeting.Id);
        var mine = !who.IsOffice;
        var can = new MeetingCan(who.IsOffice, Edit: mine && !meeting.IsSigned && Owns(who, meeting), Sign: mine && who.IsMaster && !meeting.IsSigned, Note: mine && meeting.IsSigned && Owns(who, meeting),
            AddAction: who.IsOffice || (mine && Owns(who, meeting)), Comment: who.IsOffice);
        return new MeetingDetail(meeting, vessel, comments, tasks, carried, can);
    }

    /// <summary>
    /// The actions of earlier meetings of this kind on this vessel that are still open: put in front of the room at the next one. Before the given moment, when one is given (so a
    /// meeting shows what was open when it was held).
    /// </summary>
    private async Task<List<MeetingCarriedAction>> CarriedAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, Guid typeId, DateTime before, Guid? exceptMeeting)
    {
        var earlier = await db.Meetings.AsNoTracking().Where(m => m.VesselId == vesselId && m.MeetingTypeId == typeId && m.HeldAt < before && m.Id != exceptMeeting).OrderByDescending(m => m.HeldAt).Take(12).ToListAsync();
        var carried = new List<MeetingCarriedAction>();
        foreach (var m in earlier)
            foreach (var task in await worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.Meeting, m.Id))
                if (!task.IsClosed) carried.Add(new MeetingCarriedAction(task, m.Id, m.Code, m.HeldAt));
        return carried;
    }

    /// <summary>The open actions to put in front of the room at the next meeting of a kind: from the earlier meetings of that kind on the vessel.</summary>
    public async Task<List<MeetingCarriedAction>> OpenActionsAsync(Guid companyId, Guid vesselId, Guid meetingTypeId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (!who.IsOffice && who.Crew!.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        return await CarriedAsync(db, companyId, vesselId, meetingTypeId, DateTime.UtcNow.AddDays(2), null);
    }

    public async Task<List<MeetingRow>> ListAsync(Guid companyId, Actor actor, Guid? vesselId = null, Guid? typeId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var query = db.Meetings.AsNoTracking().Include(m => m.Attendees).Include(m => m.Items).Where(m => m.CompanyId == companyId);
        if (!who.IsOffice) { var mine = who.Crew!.VesselId; query = query.Where(m => m.VesselId == mine); }
        else if (vesselId is { } v) query = query.Where(m => m.VesselId == v);
        if (typeId is { } t) query = query.Where(m => m.MeetingTypeId == t);
        var meetings = await query.OrderByDescending(m => m.HeldAt).ThenByDescending(m => m.Id).ToListAsync();
        var vessels = await db.Vessels.AsNoTracking().Where(x => x.CompanyId == companyId).ToDictionaryAsync(x => x.Id, x => x.Name);
        return meetings.Select(m => new MeetingRow(m, vessels.GetValueOrDefault(m.VesselId) ?? "", m.Attendees.Count(a => a.Attendance == DrillAttendance.Present), m.Items.Count)).ToList();
    }

    /// <summary>The scheduled meeting types on a vessel with when one was last held and when the next is due.</summary>
    public async Task<List<MeetingScheduleRow>> ScheduleAsync(Guid companyId, Guid vesselId, Actor actor, DateTime? today = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (!who.IsOffice && who.Crew!.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        var types = await db.MeetingTypes.AsNoTracking().Where(t => t.CompanyId == companyId && t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
        var held = (await db.Meetings.AsNoTracking().Where(m => m.VesselId == vesselId).Select(m => new { m.MeetingTypeId, m.HeldAt }).ToListAsync()).GroupBy(m => m.MeetingTypeId).ToDictionary(g => g.Key, g => g.Max(m => m.HeldAt));
        return MeetingSchedules.Build(types, held, (today ?? DateTime.UtcNow).Date);
    }
}

public static class MeetingSchedules
{
    /// <summary>The meeting types with their due dates: scheduled types as drills are (the last meeting plus the interval), unscheduled ones with no due date and no status beyond "never held" or the last date.</summary>
    public static List<MeetingScheduleRow> Build(IEnumerable<MeetingType> types, IReadOnlyDictionary<Guid, DateTime> lastHeld, DateTime today)
    {
        var rows = new List<MeetingScheduleRow>();
        foreach (var type in types)
        {
            DateTime? last = lastHeld.TryGetValue(type.Id, out var l) ? l.Date : null;
            if (!type.Scheduled) { rows.Add(new MeetingScheduleRow(type, null, last, null, DrillStatus.NotDue)); continue; }
            var rule = new DrillRule(type.IntervalKind, Math.Max(1, type.IntervalCount), Math.Max(0, type.WarningDays), false);
            rows.Add(new MeetingScheduleRow(type, rule, last, DrillRules.DueDate(rule, last), DrillRules.StatusOf(rule, last, today)));
        }
        return rows;
    }
}

public static class MeetingDefaults
{
    private static readonly (string Name, bool Scheduled, DrillIntervalKind Kind, int Count, string Agenda, string Reference)[] Types =
    [
        ("Safety committee", true, DrillIntervalKind.CalendarMonths, 1,
            "Actions from the last meeting\nIncidents and near misses since the last meeting\nDrills held and drills due\nUnsafe conditions and defects\nSuggestions from the crew\nHealth, welfare and hours of rest\nAny other business",
            "Company SMS and flag state (to verify)"),
        ("Toolbox talk", false, DrillIntervalKind.CalendarMonths, 1, "The job and its hazards\nControls and protective equipment\nWho does what\nStop-work and reporting", "Held when needed"),
        ("Pre-arrival or pre-departure briefing", false, DrillIntervalKind.CalendarMonths, 1, "Passage plan and pilotage\nWatch arrangements\nBerth, moorings and cargo\nSafety and security\nEmergency arrangements", "Held when needed"),
        ("Management review onboard", true, DrillIntervalKind.CalendarMonths, 6, "Non-conformities and corrective actions\nAudit and inspection findings\nIncidents and trends\nResources and training\nImprovements to the SMS", "ISM Code (to verify)"),
        ("Other meeting", false, DrillIntervalKind.CalendarMonths, 1, "", "Held when needed"),
    ];

    public static async Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId)
    {
        if (await db.MeetingTypes.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == companyId)) return;
        var order = 0;
        foreach (var (name, scheduled, kind, count, agenda, reference) in Types)
            db.MeetingTypes.Add(new MeetingType { Id = Guid.CreateVersion7(), CompanyId = companyId, Name = name, Scheduled = scheduled, IntervalKind = kind, IntervalCount = count, WarningDays = 7, StandardAgenda = agenda, Reference = reference, SortOrder = ++order });
    }
}
