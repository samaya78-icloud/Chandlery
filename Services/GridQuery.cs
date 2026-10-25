namespace Chandlery.Services;

/// <summary>One column's active filter, as sent from a server-side DataGrid.</summary>
public sealed record GridFilterValue(string Text, string Select, DateTime? From, DateTime? To);

/// <summary>What a server-side DataGrid asks for: one page of rows plus the filters currently set on its columns.</summary>
public sealed class GridRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;

    /// <summary>Only columns with an active filter appear here, keyed by the column's Key.</summary>
    public IReadOnlyDictionary<string, GridFilterValue> Filters { get; init; } = new Dictionary<string, GridFilterValue>();

    /// <summary>The column key to sort by (null = the list's default order).</summary>
    public string? SortKey { get; init; }
    public bool SortDesc { get; init; }

    public int Skip => Math.Max(0, Page - 1) * PageSize;

    public string Text(string key) => Filters.TryGetValue(key, out var f) ? f.Text : "";

    public string Select(string key) => Filters.TryGetValue(key, out var f) ? f.Select : "";

    /// <summary>An ILIKE "contains" pattern for the column's text filter (wildcards in the input escaped), or null when blank.</summary>
    public string? Like(string key)
    {
        var text = Text(key);
        if (text == "") return null;
        return "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }

    /// <summary>Start of the chosen "from" day, as UTC (Npgsql rejects unspecified-kind timestamps).</summary>
    public DateTime? FromUtc(string key) =>
        Filters.TryGetValue(key, out var f) && f.From is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;

    /// <summary>Start of the day AFTER the chosen "to" day, as UTC — use with "&lt;" so the whole to-day is included.</summary>
    public DateTime? ToUtcExclusive(string key) =>
        Filters.TryGetValue(key, out var f) && f.To is { } d ? DateTime.SpecifyKind(d.Date.AddDays(1), DateTimeKind.Utc) : null;
}

public static class GridSort
{
    /// <summary>Orders by one key, ascending or descending, so each list's sort map is a one-liner per column.</summary>
    public static IOrderedQueryable<T> By<T, TKey>(IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, TKey>> key, bool descending) =>
        descending ? query.OrderByDescending(key) : query.OrderBy(key);
}

/// <summary>One page of rows plus the total number of rows matching the filters (across all pages).</summary>
public sealed record GridResult<T>(List<T> Rows, int Total);
