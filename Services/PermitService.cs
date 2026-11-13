using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public sealed record PermitInput(Guid PermitTypeId, string Location, string Description, DateTime ValidFrom, DateTime ValidUntil, IReadOnlyList<Guid> PersonCrewIds,
    Guid? AttendantCrewId = null, Guid? TankId = null, Guid? MachineryItemId = null, Guid? RiskAssessmentId = null, Guid? PlannedJobId = null, string Isolation = "");

public sealed record PermitAnswerInput(Guid AnswerId, PermitAnswerKind Answer, string Note = "");

public sealed record PermitGasInput(string Instrument, DateTime? CalibrationDate, string Location, decimal? Oxygen, decimal? FlammablePercentLel, decimal? H2sPpm = null, decimal? CoPpm = null);

public sealed record PermitChecklistInput(string Section, string Text, bool Mandatory = true, bool NoteRequired = false);

public enum PermitCloseStatus { Finished, LeftUnsafe, HandedOver }

/// <summary>What the person looking at a permit may do now: the rules the service enforces, so a page offers only what will be accepted.</summary>
public sealed record PermitCan(bool IsOffice, bool Answer, bool Gas, bool Approve, bool Issue, bool Start, bool Enter, bool Suspend, bool Resume, bool Extend, bool FinishWork,
    bool Complete, bool Cancel, bool Withdraw, bool HandOver, bool Comment);

public sealed record PermitIssueCheck(IReadOnlyList<string> Missing, IReadOnlyList<PermitConflictInfo> Conflicts)
{
    public bool CanIssue => Missing.Count == 0;
}

public sealed record PermitConflictInfo(Guid PermitId, string Code, string Message);

public sealed record PermitDetail(Permit Permit, string VesselName, PermitTerms Terms, PermitState State, PermitValidity Validity, PermitGasStatus Gas, DateTime? NextGasDue, DateTime Until,
    IReadOnlyList<PermitEntry> Inside, IReadOnlyList<PermitComment> Comments, IReadOnlyList<WorklistTask> Actions, PermitIssueCheck Check, PermitCan Can);

public sealed record PermitRow(Permit Permit, string VesselName, PermitState State, bool Valid, string Reason, DateTime Until, int Inside);

/// <summary>Permits on one vessel's board: those that are open, and the pairs of open permits that conflict.</summary>
public sealed record PermitBoard(IReadOnlyList<PermitRow> Rows, IReadOnlyList<(Guid A, Guid B, string Message)> Overlaps);

/// <summary>
/// Permit to work (spec 14). The permit is the vessel's document: written and signed onboard, its events only ever added. The office keeps the types, checklists and conflict
/// pairs, and comments. Everything is checked here, whatever the page lets through. The state is worked out by <see cref="PermitRules"/>, never stored.
/// </summary>
public class PermitService(IDbContextFactory<ChandleryDbContext> factory, WorklistService worklist)
{
    /// <summary>The clock. A property so tests can move time.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime Now => Clock();

    private sealed record Who(bool IsOffice, Guid? UserId, string Name, CrewMember? Crew)
    {
        public bool IsMaster => Crew?.IsMaster == true;
        public bool MayIssue => IsMaster || Crew?.Has(CrewAuthority.IssuePermits) == true;
    }

