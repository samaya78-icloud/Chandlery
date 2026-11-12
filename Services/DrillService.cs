using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public sealed record DrillParticipantInput(Guid? CrewMemberId, string Name, string Rank, DrillAttendance Attendance, DrillAbsence Absence = DrillAbsence.None);

public sealed record DrillInput(Guid DrillTypeId, DateTime StartedAt, DateTime? EndedAt, IncidentShipStatus ShipStatus, string Place, string Scenario, Guid? ConductedByCrewId,
    string EquipmentTested, string Findings, string Deficiencies, string Remarks, IReadOnlyList<DrillParticipantInput> Participants);

public sealed record NewDrillAction(string Title, string Description, DateTime? Target = null, string Assignee = "", WorklistPriority Priority = WorklistPriority.Normal);

/// <summary>What the person looking at a drill may do with it: the rules the service enforces, so a page offers only what will be accepted.</summary>
public sealed record DrillCan(bool IsOffice, bool Edit, bool Sign, bool Note, bool AddAction, bool Comment);

public sealed record DrillRow(Drill Drill, string VesselName, int Present, int Absent);

public sealed record DrillDetail(Drill Drill, string VesselName, IReadOnlyList<DrillComment> Comments, IReadOnlyList<WorklistTask> Actions, bool PossibleDuplicate, TimeSpan? Duration, DrillCan Can);

public sealed record DrillPersonDue(Guid CrewId, string Name, string Rank, DateTime? LastTookPart, DateTime? Due, DrillStatus Status);

/// <summary>One type on one vessel. <c>ShipStatus</c> is the ship's own last drill; <c>Status</c> is the worst of that and every person's, so the row shows what most needs doing.</summary>
public sealed record DrillScheduleRow(DrillType Type, DrillRule Rule, DateTime? LastHeld, DateTime? Due, DrillStatus ShipStatus, DrillStatus Status, IReadOnlyList<DrillPersonDue> People);

public sealed record DrillSchedule(Guid VesselId, string VesselName, DateTime Today, IReadOnlyList<DrillScheduleRow> Rows, DrillStatus Worst, DrillQuarter Quarter);

/// <summary>One crew member's own standing for one type everyone must attend: when they last took part, when they are next due, and where that stands.</summary>
public sealed record DrillPersonalRow(DrillType Type, DateTime? LastTookPart, DateTime? Due, DrillStatus Status);

/// <summary>
/// Drills (spec 13). The drill is the vessel's document: its conductor (the Master, or crew the office gave the authority) writes it, the Master signs it, and after that only dated
/// notes are added. The schedule is worked out from the drills held. Everything is checked here, whatever the page lets through.
/// </summary>
public class DrillService(IDbContextFactory<ChandleryDbContext> factory, WorklistService worklist)
{
    private sealed record Who(bool IsOffice, Guid? UserId, string Name, CrewMember? Crew)
    {
        public bool IsMaster => Crew?.IsMaster == true;
        public bool MayConduct => IsMaster || Crew?.Has(CrewAuthority.ConductDrills) == true;
    }

