using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public sealed record IncidentPersonInput(Guid? CrewMemberId, string Name, string Rank, string Company, IncidentPersonRole Role, string InjuryType = "", string BodyPart = "",
    IncidentTreatment Treatment = IncidentTreatment.None, int DaysLost = 0, int RestrictedDays = 0);

public sealed record IncidentInput(IncidentKind Kind, DateTime OccurredAt, string Place, IncidentShipStatus ShipStatus, string Weather, string Description, string ImmediateAction,
    Guid? EquipmentId, string EquipmentName, string RiskAssessmentRef, bool AnonymousToOffice, bool ShareLesson, int ActualConsequence, int PotentialConsequence, int Likelihood,
    IReadOnlyList<IncidentPersonInput> Persons);

public sealed record InvestigationInput(string Facts, string ImmediateCause, IReadOnlyList<string> RootCauses, string WhyChain, string ContributingFactors,
    IncidentAssessmentFinding AssessmentFinding, string Lessons, string InvestigatedBy, bool Finished);

public sealed record NewIncidentAction(string Title, string Description, DateTime? Target = null, string Assignee = "", WorklistPriority Priority = WorklistPriority.Normal);

public sealed record IncidentFilter(Guid? VesselId = null, IncidentStatus? Status = null, IncidentKind? Kind = null, bool OnlyOverdue = false, string? Text = null);

public sealed record IncidentRow(Incident Incident, IncidentStatus Status, bool InvestigationOverdue, bool ReportedLate, string ReporterName, int OpenActions, string VesselName);

public sealed record IncidentDetail(Incident Incident, string VesselName, IncidentStatus Status, IReadOnlyList<IncidentEvent> Events, IReadOnlyList<IncidentInvestigation> Investigations,
    IncidentInvestigation? Investigation, IReadOnlyList<WorklistTask> Actions, int OpenActions, IReadOnlyList<string> CloseBlockers, DateTime? InvestigationDue, bool InvestigationRequired,
    bool CanSkipInvestigation, bool InvestigationOverdue, bool ReportedLate, string ReporterName, IReadOnlyList<Incident> Repeats, bool SuggestAssessmentReview, RiskMatrix Matrix);

/// <summary>
/// Incident reports (spec 12). The report is the reporter's document; the Master's review and the vessel's findings are appended onboard, and the office's
/// investigation, notification record and closing are appended ashore. Everything is checked here, whatever the page lets through.
/// </summary>
public class IncidentService(IDbContextFactory<ChandleryDbContext> factory, WorklistService worklist)
{
    private sealed record Who(bool IsOffice, Guid? UserId, string Name, CrewMember? Crew)
    {
        public bool IsMaster => Crew?.IsMaster == true;
    }

    // ---- the module ----

