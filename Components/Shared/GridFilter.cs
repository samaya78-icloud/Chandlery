namespace Chandlery.Components.Shared;

/// <summary>The kind of filter a <see cref="GridColumn{TItem}"/> shows under its header.</summary>
public enum GridFilter
{
    None,
    /// <summary>Case-insensitive "contains" box.</summary>
    Text,
    /// <summary>Drop-down of the distinct values found in the column.</summary>
    Select,
    /// <summary>From / To date pickers (inclusive).</summary>
    DateRange
}