    // ---- the module ----

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Drills);

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
            ?? throw new InvalidOperationException("Drills are recorded onboard by the crew of the vessel.");
        return new(false, actor.UserId, crew.FullName, crew);
    }

    private static void RequireOffice(Who who, string what = "This is done by the office.")
    {
        if (!who.IsOffice) throw new InvalidOperationException(what);
    }

    public async Task<CrewMember?> CrewOfUserAsync(Guid companyId, Guid? userId)
    {
        if (userId is null) return null;
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.UserId == userId && c.VesselId != null && c.IsActive);
    }

    public async Task<List<CrewMember>> CrewAboardAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive).OrderBy(c => c.FullName).ToListAsync();
    }

    // ---- types and the vessel's own rules (the office's, copied to every vessel) ----

    public async Task<List<DrillType>> TypesAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.DrillTypes.AsNoTracking().Where(t => t.CompanyId == companyId);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        return await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
    }

    public async Task<DrillType> SaveTypeAsync(Guid companyId, DrillType input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The list of drill types is the office's.");
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new InvalidOperationException("Give the drill type a name.");
        if (await db.DrillTypes.AnyAsync(t => t.CompanyId == companyId && t.Name == name && t.Id != input.Id)) throw new InvalidOperationException($"{name} is already in the list.");
        var (min, max) = input.IntervalKind == DrillIntervalKind.Days ? (1, 730) : (1, 24);
        if (input.IntervalCount < min || input.IntervalCount > max) throw new InvalidOperationException(input.IntervalKind == DrillIntervalKind.Days ? "The interval is 1 to 730 days." : "The interval is 1 to 24 months.");
        if (input.WarningDays is < 0 or > 90) throw new InvalidOperationException("The warning is 0 to 90 days before the due date.");

        var type = input.Id == Guid.Empty ? null : await db.DrillTypes.FirstOrDefaultAsync(t => t.Id == input.Id && t.CompanyId == companyId) ?? throw new InvalidOperationException("That drill type is not in the list.");
        if (type is null)
        {
            type = new DrillType { Id = Guid.CreateVersion7(), CompanyId = companyId, SortOrder = (await db.DrillTypes.Where(t => t.CompanyId == companyId).MaxAsync(t => (int?)t.SortOrder) ?? 0) + 1 };
            db.DrillTypes.Add(type);
        }
        (type.Name, type.Category, type.IntervalKind, type.IntervalCount, type.WarningDays) = (name, input.Category, input.IntervalKind, input.IntervalCount, input.WarningDays);
        (type.EveryCrewMember, type.Scenario, type.Checklist, type.Reference, type.IsActive) = (input.EveryCrewMember, (input.Scenario ?? "").Trim(), (input.Checklist ?? "").Trim(), (input.Reference ?? "").Trim(), input.IsActive);
        await db.SaveChangesAsync();
        return type;
    }

    public async Task<List<DrillOverride>> OverridesAsync(Guid companyId, Guid? vesselId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.DrillOverrides.AsNoTracking().Where(o => o.CompanyId == companyId);
        if (vesselId is { } v) query = query.Where(o => o.VesselId == v);
        return await query.ToListAsync();
    }

    /// <summary>A type that does not apply on a vessel, or has another interval there. Needs a reason. Saving it with neither change puts the vessel back on the company's rule.</summary>
    public async Task<DrillOverride> SaveOverrideAsync(Guid companyId, Guid vesselId, Guid drillTypeId, bool applies, DrillIntervalKind? kind, int? count, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("A vessel's drill rules are the office's.");
        if (!await db.Vessels.AnyAsync(v => v.Id == vesselId && v.CompanyId == companyId)) throw new InvalidOperationException("That vessel does not exist.");
        if (!await db.DrillTypes.AnyAsync(t => t.Id == drillTypeId && t.CompanyId == companyId)) throw new InvalidOperationException("That drill type is not in the list.");
        if ((kind is null) != (count is null)) throw new InvalidOperationException("Give both the kind and the number for another interval, or neither.");
        if (count is { } n && (n < 1 || n > (kind == DrillIntervalKind.Days ? 730 : 24))) throw new InvalidOperationException("Check the interval.");
        var changes = !applies || kind is not null;
        reason = (reason ?? "").Trim();
        if (changes && reason.Length < 3) throw new InvalidOperationException("Say why this vessel's rule differs.");

        var row = await db.DrillOverrides.FirstOrDefaultAsync(o => o.CompanyId == companyId && o.VesselId == vesselId && o.DrillTypeId == drillTypeId);
        if (row is null) { row = new DrillOverride { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, DrillTypeId = drillTypeId }; db.DrillOverrides.Add(row); }
        (row.Applies, row.IntervalKind, row.IntervalCount, row.Reason) = (applies, kind, count, changes ? reason : "");
        await db.SaveChangesAsync();
        return row;
    }

    // ---- recording a drill ----

    /// <summary>Records a drill held onboard. By the Master, or crew the office gave the authority to conduct drills. It stays the conductor's to change until the Master signs.</summary>
    public async Task<Drill> RecordAsync(Guid companyId, Guid vesselId, Actor actor, DrillInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (who.IsOffice) throw new InvalidOperationException("Drills are recorded onboard.");
        if (who.Crew!.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        if (!who.MayConduct) throw new InvalidOperationException("Only the Master, or crew the office has given the authority to conduct drills, record a drill.");
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Drills are not switched on for this vessel.");

        var (type, crew, conductor) = await CheckAsync(db, companyId, vesselId, who, input);
        var drill = new Drill
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CreatedAt = DateTime.UtcNow, CreatedByName = who.Name, OwnerStage = SyncStages.Vessel,
            Code = await DocumentNumbers.NextAsync(db, companyId, vesselId, DocType.Drill),
        };
        db.Drills.Add(drill);
        Apply(drill, type, conductor, input);
        AddParticipants(db, drill, crew, input.Participants);
        await db.SaveChangesAsync();
        return drill;
    }

    /// <summary>Changes a drill that the Master has not yet signed. By its conductor or the Master.</summary>
    public async Task<Drill> UpdateAsync(Guid companyId, Guid drillId, Actor actor, DrillInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var drill = await db.Drills.Include(d => d.Participants).Include(d => d.Notes).FirstOrDefaultAsync(d => d.Id == drillId && d.CompanyId == companyId) ?? throw new InvalidOperationException("Drill not found.");
        if (who.IsOffice) throw new InvalidOperationException("Drills are changed onboard.");
        if (drill.IsSigned) throw new InvalidOperationException("The Master has signed this drill. Add a note instead.");
        if (!OwnsDrill(who, drill)) throw new InvalidOperationException("Only the conductor or the Master changes a drill.");

        var (type, crew, conductor) = await CheckAsync(db, companyId, drill.VesselId, who, input);
        db.DrillParticipants.RemoveRange(drill.Participants);
        drill.Participants.Clear();
        Apply(drill, type, conductor, input);
        AddParticipants(db, drill, crew, input.Participants);
        await db.SaveChangesAsync();
        return drill;
    }

    private static bool OwnsDrill(Who who, Drill drill) => who.Crew!.VesselId == drill.VesselId && (who.IsMaster || (who.MayConduct && drill.ConductedByCrewId == who.Crew.Id));

    private static async Task<(DrillType Type, List<CrewMember> Crew, CrewMember Conductor)> CheckAsync(ChandleryDbContext db, Guid companyId, Guid vesselId, Who who, DrillInput input)
    {
        var type = await db.DrillTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.DrillTypeId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Choose the kind of drill.");
        if (!type.IsActive) throw new InvalidOperationException($"{type.Name} is no longer in use.");
        var over = await db.DrillOverrides.AsNoTracking().FirstOrDefaultAsync(o => o.VesselId == vesselId && o.DrillTypeId == type.Id);
        if (over is { Applies: false }) throw new InvalidOperationException($"{type.Name} does not apply to this vessel ({over.Reason}).");
        if (DrillRules.TimeProblem(input.StartedAt, input.EndedAt, DateTime.UtcNow) is { } problem) throw new InvalidOperationException(problem);

        var crew = await db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive).ToListAsync();
        var conductor = input.ConductedByCrewId is { } named ? crew.FirstOrDefault(c => c.Id == named) ?? throw new InvalidOperationException("The conductor is not one of the vessel's crew.") : who.Crew!;
        if (conductor.Id != who.Crew!.Id && !who.IsMaster) throw new InvalidOperationException("Only the Master names someone else as the conductor.");
        if (!conductor.IsMaster && !conductor.Has(CrewAuthority.ConductDrills)) throw new InvalidOperationException($"{conductor.FullName} has not been given the authority to conduct drills.");

        var people = input.Participants ?? [];
        if (people.Count(p => p.Attendance == DrillAttendance.Present) == 0) throw new InvalidOperationException("A drill needs at least one person who took part.");
        if (people.Where(p => p.CrewMemberId is not null).GroupBy(p => p.CrewMemberId).Any(g => g.Count() > 1)) throw new InvalidOperationException("Someone is listed twice.");
        foreach (var p in people)
        {
            if (p.CrewMemberId is { } id && crew.All(c => c.Id != id)) throw new InvalidOperationException("A person listed is not one of the vessel's crew.");
            if (p.CrewMemberId is null && (p.Attendance != DrillAttendance.Observer || string.IsNullOrWhiteSpace(p.Name))) throw new InvalidOperationException("Someone who is not crew can be added as an observer, with a name.");
            if (p.Attendance != DrillAttendance.Absent && p.Absence != DrillAbsence.None) throw new InvalidOperationException("A reason for absence belongs to someone who was absent.");
        }
        return (type, crew, conductor);
    }

    private static void Apply(Drill drill, DrillType type, CrewMember conductor, DrillInput input)
    {
        (drill.DrillTypeId, drill.TypeName, drill.Category) = (type.Id, type.Name, type.Category);
        (drill.StartedAt, drill.EndedAt) = (DateTime.SpecifyKind(input.StartedAt, DateTimeKind.Utc), input.EndedAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null);
        (drill.ShipStatus, drill.Place, drill.Scenario) = (input.ShipStatus, (input.Place ?? "").Trim(), (input.Scenario ?? "").Trim());
        (drill.ConductedByCrewId, drill.ConductedByName) = (conductor.Id, conductor.FullName);
        (drill.EquipmentTested, drill.Findings, drill.Deficiencies, drill.Remarks) = ((input.EquipmentTested ?? "").Trim(), (input.Findings ?? "").Trim(), (input.Deficiencies ?? "").Trim(), (input.Remarks ?? "").Trim());
    }

    private static void AddParticipants(ChandleryDbContext db, Drill drill, List<CrewMember> crew, IReadOnlyList<DrillParticipantInput> people)
    {
        foreach (var p in people)
        {
            var member = p.CrewMemberId is { } id ? crew.First(c => c.Id == id) : null;
            db.DrillParticipants.Add(new DrillParticipant
            {
                Id = Guid.CreateVersion7(), DrillId = drill.Id, CrewMemberId = member?.Id, Name = member?.FullName ?? p.Name.Trim(), Rank = member?.Rank ?? (p.Rank ?? "").Trim(),
                Attendance = p.Attendance, Absence = p.Attendance == DrillAttendance.Absent && p.Absence == DrillAbsence.None ? DrillAbsence.Other : p.Absence,
            });
        }
    }

    /// <summary>The Master signs the drill. After that the drill is as signed and only dated notes are added.</summary>
    public async Task<Drill> SignAsync(Guid companyId, Guid drillId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var drill = await db.Drills.Include(d => d.Participants).Include(d => d.Notes).FirstOrDefaultAsync(d => d.Id == drillId && d.CompanyId == companyId) ?? throw new InvalidOperationException("Drill not found.");
        if (who.IsOffice || !who.IsMaster || who.Crew!.VesselId != drill.VesselId) throw new InvalidOperationException("Only the Master signs a drill.");
        if (drill.IsSigned) throw new InvalidOperationException("This drill is signed already.");
        if (!drill.Participants.Any(p => p.Attendance == DrillAttendance.Present)) throw new InvalidOperationException("A drill with nobody who took part cannot be signed.");
        (drill.SignedByName, drill.SignedByCrewId, drill.SignedAt) = (who.Name, who.Crew.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return drill;
    }

    /// <summary>A dated addition to a signed drill, by the conductor or the Master. Never edited.</summary>
    public async Task<DrillNote> AddNoteAsync(Guid companyId, Guid drillId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var drill = await db.Drills.Include(d => d.Participants).Include(d => d.Notes).FirstOrDefaultAsync(d => d.Id == drillId && d.CompanyId == companyId) ?? throw new InvalidOperationException("Drill not found.");
        if (who.IsOffice) throw new InvalidOperationException("Notes are added onboard; the office comments.");
        if (!drill.IsSigned) throw new InvalidOperationException("Change an unsigned drill directly; notes are for signed ones.");
        if (!OwnsDrill(who, drill)) throw new InvalidOperationException("Only the conductor or the Master adds a note.");
        text = (text ?? "").Trim();
        if (text.Length < 3) throw new InvalidOperationException("Write the note.");
        var note = new DrillNote { Id = Guid.CreateVersion7(), DrillId = drill.Id, Text = text, ByName = who.Name, ByCrewId = who.Crew!.Id, At = DateTime.UtcNow };
        db.DrillNotes.Add(note);
        await db.SaveChangesAsync();
        return note;
    }

    /// <summary>The office's comment on a drill.</summary>
    public async Task<DrillComment> CommentAsync(Guid companyId, Guid drillId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        RequireOffice(who, "The office comments on a drill.");
        var drill = await db.Drills.AsNoTracking().FirstOrDefaultAsync(d => d.Id == drillId && d.CompanyId == companyId) ?? throw new InvalidOperationException("Drill not found.");
        text = (text ?? "").Trim();
        if (text.Length < 3) throw new InvalidOperationException("Write the comment.");
        var comment = new DrillComment { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = drill.VesselId, DrillId = drill.Id, Text = text, ByName = who.Name, ByUserId = who.UserId, At = DateTime.UtcNow };
        db.DrillComments.Add(comment);
        await db.SaveChangesAsync();
        return comment;
    }

    /// <summary>A deficiency or a finding as a Worklist task on the vessel, linked to the drill. By the office or the Master. The worklist has to be switched on for the vessel.</summary>
    public async Task<WorklistTask> AddActionAsync(Guid companyId, Guid drillId, Actor actor, NewDrillAction action)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var drill = await db.Drills.AsNoTracking().FirstOrDefaultAsync(d => d.Id == drillId && d.CompanyId == companyId) ?? throw new InvalidOperationException("Drill not found.");
        if (!who.IsOffice && (!who.IsMaster || who.Crew!.VesselId != drill.VesselId)) throw new InvalidOperationException("Actions are added by the office or by the Master.");
        if ((action.Title ?? "").Trim().Length < 3) throw new InvalidOperationException("Give the action a title.");
        var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == (who.IsOffice ? "Office" : "Vessel")).Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        var category = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Category && i.IsActive && i.ParentId == null && i.Name == "Safety").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        var acting = new Actor(who.UserId, who.Name);
        var body = $"{action.Description?.Trim()}\n\nFrom drill {drill.Code} ({drill.TypeName}, {drill.StartedAt:d MMM yyyy}).".TrimStart();
        var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(drill.VesselId, action.Title.Trim(), body, source, CategoryId: category, Priority: action.Priority, Target: action.Target, Assignee: action.Assignee ?? ""), acting);
        await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Drill, LinkTarget: drill.Id, LinkLabel: $"Drill {drill.Code}"), acting);
        return task;
    }

    // ---- reading ----

    public async Task<DrillDetail?> GetAsync(Guid companyId, Guid drillId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var drill = await db.Drills.AsNoTracking().Include(d => d.Participants).Include(d => d.Notes).FirstOrDefaultAsync(d => d.Id == drillId && d.CompanyId == companyId);
        if (drill is null || (!who.IsOffice && who.Crew!.VesselId != drill.VesselId)) return null;
        var comments = await db.DrillComments.AsNoTracking().Where(c => c.DrillId == drillId).OrderBy(c => c.At).ThenBy(c => c.Id).ToListAsync();
        var tasks = await worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.Drill, drillId);
        var others = await db.Drills.AsNoTracking().Where(d => d.VesselId == drill.VesselId && d.DrillTypeId == drill.DrillTypeId && d.Id != drillId).Select(d => d.StartedAt).ToListAsync();
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == drill.VesselId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";
        var mine = !who.IsOffice;
        var can = new DrillCan(who.IsOffice, Edit: mine && !drill.IsSigned && OwnsDrill(who, drill), Sign: mine && who.IsMaster && !drill.IsSigned, Note: mine && drill.IsSigned && OwnsDrill(who, drill),
            AddAction: who.IsOffice || who.IsMaster, Comment: who.IsOffice);
        return new DrillDetail(drill, vessel, comments, tasks, others.Any(o => DrillRules.PossibleDuplicate(o, drill.StartedAt)), drill.EndedAt is { } end ? end - drill.StartedAt : null, can);
    }

    /// <summary>The vessel's drills, newest first (onboard: its own; the office: any vessel's, or one's).</summary>
    public async Task<List<DrillRow>> ListAsync(Guid companyId, Actor actor, Guid? vesselId = null, Guid? typeId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var query = db.Drills.AsNoTracking().Include(d => d.Participants).Where(d => d.CompanyId == companyId);
        if (!who.IsOffice) { var mine = who.Crew!.VesselId; query = query.Where(d => d.VesselId == mine); }
        else if (vesselId is { } v) query = query.Where(d => d.VesselId == v);
        if (typeId is { } t) query = query.Where(d => d.DrillTypeId == t);
        var drills = await query.OrderByDescending(d => d.StartedAt).ThenByDescending(d => d.Id).ToListAsync();
        var vessels = await db.Vessels.AsNoTracking().Where(x => x.CompanyId == companyId).ToDictionaryAsync(x => x.Id, x => x.Name);
        return drills.Select(d => new DrillRow(d, vessels.GetValueOrDefault(d.VesselId) ?? "", d.Participants.Count(p => p.Attendance == DrillAttendance.Present), d.Participants.Count(p => p.Attendance == DrillAttendance.Absent))).ToList();
    }

    /// <summary>
    /// What is due on a vessel: each type that applies, with the ship's last drill, the due date and the status, and for the types everyone must attend, the people whose own date has
    /// come or passed. Also the quarter-of-crew rule for the previous month. Onboard the vessel's own; the office any vessel's.
    /// </summary>
    public async Task<DrillSchedule> ScheduleAsync(Guid companyId, Guid vesselId, Actor actor, DateTime? today = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (!who.IsOffice && who.Crew!.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        var vessel = await db.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId) ?? throw new InvalidOperationException("That vessel does not exist.");
        var day = (today ?? DateTime.UtcNow).Date;

        var types = await db.DrillTypes.AsNoTracking().Where(t => t.CompanyId == companyId && t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
        var overrides = (await db.DrillOverrides.AsNoTracking().Where(o => o.VesselId == vesselId).ToListAsync()).ToDictionary(o => o.DrillTypeId);
        var drills = await db.Drills.AsNoTracking().Include(d => d.Participants).Where(d => d.VesselId == vesselId).ToListAsync();
        var crew = await db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive).OrderBy(c => c.FullName).ToListAsync();
        var snapshot = new DrillSnapshot(types, overrides, drills, crew);
        return DrillSchedules.Build(vessel.Id, vessel.Name, day, snapshot);
    }

    /// <summary>
    /// One crew member's own drills: for each type everyone must attend, when they last took part and when they are next due. A crew member sees their own; the Master and the office anyone's.
    /// </summary>
    public async Task<List<DrillPersonalRow>> PersonalAsync(Guid companyId, Guid vesselId, Guid crewId, Actor actor, DateTime? today = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (!who.IsOffice && (who.Crew!.VesselId != vesselId || (crewId != who.Crew.Id && !who.IsMaster))) throw new InvalidOperationException("You see your own drills; the Master sees anyone's.");
        var types = await db.DrillTypes.AsNoTracking().Where(t => t.CompanyId == companyId && t.IsActive).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
        var overrides = (await db.DrillOverrides.AsNoTracking().Where(o => o.VesselId == vesselId).ToListAsync()).ToDictionary(o => o.DrillTypeId);
        var drills = await db.Drills.AsNoTracking().Include(d => d.Participants).Where(d => d.VesselId == vesselId).ToListAsync();
        var crew = await db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive).ToListAsync();
        return DrillSchedules.Personal(new DrillSnapshot(types, overrides, drills, crew), crewId, (today ?? DateTime.UtcNow).Date);
    }
}

