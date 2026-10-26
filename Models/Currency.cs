namespace Chandlery.Models;

/// <summary>A currency in the shared master (ISO 4217 code, name, decimal places). The master is the same for every company.</summary>
public class Currency
{
    /// <summary>The three-letter code, for example SGD. This is the key.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int DecimalPlaces { get; set; } = 2;
}

/// <summary>Says a company uses a currency, so it appears in that company's currency lists.</summary>
public class CompanyCurrency
{
    public int CompanyId { get; set; }
    public string CurrencyCode { get; set; } = "";
}

/// <summary>
/// How many units of a currency equal 1 USD on a date. CompanyId null is the shared market rate (fetched from a public feed);
/// a company can add its own rate for a currency and date (for example a month-end or budget rate), which wins over the market rate.
/// </summary>
public class ExchangeRate
{
    public int Id { get; set; }
    public int? CompanyId { get; set; }
    public string CurrencyCode { get; set; } = "";
    public DateTime RateDate { get; set; }

    /// <summary>Units of the currency per 1 USD. Divide an amount by this to get USD.</summary>
    public decimal UnitsPerUsd { get; set; }

    /// <summary>"ExchangeRate-API", "ECB (Frankfurter)" or "Manual".</summary>
    public string Source { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string CreatedByName { get; set; } = "";
}