    public static Task<bool> IsEnabledForVesselAsync(ChandleryDbContext db, Guid companyId, Guid vesselId) => ModuleGate.IsEnabledAsync(db, companyId, vesselId, PolicyModules.Incidents);

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Incidents);
    }

    public async Task<bool> IsEnabledForCompanyAsync(Guid companyId) => (await EnabledVesselsAsync(companyId)).Count > 0;

    private static string Stage(ChandleryDbContext db) => db.Node.IsVessel ? SyncStages.Vessel : SyncStages.Office;

    private static async Task<Who> WhoAsync(ChandleryDbContext db, Guid companyId, Actor actor)
    {
        if (!db.Node.IsVessel) return new(true, actor.UserId, actor.Name, null);
        var crew = await db.CrewMembers.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == companyId && c.UserId == actor.UserId && c.VesselId != null && c.IsActive)
            ?? throw new InvalidOperationException("Incident reports are written onboard by the crew of the vessel.");
        return new(false, actor.UserId, crew.FullName, crew);
    }

    private static void RequireOffice(Who who, string what = "This is done by the office.")
    {
        if (!who.IsOffice) throw new InvalidOperationException(what);
    }

    private static void RequireOnboard(Who who, Guid vesselId, string what = "This is done onboard.")
    {
        if (who.IsOffice) throw new InvalidOperationException(what);
        if (who.Crew!.VesselId != vesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
    }

    // ---- settings and lists (the office's, copied to every vessel) ----

    public static async Task<IncidentLimits> LimitsAsync(ChandleryDbContext db, Guid companyId)
        => IncidentLimits.Of(await db.IncidentSettingsRows.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId));

    public async Task<IncidentSettings> SettingsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.IncidentSettingsRows.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId) ?? new IncidentSettings { CompanyId = companyId };
    }

    public async Task<IncidentSettings> SaveSettingsAsync(Guid companyId, IncidentSettings input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The incident settings are the office's.");
        if (input.LateReportHours is < 1 or > 24 * 30) throw new InvalidOperationException("A report is late after 1 to 720 hours.");
        if (input.RepeatWindowMonths is < 1 or > 120) throw new InvalidOperationException("The window for repeats is 1 to 120 months.");
        var days = IncidentLimits.ParseDays(input.InvestigationDays);
        if (days.Count > 8) throw new InvalidOperationException("Give the days for at most 8 bands.");
        var row = await db.IncidentSettingsRows.FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (row is null) { row = new IncidentSettings { Id = Guid.CreateVersion7(), CompanyId = companyId }; db.IncidentSettingsRows.Add(row); }
        (row.LateReportHours, row.RepeatWindowMonths, row.InvestigationDays) = (input.LateReportHours, input.RepeatWindowMonths, string.Join(',', days));
        (row.UrgentForLostTime, row.UrgentForPollution, row.AllowAnonymousToOffice) = (input.UrgentForLostTime, input.UrgentForPollution, input.AllowAnonymousToOffice);
        await db.SaveChangesAsync();
        return row;
    }

    public async Task<List<IncidentCause>> CausesAsync(Guid companyId, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var query = db.IncidentCauses.AsNoTracking().Where(c => c.CompanyId == companyId);
        if (!includeInactive) query = query.Where(c => c.IsActive);
        return await query.OrderBy(c => c.Group).ThenBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
    }

    public async Task<IncidentCause> SaveCauseAsync(Guid companyId, Guid? id, string name, string group, bool active)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        if (db.Node.IsVessel) throw new InvalidOperationException("The list of root causes is the office's.");
        name = (name ?? "").Trim();
        if (name.Length < 2) throw new InvalidOperationException("Give the cause a name.");
        if (name.Contains(';')) throw new InvalidOperationException("A cause name cannot contain a semicolon.");
        if (await db.IncidentCauses.AnyAsync(c => c.CompanyId == companyId && c.Name == name && c.Id != id)) throw new InvalidOperationException($"{name} is already in the list.");
        var cause = id is null ? null : await db.IncidentCauses.FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId) ?? throw new InvalidOperationException("That cause is not in the list.");
        if (cause is null)
        {
            cause = new IncidentCause { Id = Guid.CreateVersion7(), CompanyId = companyId, SortOrder = (await db.IncidentCauses.Where(c => c.CompanyId == companyId).MaxAsync(c => (int?)c.SortOrder) ?? 0) + 1 };
            db.IncidentCauses.Add(cause);
        }
        (cause.Name, cause.Group, cause.IsActive) = (name, (group ?? "").Trim(), active);
        await db.SaveChangesAsync();
        return cause;
    }

    // ---- writing the report ----

    /// <summary>
    /// Starts a report or changes one that can still be changed (a draft, or one submitted but not yet reviewed). Onboard it is the reporter's or the Master's to change;
    /// the office changes only reports it entered itself.
    /// </summary>
    public async Task<Incident> SaveDraftAsync(Guid companyId, Guid vesselId, Actor actor, Guid? incidentId, IncidentInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        if (!await IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Incident reports are not switched on for this vessel.");
        if (!who.IsOffice) RequireOnboard(who, vesselId);
        if (await db.Vessels.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vesselId && v.CompanyId == companyId) is null) throw new InvalidOperationException("That vessel does not exist.");
        var persons = CheckPersons(input.Persons);

        Incident incident;
        if (incidentId is null)
        {
            incident = new Incident
            {
                Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = vesselId, CreatedAt = DateTime.UtcNow, OwnerStage = Stage(db),
                Code = await DocumentNumbers.NextAsync(db, companyId, vesselId, DocType.Incident),
                ReporterCrewId = who.Crew?.Id, ReporterName = who.Name,
            };
            db.Incidents.Add(incident);
        }
        else
        {
            incident = await db.Incidents.Include(i => i.Persons).Include(i => i.Supplements).FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId)
                ?? throw new InvalidOperationException("Report not found.");
            if (incident.VesselId != vesselId) throw new InvalidOperationException("That report belongs to another vessel.");
            var status = await StatusOfAsync(db, incident);
            if (IncidentStatusRules.TextIsLocked(status)) throw new InvalidOperationException(status == IncidentStatus.Withdrawn ? "A withdrawn report cannot be changed." : "The Master has reviewed this report. Add a supplement instead.");
            if (!CanEditText(who, incident)) throw new InvalidOperationException(who.IsOffice ? "The office changes only the reports it entered itself." : "Only the reporter or the Master changes a report.");
            db.IncidentPersons.RemoveRange(incident.Persons);
            incident.Persons.Clear();
        }

        Apply(incident, input);
        foreach (var p in persons) db.IncidentPersons.Add(new IncidentPerson
        {
            Id = Guid.CreateVersion7(), IncidentId = incident.Id, CrewMemberId = p.CrewMemberId, Name = p.Name.Trim(), Rank = (p.Rank ?? "").Trim(), Company = (p.Company ?? "").Trim(), Role = p.Role,
            InjuryType = (p.InjuryType ?? "").Trim(), BodyPart = (p.BodyPart ?? "").Trim(), Treatment = p.Treatment, DaysLost = p.DaysLost, RestrictedDays = p.RestrictedDays,
        });
        if (incident.SubmittedAt is not null) await RescoreAsync(db, incident);
        await db.SaveChangesAsync();
        return incident;
    }

    private static bool CanEditText(Who who, Incident incident)
        => who.IsOffice ? incident.OwnerStage == SyncStages.Office : incident.OwnerStage == SyncStages.Vessel && (who.IsMaster || (incident.ReporterCrewId is { } r && r == who.Crew!.Id));

    private static void Apply(Incident i, IncidentInput input)
    {
        i.Kind = input.Kind;
        i.OccurredAt = input.OccurredAt;
        (i.Place, i.ShipStatus, i.Weather) = ((input.Place ?? "").Trim(), input.ShipStatus, (input.Weather ?? "").Trim());
        (i.Description, i.ImmediateAction) = ((input.Description ?? "").Trim(), (input.ImmediateAction ?? "").Trim());
        (i.EquipmentId, i.EquipmentName, i.RiskAssessmentRef) = (input.EquipmentId, (input.EquipmentName ?? "").Trim(), (input.RiskAssessmentRef ?? "").Trim());
        (i.AnonymousToOffice, i.ShareLesson) = (input.AnonymousToOffice, input.ShareLesson);
        (i.ActualConsequence, i.PotentialConsequence, i.Likelihood) = (input.ActualConsequence, input.PotentialConsequence, input.Likelihood);
    }

    private static List<IncidentPersonInput> CheckPersons(IReadOnlyList<IncidentPersonInput>? persons)
    {
        var list = (persons ?? []).ToList();
        foreach (var p in list)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) throw new InvalidOperationException("Every person in the report needs a name.");
            if (p.DaysLost < 0 || p.RestrictedDays < 0 || p.DaysLost > 3650 || p.RestrictedDays > 3650) throw new InvalidOperationException("Check the days lost and the days of restricted work.");
        }
        return list;
    }

    /// <summary>Sets the band and the urgent flag from the severity and the persons, as the company's matrix and settings read now.</summary>
    private static async Task RescoreAsync(ChandleryDbContext db, Incident incident)
    {
        var matrix = await RiskAssessmentService.MatrixAsync(db, incident.CompanyId);
        var limits = await LimitsAsync(db, incident.CompanyId);
        incident.Band = IncidentRules.BandOf(matrix, incident.ActualConsequence, incident.PotentialConsequence, incident.Likelihood);
        incident.IsUrgent = IncidentRules.IsUrgent(incident, matrix, limits);
    }

    /// <summary>Submits the report to the Master. It needs what happened, when, the persons for an injury, and the severity; a serious report goes up on the urgent lane at once.</summary>
    public async Task<Incident> SubmitAsync(Guid companyId, Guid incidentId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var incident = await db.Incidents.Include(i => i.Persons).Include(i => i.Supplements).FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Report not found.");
        if (incident.SubmittedAt is not null) throw new InvalidOperationException("This report has been submitted already.");
        if (await StatusOfAsync(db, incident) == IncidentStatus.Withdrawn) throw new InvalidOperationException("A withdrawn report cannot be submitted.");
        if (!CanEditText(who, incident)) throw new InvalidOperationException("Only the reporter or the Master submits a report.");

        if (incident.Description.Length < 3) throw new InvalidOperationException("Say what happened.");
        if (incident.OccurredAt > DateTime.UtcNow.AddDays(1)) throw new InvalidOperationException("It cannot have happened in the future.");
        if (IncidentRules.SeverityProblem(incident.ActualConsequence, incident.PotentialConsequence, incident.Likelihood) is { } problem) throw new InvalidOperationException(problem);
        if (incident.Kind == IncidentKind.InjuryIllness && !incident.Persons.Any(p => p.Role == IncidentPersonRole.Injured)) throw new InvalidOperationException("Name the person who was injured or fell ill.");

        incident.SubmittedAt = DateTime.UtcNow;
        await RescoreAsync(db, incident);
        await db.SaveChangesAsync();
        return incident;
    }

    /// <summary>A dated addition to a submitted report, by whoever owns it (the reporter or the Master onboard; the office for its own). Never edited.</summary>
    public async Task<IncidentSupplement> AddSupplementAsync(Guid companyId, Guid incidentId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var incident = await db.Incidents.Include(i => i.Persons).Include(i => i.Supplements).FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Report not found.");
        text = (text ?? "").Trim();
        if (text.Length < 3) throw new InvalidOperationException("Write the addition.");
        if (incident.SubmittedAt is null) throw new InvalidOperationException("Change a draft directly; supplements are for submitted reports.");
        if (await StatusOfAsync(db, incident) is IncidentStatus.Withdrawn or IncidentStatus.Closed) throw new InvalidOperationException("This report is closed or withdrawn.");
        if (!CanEditText(who, incident)) throw new InvalidOperationException(who.IsOffice ? "A supplement is added by the side that owns the report; the office adds to the investigation." : "Only the reporter or the Master adds to a report.");
        var supplement = new IncidentSupplement { Id = Guid.CreateVersion7(), IncidentId = incident.Id, Text = text, ByName = who.Name, ByCrewId = who.Crew?.Id, At = DateTime.UtcNow };
        db.IncidentSupplements.Add(supplement);
        await db.SaveChangesAsync();
        return supplement;
    }

    /// <summary>
    /// The Master's review: the facts are complete, the severity is confirmed (and may be changed here), and the report goes to the office. After it the reporter's text is locked.
    /// </summary>
    public async Task<IncidentEvent> ReviewAsync(Guid companyId, Guid incidentId, Actor actor, string comment, (int Actual, int Potential, int Likelihood)? severity = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var incident = await db.Incidents.Include(i => i.Persons).FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Report not found.");
        RequireOnboard(who, incident.VesselId, "The Master reviews the report onboard.");
        if (!who.IsMaster) throw new InvalidOperationException("Only the Master reviews a report.");
        var status = await StatusOfAsync(db, incident);
        if (incident.SubmittedAt is null) throw new InvalidOperationException("The report has not been submitted yet.");
        if (status is IncidentStatus.Withdrawn) throw new InvalidOperationException("The report was withdrawn.");
        if (status != IncidentStatus.Reported) throw new InvalidOperationException("The Master has reviewed this report already.");

        if (severity is { } s)
        {
            if (incident.OwnerStage != SyncStages.Vessel) throw new InvalidOperationException("The severity of a report entered by the office is the office's to change.");
            if (IncidentRules.SeverityProblem(s.Actual, s.Potential, s.Likelihood) is { } problem) throw new InvalidOperationException(problem);
            (incident.ActualConsequence, incident.PotentialConsequence, incident.Likelihood) = (s.Actual, s.Potential, s.Likelihood);
            await RescoreAsync(db, incident);
        }
        var ev = NewEvent(db, incident, IncidentEventKind.Reviewed, comment, who, SyncStages.Vessel);
        await db.SaveChangesAsync();
        return ev;
    }

    /// <summary>The vessel's own findings, added next to the office's investigation. By the reporter or the Master.</summary>
    public async Task<IncidentEvent> AddVesselFindingsAsync(Guid companyId, Guid incidentId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Report not found.");
        RequireOnboard(who, incident.VesselId, "The vessel's findings are written onboard.");
        if (!who.IsMaster && incident.ReporterCrewId != who.Crew!.Id) throw new InvalidOperationException("Only the reporter or the Master adds the vessel's findings.");
        if ((text ?? "").Trim().Length < 3) throw new InvalidOperationException("Write the findings.");
        if (await StatusOfAsync(db, incident) is IncidentStatus.Draft or IncidentStatus.Withdrawn or IncidentStatus.Closed) throw new InvalidOperationException("Findings are added to a submitted report that is still open.");
        var ev = NewEvent(db, incident, IncidentEventKind.VesselFindings, text!, who, SyncStages.Vessel);
        await db.SaveChangesAsync();
        return ev;
    }

    /// <summary>Entered by mistake. It stays in the history and leaves the statistics. By the reporter or the Master onboard, or the office.</summary>
    public async Task<IncidentEvent> WithdrawAsync(Guid companyId, Guid incidentId, Actor actor, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Report not found.");
        if (!who.IsOffice && !who.IsMaster && incident.ReporterCrewId != who.Crew!.Id) throw new InvalidOperationException("Only the reporter, the Master or the office withdraws a report.");
        if (!who.IsOffice && who.Crew!.VesselId != incident.VesselId) throw new InvalidOperationException("You are not signed on to this vessel.");
        if ((reason ?? "").Trim().Length < 3) throw new InvalidOperationException("Say why the report is withdrawn.");
        var status = await StatusOfAsync(db, incident);
        if (status == IncidentStatus.Withdrawn) throw new InvalidOperationException("The report was withdrawn already.");
        if (status == IncidentStatus.Closed) throw new InvalidOperationException("A closed report cannot be withdrawn. Reopen it first.");
        var ev = NewEvent(db, incident, IncidentEventKind.Withdrawn, reason, who, Stage(db));
        await db.SaveChangesAsync();
        return ev;
    }

    // ---- the office: investigation, notification, closing ----

    public async Task<IncidentEvent> OpenInvestigationAsync(Guid companyId, Guid incidentId, Actor actor, string note = "")
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (incident, who, facts) = await OfficeFactsAsync(db, companyId, incidentId, actor);
        var status = IncidentStatusRules.Of(facts);
        if (incident.SubmittedAt is null || status is IncidentStatus.Withdrawn or IncidentStatus.Closed) throw new InvalidOperationException("An investigation is opened on a submitted report that is still open.");
        if (facts.Events.Any(e => e.Kind == IncidentEventKind.InvestigationOpened) || facts.Investigation is not null) throw new InvalidOperationException("An investigation is open already.");
        var ev = NewEvent(db, incident, IncidentEventKind.InvestigationOpened, note, who, SyncStages.Office);
        await db.SaveChangesAsync();
        return ev;
    }

    /// <summary>The office decides no investigation is needed, and says why. Refused for a report of a band that needs one, other than an observation.</summary>
    public async Task<IncidentEvent> SkipInvestigationAsync(Guid companyId, Guid incidentId, Actor actor, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (incident, who, facts) = await OfficeFactsAsync(db, companyId, incidentId, actor);
        var matrix = await RiskAssessmentService.MatrixAsync(db, companyId);
        var limits = await LimitsAsync(db, companyId);
        if (incident.SubmittedAt is null || IncidentStatusRules.Of(facts) is IncidentStatus.Withdrawn or IncidentStatus.Closed) throw new InvalidOperationException("This is decided on a submitted report that is still open.");
        if (!IncidentRules.CanSkipInvestigation(incident, matrix, limits)) throw new InvalidOperationException($"A report of band {incident.Band} needs an investigation.");
        if (facts.Events.Any(e => e.Kind is IncidentEventKind.InvestigationOpened or IncidentEventKind.NoInvestigationNeeded) || facts.Investigation is not null) throw new InvalidOperationException("An investigation was opened or waived already.");
        if ((reason ?? "").Trim().Length < 3) throw new InvalidOperationException("Say why no investigation is needed.");
        var ev = NewEvent(db, incident, IncidentEventKind.NoInvestigationNeeded, reason, who, SyncStages.Office);
        await db.SaveChangesAsync();
        return ev;
    }

    /// <summary>Saves the investigation as a new version (the newest counts). The first save opens it. Finishing needs the facts and a root cause.</summary>
    public async Task<IncidentInvestigation> SaveInvestigationAsync(Guid companyId, Guid incidentId, Actor actor, InvestigationInput input)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (incident, who, facts) = await OfficeFactsAsync(db, companyId, incidentId, actor);
        if (incident.SubmittedAt is null || IncidentStatusRules.Of(facts) is IncidentStatus.Withdrawn or IncidentStatus.Closed) throw new InvalidOperationException("An investigation is written on a submitted report that is still open.");
        if (facts.Events.Any(e => e.Kind == IncidentEventKind.NoInvestigationNeeded) && facts.Investigation is null) throw new InvalidOperationException("No investigation was needed for this report. Reopen it to investigate.");

        var causes = (await db.IncidentCauses.AsNoTracking().Where(c => c.CompanyId == companyId).Select(c => c.Name).ToListAsync()).ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
        var asked = (input.RootCauses ?? []).Select(c => c.Trim()).Where(c => c != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (asked.FirstOrDefault(c => !causes.ContainsKey(c)) is { } unknown) throw new InvalidOperationException($"{unknown} is not in the list of root causes.");
        var picked = asked.Select(c => causes[c]).ToList();
        if (input.Finished)
        {
            if ((input.Facts ?? "").Trim().Length < 3) throw new InvalidOperationException("Write down the facts before finishing the investigation.");
            if (picked.Count == 0) throw new InvalidOperationException("Pick at least one root cause before finishing the investigation.");
        }

        if (!facts.Events.Any(e => e.Kind == IncidentEventKind.InvestigationOpened)) NewEvent(db, incident, IncidentEventKind.InvestigationOpened, "", who, SyncStages.Office);
        var version = (await db.IncidentInvestigations.Where(v => v.IncidentId == incidentId).MaxAsync(v => (int?)v.Version) ?? 0) + 1;
        var now = DateTime.UtcNow;
        var saved = new IncidentInvestigation
        {
            Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = incident.VesselId, IncidentId = incidentId, Version = version,
            Facts = (input.Facts ?? "").Trim(), ImmediateCause = (input.ImmediateCause ?? "").Trim(), RootCauses = string.Join("; ", picked), WhyChain = (input.WhyChain ?? "").Trim(),
            ContributingFactors = (input.ContributingFactors ?? "").Trim(), AssessmentFinding = input.AssessmentFinding, Lessons = (input.Lessons ?? "").Trim(),
            InvestigatedBy = (input.InvestigatedBy ?? "").Trim(), Finished = input.Finished, FinishedOn = input.Finished ? now : null, ByName = who.Name, ByUserId = who.UserId, At = now,
            OwnerStage = SyncStages.Office,
        };
        db.IncidentInvestigations.Add(saved);
        await db.SaveChangesAsync();
        return saved;
    }

    /// <summary>Who was told (flag state, class, P&amp;I club, charterer, port state), when, and the reference they gave. Recorded only.</summary>
    public async Task<IncidentEvent> RecordNotificationAsync(Guid companyId, Guid incidentId, Actor actor, string text)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (incident, who, _) = await OfficeFactsAsync(db, companyId, incidentId, actor);
        if ((text ?? "").Trim().Length < 3) throw new InvalidOperationException("Say who was told, and when.");
        var ev = NewEvent(db, incident, IncidentEventKind.NotificationRecorded, text!, who, SyncStages.Office);
        await db.SaveChangesAsync();
        return ev;
    }

    /// <summary>Closes the report once the Master has reviewed it, the investigation (where one is needed) is finished and every action is done.</summary>
    public async Task<IncidentEvent> CloseAsync(Guid companyId, Guid incidentId, Actor actor, string note)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (incident, who, facts) = await OfficeFactsAsync(db, companyId, incidentId, actor);
        var blockers = IncidentRules.CloseBlockers(facts);
        if (blockers.Count > 0) throw new InvalidOperationException("It cannot be closed yet. " + string.Join(" ", blockers));
        var ev = NewEvent(db, incident, IncidentEventKind.Closed, note, who, SyncStages.Office);
        await db.SaveChangesAsync();
        return ev;
    }

    public async Task<IncidentEvent> ReopenAsync(Guid companyId, Guid incidentId, Actor actor, string reason)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var (incident, who, facts) = await OfficeFactsAsync(db, companyId, incidentId, actor);
        if (IncidentStatusRules.Of(facts) != IncidentStatus.Closed) throw new InvalidOperationException("Only a closed report is reopened.");
        if ((reason ?? "").Trim().Length < 3) throw new InvalidOperationException("Say why the report is reopened.");
        var ev = NewEvent(db, incident, IncidentEventKind.Reopened, reason, who, SyncStages.Office);
        await db.SaveChangesAsync();
        return ev;
    }

    /// <summary>
    /// A corrective or preventive action: a Worklist task on the vessel, linked to the report. By the office or the Master onboard, on a report that is submitted and not closed.
    /// The worklist has to be switched on for the vessel.
    /// </summary>
    public async Task<WorklistTask> AddActionAsync(Guid companyId, Guid incidentId, Actor actor, NewIncidentAction action)
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CurrentCompanyId = companyId;
            var who = await WhoAsync(db, companyId, actor);
            var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Report not found.");
            if (!who.IsOffice && (!who.IsMaster || who.Crew!.VesselId != incident.VesselId)) throw new InvalidOperationException("Actions are added by the office or by the Master.");
            var status = await StatusOfAsync(db, incident);
            if (incident.SubmittedAt is null || status is IncidentStatus.Withdrawn or IncidentStatus.Closed) throw new InvalidOperationException("Actions are added to a submitted report that is still open.");
            if ((action.Title ?? "").Trim().Length < 3) throw new InvalidOperationException("Give the action a title.");
            var source = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Source && i.IsActive && i.Name == (who.IsOffice ? "Office" : "Vessel")).Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
            var category = await db.WorklistListItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.Kind == WorklistListKind.Category && i.IsActive && i.ParentId == null && i.Name == "Safety").Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
            var body = $"{action.Description?.Trim()}\n\nFrom incident report {incident.Code}.".TrimStart();
            var acting = new Actor(who.UserId, who.Name);
            var task = await worklist.CreateTaskAsync(companyId, new NewWorklistTask(incident.VesselId, action.Title.Trim(), body, source, CategoryId: category, Priority: action.Priority, Target: action.Target, Assignee: action.Assignee ?? ""), acting);
            await worklist.AddEntryAsync(companyId, task.Id, new WorklistEntryInput(WorklistEntryType.Link, LinkType: WorklistLinkTypes.Incident, LinkTarget: incident.Id, LinkLabel: $"Incident {incident.Code}"), acting);
            return task;
        }
    }

    // ---- reading ----

    /// <summary>One report with everything that belongs to it, or null when the person may not see it (a crew member sees their own; the Master the vessel's; the office all).</summary>
    public async Task<IncidentDetail?> GetAsync(Guid companyId, Guid incidentId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        var incident = await db.Incidents.AsNoTracking().Include(i => i.Persons).Include(i => i.Supplements).FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId);
        if (incident is null || !CanSee(who, incident)) return null;

        var events = await db.IncidentEvents.AsNoTracking().Where(e => e.IncidentId == incidentId).OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync();
        var versions = await db.IncidentInvestigations.AsNoTracking().Where(v => v.IncidentId == incidentId).OrderBy(v => v.Version).ToListAsync();
        var tasks = await worklist.TasksLinkedToAsync(companyId, WorklistLinkTypes.Incident, incidentId);
        var open = tasks.Count(t => !t.IsClosed);
        var matrix = await RiskAssessmentService.MatrixAsync(db, companyId);
        var limits = await LimitsAsync(db, companyId);
        var facts = new IncidentFacts(incident, events, versions.LastOrDefault(), open, IncidentRules.InvestigationRequired(incident.Band, matrix, limits));
        var status = IncidentStatusRules.Of(facts);

        var others = await db.Incidents.AsNoTracking().Where(i => i.CompanyId == companyId && i.VesselId == incident.VesselId && i.Kind == incident.Kind && i.Id != incidentId && i.SubmittedAt != null).ToListAsync();
        var withdrawn = (await db.IncidentEvents.AsNoTracking().Where(e => e.VesselId == incident.VesselId && e.Kind == IncidentEventKind.Withdrawn).Select(e => e.IncidentId).ToListAsync()).ToHashSet();
        var repeats = IncidentRules.Repeats(incident, others.Where(o => !withdrawn.Contains(o.Id)), limits.RepeatWindowMonths);
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == incident.VesselId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";

        return new IncidentDetail(incident, vessel, status, events, versions, facts.Investigation, tasks, open, status is IncidentStatus.Closed or IncidentStatus.Withdrawn ? [] : IncidentRules.CloseBlockers(facts),
            IncidentRules.InvestigationDue(incident, matrix, limits), facts.InvestigationRequired, IncidentRules.CanSkipInvestigation(incident, matrix, limits),
            IncidentRules.InvestigationOverdue(facts, status, matrix, limits, DateTime.UtcNow), IncidentRules.ReportedLate(incident, limits), IncidentRules.ReporterNameFor(incident, who.IsOffice, limits),
            repeats, IncidentRules.SuggestsAssessmentReview(repeats.Count), matrix);
    }

    private static bool CanSee(Who who, Incident incident)
        => who.IsOffice || (who.Crew!.VesselId == incident.VesselId && (who.IsMaster || incident.ReporterCrewId == who.Crew.Id));

    /// <summary>The reports a person may see, newest first: onboard the Master sees the vessel's and anyone else only their own; the office sees every vessel's.</summary>
    public async Task<List<IncidentRow>> ListAsync(Guid companyId, Actor actor, IncidentFilter? filter = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var who = await WhoAsync(db, companyId, actor);
        filter ??= new IncidentFilter();
        var query = db.Incidents.AsNoTracking().Include(i => i.Persons).Where(i => i.CompanyId == companyId);
        if (!who.IsOffice)
        {
            var vessel = who.Crew!.VesselId;
            query = query.Where(i => i.VesselId == vessel);
            if (!who.IsMaster) { var me = who.Crew.Id; query = query.Where(i => i.ReporterCrewId == me); }
        }
        if (filter.VesselId is { } v) query = query.Where(i => i.VesselId == v);
        if (filter.Kind is { } k) query = query.Where(i => i.Kind == k);
        var incidents = await query.OrderByDescending(i => i.OccurredAt).ThenByDescending(i => i.Id).ToListAsync();
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var t = filter.Text.Trim();
            incidents = incidents.Where(i => i.Code.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Description.Contains(t, StringComparison.OrdinalIgnoreCase) || i.Place.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (incidents.Count == 0) return [];

        var ids = incidents.Select(i => i.Id).ToList();
        var events = (await db.IncidentEvents.AsNoTracking().Where(e => ids.Contains(e.IncidentId)).ToListAsync()).GroupBy(e => e.IncidentId).ToDictionary(g => g.Key, g => (IReadOnlyList<IncidentEvent>)g.ToList());
        var investigations = (await db.IncidentInvestigations.AsNoTracking().Where(x => ids.Contains(x.IncidentId)).ToListAsync()).GroupBy(x => x.IncidentId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Version).Last());
        var openActions = await OpenActionCountsAsync(db, companyId, ids);
        var matrix = await RiskAssessmentService.MatrixAsync(db, companyId);
        var limits = await LimitsAsync(db, companyId);
        var vessels = await db.Vessels.AsNoTracking().Where(x => x.CompanyId == companyId).ToDictionaryAsync(x => x.Id, x => x.Name);

        var rows = new List<IncidentRow>();
        foreach (var i in incidents)
        {
            var facts = new IncidentFacts(i, events.GetValueOrDefault(i.Id) ?? [], investigations.GetValueOrDefault(i.Id), openActions.GetValueOrDefault(i.Id), IncidentRules.InvestigationRequired(i.Band, matrix, limits));
            var status = IncidentStatusRules.Of(facts);
            var overdue = IncidentRules.InvestigationOverdue(facts, status, matrix, limits, DateTime.UtcNow);
            if (filter.Status is { } s && s != status) continue;
            if (filter.OnlyOverdue && !overdue) continue;
            rows.Add(new IncidentRow(i, status, overdue, IncidentRules.ReportedLate(i, limits), IncidentRules.ReporterNameFor(i, who.IsOffice, limits), facts.OpenActions, vessels.GetValueOrDefault(i.VesselId) ?? ""));
        }
        return rows;
    }

    /// <summary>The lessons shared with everyone: reviewed reports marked to be shared, with no names. Newest first.</summary>
    public async Task<List<IncidentLesson>> LessonsAsync(Guid companyId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        _ = await WhoAsync(db, companyId, actor);
        var incidents = await db.Incidents.AsNoTracking().Where(i => i.CompanyId == companyId && i.ShareLesson && i.SubmittedAt != null).OrderByDescending(i => i.OccurredAt).ToListAsync();
        var ids = incidents.Select(i => i.Id).ToList();
        var events = (await db.IncidentEvents.AsNoTracking().Where(e => ids.Contains(e.IncidentId)).ToListAsync()).GroupBy(e => e.IncidentId).ToDictionary(g => g.Key, g => g.ToList());
        var investigations = (await db.IncidentInvestigations.AsNoTracking().Where(x => ids.Contains(x.IncidentId)).ToListAsync()).GroupBy(x => x.IncidentId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Version).Last());
        var lessons = new List<IncidentLesson>();
        foreach (var i in incidents)
        {
            var list = events.GetValueOrDefault(i.Id) ?? [];
            if (list.Any(e => e.Kind == IncidentEventKind.Withdrawn)) continue;
            if (IncidentRules.LessonOf(i, investigations.GetValueOrDefault(i.Id), list.Any(e => e.Kind == IncidentEventKind.Reviewed)) is { } lesson) lessons.Add(lesson);
        }
        return lessons;
    }

    // ---- shared helpers ----

    private async Task<(Incident Incident, Who Who, IncidentFacts Facts)> OfficeFactsAsync(ChandleryDbContext db, Guid companyId, Guid incidentId, Actor actor)
    {
        var who = await WhoAsync(db, companyId, actor);
        RequireOffice(who);
        var incident = await db.Incidents.Include(i => i.Persons).FirstOrDefaultAsync(i => i.Id == incidentId && i.CompanyId == companyId) ?? throw new InvalidOperationException("Report not found.");
        return (incident, who, await FactsAsync(db, incident));
    }

    private async Task<IncidentFacts> FactsAsync(ChandleryDbContext db, Incident incident)
    {
        var events = await db.IncidentEvents.AsNoTracking().Where(e => e.IncidentId == incident.Id).ToListAsync();
        var latest = (await db.IncidentInvestigations.AsNoTracking().Where(v => v.IncidentId == incident.Id).ToListAsync()).OrderBy(v => v.Version).LastOrDefault();
        var open = (await worklist.TasksLinkedToAsync(incident.CompanyId, WorklistLinkTypes.Incident, incident.Id)).Count(t => !t.IsClosed);
        var matrix = await RiskAssessmentService.MatrixAsync(db, incident.CompanyId);
        var limits = await LimitsAsync(db, incident.CompanyId);
        return new IncidentFacts(incident, events, latest, open, IncidentRules.InvestigationRequired(incident.Band, matrix, limits));
    }

    private async Task<IncidentStatus> StatusOfAsync(ChandleryDbContext db, Incident incident) => IncidentStatusRules.Of(await FactsAsync(db, incident));

    private static async Task<Dictionary<Guid, int>> OpenActionCountsAsync(ChandleryDbContext db, Guid companyId, IReadOnlyCollection<Guid> incidentIds)
    {
        var wanted = incidentIds.ToHashSet();
        var entries = await db.WorklistEntries.AsNoTracking().Where(e => e.CompanyId == companyId && e.Type == WorklistEntryType.Link && e.Payload.Contains(WorklistLinkTypes.Incident)).ToListAsync();
        var tasksOf = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var e in entries)
            foreach (var link in WorklistService.LinksOf([e]).Where(l => l.Type == WorklistLinkTypes.Incident && wanted.Contains(l.Target)))
                (tasksOf.TryGetValue(link.Target, out var set) ? set : tasksOf[link.Target] = []).Add(e.TaskId);
        var taskIds = tasksOf.Values.SelectMany(s => s).Distinct().ToList();
        var open = (await db.WorklistTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).ToListAsync()).Where(t => !t.IsClosed).Select(t => t.Id).ToHashSet();
        return tasksOf.ToDictionary(p => p.Key, p => p.Value.Count(open.Contains));
    }

    private static IncidentEvent NewEvent(ChandleryDbContext db, Incident incident, IncidentEventKind kind, string? text, Who who, string stage)
    {
        var ev = new IncidentEvent
        {
            Id = Guid.CreateVersion7(), CompanyId = incident.CompanyId, VesselId = incident.VesselId, IncidentId = incident.Id, Kind = kind, Text = (text ?? "").Trim(),
            ByName = who.Name, ByUserId = who.UserId, At = DateTime.UtcNow, OwnerStage = stage,
        };
        db.IncidentEvents.Add(ev);
        return ev;
    }
}