/// <summary>Everything a schedule is worked out from, so the working-out is a pure function.</summary>
public sealed record DrillSnapshot(IReadOnlyList<DrillType> Types, IReadOnlyDictionary<Guid, DrillOverride> Overrides, IReadOnlyList<Drill> Drills, IReadOnlyList<CrewMember> Crew);

public static class DrillSchedules
{
    /// <summary>One person's standing for each type everyone must attend (the types that apply on the vessel), with nothing left out: a person who is not due yet is shown with their date.</summary>
    public static List<DrillPersonalRow> Personal(DrillSnapshot s, Guid crewId, DateTime today)
    {
        var member = s.Crew.FirstOrDefault(c => c.Id == crewId);
        var rows = new List<DrillPersonalRow>();
        foreach (var type in s.Types)
        {
            var over = s.Overrides.GetValueOrDefault(type.Id);
            if (over is { Applies: false }) continue;
            var rule = DrillRules.Effective(type, over);
            if (!rule.EveryCrewMember) continue;
            var took = s.Drills.Where(d => d.DrillTypeId == type.Id && d.Participants.Any(p => p.CrewMemberId == crewId && p.Attendance == DrillAttendance.Present))
                .Select(d => (DateTime?)d.StartedAt.Date).DefaultIfEmpty(null).Max();
            rows.Add(new DrillPersonalRow(type, took, DrillRules.PersonDue(rule, took, member?.SignOnDate), DrillRules.PersonStatus(rule, took, member?.SignOnDate, today)));
        }
        return rows;
    }

