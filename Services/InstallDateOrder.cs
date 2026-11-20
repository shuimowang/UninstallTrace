using System.ComponentModel;

namespace DeepUninstaller.Services;

internal static class InstallDateOrder
{
    public static IOrderedEnumerable<TItem> Sort<TItem, TDate>(IEnumerable<TItem> items,
        Func<TItem, TDate?> date, ListSortDirection direction) where TDate : struct, IComparable<TDate>
    {
        var knownFirst = items.OrderBy(item => !date(item).HasValue);
        return direction == ListSortDirection.Descending
            ? knownFirst.ThenByDescending(date)
            : knownFirst.ThenBy(date);
    }
}
