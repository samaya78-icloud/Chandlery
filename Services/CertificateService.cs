using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record NewCertificate(
    Guid VesselId, CertificateKind Kind, string Title, string CertificateNumber = "", string IssuedBy = "", DateTime? IssuedOn = null, DateTime? DueDate = null,
    int WindowBeforeDays = 0, int WindowAfterDays = 0, int? IntervalMonths = null, Guid? MachineryItemId = null, string Notes = "");

public record CertificateEdit(
    string Title, string CertificateNumber, string IssuedBy, DateTime? IssuedOn, DateTime? DueDate, int WindowBeforeDays, int WindowAfterDays, int? IntervalMonths,
    Guid? MachineryItemId, string Notes, bool IsActive);

public record NewCertificateEvent(CertificateEventType Type, DateTime OccurredOn, DateTime? NewDueDate = null, string Reference = "", string Note = "");

/// <summary>A certificate as a list shows it: where it stands today, and how much has been recorded on it.</summary>
public record CertificateRow(ShipCertificate Cert, string VesselName, CertificateStatus Status, int EventCount, DateTime? LastEventOn);

public record CertificateDetail(ShipCertificate Cert, string VesselName, CertificateStatus Status, List<CertificateEvent> Events)
{
    public IEnumerable<CertificateFile> Files => Events.SelectMany(e => e.Files).OrderBy(f => f.At);
}

/// <summary>What the dashboard and the menu count. Null counts are never made: a company with no vessel on PMS has no certificates card.</summary>
public record CertificateCounts(int Total, int Due90, int Due30, int Overdue)
{
    /// <summary>Due within 30 days or already past.</summary>
    public int NeedingAttention => Due30 + Overdue;
}

/// <summary>One vessel's line on the fleet overview.</summary>
public record CertificateFleetRow(Guid VesselId, string Vessel, int Total, int Overdue, int Due30, int Due60, int Due90, int NoDate, DateTime? NextDeadline);

public static class CertificateFleet
{
    /// <summary>Counts per vessel, the vessels with the most to deal with first. Pure, so it can be tested with rows made by hand.</summary>
    public static List<CertificateFleetRow> Of(IEnumerable<CertificateRow> rows) => rows
        .GroupBy(r => (r.Cert.VesselId, r.VesselName))
        .Select(g => new CertificateFleetRow(g.Key.VesselId, g.Key.VesselName, g.Count(),
            g.Count(r => r.Status.State == CertificateState.Overdue), g.Count(r => r.Status.State == CertificateState.Due30),
            g.Count(r => r.Status.State == CertificateState.Due60), g.Count(r => r.Status.State == CertificateState.Due90),
            g.Count(r => r.Status.State == CertificateState.NoDate),
            g.Where(r => r.Status.Deadline is not null).Select(r => r.Status.Deadline).Min()))
        .OrderByDescending(r => r.Overdue).ThenByDescending(r => r.Due30).ThenBy(r => r.NextDeadline ?? DateTime.MaxValue).ThenBy(r => r.Vessel)
        .ToList();

    public static CertificateCounts CountsOf(IEnumerable<CertificateRow> rows)
    {
        var list = rows.ToList();
        return new CertificateCounts(list.Count,
            list.Count(r => r.Status.State is CertificateState.Due90 or CertificateState.Due60 or CertificateState.Due30 or CertificateState.Overdue),
            list.Count(r => r.Status.State == CertificateState.Due30), list.Count(r => r.Status.State == CertificateState.Overdue));
    }
}

public static class CertificateReports
{
    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

    public static string Csv(IEnumerable<CertificateRow> rows) => WorklistReports.Csv(
        ["Vessel", "Reference", "Kind", "Title", "Number", "Issued by", "Issued", "Due", "Last day", "Days left", "Status", "Active"],
        rows.Select(r => new[]
        {
            r.VesselName, r.Cert.Code, CertificateLook.Label(r.Cert.Kind), r.Cert.Title, r.Cert.CertificateNumber, r.Cert.IssuedBy, Date(r.Cert.IssuedOn),
            Date(r.Status.DueDate), Date(r.Status.Deadline), r.Status.DaysLeft?.ToString() ?? "", CertificateLook.Label(r.Status.State), r.Cert.IsActive ? "yes" : "no",
        }));
}