    // ---- the module ----

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.PermitToWork);

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.PermitToWork);
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    private static async Task<Who> WhoAsync(ChandleryDbContext db, Guid companyId, Actor actor)
    {
        if (!db.Node.IsVessel) return new(true, actor.UserId, actor.Name, null);
        var crew = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.UserId == actor.UserId && c.VesselId != null && c.IsActive)
            ?? throw new InvalidOperationException("Permits are worked onboard by the crew of the vessel.");
        return new(false, actor.UserId, crew.FullName, crew);
    }

    private static Who Aboard(Who who, Permit permit, string what)
    {
        if (who.IsOffice || who.Crew!.VesselId != permit.VesselId) throw new InvalidOperationException(what);
        return who;
    }

    // ---- types, checklists and conflict pairs (the office's, copied to every vessel) ----

    public async Task<List<PermitType>> TypesAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.PermitTypes.AsNoTracking().Where(t => t.CompanyId == companyId);
        if (!includeInactive) query = query.Where(t => t.IsActive);
        return await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
    }

    /// <summary>Adds or changes a permit type. A change applies to permits requested after it; a permit already requested keeps the terms it was requested under.</summary>
    public async Task<PermitType> SaveTypeAsync(Guid companyId, PermitType input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The list of permit types is the office's.");
        var name = (input.Name ?? "").Trim();
        var code = (input.Code ?? "").Trim().ToUpperInvariant();
        if (name.Length < 2) throw new InvalidOperationException("Give the permit type a name.");
        if (code.Length is < 2 or > 6 || !code.All(char.IsLetterOrDigit)) throw new InvalidOperationException("The code is two to six letters or digits.");
        if (await db.PermitTypes.AnyAsync(t => t.CompanyId == companyId && t.Name == name && t.Id != input.Id)) throw new InvalidOperationException($"{name} is already in the list.");
        if (await db.PermitTypes.AnyAsync(t => t.CompanyId == companyId && t.Code == code && t.Id != input.Id)) throw new InvalidOperationException($"The code {code} is used by another type.");
        if (input.MaxValidityHours is < 1 or > 72) throw new InvalidOperationException("The longest validity is 1 to 72 hours.");
        if (input.GasTestRequired)
        {
            if (input.GasRepeatMinutes is < 5 or > 240) throw new InvalidOperationException("The gas reading is repeated every 5 to 240 minutes.");
            if (input.OxygenMin < 0 || input.OxygenMax > 100 || input.OxygenMin >= input.OxygenMax) throw new InvalidOperationException("Check the oxygen limits.");
            if (input.FlammableMaxPercentLel is < 0 or > 100 || input.H2sMaxPpm < 0 || input.CoMaxPpm < 0) throw new InvalidOperationException("Check the gas limits.");
        }

        var type = input.Id == Guid.Empty ? null : await db.PermitTypes.FirstOrDefaultAsync(t => t.Id == input.Id && t.CompanyId == companyId) ?? throw new InvalidOperationException("That permit type is not in the list.");
        if (type is null)
        {
            type = new PermitType { Id = Guid.CreateVersion7(), CompanyId = companyId, SortOrder = (await db.PermitTypes.Where(t => t.CompanyId == companyId).MaxAsync(t => (int?)t.SortOrder) ?? 0) + 1 };
            db.PermitTypes.Add(type);
        }
        (type.Name, type.Code, type.MaxValidityHours, type.RequesterRole, type.SameRequesterAndIssuer) = (name, code, input.MaxValidityHours, input.RequesterRole, input.SameRequesterAndIssuer);
        (type.GasTestRequired, type.GasRepeatMinutes, type.OxygenMin, type.OxygenMax) = (input.GasTestRequired, input.GasRepeatMinutes, input.OxygenMin, input.OxygenMax);
        (type.FlammableMaxPercentLel, type.H2sMaxPpm, type.CoMaxPpm) = (input.FlammableMaxPercentLel, input.H2sMaxPpm, input.CoMaxPpm);
        (type.AttendantRequired, type.EntryLogRequired, type.RiskAssessmentRequired, type.MasterApprovalRequired) = (input.AttendantRequired, input.EntryLogRequired, input.RiskAssessmentRequired, input.MasterApprovalRequired);
        (type.Reference, type.IsActive) = ((input.Reference ?? "").Trim(), input.IsActive);
        await db.SaveChangesAsync();
        return type;
    }

    public async Task<List<PermitChecklistItem>> ChecklistAsync(Guid companyId, Guid typeId, int? version = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var current = version ?? await db.PermitTypes.AsNoTracking().Where(t => t.Id == typeId && t.CompanyId == companyId).Select(t => t.ChecklistVersion).FirstOrDefaultAsync();
        return await db.PermitChecklistItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.PermitTypeId == typeId && i.Version == current).OrderBy(i => i.Position).ToListAsync();
    }

    /// <summary>
    /// Saves the checklist as a new version, when it differs from the current one. Items of older versions stay, so a permit issued on an older version still reads as it was.
    /// Returns the version now in force.
    /// </summary>
    public async Task<int> SaveChecklistAsync(Guid companyId, Guid typeId, IReadOnlyList<PermitChecklistInput> items)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The checklists are the office's.");
        var type = await db.PermitTypes.FirstOrDefaultAsync(t => t.Id == typeId && t.CompanyId == companyId) ?? throw new InvalidOperationException("That permit type is not in the list.");
        var clean = items.Select(i => new PermitChecklistInput((i.Section ?? "").Trim(), (i.Text ?? "").Trim(), i.Mandatory, i.NoteRequired)).Where(i => i.Text.Length > 0).ToList();
        if (clean.Count == 0) throw new InvalidOperationException("A checklist needs at least one item.");
        if (clean.Any(i => i.Text.Length > 500)) throw new InvalidOperationException("An item is longer than 500 characters.");

        var now = await db.PermitChecklistItems.AsNoTracking().Where(i => i.PermitTypeId == typeId && i.Version == type.ChecklistVersion).OrderBy(i => i.Position).ToListAsync();
        if (now.Count == clean.Count && now.Zip(clean).All(p => (p.First.Section, p.First.Text, p.First.Mandatory, p.First.NoteRequired) == (p.Second.Section, p.Second.Text, p.Second.Mandatory, p.Second.NoteRequired)))
            return type.ChecklistVersion;
        var version = (now.Count == 0 ? type.ChecklistVersion : type.ChecklistVersion + 1);
        var position = 0;
        foreach (var i in clean)
            db.PermitChecklistItems.Add(new PermitChecklistItem { Id = Guid.CreateVersion7(), CompanyId = companyId, PermitTypeId = typeId, Version = version, Position = ++position, Section = i.Section, Text = i.Text, Mandatory = i.Mandatory, NoteRequired = i.NoteRequired });
        type.ChecklistVersion = version;
        await db.SaveChangesAsync();
        return version;
    }

    public async Task<List<PermitConflictPair>> ConflictPairsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.PermitConflictPairs.AsNoTracking().Where(p => p.CompanyId == companyId).ToListAsync();
    }

    public async Task<PermitConflictPair> SaveConflictPairAsync(Guid companyId, Guid typeA, Guid typeB, bool sameLocationOnly, string message)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The conflict pairs are the office's.");
        if (typeA == typeB) throw new InvalidOperationException("Choose two different permit types.");
        var found = await db.PermitTypes.AsNoTracking().CountAsync(t => t.CompanyId == companyId && (t.Id == typeA || t.Id == typeB));
        if (found != 2) throw new InvalidOperationException("Both permit types must be in the list.");
        if (await db.PermitConflictPairs.AnyAsync(p => p.CompanyId == companyId && ((p.PermitTypeAId == typeA && p.PermitTypeBId == typeB) || (p.PermitTypeAId == typeB && p.PermitTypeBId == typeA))))
            throw new InvalidOperationException("That pair is already listed.");
        message = (message ?? "").Trim();
        if (message.Length < 3) throw new InvalidOperationException("Say what the warning is.");
        var pair = new PermitConflictPair { Id = Guid.CreateVersion7(), CompanyId = companyId, PermitTypeAId = typeA, PermitTypeBId = typeB, SameLocationOnly = sameLocationOnly, Message = message };
        db.PermitConflictPairs.Add(pair);
        await db.SaveChangesAsync();
        return pair;
    }

    public async Task RemoveConflictPairAsync(Guid companyId, Guid pairId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The conflict pairs are the office's.");
        var pair = await db.PermitConflictPairs.FirstOrDefaultAsync(p => p.Id == pairId && p.CompanyId == companyId) ?? throw new InvalidOperationException("That pair is not listed.");
        db.PermitConflictPairs.Remove(pair);
        await db.SaveChangesAsync();
    }

    // ---- loading and appending ----

    private static IQueryable<Permit> Full(ChandleryDbContext db) =>
        db.Permits.Include(p => p.Answers).Include(p => p.Persons).Include(p => p.Readings).Include(p => p.Entries).Include(p => p.Events).AsSplitQuery();

    private static async Task<Permit> LoadAsync(ChandleryDbContext db, Guid companyId, Guid permitId) =>
        await Full(db).FirstOrDefaultAsync(p => p.Id == permitId && p.CompanyId == companyId) ?? throw new InvalidOperationException("Permit not found.");

    private PermitEvent Add(ChandleryDbContext db, Permit permit, PermitEventKind kind, string by, Guid? byCrew, string note = "", DateTime? until = null)
    {
        var e = new PermitEvent { Id = Guid.CreateVersion7(), PermitId = permit.Id, Kind = kind, At = Now, ByName = by, ByCrewId = byCrew, Note = note, Until = until };
        db.PermitEvents.Add(e);
        return e;
    }

    private PermitState StateOf(Permit p) => PermitRules.State(p.Events, p.ValidUntil, Now);

    private static bool IsRequester(Who who, Permit p) => who.Crew is not null && p.RequesterCrewId == who.Crew.Id;
    private static bool IsAttendant(Who who, Permit p) => who.Crew is not null && p.AttendantCrewId == who.Crew.Id;

    /// <summary>Those who work a permit: the person in charge, the attendant, an issuer and the Master.</summary>
    private static bool Works(Who who, Permit p) => !who.IsOffice && who.Crew!.VesselId == p.VesselId && (IsRequester(who, p) || IsAttendant(who, p) || who.MayIssue);

    private static void Need(PermitState actual, string what, params PermitState[] allowed)
    {
        if (!allowed.Contains(actual)) throw new InvalidOperationException($"{what} The permit is {Words(actual)}.");
    }

    private static string Words(PermitState s) => s switch
    {
        PermitState.Requested => "waiting to be issued",
        PermitState.Issued => "issued but not started",
        PermitState.Active => "active",
        PermitState.Suspended => "suspended",
        PermitState.Expired => "expired and not closed",
        PermitState.Completed => "completed",
        PermitState.Cancelled => "cancelled",
        _ => "withdrawn",
    };

    private async Task<WorklistTask?> RaiseTaskAsync(ChandleryDbContext db, Permit permit, string title, string body, Actor actor)
    {
        try
        {
            var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == permit.CompanyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == "Vessel").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
            var category = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == permit.CompanyId && i.Kind == WorklistListKind.Category && i.IsActive && i.ParentId == null && i.Name == "Safety").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
            var task = await worklist.CreateTaskAsync(permit.CompanyId, new NewWorklistTask(permit.VesselId, title, $"{body}{Environment.NewLine}{Environment.NewLine}Permit {permit.Code} ({permit.TypeName}), {permit.Location}.", source, CategoryId: category, Priority: WorklistPriority.Urgent), actor);
            await worklist.AddEntryAsync(permit.CompanyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Permit, LinkTarget: permit.Id, LinkLabel: $"Permit {permit.Code}"), actor);
            return task;
        }
        catch (InvalidOperationException) { return null; }     // the worklist is not switched on for this vessel: the permit page still shows it
    }

    // ---- requesting, and the checklist ----

    /// <summary>Requests a permit: the type's checklist at its current version is copied onto it, with the type's terms. By a crew member the type lets request.</summary>
    public async Task<Permit> RequestAsync(Guid companyId, Guid vesselId, Actor actor, PermitInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (who.IsOffice) throw new InvalidOperationException("Permits are requested onboard; the office does not issue or sign them.");
        if (who.Crew!.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Permit to work is not switched on for this vessel.");

        var type = await db.PermitTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == input.PermitTypeId && t.CompanyId == companyId) ?? throw new InvalidOperationException("Choose the kind of permit.");
        if (!type.IsActive) throw new InvalidOperationException($"{type.Name} is no longer in use.");
        if (!PermitRules.Meets(type.RequesterRole, who.IsMaster, who.Crew.Rank))
            throw new InvalidOperationException($"A {type.Name.ToLowerInvariant()} permit is requested by {(type.RequesterRole == PermitRole.Master ? "the Master" : "an officer")}.");
        var location = (input.Location ?? "").Trim();
        if (location.Length < 2) throw new InvalidOperationException("Say where the work is.");
        var description = (input.Description ?? "").Trim();
        if (description.Length < 3) throw new InvalidOperationException("Describe the work.");
        var from = DateTime.SpecifyKind(input.ValidFrom, DateTimeKind.Utc);
        var until = DateTime.SpecifyKind(input.ValidUntil, DateTimeKind.Utc);
        if (until <= from) throw new InvalidOperationException("The permit must end after it starts.");
        if (until - from > TimeSpan.FromHours(type.MaxValidityHours)) throw new InvalidOperationException($"A {type.Name.ToLowerInvariant()} permit is valid for at most {type.MaxValidityHours} hours.");
        if (until <= Now) throw new InvalidOperationException("The permit would already have ended.");

        var crew = await db.CrewMembers.AsNoTracking().Where(c => c.CompanyId == companyId && c.VesselId == vesselId && c.IsActive).ToListAsync();
        var people = (input.PersonCrewIds ?? []).Distinct().ToList();
        if (people.Any(id => crew.All(c => c.Id != id))) throw new InvalidOperationException("A person named for the work is not one of the vessel's crew.");
        CrewMember? attendant = null;
        if (input.AttendantCrewId is { } attendantId) attendant = crew.FirstOrDefault(c => c.Id == attendantId) ?? throw new InvalidOperationException("The attendant is not one of the vessel's crew.");
        if (attendant is not null && people.Contains(attendant.Id)) throw new InvalidOperationException("The attendant cannot be one of the people doing the work.");
        if (input.TankId is { } tank && !await db.Tanks.AnyAsync(t => t.Id == tank && t.VesselId == vesselId)) throw new InvalidOperationException("That tank is not on this vessel.");
        if (input.MachineryItemId is { } machine && !await db.MachineryItems.AnyAsync(m => m.Id == machine && m.VesselId == vesselId)) throw new InvalidOperationException("That equipment is not on this vessel's maintenance plan.");
        if (input.RiskAssessmentId is { } risk && !await db.RiskAssessments.AnyAsync(r => r.Id == risk && r.VesselId == vesselId)) throw new InvalidOperationException("That risk assessment is not for this vessel.");

        var items = await db.PermitChecklistItems.AsNoTracking().Where(i => i.PermitTypeId == type.Id && i.Version == type.ChecklistVersion).OrderBy(i => i.Position).ToListAsync();
        var permit = new Permit
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CreatedAt = Now, OwnerStage = SyncStages.Vessel,
            Code = await DocumentNumbers.NextAsync(db, companyId, vesselId, DocType.Permit),
            PermitTypeId = type.Id, TypeName = type.Name, ChecklistVersion = type.ChecklistVersion, Terms = PermitRules.Serialize(PermitRules.TermsOf(type)),
            Location = location, Description = description, Isolation = (input.Isolation ?? "").Trim(), TankId = input.TankId, MachineryItemId = input.MachineryItemId,
            RiskAssessmentId = input.RiskAssessmentId, PlannedJobId = input.PlannedJobId,
            RequesterCrewId = who.Crew.Id, RequesterName = who.Name, AttendantCrewId = attendant?.Id, AttendantName = attendant?.FullName ?? "",
            ValidFrom = from, ValidUntil = until,
        };
        // The permit code carries the type: PTW-{vessel}-{TYPE}-... is built from the generic number, so the type's letters are added after the vessel code.
        permit.Code = permit.Code.Replace("-PTW-", $"-PTW-{type.Code}-", StringComparison.Ordinal);
        db.Permits.Add(permit);
        foreach (var i in items)
            db.PermitAnswers.Add(new PermitAnswer { Id = Guid.CreateVersion7(), PermitId = permit.Id, ChecklistItemId = i.Id, Position = i.Position, Section = i.Section, Text = i.Text, Mandatory = i.Mandatory, NoteRequired = i.NoteRequired });
        foreach (var id in people)
            db.PermitPersons.Add(new PermitPerson { Id = Guid.CreateVersion7(), PermitId = permit.Id, CrewMemberId = id, Name = crew.First(c => c.Id == id).FullName });
        Add(db, permit, PermitEventKind.Requested, who.Name, who.Crew.Id);
        if (items.All(i => !i.Mandatory)) Add(db, permit, PermitEventKind.ChecklistCompleted, who.Name, who.Crew.Id);
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>Answers checklist items. Open until the permit is issued; after that the checklist is as signed. By the person in charge, an issuer or the Master.</summary>
    public async Task<Permit> AnswerAsync(Guid companyId, Guid permitId, Actor actor, IReadOnlyList<PermitAnswerInput> answers)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        if (!Works(who, permit)) throw new InvalidOperationException("The checklist is answered by the person in charge, the issuer or the Master.");
        Need(StateOf(permit), "The checklist is closed once the permit is issued.", PermitState.Requested);
        foreach (var a in answers)
        {
            var row = permit.Answers.FirstOrDefault(x => x.Id == a.AnswerId) ?? throw new InvalidOperationException("That item is not on this permit.");
            if (a.Answer == PermitAnswerKind.Unanswered) throw new InvalidOperationException("Answer yes, no or not applicable.");
            row.Answer = a.Answer;
            row.Note = (a.Note ?? "").Trim();
            (row.ByName, row.ByCrewId, row.At) = (who.Name, who.Crew!.Id, Now);
        }
        var done = permit.Answers.Where(x => x.Mandatory).All(x => x.Answer is PermitAnswerKind.Yes or PermitAnswerKind.NotApplicable);
        var marked = permit.Events.Any(e => e.Kind == PermitEventKind.ChecklistCompleted);
        if (done && !marked) Add(db, permit, PermitEventKind.ChecklistCompleted, who.Name, who.Crew!.Id);
        await db.SaveChangesAsync();
        return permit;
    }

    // ---- gas ----

    /// <summary>
    /// Records a gas reading against the type's limits. One outside the limits is kept in the record and stops the permit: an active permit is suspended and a task is raised. By the
    /// person in charge, the attendant, an issuer or the Master.
    /// </summary>
    public async Task<PermitGasReading> RecordGasAsync(Guid companyId, Guid permitId, Actor actor, PermitGasInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        if (!Works(who, permit)) throw new InvalidOperationException("A gas reading is recorded by the person in charge, the attendant, the issuer or the Master.");
        var terms = PermitRules.ReadTerms(permit.Terms);
        if (!terms.GasTestRequired) throw new InvalidOperationException("This kind of permit does not need a gas test.");
        var state = StateOf(permit);
        Need(state, "A reading cannot be added.", PermitState.Requested, PermitState.Issued, PermitState.Active, PermitState.Suspended);
        if ((input.Instrument ?? "").Trim().Length < 2) throw new InvalidOperationException("Name the gas detector used.");
        if (input.CalibrationDate is { } cal && cal.Date > Now.Date) throw new InvalidOperationException("The calibration date is in the future.");
        if (new[] { input.Oxygen, input.FlammablePercentLel, input.H2sPpm, input.CoPpm }.Any(v => v < 0)) throw new InvalidOperationException("A reading cannot be negative.");

        var failures = PermitRules.GasFailures(terms, input.Oxygen, input.FlammablePercentLel, input.H2sPpm, input.CoPpm);
        var reading = new PermitGasReading
        {
            Id = Guid.CreateVersion7(), PermitId = permit.Id, At = Now, Instrument = input.Instrument.Trim(), CalibrationDate = input.CalibrationDate?.Date, Location = (input.Location ?? "").Trim(),
            Oxygen = input.Oxygen, FlammablePercentLel = input.FlammablePercentLel, H2sPpm = input.H2sPpm, CoPpm = input.CoPpm, WithinLimits = failures.Count == 0,
            Failures = string.Join("; ", failures), ByName = who.Name, ByCrewId = who.Crew!.Id,
        };
        db.PermitGasReadings.Add(reading);
        Add(db, permit, PermitEventKind.GasTested, who.Name, who.Crew.Id, failures.Count == 0 ? "Within limits" : reading.Failures);
        if (failures.Count > 0 && state == PermitState.Active) Add(db, permit, PermitEventKind.Suspended, who.Name, who.Crew.Id, "Gas reading outside limits");
        await db.SaveChangesAsync();
        if (failures.Count > 0) await RaiseTaskAsync(db, permit, $"Gas reading outside limits on {permit.Code}", reading.Failures, new Actor(who.UserId, who.Name));
        return reading;
    }

    // ---- issuing ----

    private async Task<PermitIssueCheck> CheckAsync(ChandleryDbContext db, Permit permit, Who who)
    {
        var terms = PermitRules.ReadTerms(permit.Terms);
        var riskSigned = false;
        if (permit.RiskAssessmentId is { } rid && await db.RiskAssessments.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid) is { } risk)
            riskSigned = RaStatusRules.Of(risk.SubmissionCount, await db.RaSignOffs.AsNoTracking().Where(s => s.AssessmentId == risk.Id).ToListAsync()) == RaStatus.Signed;
        var facts = new PermitIssueFacts(terms, permit.Answers, permit.Readings, permit.Events, permit.RequesterCrewId, who.Crew?.Id, who.MayIssue && !who.IsOffice, permit.AttendantCrewId,
            permit.Persons.Select(p => p.CrewMemberId).ToList(), permit.RiskAssessmentId is not null, riskSigned, permit.ValidFrom, permit.ValidUntil, Now);
        var missing = PermitRules.IssueCheck(facts);
        return new(missing, await ConflictsAsync(db, permit));
    }

    private async Task<List<PermitConflictInfo>> ConflictsAsync(ChandleryDbContext db, Permit permit)
    {
        var pairs = await db.PermitConflictPairs.AsNoTracking().Where(p => p.CompanyId == permit.CompanyId).ToListAsync();
        if (pairs.Count == 0) return [];
        var others = await Full(db).AsNoTracking().Where(p => p.VesselId == permit.VesselId && p.Id != permit.Id).ToListAsync();
        var overlapping = others.Where(o => PermitRules.State(o.Events, o.ValidUntil, Now) is PermitState.Issued or PermitState.Active or PermitState.Suspended
            && o.ValidFrom < PermitRules.Until(permit.ValidUntil, permit.Events) && permit.ValidFrom < PermitRules.Until(o.ValidUntil, o.Events)).ToList();
        var codes = overlapping.ToDictionary(o => o.Id, o => o.Code);
        var board = overlapping.Select(o => new PermitOnBoard(o.Id, o.PermitTypeId, o.Location));
        return PermitRules.Conflicts(permit.PermitTypeId, permit.Location, board, pairs).Select(c => new PermitConflictInfo(c.PermitId, codes[c.PermitId], c.Message)).ToList();
    }

    /// <summary>What stops this permit being issued by this person, and the permits it would conflict with. Changes nothing.</summary>
    public async Task<PermitIssueCheck> CheckIssueAsync(Guid companyId, Guid permitId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        return await CheckAsync(db, permit, who);
    }

    /// <summary>The Master approves, for the types that need it. Before the permit is issued.</summary>
    public async Task<Permit> ApproveAsync(Guid companyId, Guid permitId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        if (who.IsOffice || !who.IsMaster || who.Crew!.VesselId != permit.VesselId) throw new InvalidOperationException("Only the Master approves a permit.");
        Need(StateOf(permit), "Approval comes before the permit is issued.", PermitState.Requested);
        if (permit.Events.Any(e => e.Kind == PermitEventKind.MasterApproved)) throw new InvalidOperationException("The Master has approved this permit already.");
        Add(db, permit, PermitEventKind.MasterApproved, who.Name, who.Crew.Id);
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>
    /// Issues the permit. Refused with the list of what is missing. A conflict with another permit is a warning the issuer acknowledges, and the acknowledgement is recorded.
    /// </summary>
    public async Task<Permit> IssueAsync(Guid companyId, Guid permitId, Actor actor, bool acknowledgeConflicts = false, string conditions = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "A permit is issued onboard by an authorised officer or the Master.");
        Need(StateOf(permit), "Only a permit waiting to be issued can be issued.", PermitState.Requested);
        var check = await CheckAsync(db, permit, who);
        if (!check.CanIssue) throw new InvalidOperationException("The permit cannot be issued yet: " + string.Join("; ", check.Missing) + ".");
        if (check.Conflicts.Count > 0)
        {
            if (!acknowledgeConflicts) throw new InvalidOperationException("The permit conflicts with another: " + string.Join("; ", check.Conflicts.Select(c => $"{c.Message} ({c.Code})")) + ". Acknowledge the warning to issue it.");
            Add(db, permit, PermitEventKind.ConflictAcknowledged, who.Name, who.Crew!.Id, string.Join("; ", check.Conflicts.Select(c => $"{c.Message} ({c.Code})")));
        }
        (permit.IssuerCrewId, permit.IssuerName) = (who.Crew!.Id, who.Name);
        if (!string.IsNullOrWhiteSpace(conditions)) permit.Conditions = conditions.Trim();
        Add(db, permit, PermitEventKind.Issued, who.Name, who.Crew.Id);
        await db.SaveChangesAsync();
        return permit;
    }

    // ---- working under the permit ----

    /// <summary>Starts the work: by the person in charge, the issuer or the Master, inside the window and with the gas reading in date.</summary>
    public async Task<Permit> StartAsync(Guid companyId, Guid permitId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        if (!Works(who, permit) || IsAttendant(who, permit) && !who.MayIssue && !IsRequester(who, permit)) throw new InvalidOperationException("The work is started by the person in charge, the issuer or the Master.");
        Need(StateOf(permit), "Only an issued permit is started.", PermitState.Issued);
        if (Now < permit.ValidFrom) throw new InvalidOperationException($"The permit is not valid until {permit.ValidFrom:d MMM HH:mm}.");
        var terms = PermitRules.ReadTerms(permit.Terms);
        var gas = PermitRules.GasStatus(terms, permit.Readings, Now);
        if (gas is PermitGasStatus.Missing or PermitGasStatus.OutOfLimits or PermitGasStatus.Overdue) throw new InvalidOperationException("Take a gas reading within limits before the work starts.");
        Add(db, permit, PermitEventKind.Started, who.Name, who.Crew!.Id);
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>Records a person going into the space. By the attendant, the person in charge or the Master; only someone named on the permit, never the attendant, never twice.</summary>
    public async Task<PermitEntry> EnterAsync(Guid companyId, Guid permitId, Actor actor, Guid personId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        if (!Works(who, permit)) throw new InvalidOperationException("Entries are recorded by the attendant, the person in charge or the Master.");
        var terms = PermitRules.ReadTerms(permit.Terms);
        if (!terms.EntryLogRequired) throw new InvalidOperationException("This kind of permit has no entry log.");
        var validity = PermitRules.Validity(terms, permit.Events, permit.Readings, permit.ValidFrom, permit.ValidUntil, Now);
        if (!validity.Valid) throw new InvalidOperationException($"Nobody enters while the permit is not valid ({validity.Reason.ToLowerInvariant()}).");
        var person = permit.Persons.FirstOrDefault(p => p.CrewMemberId == personId) ?? throw new InvalidOperationException("That person is not named on the permit.");
        if (permit.AttendantCrewId == personId) throw new InvalidOperationException("The attendant stays outside.");
        if (PermitRules.Inside(permit.Entries).Any(e => e.CrewMemberId == personId)) throw new InvalidOperationException($"{person.Name} is already inside.");
        var entry = new PermitEntry { Id = Guid.CreateVersion7(), PermitId = permit.Id, CrewMemberId = personId, PersonName = person.Name, In = Now, AttendantName = permit.AttendantName };
        db.PermitEntries.Add(entry);
        Add(db, permit, PermitEventKind.PersonEntered, who.Name, who.Crew!.Id, person.Name);
        await db.SaveChangesAsync();
        return entry;
    }

    /// <summary>Records a person coming out. Allowed whatever the state of the permit, so nobody is ever left on the log.</summary>
    public async Task<PermitEntry> LeaveAsync(Guid companyId, Guid permitId, Actor actor, Guid entryId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        if (!Works(who, permit)) throw new InvalidOperationException("Exits are recorded by the attendant, the person in charge or the Master.");
        var entry = permit.Entries.FirstOrDefault(e => e.Id == entryId) ?? throw new InvalidOperationException("That entry is not on this permit.");
        if (entry.Out is not null) throw new InvalidOperationException($"{entry.PersonName} is already out.");
        entry.Out = Now;
        Add(db, permit, PermitEventKind.PersonLeft, who.Name, who.Crew!.Id, entry.PersonName);
        await db.SaveChangesAsync();
        return entry;
    }

    public async Task<Permit> SuspendAsync(Guid companyId, Guid permitId, Actor actor, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        if (!Works(who, permit)) throw new InvalidOperationException("The work is suspended by the person in charge, the attendant, the issuer or the Master.");
        Need(StateOf(permit), "Only active work is suspended.", PermitState.Active);
        if ((reason ?? "").Trim().Length < 3) throw new InvalidOperationException("Say why the work is suspended.");
        Add(db, permit, PermitEventKind.Suspended, who.Name, who.Crew!.Id, reason.Trim());
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>Resumes suspended work: by an issuer or the Master, with the gas reading in date where one is needed.</summary>
    public async Task<Permit> ResumeAsync(Guid companyId, Guid permitId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "Work is resumed onboard.");
        if (!who.MayIssue) throw new InvalidOperationException("Work is resumed by an issuer or the Master.");
        Need(StateOf(permit), "Only suspended work is resumed.", PermitState.Suspended);
        var gas = PermitRules.GasStatus(PermitRules.ReadTerms(permit.Terms), permit.Readings, Now);
        if (gas is PermitGasStatus.Missing or PermitGasStatus.OutOfLimits or PermitGasStatus.Overdue) throw new InvalidOperationException("Take a gas reading within limits before the work resumes.");
        Add(db, permit, PermitEventKind.Resumed, who.Name, who.Crew!.Id);
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>A new end for the window, by an issuer, with a fresh gas reading where the type needs one. An expired permit is closed, not extended.</summary>
    public async Task<Permit> ExtendAsync(Guid companyId, Guid permitId, Actor actor, DateTime newUntil)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "A permit is extended onboard.");
        if (!who.MayIssue) throw new InvalidOperationException("A permit is extended by an issuer or the Master.");
        Need(StateOf(permit), "Only a permit still inside its window is extended; close an expired one.", PermitState.Issued, PermitState.Active, PermitState.Suspended);
        var until = DateTime.SpecifyKind(newUntil, DateTimeKind.Utc);
        var missing = PermitRules.ExtensionCheck(PermitRules.ReadTerms(permit.Terms), permit.Readings, PermitRules.Until(permit.ValidUntil, permit.Events), until, Now);
        if (missing.Count > 0) throw new InvalidOperationException("The permit cannot be extended: " + string.Join("; ", missing) + ".");
        Add(db, permit, PermitEventKind.Extended, who.Name, who.Crew!.Id, $"Until {until:d MMM HH:mm}", until);
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>Hands the work to the next person in charge, with the state of the work. By the person in charge now, or the Master.</summary>
    public async Task<Permit> HandOverAsync(Guid companyId, Guid permitId, Actor actor, Guid toCrewId, string state)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "A permit is handed over onboard.");
        if (!IsRequester(who, permit) && !who.IsMaster) throw new InvalidOperationException("The person in charge, or the Master, hands the permit over.");
        Need(StateOf(permit), "Only an open permit is handed over.", PermitState.Issued, PermitState.Active, PermitState.Suspended);
        if ((state ?? "").Trim().Length < 3) throw new InvalidOperationException("Say what state the work is in.");
        var next = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == toCrewId && c.CompanyId == companyId && c.VesselId == permit.VesselId && c.IsActive) ?? throw new InvalidOperationException("The person taking over is not one of the vessel's crew.");
        if (next.Id == permit.RequesterCrewId) throw new InvalidOperationException("The permit is with that person already.");
        var terms = PermitRules.ReadTerms(permit.Terms);
        if (next.Id == permit.IssuerCrewId && !terms.SameRequesterAndIssuer) throw new InvalidOperationException("The issuer cannot also be the person in charge.");
        if (next.Id == permit.AttendantCrewId) throw new InvalidOperationException("The attendant cannot be the person in charge.");
        var from = permit.RequesterName;
        (permit.RequesterCrewId, permit.RequesterName) = (next.Id, next.FullName);
        Add(db, permit, PermitEventKind.HandedOver, who.Name, who.Crew!.Id, $"{from} to {next.FullName}: {state.Trim()}");
        await db.SaveChangesAsync();
        return permit;
    }

    // ---- closing ----

    /// <summary>The person in charge signs that the work is finished, the area safe and everyone out. Refused while anyone is on the entry log.</summary>
    public async Task<Permit> FinishWorkAsync(Guid companyId, Guid permitId, Actor actor, string note = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "A permit is signed off onboard.");
        if (!IsRequester(who, permit) && !who.IsMaster) throw new InvalidOperationException("The person in charge signs that the work is finished.");
        Need(StateOf(permit), "Only an open permit is finished.", PermitState.Issued, PermitState.Active, PermitState.Suspended, PermitState.Expired);
        if (permit.Events.Any(e => e.Kind == PermitEventKind.WorkFinished)) throw new InvalidOperationException("The work is signed off already; the issuer closes the permit.");
        var blockers = PermitRules.CloseBlockers(permit.Entries);
        if (blockers.Count > 0) throw new InvalidOperationException("The permit cannot be closed: " + string.Join("; ", blockers) + ".");
        Add(db, permit, PermitEventKind.WorkFinished, who.Name, who.Crew!.Id, (note ?? "").Trim());
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>
    /// The issuer inspects and closes. Needs the person in charge's signature first, unless the permit has expired: an expired permit is closed by the issuer with the status of the
    /// work, which must be explained when it is not simply finished. A closed permit is locked.
    /// </summary>
    public async Task<Permit> CompleteAsync(Guid companyId, Guid permitId, Actor actor, PermitCloseStatus status = PermitCloseStatus.Finished, string note = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "A permit is closed onboard.");
        if (!who.MayIssue) throw new InvalidOperationException("The issuer, or the Master, inspects and closes the permit.");
        var state = StateOf(permit);
        Need(state, "Only an open permit is closed.", PermitState.Issued, PermitState.Active, PermitState.Suspended, PermitState.Expired);
        var terms = PermitRules.ReadTerms(permit.Terms);
        if (IsRequester(who, permit) && !terms.SameRequesterAndIssuer) throw new InvalidOperationException("The issuer cannot close a permit they requested.");
        if (state != PermitState.Expired && !permit.Events.Any(e => e.Kind == PermitEventKind.WorkFinished)) throw new InvalidOperationException("The person in charge signs that the work is finished first.");
        var blockers = PermitRules.CloseBlockers(permit.Entries);
        if (blockers.Count > 0) throw new InvalidOperationException("The permit cannot be closed: " + string.Join("; ", blockers) + ".");
        note = (note ?? "").Trim();
        if ((status != PermitCloseStatus.Finished || state == PermitState.Expired) && note.Length < 3) throw new InvalidOperationException("Say what state the work was left in.");
        Add(db, permit, PermitEventKind.Completed, who.Name, who.Crew!.Id, $"{status}{(note.Length > 0 ? ": " + note : "")}");
        await db.SaveChangesAsync();
        if (status == PermitCloseStatus.LeftUnsafe) await RaiseTaskAsync(db, permit, $"Work left unsafe under {permit.Code}", note, new Actor(who.UserId, who.Name));
        return permit;
    }

    /// <summary>Cancels a permit with a reason. The Master cancels any open permit; the person in charge or the issuer, one not yet started. Not while anyone is inside.</summary>
    public async Task<Permit> CancelAsync(Guid companyId, Guid permitId, Actor actor, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "A permit is cancelled onboard.");
        var state = StateOf(permit);
        Need(state, "Only an open permit is cancelled.", PermitState.Requested, PermitState.Issued, PermitState.Active, PermitState.Suspended, PermitState.Expired);
        var notStarted = state is PermitState.Requested or PermitState.Issued;
        if (!who.IsMaster && !(notStarted && (IsRequester(who, permit) || who.MayIssue))) throw new InvalidOperationException("The Master cancels a permit; the person in charge or the issuer may cancel one that has not started.");
        if ((reason ?? "").Trim().Length < 3) throw new InvalidOperationException("Say why the permit is cancelled.");
        var blockers = PermitRules.CloseBlockers(permit.Entries);
        if (blockers.Count > 0) throw new InvalidOperationException("The permit cannot be cancelled: " + string.Join("; ", blockers) + ".");
        Add(db, permit, PermitEventKind.Cancelled, who.Name, who.Crew!.Id, reason.Trim());
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>A permit raised in error is withdrawn with a reason, while it is still only a request. It is never deleted.</summary>
    public async Task<Permit> WithdrawAsync(Guid companyId, Guid permitId, Actor actor, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await LoadAsync(db, companyId, permitId);
        Aboard(who, permit, "A permit is withdrawn onboard.");
        if (!IsRequester(who, permit) && !who.IsMaster) throw new InvalidOperationException("The person who requested it, or the Master, withdraws a permit.");
        Need(StateOf(permit), "Only a request not yet issued is withdrawn; cancel an issued permit.", PermitState.Requested);
        if ((reason ?? "").Trim().Length < 3) throw new InvalidOperationException("Say why it is withdrawn.");
        Add(db, permit, PermitEventKind.Withdrawn, who.Name, who.Crew!.Id, reason.Trim());
        await db.SaveChangesAsync();
        return permit;
    }

    /// <summary>
    /// Marks the permits whose window has ended without being closed, once each, and raises a task for the issuer. Runs onboard; ashore the state is worked out from the clock when
    /// a permit is read. Returns how many it marked.
    /// </summary>
    public async Task<int> SweepAsync(Guid companyId, Guid vesselId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (!db.Node.IsVessel) return 0;
        var open = await Full(db).Where(p => p.CompanyId == companyId && p.VesselId == vesselId).ToListAsync();
        var marked = 0;
        foreach (var permit in open.Where(p => StateOf(p) == PermitState.Expired && p.Events.All(e => e.Kind != PermitEventKind.Expired)))
        {
            Add(db, permit, PermitEventKind.Expired, "System", null, $"The window ended {PermitRules.Until(permit.ValidUntil, permit.Events):d MMM HH:mm} and the permit was not closed");
            marked++;
        }
        if (marked == 0) return 0;
        await db.SaveChangesAsync();
        foreach (var permit in open.Where(p => p.Events.Any(e => e.Kind == PermitEventKind.Expired && e.At >= Now.AddSeconds(-5)) && StateOf(p) == PermitState.Expired))
            await RaiseTaskAsync(db, permit, $"Permit {permit.Code} has expired and is not closed", $"Close it with the status of the work. Issuer: {(permit.IssuerName == "" ? "not recorded" : permit.IssuerName)}.", new Actor(null, "System"));
        return marked;
    }

    // ---- the office ----

    /// <summary>The office's comment on a permit, for the audit. Written once.</summary>
    public async Task<PermitComment> CommentAsync(Guid companyId, Guid permitId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (!who.IsOffice) throw new InvalidOperationException("The office comments on a permit.");
        var permit = await db.Permits.AsNoTracking().FirstOrDefaultAsync(p => p.Id == permitId && p.CompanyId == companyId) ?? throw new InvalidOperationException("Permit not found.");
        text = (text ?? "").Trim();
        if (text.Length < 3) throw new InvalidOperationException("Write the comment.");
        var comment = new PermitComment { Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = permit.VesselId, PermitId = permit.Id, Text = text, ByName = who.Name, ByUserId = who.UserId, At = Now };
        db.PermitComments.Add(comment);
        await db.SaveChangesAsync();
        return comment;
    }

    // ---- reading ----

    public async Task<PermitDetail?> GetAsync(Guid companyId, Guid permitId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var permit = await Full(db).AsNoTracking().FirstOrDefaultAsync(p => p.Id == permitId && p.CompanyId == companyId);
        if (permit is null || (!who.IsOffice && who.Crew!.VesselId != permit.VesselId)) return null;
        var terms = PermitRules.ReadTerms(permit.Terms);
        var state = StateOf(permit);
        var validity = PermitRules.Validity(terms, permit.Events, permit.Readings, permit.ValidFrom, permit.ValidUntil, Now);
        var gas = PermitRules.GasStatus(terms, permit.Readings, Now);
        var comments = await db.PermitComments.AsNoTracking().Where(c => c.PermitId == permitId).OrderBy(c => c.At).ThenBy(c => c.Id).ToListAsync();
        var tasks = await worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.Permit, permitId);
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == permit.VesselId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";
        var check = who.IsOffice ? new PermitIssueCheck([], []) : await CheckAsync(db, permit, who);

        var open = PermitRules.IsOpen(state);
        var works = Works(who, permit);
        var gasNeeded = terms.GasTestRequired && state is PermitState.Requested or PermitState.Issued or PermitState.Active or PermitState.Suspended;
        var can = new PermitCan(who.IsOffice,
            Answer: works && state == PermitState.Requested,
            Gas: works && gasNeeded,
            Approve: !who.IsOffice && who.IsMaster && state == PermitState.Requested && terms.MasterApprovalRequired && permit.Events.All(e => e.Kind != PermitEventKind.MasterApproved),
            Issue: !who.IsOffice && who.MayIssue && state == PermitState.Requested,
            Start: works && state == PermitState.Issued && (IsRequester(who, permit) || who.MayIssue),
            Enter: works && terms.EntryLogRequired && validity.Valid,
            Suspend: works && state == PermitState.Active,
            Resume: !who.IsOffice && who.MayIssue && state == PermitState.Suspended,
            Extend: !who.IsOffice && who.MayIssue && state is PermitState.Issued or PermitState.Active or PermitState.Suspended,
            FinishWork: !who.IsOffice && (IsRequester(who, permit) || who.IsMaster) && open && permit.Events.All(e => e.Kind != PermitEventKind.WorkFinished),
            Complete: !who.IsOffice && who.MayIssue && open && (state == PermitState.Expired || permit.Events.Any(e => e.Kind == PermitEventKind.WorkFinished)),
            Cancel: !who.IsOffice && open && (who.IsMaster || (state is PermitState.Requested or PermitState.Issued && (IsRequester(who, permit) || who.MayIssue))),
            Withdraw: !who.IsOffice && state == PermitState.Requested && (IsRequester(who, permit) || who.IsMaster),
            HandOver: !who.IsOffice && (IsRequester(who, permit) || who.IsMaster) && state is PermitState.Issued or PermitState.Active or PermitState.Suspended,
            Comment: who.IsOffice);
        return new(permit, vessel, terms, state, validity, gas, PermitRules.NextGasDue(terms, PermitRules.LastReading(permit.Readings)), PermitRules.Until(permit.ValidUntil, permit.Events),
            PermitRules.Inside(permit.Entries), comments, tasks, check, can);
    }

    /// <summary>The permits an office user or a crew member may see: the office all, a vessel only its own.</summary>
    public async Task<List<PermitRow>> ListAsync(Guid companyId, Actor actor, Guid? vesselId = null, bool openOnly = false, DateTime? from = null, DateTime? to = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var query = Full(db).AsNoTracking().Where(p => p.CompanyId == companyId);
        if (!who.IsOffice) query = query.Where(p => p.VesselId == who.Crew!.VesselId);
        else if (vesselId is { } v) query = query.Where(p => p.VesselId == v);
        if (from is { } f) query = query.Where(p => p.ValidUntil >= f);
        if (to is { } t) query = query.Where(p => p.ValidFrom <= t);
        var permits = await query.OrderByDescending(p => p.ValidFrom).ToListAsync();
        var vessels = await db.Vessels.AsNoTracking().Where(v => v.CompanyId == companyId).ToDictionaryAsync(v => v.Id, v => v.Name);
        var rows = permits.Select(p =>
        {
            var terms = PermitRules.ReadTerms(p.Terms);
            var validity = PermitRules.Validity(terms, p.Events, p.Readings, p.ValidFrom, p.ValidUntil, Now);
            return new PermitRow(p, vessels.GetValueOrDefault(p.VesselId, ""), StateOf(p), validity.Valid, validity.Reason, validity.Until, PermitRules.Inside(p.Entries).Count);
        });
        return (openOnly ? rows.Where(r => PermitRules.IsOpen(r.State) || r.State == PermitState.Requested) : rows).ToList();
    }

    /// <summary>The board: what is open on a vessel now, and which pairs of open permits conflict.</summary>
    public async Task<PermitBoard> BoardAsync(Guid companyId, Actor actor, Guid? vesselId = null)
    {
        var rows = await ListAsync(companyId, actor, vesselId, openOnly: true);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var pairs = await db.PermitConflictPairs.AsNoTracking().Where(p => p.CompanyId == companyId).ToListAsync();
        var overlaps = new List<(Guid, Guid, string)>();
        var live = rows.Where(r => r.State is PermitState.Issued or PermitState.Active or PermitState.Suspended).ToList();
        foreach (var r in live)
            foreach (var c in PermitRules.Conflicts(r.Permit.PermitTypeId, r.Permit.Location, live.Where(o => o.Permit.Id != r.Permit.Id && o.Permit.VesselId == r.Permit.VesselId
                         && o.Permit.ValidFrom < r.Until && r.Permit.ValidFrom < o.Until).Select(o => new PermitOnBoard(o.Permit.Id, o.Permit.PermitTypeId, o.Permit.Location)), pairs))
                if (r.Permit.Id.CompareTo(c.PermitId) < 0) overlaps.Add((r.Permit.Id, c.PermitId, c.Message));
        return new(rows, overlaps);
    }
}
