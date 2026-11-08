using System.Data;
using System.Text.RegularExpressions;
using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Chandlery.Services;

/// <summary>
/// Human reference codes for documents: <c>{VESSEL}-{TYPE}-{YEAR}-{LANE}{SEQ}</c>, for example <c>MVSF-REQ-2026-H0007</c>.
/// The vessel short code comes first so any party can see which vessel a document concerns. The lane letter (H = created ashore,
/// none = created onboard) keeps a vessel and the office from ever issuing the same number while disconnected.
/// The GUID key stays internal; people quote this code.
/// </summary>
public static class DocumentNumbers
{
    public static string TypeCode(DocType type) => type switch
    {
        DocType.Requisition => "REQ",
        DocType.Rfq => "RFQ",
        DocType.PurchaseOrder => "PO",
        DocType.Delivery => "DO",
        DocType.Invoice => "INV",
        DocType.Worklist => "WL",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>The next code. Documents without a vessel use the company's office code in the vessel position.</summary>
    public static async Task<string> NextAsync(ChandleryDbContext db, Guid companyId, Guid? vesselId, DocType type)
    {
        var scope = await ScopeAsync(db, companyId, vesselId);
        var year = DateTime.UtcNow.Year;
        var lane = db.Node.IsVessel ? "" : "H";
        var seq = await IncrementAsync(db, scope, TypeCode(type), year, lane);
        return $"{scope}-{TypeCode(type)}-{year}-{lane}{seq:0000}";
    }

    /// <summary>The vessel a purchase order is for: its own, else the one on the RFQ it came from (older RFQ-based POs carry none).</summary>
    public static async Task<Guid?> VesselOfPoAsync(ChandleryDbContext db, PurchaseOrder po)
    {
        if (po.VesselId.HasValue || !po.RfqId.HasValue) return po.VesselId;
        return await db.Rfqs.IgnoreQueryFilters().Where(r => r.Id == po.RfqId.Value).Select(r => r.VesselId).FirstOrDefaultAsync();
    }

    private static async Task<string> ScopeAsync(ChandleryDbContext db, Guid companyId, Guid? vesselId)
    {
        if (vesselId.HasValue)
        {
            var code = await db.Vessels.IgnoreQueryFilters().Where(v => v.Id == vesselId.Value).Select(v => v.ShortCode).FirstOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(code)) return code;
        }
        var office = await db.Companies.IgnoreQueryFilters().Where(c => c.Id == companyId).Select(c => c.OfficeCode).FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(office) ? "HO" : office;
    }

    /// <summary>One atomic statement, so two documents created at the same moment can never get the same number.</summary>
    private static async Task<int> IncrementAsync(ChandleryDbContext db, string scope, string type, int year, string lane)
    {
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
            opened = true;
        }
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                INSERT INTO "NumberSequences" ("Scope", "Type", "Year", "Lane", "LastValue") VALUES (@scope, @type, @year, @lane, 1)
                ON CONFLICT ("Scope", "Type", "Year", "Lane") DO UPDATE SET "LastValue" = "NumberSequences"."LastValue" + 1
                RETURNING "LastValue"
                """;
            command.Parameters.Add(new NpgsqlParameter("scope", scope));
            command.Parameters.Add(new NpgsqlParameter("type", type));
            command.Parameters.Add(new NpgsqlParameter("year", year));
            command.Parameters.Add(new NpgsqlParameter("lane", lane));
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}

public static class VesselCodes
{
    private static readonly HashSet<string> Prefixes = new(StringComparer.OrdinalIgnoreCase) { "MV", "MT", "MS", "SS", "M/V", "M/T" };

    /// <summary>A short code from a vessel's name ("MV Sea Falcon" gives SFAL...), made unique among the codes already taken.</summary>
    public static string Suggest(string name, IEnumerable<string> taken)
    {
        var words = Regex.Split(name ?? "", @"[^A-Za-z0-9]+").Where(w => w.Length > 0 && !Prefixes.Contains(w)).ToList();
        string baseCode;
        if (words.Count == 0) baseCode = "VSL";
        else if (words.Count == 1) baseCode = words[0].ToUpperInvariant()[..Math.Min(4, words[0].Length)];
        else baseCode = string.Concat(words.Take(4).Select(w => char.ToUpperInvariant(w[0])));

        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var code = baseCode;
        for (var i = 2; used.Contains(code); i++) code = baseCode + i;
        return code;
    }
}
