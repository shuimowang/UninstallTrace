using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DeepUninstaller;
using DeepUninstaller.Services;

internal static class InventoryLayoutVerification
{
    public static void Run(MainWindow window)
    {
        var command = $"\"{Environment.ProcessPath}\" --layout-fixture \"Copy this argument without starting the executable\"";
        var apps = new (InstalledApp App, string? Unit)[]
        {
            (Fixture("Small", 72, command), "B"),
            (Fixture("Album", 768L << 10, command), "KB"),
            (Fixture("Tools", 2L << 20, command), "MB"),
            (Fixture("Wide size", (1023L << 20) + (768L << 10), command), "MB"),
            (Fixture("Platform", 3L << 30, command), "GB"),
            (Fixture("Library", 7L << 40, command), "TB"),
            (Fixture("Archive", 5L << 50, command), "PB"),
            (Fixture("Full application name for the selected program's details", long.MaxValue, command), "EB"),
            (Fixture("No size supplied", null, command), null),
        }.ToDictionary(row => row.App.Name, StringComparer.Ordinal);

        window.SetInventory(apps.Values.Select(row => row.App).ToArray());
        var content = (FrameworkElement)window.Content;
        var grid = Named<DataGrid>(window, "AppGrid");
        var name = grid.Columns.Single(column => column.SortMemberPath == "Name");
        var size = grid.Columns.Single(column => column.SortMemberPath == "Size");
        var height = window.Height - SystemParameters.CaptionHeight - 2 * SystemParameters.ResizeFrameHorizontalBorderHeight;
        var frameWidth = 2 * SystemParameters.ResizeFrameVerticalBorderWidth;
        var minimum = new Size(window.MinWidth - frameWidth, height);
        var normal = new Size(window.Width - frameWidth, height);
        var expanded = new Size(window.Width * 1.25 - frameWidth, height);

        foreach (var (label, bounds) in new[] { ("minimum", minimum), ("default", normal), ("expanded", expanded) })
        {
            Layout(content, bounds);
            Require(grid.Items.Count == apps.Count, "The table did not bind all synthetic inventory entries.");
            foreach (var item in grid.Items)
            {
                var row = apps[CellText(name, item).Text];
                var text = CellText(size, item);
                if (row.Unit is { } unit)
                    Require(text.Text.EndsWith(" " + unit, StringComparison.Ordinal), $"Expected {unit} in the size cell at {label} width.");
                RequireFits(text, grid, label);
            }
        }

        Layout(content, minimum);
        var largestName = apps.Values.Single(row => row.App.EstimatedSizeBytes == long.MaxValue).App.Name;
        var largest = grid.Items.Cast<object>().Single(item => CellText(name, item).Text == largestName);
        grid.SelectedItem = largest;
        Layout(content, minimum);
        VerifyDetails(window, apps[largestName].App, CellText(size, largest).Text, command);

        var sort = SizeHeaderInvokeProvider(grid, size);
        VerifySort(ListSortDirection.Descending);
        VerifySort(ListSortDirection.Ascending);

        void VerifySort(ListSortDirection direction)
        {
            sort.Invoke();
            Layout(content, minimum);
            var actual = grid.Items.Cast<object>().Select(item => apps[CellText(name, item).Text].App.EstimatedSizeBytes).ToArray();
            var known = apps.Values.Select(row => row.App.EstimatedSizeBytes).OfType<long>();
            var expected = direction == ListSortDirection.Ascending ? known.Order() : known.OrderDescending();
            Require(size.SortDirection == direction && actual.LastOrDefault() is null && actual.OfType<long>().SequenceEqual(expected),
                $"The Size header did not sort numeric sizes in {direction} order with the unknown size last.");
            foreach (var item in grid.Items)
                RequireFits(CellText(size, item), grid, $"minimum ({direction})");
        }
    }

    private static InstalledApp Fixture(string name, long? bytes, string command) => new()
    {
        Name = name,
        Version = "2026.11.21.1005 (full version information)",
        Publisher = "A long publisher name with its complete details intact",
        InstallLocation = @"C:\Program Files\UninstallTrace layout fixture\nested folder that must remain selectable",
        UninstallString = command,
        EstimatedSizeBytes = bytes,
        RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{name}",
    };

