namespace Chandlery.Services;

/// <summary>One choice in a SelectBox. Description is a smaller second line; a disabled option shows the reason beside it.</summary>
public sealed record SelectOption<TValue>(TValue Value, string Label, string? Description = null, bool Disabled = false, string? DisabledReason = null)
    where TValue : notnull;

/// <summary>What a server-side SelectBox asks for: the typed text and which slice of the matches it needs.</summary>
public sealed record SelectQuery(string Term, int Skip, int Take);

/// <summary>One page of matches for a SelectQuery, plus how many match in total (so the box can offer "Load more").</summary>
public sealed record SelectResult<TValue>(IReadOnlyList<SelectOption<TValue>> Options, int Total) where TValue : notnull;

public static class SelectOptions
{
    /// <summary>Builds options from any list: <c>SelectOptions.From(vessels, v => v.Id, v => v.Name)</c>.</summary>
    public static List<SelectOption<int>> From<T>(IEnumerable<T> source, Func<T, int> value, Func<T, string> label, Func<T, string?>? description = null) =>
        source.Select(x => new SelectOption<int>(value(x), label(x), description?.Invoke(x))).ToList();

    /// <summary>Builds options from every value of an enum, with friendlier labels when you pass them.</summary>
    public static List<SelectOption<TEnum>> FromEnum<TEnum>(Func<TEnum, string>? label = null) where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(v => new SelectOption<TEnum>(v, label?.Invoke(v) ?? v.ToString())).ToList();
}