/// <summary>The starting lists for a new company: the root causes and the settings.</summary>
public static class IncidentDefaults
{
    private static readonly (string Group, string[] Names)[] Causes =
    [
        ("People", ["Lapse in attention or judgement", "Fatigue", "Not enough training or familiarisation", "Poor communication", "Not enough supervision"]),
        ("Procedures", ["No procedure for the task", "Procedure not followed", "Procedure unclear or wrong"]),
        ("Equipment", ["Equipment failure", "Maintenance not done or not enough", "Design or layout", "Protective equipment missing or defective"]),
        ("Management", ["Planning or risk assessment not adequate", "Not enough people or time"]),
        ("Environment", ["Weather or sea state", "Ship motion", "Third party (port, contractor, visitor)"]),
    ];

    public static async Task EnsureDefaultsAsync(ChandleryDbContext db, Guid companyId)
    {
        if (!await db.IncidentSettingsRows.IgnoreQueryFilters().AnyAsync(s => s.CompanyId == companyId))
            db.IncidentSettingsRows.Add(new IncidentSettings { Id = Guid.CreateVersion7(), CompanyId = companyId });
        if (await db.IncidentCauses.IgnoreQueryFilters().AnyAsync(c => c.CompanyId == companyId)) return;
        var order = 0;
        foreach (var (group, names) in Causes)
            foreach (var name in names)
                db.IncidentCauses.Add(new IncidentCause { Id = Guid.CreateVersion7(), CompanyId = companyId, Group = group, Name = name, SortOrder = ++order });
    }
}