    public static DrillSchedule Build(Guid vesselId, string vesselName, DateTime today, DrillSnapshot s)
    {
        var rows = new List<DrillScheduleRow>();
        foreach (var type in s.Types)
        {
            var over = s.Overrides.GetValueOrDefault(type.Id);
            if (over is { Applies: false }) continue;
            var rule = DrillRules.Effective(type, over);
            var held = s.Drills.Where(d => d.DrillTypeId == type.Id).ToList();
            DateTime? last = held.Count == 0 ? null : held.Max(d => d.StartedAt.Date);
            var shipStatus = DrillRules.StatusOf(rule, last, today);

            var people = new List<DrillPersonDue>();
            if (rule.EveryCrewMember)
                foreach (var member in s.Crew)
                {
                    var took = held.Where(d => d.Participants.Any(p => p.CrewMemberId == member.Id && p.Attendance == DrillAttendance.Present)).Select(d => (DateTime?)d.StartedAt.Date).DefaultIfEmpty(null).Max();
                    var status = DrillRules.PersonStatus(rule, took, member.SignOnDate, today);
                    if (status is DrillStatus.NotDue) continue;
                    people.Add(new DrillPersonDue(member.Id, member.FullName, member.Rank, took, DrillRules.PersonDue(rule, took, member.SignOnDate), status));
                }
            rows.Add(new DrillScheduleRow(type, rule, last, DrillRules.DueDate(rule, last), shipStatus, DrillRules.Worst([shipStatus, .. people.Select(p => p.Status)]), people));
        }

        var (from, to) = DrillRules.PreviousMonth(today);
        var everyoneTypes = rows.Where(r => r.Rule.EveryCrewMember).Select(r => r.Type.Id).ToHashSet();
        var tookPart = s.Drills.Where(d => everyoneTypes.Contains(d.DrillTypeId) && d.StartedAt.Date >= from && d.StartedAt.Date <= to)
            .SelectMany(d => d.Participants.Where(p => p.Attendance == DrillAttendance.Present && p.CrewMemberId is not null).Select(p => p.CrewMemberId!.Value)).ToList();
        // With no type that everyone attends on this vessel there is nothing to count against, so the rule cannot be triggered.
        var quarter = everyoneTypes.Count == 0 ? new DrillQuarter(s.Crew.Count, 0, false) : DrillRules.QuarterRule(s.Crew.Select(c => c.Id).ToList(), tookPart);
        return new DrillSchedule(vesselId, vesselName, today, rows, DrillRules.Worst(rows.Select(r => r.Status)), quarter);
    }
}

