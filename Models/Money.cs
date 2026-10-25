namespace Chandlery.Models;

public static class Money
{
    public static readonly string[] Currencies = { "USD", "EUR", "GBP", "SGD", "AED", "JPY", "CNY", "INR" };

    /// <summary>Quantity x price, less the line discount, rounded to 2dp.</summary>
    /// <summary>Money rounding: 2dp, half away from zero (2.675 gives 2.68), matching how PostgreSQL rounds numerics.</summary>
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal LineNet(decimal quantity, decimal unitPrice, decimal discountPct) =>
        Round(quantity * unitPrice * (1 - discountPct / 100m));

    /// <summary>The amount taken off a subtotal by a header discount.</summary>
    public static decimal HeaderDiscount(decimal subtotal, decimal headerDiscountPct) =>
        subtotal - Round(subtotal * (1 - headerDiscountPct / 100m));

    /// <summary>Quantity for display: 12 not 12.000, 12.5 not 12.500.</summary>
    public static string Qty(decimal quantity) => quantity.ToString("0.###");

    /// <summary>Applies the header discount to a subtotal and adds additional charges.</summary>
    public static decimal GrandTotal(decimal subtotal, decimal headerDiscountPct, decimal additionalCharges) =>
        Round(subtotal * (1 - headerDiscountPct / 100m)) + additionalCharges;

    /// <summary>"USD 1,234.50". InvariantGlobalization is on, so culture currency formats can't be used.</summary>
    public static string Format(decimal amount, string? currency) =>
        $"{(string.IsNullOrWhiteSpace(currency) ? "USD" : currency)} {amount:#,##0.00}";
}
