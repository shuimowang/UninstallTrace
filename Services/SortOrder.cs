using System.ComponentModel;

namespace DeepUninstaller.Services;

internal static class SortOrder
{
    public static IOrderedEnumerable<TItem> KnownFirst<TItem, TValue>(IEnumerable<TItem> items,
        Func<TItem, TValue?> value, ListSortDirection direction) where TValue : struct, IComparable<TValue>
    {
        var knownFirst = items.OrderBy(item => !value(item).HasValue);
        return direction == ListSortDirection.Descending
            ? knownFirst.ThenByDescending(value)
            : knownFirst.ThenBy(value);
    }
}
