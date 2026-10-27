using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

public record UomRow(Uom Uom, int UsedOn);
public record UomConversionRow(int Id, string From, string To, decimal Factor, int? ItemId, string ItemLabel);

/// <summary>The unit-of-measure master and the conversion table between units.</summary>
public class UomService(IDbContextFactory<ChandleryDbContext> factory)
{
    // ---- Starter data ----

    /// <summary>Adds the starter units and conversions that are missing. Safe to run repeatedly; never touches what the company already has.</summary>
    internal static async Task EnsureStarterAsync(ChandleryDbContext db, int companyId)
    {
        var have = (await db.Uoms.IgnoreQueryFilters().Where(u => u.CompanyId == companyId).Select(u => u.Code).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = false;
        foreach (var (code, name) in StarterUoms.Units)
            if (have.Add(code)) { db.Uoms.Add(new Uom { CompanyId = companyId, Code = code, Name = name }); added = true; }
        if (added) await db.SaveChangesAsync();

        var any = await db.UomConversions.IgnoreQueryFilters().AnyAsync(c => c.CompanyId == companyId);
        if (any) return;
        var ids = await db.Uoms.IgnoreQueryFilters().Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Code, u => u.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to, factor) in StarterUoms.Conversions)
            if (ids.TryGetValue(from, out var f) && ids.TryGetValue(to, out var t))
                db.UomConversions.Add(new UomConversion { CompanyId = companyId, FromUomId = f, ToUomId = t, Factor = factor });
        await db.SaveChangesAsync();
    }

    /// <summary>Makes sure a unit with this code exists (an import or older data may name one that was never set up), and returns its code as stored.</summary>
    internal static async Task<string> EnsureExistsAsync(ChandleryDbContext db, int companyId, string? code)
    {
        code = (code ?? "").Trim();
        if (code == "") return "";
        if (code.Length > 12) throw new InvalidOperationException($"The unit '{code}' is too long: use a short code of up to 12 characters.");
        var lower = code.ToLower();
        var existing = await db.Uoms.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.CompanyId == companyId && u.Code.ToLower() == lower);
        if (existing is not null) return existing.Code;
        db.Uoms.Add(new Uom { CompanyId = companyId, Code = code, Name = code });
        await db.SaveChangesAsync();
        return code;
    }

    // ---- Units ----

    public async Task<List<UomRow>> GetAllAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var units = await db.Uoms.Where(u => u.CompanyId == companyId).OrderBy(u => u.Code).ToListAsync();
        var used = await UsageAsync(db, companyId);
        return units.Select(u => new UomRow(u, used.GetValueOrDefault(u.Code.ToLower()))).ToList();
    }

    /// <summary>How many items and document lines use each unit (lower-case code), so unused ones can be deleted and the rest only disabled.</summary>
    private static async Task<Dictionary<string, int>> UsageAsync(ChandleryDbContext db, int companyId)
    {
        var counts = new Dictionary<string, int>();
        void Add(IEnumerable<string?> codes)
        {
            foreach (var code in codes)
                if (!string.IsNullOrEmpty(code)) counts[code.ToLower()] = counts.GetValueOrDefault(code.ToLower()) + 1;
        }
        Add(await db.Items.Where(i => i.CompanyId == companyId).Select(i => (string?)i.UnitOfMeasure).ToListAsync());
        Add(await db.Requisitions.Where(r => r.CompanyId == companyId).SelectMany(r => r.Lines).Select(l => (string?)l.Uom).ToListAsync());
        Add(await db.Rfqs.Where(r => r.CompanyId == companyId).SelectMany(r => r.Lines).Select(l => (string?)l.Uom).ToListAsync());
        Add(await db.PurchaseOrders.Where(p => p.CompanyId == companyId).SelectMany(p => p.Lines).Select(l => (string?)l.Uom).ToListAsync());
        return counts;
    }

    /// <summary>The enabled units as select options (code as the label, the full name underneath).</summary>
    public async Task<List<SelectOption<string>>> GetOptionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var units = await db.Uoms.Where(u => u.CompanyId == companyId && u.IsActive).OrderBy(u => u.Code).ToListAsync();
        return units.Select(u => new SelectOption<string>(u.Code, u.Code, u.Name == u.Code ? null : u.Name)).ToList();
    }

    public async Task<Uom> AddAsync(int companyId, string code, string name)
    {
        code = (code ?? "").Trim();
        name = (name ?? "").Trim();
        if (code == "") throw new InvalidOperationException("Enter a short code, for example Drum.");
        if (code.Length > 12) throw new InvalidOperationException("The code can be up to 12 characters.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var lower = code.ToLower();
        if (await db.Uoms.AnyAsync(u => u.CompanyId == companyId && u.Code.ToLower() == lower))
            throw new InvalidOperationException($"A unit with the code {code} already exists.");
        var uom = new Uom { CompanyId = companyId, Code = code, Name = name == "" ? code : name };
        db.Uoms.Add(uom);
        await db.SaveChangesAsync();
        return uom;
    }

    /// <summary>The code is not editable (documents refer to it); the name and enabled flag are.</summary>
    public async Task UpdateAsync(int companyId, int uomId, string name, bool isActive)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var uom = await db.Uoms.FirstOrDefaultAsync(u => u.Id == uomId && u.CompanyId == companyId)
            ?? throw new InvalidOperationException("Unit not found.");
        uom.Name = string.IsNullOrWhiteSpace(name) ? uom.Code : name.Trim();
        uom.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int companyId, int uomId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var uom = await db.Uoms.FirstOrDefaultAsync(u => u.Id == uomId && u.CompanyId == companyId)
            ?? throw new InvalidOperationException("Unit not found.");
        if ((await UsageAsync(db, companyId)).GetValueOrDefault(uom.Code.ToLower()) > 0)
            throw new InvalidOperationException($"{uom.Code} is used by items or documents, so it cannot be deleted. Disable it instead.");
        db.UomConversions.RemoveRange(await db.UomConversions.Where(c => c.CompanyId == companyId && (c.FromUomId == uomId || c.ToUomId == uomId)).ToListAsync());
        db.Uoms.Remove(uom);
        await db.SaveChangesAsync();
    }

    // ---- Item pickers (for item-specific conversions) ----

    /// <summary>Server-side search over the company's catalog for a SelectBox (code or name).</summary>
    public Func<SelectQuery, Task<SelectResult<int>>> ItemSelectProvider(int companyId) => async query =>
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var items = db.Items.Where(i => i.CompanyId == companyId);
        var term = (query.Term ?? "").Trim();
        if (term != "")
        {
            var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            items = items.Where(i => EF.Functions.ILike(i.Code, like) || EF.Functions.ILike(i.Name, like));
        }
        var total = await items.CountAsync();
        var rows = await items.OrderBy(i => i.Code).Skip(query.Skip).Take(query.Take).ToListAsync();
        return new SelectResult<int>(rows.Select(i => new SelectOption<int>(i.Id, i.Code, i.Name)).ToList(), total);
    };

    public Func<IReadOnlyCollection<int>, Task<IReadOnlyList<SelectOption<int>>>> ItemSelectResolver() => async ids =>
    {
        await using var db = await factory.CreateDbContextAsync();
        var list = ids.ToList();
        var rows = await db.Items.IgnoreQueryFilters().Where(i => list.Contains(i.Id)).ToListAsync();
        return rows.Select(i => new SelectOption<int>(i.Id, i.Code, i.Name)).ToList();
    };

    // ---- Conversions ----

    public async Task<List<UomConversionRow>> GetConversionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var codes = await db.Uoms.Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Id, u => u.Code);
        var rows = await db.UomConversions.Where(c => c.CompanyId == companyId).ToListAsync();
        var itemIds = rows.Where(r => r.ItemId.HasValue).Select(r => r.ItemId!.Value).Distinct().ToList();
        var items = await db.Items.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => $"{i.Code} · {i.Name}");
        return rows
            .Select(r => new UomConversionRow(r.Id, codes.GetValueOrDefault(r.FromUomId) ?? "?", codes.GetValueOrDefault(r.ToUomId) ?? "?", r.Factor,
                r.ItemId, r.ItemId.HasValue ? items.GetValueOrDefault(r.ItemId.Value) ?? "" : ""))
            .OrderBy(r => r.ItemId.HasValue).ThenBy(r => r.From).ThenBy(r => r.To).ToList();
    }

    public async Task AddConversionAsync(int companyId, string fromCode, string toCode, decimal factor, int? itemId)
    {
        if (string.Equals(fromCode, toCode, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose two different units.");
        if (factor <= 0) throw new InvalidOperationException("The factor must be above zero.");
        if (factor > 1_000_000_000m) throw new InvalidOperationException("That factor is too large.");
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var from = await db.Uoms.FirstOrDefaultAsync(u => u.CompanyId == companyId && u.Code == fromCode) ?? throw new InvalidOperationException("Choose the unit to convert from.");
        var to = await db.Uoms.FirstOrDefaultAsync(u => u.CompanyId == companyId && u.Code == toCode) ?? throw new InvalidOperationException("Choose the unit to convert to.");
        if (itemId.HasValue && !await db.Items.AnyAsync(i => i.Id == itemId.Value && i.CompanyId == companyId))
            throw new InvalidOperationException("Item not found.");

        // The same pair in either direction for the same scope would contradict itself, so replace instead of adding a second one.
        var existing = await db.UomConversions.Where(c => c.CompanyId == companyId && c.ItemId == itemId
            && ((c.FromUomId == from.Id && c.ToUomId == to.Id) || (c.FromUomId == to.Id && c.ToUomId == from.Id))).ToListAsync();
        if (existing.Count > 0) db.UomConversions.RemoveRange(existing);
        db.UomConversions.Add(new UomConversion { CompanyId = companyId, FromUomId = from.Id, ToUomId = to.Id, Factor = factor, ItemId = itemId });
        await db.SaveChangesAsync();
    }

    public async Task DeleteConversionAsync(int companyId, int conversionId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var row = await db.UomConversions.FirstOrDefaultAsync(c => c.Id == conversionId && c.CompanyId == companyId);
        if (row is null) return;
        db.UomConversions.Remove(row);
        await db.SaveChangesAsync();
    }

    /// <summary>The company's whole conversion table as a converter. Suppliers get the buyer's table for the RFQ they are quoting on.</summary>
    public async Task<UomConverter> GetConverterAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await BuildConverterAsync(db, companyId);
    }

    internal static async Task<UomConverter> BuildConverterAsync(ChandleryDbContext db, int companyId)
    {
        var codes = await db.Uoms.IgnoreQueryFilters().Where(u => u.CompanyId == companyId).ToDictionaryAsync(u => u.Id, u => u.Code);
        var converter = new UomConverter();
        foreach (var c in await db.UomConversions.IgnoreQueryFilters().Where(c => c.CompanyId == companyId).ToListAsync())
            if (codes.TryGetValue(c.FromUomId, out var from) && codes.TryGetValue(c.ToUomId, out var to))
                converter.Add(from, to, c.Factor, c.ItemId);
        return converter;
    }

    /// <summary>The units a quote on this line may be priced in: the requested one, then every enabled unit it can be converted to.</summary>
    public static List<SelectOption<string>> QuotableOptions(UomConverter converter, IEnumerable<SelectOption<string>> all, string requestedUom, int itemId) =>
        all.Where(o => string.Equals(o.Value, requestedUom, StringComparison.OrdinalIgnoreCase) || converter.Factor(requestedUom, o.Value, itemId) is not null)
           .OrderBy(o => !string.Equals(o.Value, requestedUom, StringComparison.OrdinalIgnoreCase)).ThenBy(o => o.Value).ToList();
}
