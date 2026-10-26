using Chandlery.Data;
using Chandlery.Models;
using Microsoft.EntityFrameworkCore;

namespace Chandlery.Services;

/// <summary>A rate that was found for a currency, with the day it is dated and where it came from.</summary>
public record FxRate(string Currency, decimal UnitsPerUsd, DateTime RateDate, string Source, bool IsCompanyRate);

public record ExchangeRateRow(ExchangeRate Rate, string CurrencyName);

public record RefreshResult(int Saved, DateTime Date);

/// <summary>Finds and converts rates. The static part works on any open context so routing code can use it inside its own transaction.</summary>
public static class Fx
{
    /// <summary>
    /// The rate to use on a date: a company's own rate beats the market rate for the same day, and otherwise the latest rate on or
    /// before the date is used. If nothing is that old yet, the earliest rate we have is used. Null when the currency has no rates at all.
    /// </summary>
    public static async Task<FxRate?> LookupAsync(ChandleryDbContext db, int? companyId, string? currency, DateTime date)
    {
        var code = (currency ?? "USD").Trim().ToUpperInvariant();
        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        if (code == "USD") return new FxRate("USD", 1m, day, "USD", false);

        var candidates = db.ExchangeRates.Where(r => r.CurrencyCode == code && (r.CompanyId == null || r.CompanyId == companyId));
        var row = await candidates.Where(r => r.RateDate <= day)
                      .OrderByDescending(r => r.RateDate).ThenByDescending(r => r.CompanyId != null).FirstOrDefaultAsync()
                  ?? await candidates.OrderBy(r => r.RateDate).ThenByDescending(r => r.CompanyId != null).FirstOrDefaultAsync();
        return row is null ? null : new FxRate(code, row.UnitsPerUsd, row.RateDate, row.Source, row.CompanyId != null);
    }

    public static decimal ToUsd(decimal amount, decimal unitsPerUsd) =>
        unitsPerUsd <= 0 ? amount : Math.Round(amount / unitsPerUsd, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The currency master, the company's currency list and the exchange-rate master.</summary>
public class FxService(IDbContextFactory<ChandleryDbContext> factory, FxFeed feed)
{
    // ---- Currency master and the company's list ----

    public async Task<List<Currency>> GetMasterAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Currencies.OrderBy(c => c.Code).ToListAsync();
    }

    public async Task<HashSet<string>> GetCompanyCurrencyCodesAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return (await db.CompanyCurrencies.Where(c => c.CompanyId == companyId).Select(c => c.CurrencyCode).ToListAsync()).ToHashSet();
    }

    /// <summary>Replaces the company's list. USD is always kept, because every rate is quoted against it.</summary>
    public async Task SetCompanyCurrenciesAsync(int companyId, IEnumerable<string> codes)
    {
        var wanted = codes.Select(c => c.Trim().ToUpperInvariant()).Where(CurrencyCatalog.Contains).Append("USD").Distinct().ToList();
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var existing = await db.CompanyCurrencies.Where(c => c.CompanyId == companyId).ToListAsync();
        db.CompanyCurrencies.RemoveRange(existing.Where(e => !wanted.Contains(e.CurrencyCode)));
        foreach (var code in wanted.Where(w => existing.All(e => e.CurrencyCode != w)))
            db.CompanyCurrencies.Add(new CompanyCurrency { CompanyId = companyId, CurrencyCode = code });
        await db.SaveChangesAsync();
    }