    private static void VerifyDetails(MainWindow window, InstalledApp selected, string size, string command)
    {
        Require(ReadOnlyValue(window, "AppName") == selected.Name, "The selected app name is incomplete.");
        Require(ReadOnlyValue(window, "AppPublisher") == selected.Publisher, "The selected publisher is incomplete.");
        Require(ReadOnlyValue(window, "AppVersion") == selected.Version, "The selected version is incomplete.");
        Require(ReadOnlyValue(window, "AppPath") == selected.InstallLocation, "The install path is incomplete.");
        Require(ReadOnlyValue(window, "AppCommand") == command, "The uninstall command is incomplete.");
        var detailedSize = ReadOnlyValue(window, "AppSize");
        var digits = new string(detailedSize.Where(char.IsAsciiDigit).ToArray());
        Require(detailedSize.Contains(size, StringComparison.Ordinal)
            && digits.Contains(long.MaxValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "The selected size needs its full formatted value and the exact byte count.");
    }

    private static string ReadOnlyValue(MainWindow window, string name)
    {
        var field = Named<TextBox>(window, name);
        Require(field.IsReadOnly && field.Focusable && field.IsEnabled && field.RenderSize.Width > 0,
            $"{name} must be a reachable, read-only field.");
        field.SelectAll();
        Require(field.SelectedText == field.Text, $"{name} must allow the whole value to be selected.");
        return field.Text;
    }

    private static void RequireFits(TextBlock text, DataGrid grid, string label)
    {
        Require(text.Text.Length > 0, $"The size was blank at {label} width.");
        var pixelsPerDip = VisualTreeHelper.GetDpi(text).PixelsPerDip;
        var tolerance = 1 / pixelsPerDip;
        var measured = new FormattedText(text.Text, DeepUninstaller.Localization.Culture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
            null, TextOptions.GetTextFormattingMode(text), pixelsPerDip).WidthIncludingTrailingWhitespace;
        var contentWidth = text.ActualWidth - text.Padding.Left - text.Padding.Right;
        Require(measured <= contentWidth + tolerance,
            $"The size '{text.Text}' outgrew its {contentWidth:0.0}-DIP cell at {label} width.");

        var viewport = VisualAncestors(text).OfType<ScrollContentPresenter>().FirstOrDefault() as FrameworkElement ?? grid;
        var rightAlignedStart = text.ActualWidth - text.Padding.Right - measured;
        Require(text.TextAlignment == TextAlignment.Right, "The estimated sizes must use a consistent right alignment.");
        var start = text.TranslatePoint(new Point(rightAlignedStart, 0), viewport).X;
        Require(start >= -tolerance && start + measured <= viewport.ActualWidth + tolerance,
            $"The size '{text.Text}' is outside the visible grid viewport at {label} width.");
    }

    private static void Layout(FrameworkElement content, Size bounds)
    {
        content.Measure(bounds);
        content.Arrange(new Rect(bounds));
        content.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        content.UpdateLayout();
    }

    private static IInvokeProvider SizeHeaderInvokeProvider(DataGrid grid, DataGridColumn column)
    {
        var owner = VisualDescendants(grid).OfType<DataGridColumnHeadersPresenter>().Single();
        var peer = new DataGridColumnHeaderItemAutomationPeer(column.Header, column, new DataGridColumnHeadersPresenterAutomationPeer(owner));
        return peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("The Size header does not expose its sorting action.");
    }

    private static TextBlock CellText(DataGridColumn column, object item) => column.GetCellContent(item) as TextBlock
        ?? throw new InvalidOperationException($"The '{column.SortMemberPath}' cell did not render a text block.");

    private static T Named<T>(Window window, string name) where T : FrameworkElement => window.FindName(name) as T
        ?? throw new InvalidOperationException($"The window has no {typeof(T).Name} named '{name}'.");

    private static IEnumerable<DependencyObject> VisualAncestors(DependencyObject child)
    {
        for (var ancestor = VisualTreeHelper.GetParent(child); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            yield return ancestor;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child))
                yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