/// <summary>The starting list of drill types for a new company. Every interval is a common default to check against the flag state, class and the safety management system.</summary>
public static class DrillDefaults
{
    private const string Check = " (to verify against the flag state, class and the SMS)";

    private static readonly (string Name, DrillCategory Category, DrillIntervalKind Kind, int Count, bool Everyone, string Reference, string Checklist)[] Types =
    [
        ("Fire drill", DrillCategory.Emergency, DrillIntervalKind.CalendarMonths, 1, true, "SOLAS III/19" + Check, "General alarm sounded\nMuster and roll call\nFire party and equipment\nFire pump and hydrants\nBreathing apparatus\nCommunications"),
        ("Abandon ship drill", DrillCategory.Emergency, DrillIntervalKind.CalendarMonths, 1, true, "SOLAS III/19" + Check, "Alarm and muster\nLifejackets and immersion suits\nLifeboat engine started and lowered where the rules allow\nRescue boat\nCommunications and distress alerting"),
        ("Man overboard", DrillCategory.Emergency, DrillIntervalKind.CalendarMonths, 3, false, "Company SMS" + Check, "Alarm and Williamson turn or the company's manoeuvre\nLookouts posted\nRescue boat readiness"),
        ("Enclosed space entry and rescue", DrillCategory.Emergency, DrillIntervalKind.CalendarMonths, 2, false, "SOLAS XI-1/7" + Check, "Entry permit procedure\nGas testing\nRescue equipment ready\nCasualty extraction"),
        ("Steering gear emergency", DrillCategory.Emergency, DrillIntervalKind.CalendarMonths, 3, false, "SOLAS V/26" + Check, "Change to emergency steering\nCommunications bridge to steering gear room"),
        ("Blackout", DrillCategory.Emergency, DrillIntervalKind.CalendarMonths, 3, false, "Company SMS" + Check, "Emergency generator start\nRestoring main power\nBridge and engine room communications"),
        ("Oil spill (SOPEP)", DrillCategory.Pollution, DrillIntervalKind.CalendarMonths, 3, false, "MARPOL Annex I, SOPEP" + Check, "Reporting\nSpill containment equipment\nSounding and transfer to reduce the spill"),
        ("Security drill", DrillCategory.Security, DrillIntervalKind.CalendarMonths, 3, false, "ISPS Code A/13" + Check, "Security alert\nAccess control\nSearch and reporting"),
        ("Medical emergency", DrillCategory.Medical, DrillIntervalKind.CalendarMonths, 6, false, "Company SMS" + Check, "First response\nMedical chest and oxygen\nRadio medical advice"),
        ("Collision or grounding", DrillCategory.Emergency, DrillIntervalKind.CalendarMonths, 6, false, "Company SMS" + Check, "Damage control\nWatertight doors\nReporting"),
        ("Cargo or fuel spill", DrillCategory.Pollution, DrillIntervalKind.CalendarMonths, 6, false, "Company SMS, SMPEP" + Check, "Stop transfer\nContainment\nReporting"),
    ];

    public static async Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId)
    {
        if (await db.DrillTypes.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == companyId)) return;
        var order = 0;
        foreach (var (name, category, kind, count, everyone, reference, checklist) in Types)
            db.DrillTypes.Add(new DrillType
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, Name = name, Category = category, IntervalKind = kind, IntervalCount = count, EveryCrewMember = everyone,
                WarningDays = 7, Reference = reference, Checklist = checklist, SortOrder = ++order,
            });
    }
}