    /// <summary>The currencies a company can choose on its documents, as select options ("SGD · Singapore Dollar").</summary>
    public async Task<List<SelectOption<string>>> GetCompanyCurrencyOptionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var codes = await db.CompanyCurrencies.Where(c => c.CompanyId == companyId).Select(c => c.CurrencyCode).ToListAsync();
        var master = await db.Currencies.Where(c => codes.Contains(c.Code)).OrderBy(c => c.Code).ToListAsync();
        return master.Select(c => new SelectOption<string>(c.Code, c.Code, c.Name)).ToList();
    }

    /// <summary>Every currency in the master (a supplier quoting to any buyer can choose from all of them).</summary>
    public async Task<List<SelectOption<string>>> GetAllCurrencyOptionsAsync()
    {
        var master = await GetMasterAsync();
        return master.Select(c => new SelectOption<string>(c.Code, c.Code, c.Name)).ToList();
    }

    /// <summary>Makes sure the master holds every standard currency. Safe to run on every start.</summary>
    public async Task EnsureMasterAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var have = (await db.Currencies.Select(c => c.Code).ToListAsync()).ToHashSet();
        var missing = CurrencyCatalog.All().Where(c => !have.Contains(c.Code)).ToList();
        if (missing.Count == 0) return;
        db.Currencies.AddRange(missing);
        await db.SaveChangesAsync();
    }

    // ---- Looking up and converting ----

    public async Task<FxRate?> GetRateAsync(int? companyId, string currency, DateTime? date = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await Fx.LookupAsync(db, companyId, currency, date ?? DateTime.UtcNow);
    }

    /// <summary>The current rate for each of the given currencies (those with no rate are left out).</summary>
    public async Task<Dictionary<string, FxRate>> GetRatesAsync(int? companyId, IEnumerable<string> currencies, DateTime? date = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var result = new Dictionary<string, FxRate>();
        foreach (var code in currencies.Select(c => (c ?? "USD").ToUpperInvariant()).Distinct())
            if (await Fx.LookupAsync(db, companyId, code, date ?? DateTime.UtcNow) is { } rate) result[code] = rate;
        return result;
    }

    // ---- Fetching ----

    /// <summary>Pulls the latest market rates and saves them for the day the provider dated them.</summary>
    public async Task<RefreshResult> RefreshMarketAsync(CancellationToken ct = default)
    {
        var (date, rates) = await feed.FetchLatestAsync(ct);
        await using var db = await factory.CreateDbContextAsync();
        var existing = await db.ExchangeRates.Where(r => r.CompanyId == null && r.RateDate == date).ToDictionaryAsync(r => r.CurrencyCode, ct);

        var saved = 0;
        foreach (var (code, rate) in rates.Where(kv => CurrencyCatalog.Contains(kv.Key)))
        {
            if (existing.TryGetValue(code, out var row))
            {
                if (row.UnitsPerUsd == rate) continue;
                row.UnitsPerUsd = rate;
                row.Source = FxFeed.LatestSource;
                row.CreatedAt = DateTime.UtcNow;
            }
            else
            {
                db.ExchangeRates.Add(new ExchangeRate
                {
                    CompanyId = null, CurrencyCode = code, RateDate = date, UnitsPerUsd = rate,
                    Source = FxFeed.LatestSource, CreatedAt = DateTime.UtcNow, CreatedByName = "System"
                });
            }
            saved++;
        }
        await db.SaveChangesAsync(ct);
        return new RefreshResult(saved, date);
    }

    /// <summary>Fills in past days for the ECB currencies, keeping any rate we already have.</summary>
    public async Task<int> BackfillHistoryAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 366);
        var to = DateTime.UtcNow.Date;
        var from = to.AddDays(-days);
        var history = await feed.FetchHistoryAsync(from, to, ct);

        await using var db = await factory.CreateDbContextAsync();
        var start = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var have = (await db.ExchangeRates.Where(r => r.CompanyId == null && r.RateDate >= start)
            .Select(r => new { r.CurrencyCode, r.RateDate }).ToListAsync(ct)).Select(r => (r.CurrencyCode, r.RateDate)).ToHashSet();

        var added = 0;
        foreach (var (date, rates) in history)
        foreach (var (code, rate) in rates.Where(kv => CurrencyCatalog.Contains(kv.Key)))
        {
            if (have.Contains((code, date))) continue;
            db.ExchangeRates.Add(new ExchangeRate
            {
                CompanyId = null, CurrencyCode = code, RateDate = date, UnitsPerUsd = rate,
                Source = FxFeed.HistorySource, CreatedAt = DateTime.UtcNow, CreatedByName = "System"
            });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>When the market rates were last fetched (the newest rate date) and how many currencies have one.</summary>
    public async Task<(DateTime? LatestDate, int Currencies)> GetMarketStatusAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var latest = await db.ExchangeRates.Where(r => r.CompanyId == null).MaxAsync(r => (DateTime?)r.RateDate);
        if (latest is null) return (null, 0);
        var count = await db.ExchangeRates.CountAsync(r => r.CompanyId == null && r.RateDate == latest);
        return (latest, count);
    }

    // ---- The company's own rates ----

    public async Task AddCompanyRateAsync(int companyId, string currency, DateTime date, decimal unitsPerUsd, string userName)
    {
        var code = (currency ?? "").Trim().ToUpperInvariant();
        if (!CurrencyCatalog.Contains(code)) throw new InvalidOperationException("Choose a currency.");
        if (code == "USD") throw new InvalidOperationException("USD is the base currency, so its rate is always 1.");
        if (unitsPerUsd <= 0) throw new InvalidOperationException("The rate must be above zero.");
        if (date.Date > DateTime.UtcNow.Date.AddDays(1)) throw new InvalidOperationException("The rate date cannot be in the future.");

        var day = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var row = await db.ExchangeRates.FirstOrDefaultAsync(r => r.CompanyId == companyId && r.CurrencyCode == code && r.RateDate == day);
        if (row is null)
        {
            row = new ExchangeRate { CompanyId = companyId, CurrencyCode = code, RateDate = day };
            db.ExchangeRates.Add(row);
        }
        row.UnitsPerUsd = unitsPerUsd;
        row.Source = "Manual";
        row.CreatedAt = DateTime.UtcNow;
        row.CreatedByName = userName;
        await db.SaveChangesAsync();
    }

    public async Task DeleteCompanyRateAsync(int companyId, int rateId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        var row = await db.ExchangeRates.FirstOrDefaultAsync(r => r.Id == rateId && r.CompanyId == companyId);
        if (row is null) return;
        db.ExchangeRates.Remove(row);
        await db.SaveChangesAsync();
    }

    // ---- The rates grid ----

    public async Task<GridResult<ExchangeRateRow>> GetPageAsync(int companyId, GridRequest req)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;

        var q = db.ExchangeRates.Where(r => r.CompanyId == null || r.CompanyId == companyId);
        if (req.Like("currency") is { } like) q = q.Where(r => EF.Functions.ILike(r.CurrencyCode, like));
        if (req.Select("source") is { Length: > 0 } source) q = q.Where(r => r.Source == source);
        if (req.FromUtc("date") is { } from) q = q.Where(r => r.RateDate >= from);
        if (req.ToUtcExclusive("date") is { } to) q = q.Where(r => r.RateDate < to);

        var total = await q.CountAsync();
        var desc = req.SortDesc;
        IOrderedQueryable<ExchangeRate> ordered = req.SortKey switch
        {
            "currency" => GridSort.By(q, r => r.CurrencyCode, desc),
            "date" => GridSort.By(q, r => r.RateDate, desc),
            "rate" => GridSort.By(q, r => r.UnitsPerUsd, desc),
            "source" => GridSort.By(q, r => r.Source, desc),
            _ => q.OrderByDescending(r => r.RateDate)
        };
        var rows = await ordered.ThenBy(r => r.CurrencyCode).ThenByDescending(r => r.CompanyId != null).Skip(req.Skip).Take(req.PageSize).ToListAsync();
        var names = await db.Currencies.Where(c => rows.Select(r => r.CurrencyCode).Contains(c.Code)).ToDictionaryAsync(c => c.Code, c => c.Name);
        return new GridResult<ExchangeRateRow>(rows.Select(r => new ExchangeRateRow(r, names.GetValueOrDefault(r.CurrencyCode) ?? "")).ToList(), total);
    }

    public async Task<List<string>> GetSourceOptionsAsync(int companyId)
    {
        await using var db = await factory.CreateDbContextAsync();
        db.CurrentCompanyId = companyId;
        return await db.ExchangeRates.Where(r => r.CompanyId == null || r.CompanyId == companyId).Select(r => r.Source).Distinct().OrderBy(s => s).ToListAsync();
    }
}