/// <summary>
/// Certificates and surveys of a vessel: set up by the office, with a history that either side adds to (endorsements, extensions, surveys done, scans).
/// The history is only ever added to; the date that counts is worked out from it by <see cref="CertificateRules"/>. Part of planned maintenance, so it
/// is switched on with that module.
/// </summary>
public class CertificateService(IDbContextFactory<ChandleryDbContext> factory)
{
    /// <summary>The clock, replaceable so a test can move time.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    private DateTime Today => Clock().Date;

    /// <summary>A date from a form has no time zone, and the database takes only UTC: keep the day, mark it UTC.</summary>
    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v.Date, DateTimeKind.Utc) : null;

    public async Task<List<(Guid Id, string Name)>> EnabledVesselsAsync(Guid companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Pms);
    }

    private static async Task RequireEnabledAsync(ChandleryDbContext db, Guid companyId, Guid vesselId)
    {
        if (!await PmsService.IsEnabledForVesselAsync(db, companyId, vesselId)) throw new InvalidOperationException("Planned maintenance, with its certificates and surveys, is not switched on for this vessel.");
    }

    private static void RequireOffice(ChandleryDbContext db)
    {
        if (db.Node.IsVessel) throw new InvalidOperationException("Certificates and surveys are set up by the office. Record what happens to one on its page.");
    }

    // ---- reading ----

    public async Task<List<CertificateRow>> ListAsync(Guid companyId, Guid? vesselId = null, bool includeInactive = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var enabled = await ModuleGate.EnabledVesselsAsync(db, companyId, PolicyModules.Pms);
        var names = enabled.ToDictionary(v => v.Id, v => v.Name);
        var vesselIds = names.Keys.ToList();
        var query = db.ShipCertificates.AsNoTracking().Where(c => c.CompanyId == companyId && vesselIds.Contains(c.VesselId));
        if (vesselId is { } v) query = query.Where(c => c.VesselId == v);
        if (!includeInactive) query = query.Where(c => c.IsActive);
        var certs = await query.ToListAsync();
        var ids = certs.Select(c => c.Id).ToList();
        var events = (await db.CertificateEvents.AsNoTracking().Where(e => ids.Contains(e.CertificateId)).ToListAsync()).ToLookup(e => e.CertificateId);
        return certs
            .Select(c => new CertificateRow(c, names[c.VesselId], CertificateRules.StatusOf(c, events[c.Id], Today), events[c.Id].Count(), events[c.Id].Select(e => (DateTime?)e.OccurredOn).Max()))
            .OrderBy(r => r.Status.State == CertificateState.NoDate ? 1 : 0).ThenBy(r => r.Status.Deadline).ThenBy(r => r.VesselName).ThenBy(r => r.Cert.Title)
            .ToList();
    }

    public async Task<CertificateDetail?> GetAsync(Guid companyId, Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var cert = await db.ShipCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId);
        if (cert is null || !await PmsService.IsEnabledForVesselAsync(db, companyId, cert.VesselId)) return null;
        var events = await db.CertificateEvents.AsNoTracking().Include(e => e.Files).Where(e => e.CertificateId == id).ToListAsync();
        var vessel = await db.Vessels.AsNoTracking().Where(v => v.Id == cert.VesselId).Select(v => v.Name).FirstOrDefaultAsync() ?? "";
        return new CertificateDetail(cert, vessel, CertificateRules.StatusOf(cert, events, Today), events.OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.At).ToList());
    }

    /// <summary>What the dashboard counts, or null when planned maintenance is on for no vessel.</summary>
    public async Task<CertificateCounts?> CountsAsync(Guid companyId)
    {
        if ((await EnabledVesselsAsync(companyId)).Count == 0) return null;
        return CertificateFleet.CountsOf(await ListAsync(companyId));
    }

    // ---- the office sets them up ----

    private static string Check(NewCertificate c) => Check(c.Title, c.WindowBeforeDays, c.WindowAfterDays, c.IntervalMonths, c.IssuedOn, c.DueDate);

    private static string Check(string title, int before, int after, int? interval, DateTime? issued, DateTime? due)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("Give the certificate or survey a title.");
        if (before < 0 || after < 0 || before > 730 || after > 730) throw new InvalidOperationException("A survey window is between 0 and 730 days.");
        if (interval is < 1 or > 240) throw new InvalidOperationException("The cycle is between 1 and 240 months.");
        if (issued is { } i && due is { } d && d < i) throw new InvalidOperationException("The due date is before the issue date.");
        return title.Trim();
    }

    public async Task<ShipCertificate> AddAsync(Guid companyId, NewCertificate input, Actor actor)
    {
        var title = Check(input);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireEnabledAsync(db, companyId, input.VesselId);
        if (!await db.Vessels.AnyAsync(v => v.Id == input.VesselId && v.CompanyId == companyId)) throw new InvalidOperationException("Vessel not found.");
        if (input.MachineryItemId is { } m && !await db.MachineryItems.AnyAsync(x => x.Id == m && x.VesselId == input.VesselId)) throw new InvalidOperationException("That machinery is not on this vessel.");
        var cert = Build(companyId, input, title, actor);
        cert.Code = await DocumentNumbers.NextAsync(db, companyId, input.VesselId, DocType.Certificate);
        db.ShipCertificates.Add(cert);
        await db.SaveChangesAsync();
        return cert;
    }

    private ShipCertificate Build(Guid companyId, NewCertificate input, string title, Actor actor) => new()
    {
        Id = Guid.CreateVersion7(), CompanyId = companyId, VesselId = input.VesselId, Kind = input.Kind, Title = title,
        CertificateNumber = (input.CertificateNumber ?? "").Trim(), IssuedBy = (input.IssuedBy ?? "").Trim(), IssuedOn = Utc(input.IssuedOn), DueDate = Utc(input.DueDate),
        WindowBeforeDays = input.Kind == CertificateKind.Survey ? input.WindowBeforeDays : 0, WindowAfterDays = input.Kind == CertificateKind.Survey ? input.WindowAfterDays : 0,
        IntervalMonths = input.IntervalMonths, MachineryItemId = input.MachineryItemId, Notes = (input.Notes ?? "").Trim(),
        CreatedByName = actor.Name, CreatedAt = Clock(), OwnerStage = SyncStages.Office,
    };

    /// <summary>Adds the usual certificates and surveys to a vessel, skipping any whose title it already has. Returns how many were added; the office then enters the dates.</summary>
    public async Task<int> AddStandardSetAsync(Guid companyId, Guid vesselId, Actor actor)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        await RequireEnabledAsync(db, companyId, vesselId);
        if (!await db.Vessels.AnyAsync(v => v.Id == vesselId && v.CompanyId == companyId)) throw new InvalidOperationException("Vessel not found.");
        var have = (await db.ShipCertificates.Where(c => c.VesselId == vesselId).Select(c => c.Title).ToListAsync()).Select(t => t.ToLowerInvariant()).ToHashSet();
        var added = 0;
        foreach (var t in CertificateStarter.Standard.Where(t => !have.Contains(t.Title.ToLowerInvariant())))
        {
            var cert = Build(companyId, new NewCertificate(vesselId, t.Kind, t.Title, IssuedBy: t.IssuedBy, WindowBeforeDays: t.WindowBefore, WindowAfterDays: t.WindowAfter, IntervalMonths: t.IntervalMonths), t.Title, actor);
            cert.Code = await DocumentNumbers.NextAsync(db, companyId, vesselId, DocType.Certificate);
            db.ShipCertificates.Add(cert);
            added++;
        }
        if (added > 0) await db.SaveChangesAsync();
        return added;
    }

    public async Task UpdateAsync(Guid companyId, Guid id, CertificateEdit edit, Actor actor)
    {
        var title = Check(edit.Title, edit.WindowBeforeDays, edit.WindowAfterDays, edit.IntervalMonths, edit.IssuedOn, edit.DueDate);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        RequireOffice(db);
        var cert = await db.ShipCertificates.FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId) ?? throw new InvalidOperationException("Certificate not found.");
        await RequireEnabledAsync(db, companyId, cert.VesselId);
        if (edit.MachineryItemId is { } m && !await db.MachineryItems.AnyAsync(x => x.Id == m && x.VesselId == cert.VesselId)) throw new InvalidOperationException("That machinery is not on this vessel.");
        var survey = cert.Kind == CertificateKind.Survey;
        (cert.Title, cert.CertificateNumber, cert.IssuedBy, cert.IssuedOn, cert.DueDate) = (title, (edit.CertificateNumber ?? "").Trim(), (edit.IssuedBy ?? "").Trim(), Utc(edit.IssuedOn), Utc(edit.DueDate));
        (cert.WindowBeforeDays, cert.WindowAfterDays, cert.IntervalMonths) = (survey ? edit.WindowBeforeDays : 0, survey ? edit.WindowAfterDays : 0, edit.IntervalMonths);
        (cert.MachineryItemId, cert.Notes, cert.IsActive) = (edit.MachineryItemId, (edit.Notes ?? "").Trim(), edit.IsActive);
        await db.SaveChangesAsync();
    }

    // ---- history: either side adds to it ----

    public async Task<CertificateEvent> AddEventAsync(Guid companyId, Guid certificateId, NewCertificateEvent input, Actor actor)
    {
        if (input.Type == CertificateEventType.Document) throw new InvalidOperationException("Add a scan or a photo with the file picker.");
        if (CertificateRules.Problem(input.Type, input.NewDueDate, input.Reference, input.Note) is { } problem) throw new InvalidOperationException(problem);
        if (input.OccurredOn.Date > Today) throw new InvalidOperationException("The date cannot be in the future.");
        if (input.NewDueDate is { } due && due.Date < input.OccurredOn.Date) throw new InvalidOperationException("The new due date is before the day it happened.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var cert = await db.ShipCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == certificateId && c.CompanyId == companyId) ?? throw new InvalidOperationException("Certificate not found.");
        await RequireEnabledAsync(db, companyId, cert.VesselId);
        if (!cert.IsActive) throw new InvalidOperationException("This certificate is no longer in use.");
        var e = NewEvent(cert, input.Type, input.OccurredOn, input.NewDueDate, input.Reference, input.Note, actor);
        db.CertificateEvents.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private CertificateEvent NewEvent(ShipCertificate cert, CertificateEventType type, DateTime occurredOn, DateTime? newDue, string reference, string note, Actor actor) => new()
    {
        Id = Guid.CreateVersion7(), CompanyId = cert.CompanyId, VesselId = cert.VesselId, CertificateId = cert.Id, Type = type, OccurredOn = Utc(occurredOn)!.Value, NewDueDate = Utc(newDue),
        Reference = (reference ?? "").Trim(), Note = (note ?? "").Trim(), ByName = actor.Name, AuthorUserId = actor.UserId, At = Clock(),
    };

    /// <summary>
    /// Records a scan or a photo as an event of its own, so it travels like the rest of the history. The bytes are in the file store already (see
    /// <see cref="RecordFileService"/>); this adds the record. A certificate keeps up to <see cref="RecordFileRules.MaxPerRecord"/> files.
    /// </summary>
    public async Task<CertificateFile> AddFileAsync(Guid companyId, Guid certificateId, Actor actor, CertificateFile file)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var cert = await db.ShipCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == certificateId && c.CompanyId == companyId) ?? throw new InvalidOperationException("Certificate not found.");
        await RequireEnabledAsync(db, companyId, cert.VesselId);
        if (!cert.IsActive) throw new InvalidOperationException("This certificate is no longer in use.");
        var kept = await db.CertificateFiles.CountAsync(f => db.CertificateEvents.Any(e => e.Id == f.CertificateEventId && e.CertificateId == certificateId));
        if (kept >= RecordFileRules.MaxPerRecord) throw new InvalidOperationException($"A certificate keeps up to {RecordFileRules.MaxPerRecord} files.");
        var e = NewEvent(cert, CertificateEventType.Document, Clock(), null, "", file.Caption, actor);
        (file.Id, file.CertificateEventId, file.ByName, file.ByCrewId, file.At, file.HeldBy) = (Guid.CreateVersion7(), e.Id, actor.Name, null, Clock(), db.Node.IsVessel ? "Vessel" : "Office");
        e.Files.Add(file);
        db.CertificateEvents.Add(e);
        await db.SaveChangesAsync();
        return file;
    }
}
