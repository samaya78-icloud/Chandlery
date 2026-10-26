using System.Globalization;
using System.Text.Json;

namespace Chandlery.Services;

/// <summary>
/// Reads exchange rates from free public feeds. The latest rates for about 170 currencies come from ExchangeRate-API's open
/// endpoint (once a day, attribution required); history for the ECB currencies comes from Frankfurter. Everything is "units per
/// 1 USD". Keeping the feeds in one class means a paid provider can replace them without touching the rest of the app.
/// </summary>
public class FxFeed(HttpClient http, IConfiguration configuration, ILogger<FxFeed> logger)
{
    public const string LatestSource = "ExchangeRate-API";
    public const string HistorySource = "ECB (Frankfurter)";

    private string LatestUrl => configuration["Fx:LatestUrl"] ?? "https://open.er-api.com/v6/latest/USD";
    private string HistoryUrl => configuration["Fx:HistoryUrl"] ?? "https://api.frankfurter.dev/v1";

    /// <summary>The most recent rates and the day (UTC) the provider last updated them.</summary>
    public async Task<(DateTime Date, Dictionary<string, decimal> Rates)> FetchLatestAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync(LatestUrl, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var result) && result.GetString() != "success")
            throw new InvalidOperationException("The rate provider did not return rates.");

        var unix = root.TryGetProperty("time_last_update_unix", out var u) && u.TryGetInt64(out var seconds) ? seconds : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var date = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.Date;

        var rates = new Dictionary<string, decimal>();
        foreach (var p in root.GetProperty("rates").EnumerateObject())
            if (p.Value.TryGetDecimal(out var value) && value > 0) rates[p.Name] = value;
        logger.LogInformation("Fetched {Count} exchange rates dated {Date:yyyy-MM-dd}", rates.Count, date);
        return (DateTime.SpecifyKind(date, DateTimeKind.Utc), rates);
    }

    /// <summary>Daily rates between two dates (inclusive) for the ECB's currencies, keyed by day.</summary>
    public async Task<SortedDictionary<DateTime, Dictionary<string, decimal>>> FetchHistoryAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var url = $"{HistoryUrl}/{from:yyyy-MM-dd}..{to:yyyy-MM-dd}?base=USD";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var days = new SortedDictionary<DateTime, Dictionary<string, decimal>>();
        foreach (var day in doc.RootElement.GetProperty("rates").EnumerateObject())
        {
            if (!DateTime.TryParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) continue;
            var rates = new Dictionary<string, decimal>();
            foreach (var p in day.Value.EnumerateObject())
                if (p.Value.TryGetDecimal(out var value) && value > 0) rates[p.Name] = value;
            days[DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)] = rates;
        }
        return days;
    }
}
