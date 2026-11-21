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
            (Fixture("Full application name for the selected program's details", long.MaxValue, command) with
            {
                Publisher = "A long publisher name with its complete details intact",
                Version = "2026.11.21.10051112131415161718192021222324252627282930"
            }, "EB"),
            (Fixture("No size supplied", null, command), null),
        }.ToDictionary(row => row.App.Name, StringComparer.Ordinal);

        window.SetInventory(apps.Values.Select(row => row.App).ToArray());
        var content = (FrameworkElement)window.Content;
        var grid = Named<DataGrid>(window, "AppGrid");
        var name = grid.Columns.Single(column => column.SortMemberPath == "Name");
        var publisher = grid.Columns.Single(column => column.SortMemberPath == "Publisher");
        var version = grid.Columns.Single(column => column.SortMemberPath == "Version");
        var date = grid.Columns.Single(column => column.SortMemberPath == "InstalledOn");
        var size = grid.Columns.Single(column => column.SortMemberPath == "Size");
        var height = window.Height - SystemParameters.CaptionHeight - 2 * SystemParameters.ResizeFrameHorizontalBorderHeight;
        var frameWidth = 2 * SystemParameters.ResizeFrameVerticalBorderWidth;
        var minimum = new Size(window.MinWidth - frameWidth, height);
        var normal = new Size(window.Width - frameWidth, height);
        var expanded = new Size(window.Width * 1.25 - frameWidth, height);
        var largestApp = apps.Values.Single(row => row.App.EstimatedSizeBytes == long.MaxValue).App;

        VerifyTopNavigation(window, minimum);

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
                RequireFits(CellText(date, item), grid, label);
            }

            var longItem = grid.Items.Cast<object>().Single(item => CellText(name, item).Text == largestApp.Name);
            RequireFullWrappedText(CellText(name, longItem), largestApp.Name, label);
            RequireFullWrappedText(CellText(publisher, longItem), largestApp.Publisher, label);
            RequireFullWrappedText(CellText(version, longItem), largestApp.Version, label);

            var compactItem = grid.Items.Cast<object>().Single(item => CellText(name, item).Text == "Small");
            var compactRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(compactItem);
            var wrappedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(longItem);
            Require(compactRow.ActualHeight <= grid.MinRowHeight + 1 && wrappedRow.ActualHeight > compactRow.ActualHeight,
                $"The inventory must grow wrapped rows and keep short rows compact at {label} width.");
        }

        Layout(content, minimum);
        var largest = grid.Items.Cast<object>().Single(item => CellText(name, item).Text == largestApp.Name);
        grid.SelectedItem = largest;
        Layout(content, minimum);
        VerifyDetails(window, largestApp, CellText(size, largest).Text, command);

        var sort = HeaderInvokeProvider(grid, size);
        VerifySort(ListSortDirection.Descending);
        VerifySort(ListSortDirection.Ascending);
        VerifySortLabels(window, minimum);
        VerifyTranslatedLayout(minimum, command);
        VerifyVirtualization(window, minimum, command);

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
        Version = "1.2.3",
        Publisher = "Acme",
        InstallLocation = @"C:\Program Files\UninstallTrace layout fixture\nested folder that must remain selectable",
        UninstallString = command,
        EstimatedSizeBytes = bytes,
        InstalledOn = new DateTime(2026, 11, 21),
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
        Require(text.Text.Length > 0, $"A number cell was blank at {label} width.");
        var tolerance = 1 / VisualTreeHelper.GetDpi(text).PixelsPerDip;
        var measured = MeasureText(text).WidthIncludingTrailingWhitespace;
        var contentWidth = text.ActualWidth - text.Padding.Left - text.Padding.Right;
        Require(measured <= contentWidth + tolerance,
            $"The value '{text.Text}' outgrew its {contentWidth:0.0}-DIP cell at {label} width.");

        var viewport = VisualAncestors(text).OfType<ScrollContentPresenter>().FirstOrDefault() as FrameworkElement ?? grid;
        var rightAlignedStart = text.ActualWidth - text.Padding.Right - measured;
        Require(text.TextAlignment == TextAlignment.Right, "The date and size columns must use a consistent right alignment.");
        var start = text.TranslatePoint(new Point(rightAlignedStart, 0), viewport).X;
        Require(start >= -tolerance && start + measured <= viewport.ActualWidth + tolerance,
            $"The value '{text.Text}' is outside the visible grid viewport at {label} width.");
    }

    private static void RequireFullWrappedText(TextBlock text, string expected, string label)
    {
        Require(text.Text == expected && text.TextWrapping == TextWrapping.Wrap && text.TextTrimming == TextTrimming.None,
            $"Inventory text must be complete, wrap, and preserve unbroken text at {label} width.");
        var metrics = MeasureText(text);
        metrics.MaxTextWidth = text.ActualWidth - text.Padding.Left - text.Padding.Right;
        metrics.Trimming = TextTrimming.None;
        var tolerance = 1 / VisualTreeHelper.GetDpi(text).PixelsPerDip;
        var row = VisualAncestors(text).OfType<DataGridRow>().First();
        var position = text.TranslatePoint(new Point(0, 0), row);
        Require(metrics.Height + text.Padding.Top + text.Padding.Bottom <= text.ActualHeight + tolerance
            && position.Y >= -tolerance && position.Y + text.ActualHeight <= row.ActualHeight + tolerance,
            $"A wrapped inventory cell was vertically clipped at {label} width.");
    }

    private static void VerifySortLabels(MainWindow window, Size bounds)
    {
        var content = (FrameworkElement)window.Content;
        var selector = Named<ComboBox>(window, "AppSortSelector");
        var grid = Named<DataGrid>(window, "AppGrid");
        for (var index = 0; index < 4; index++)
        {
            selector.SelectedIndex = index;
            Layout(content, bounds);
            RequireSelectedLabelFits(selector);
        }

        foreach (var member in new[] { "Publisher", "Version", "Size" })
        {
            var column = grid.Columns.Single(candidate => candidate.SortMemberPath == member);
            var sort = HeaderInvokeProvider(grid, column);
            foreach (var _ in new[] { ListSortDirection.Ascending, ListSortDirection.Descending })
            {
                sort.Invoke();
                Layout(content, bounds);
                Require(selector.SelectionBoxItem is string label && label.Contains(column.Header.ToString()!)
                    && (label.EndsWith('↑') || label.EndsWith('↓')), "The selected sort label must include its field and direction.");
                RequireSelectedLabelFits(selector);
            }
        }
    }

    private static void RequireSelectedLabelFits(ComboBox selector)
    {
        var label = (string)selector.SelectionBoxItem;
        var text = VisualDescendants(selector).OfType<TextBlock>().Single(candidate => candidate.Text == label);
        var presenter = VisualAncestors(text).OfType<ContentPresenter>().First();
        var left = text.TranslatePoint(new Point(text.Padding.Left, 0), presenter).X;
        var measured = MeasureText(text).WidthIncludingTrailingWhitespace;
        var tolerance = 1 / VisualTreeHelper.GetDpi(text).PixelsPerDip;
        Require(measured <= text.ActualWidth - text.Padding.Left - text.Padding.Right + tolerance
            && left + measured <= presenter.ActualWidth + tolerance,
            $"The selected sort label '{label}' is clipped.");
    }

    private static void VerifyTranslatedLayout(Size bounds, string command)
    {
        var alternate = DeepUninstaller.Localization.Current == AppLanguage.English
            ? AppLanguage.SimplifiedChinese : AppLanguage.English;
        var original = DeepUninstaller.Localization.Preference;
        var setPreference = typeof(DeepUninstaller.Localization).GetProperty(nameof(DeepUninstaller.Localization.Preference))!
            .GetSetMethod(nonPublic: true)!;
        try
        {
            // The second window uses the other locale without saving any settings to disk.
            setPreference.Invoke(null, [alternate]);
            var window = new MainWindow();
            try
            {
                window.SetInventory([Fixture("Translation layout fixture", 1024, command)]);
                VerifyTopNavigation(window, bounds);
                VerifySortLabels(window, bounds);
            }
            finally { window.Close(); }
        }
        finally { setPreference.Invoke(null, [original]); }
    }

    private static void VerifyTopNavigation(MainWindow window, Size bounds)
    {
        var content = (FrameworkElement)window.Content;
        var account = Named<TextBlock>(window, "AccountText");
        var originalAccountText = account.Text;
        try
        {
            account.Text = DeepUninstaller.Localization.Text("管理员权限", "Administrator");
            Layout(content, bounds);
            var tolerance = 1 / VisualTreeHelper.GetDpi(content).PixelsPerDip;
            Require(!VisualDescendants(content).OfType<TextBlock>()
                    .Any(text => text.Text == window.Title && text.ActualWidth > 0 && text.ActualHeight > 0),
                "The client area repeats the app title from the title bar.");

            var tabs = new[] { "AppsNav", "AttemptsNav", "HistoryNav", "SettingsNav" }
                .Select(name => Named<Button>(window, name)).ToArray();
            var tabBounds = tabs.Select(tab => BoundsIn(tab, content)).ToArray();
            var navigationMiddle = tabBounds[0].Top + tabBounds[0].Height / 2;
            for (var index = 0; index < tabs.Length; index++)
            {
                var tab = tabs[index];
                var position = tabBounds[index];
                Require(position.Width > 0 && position.Height > 0 && position.Left >= -tolerance
                    && position.Right <= content.ActualWidth + tolerance
                    && Math.Abs(position.Top + position.Height / 2 - navigationMiddle) <= tolerance
                    && (index == 0 || tabBounds[index - 1].Right <= position.Left + tolerance),
                    "All four navigation buttons must fit in one horizontal row at minimum width.");

                var peer = new ButtonAutomationPeer(tab);
                Require(tab.IsEnabled && tab.Focusable && tab.IsTabStop && peer.GetName().Length > 0
                    && peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider,
                    $"Navigation button '{tab.Name}' must remain accessible by keyboard and automation.");
                var label = VisualDescendants(tab).OfType<TextBlock>().FirstOrDefault(text => text.Text.Any(char.IsLetterOrDigit));
                Require(label is not null && MeasureText(label).WidthIncludingTrailingWhitespace <= label.ActualWidth + tolerance,
                    $"Navigation label '{tab.Name}' is clipped or missing.");
            }

            var listing = BoundsIn(Named<DataGrid>(window, "AppGrid"), content);
            var body = BoundsIn(Named<Grid>(window, "AppsView"), content);
            var accountBounds = BoundsIn(account, content);
            var lastTab = tabBounds[^1];
            Require(accountBounds.Left >= lastTab.Right - tolerance && accountBounds.Top < lastTab.Bottom
                && accountBounds.Bottom > lastTab.Top && accountBounds.Right <= content.ActualWidth + tolerance
                && content.ActualWidth - accountBounds.Right <= lastTab.Height
                && MeasureText(account).WidthIncludingTrailingWhitespace <= account.ActualWidth + tolerance,
                "The account label must fit at the far right of the top navigation row.");
            Require(body.Width >= content.ActualWidth * 0.9 && listing.Width > content.ActualWidth / 2
                && listing.Top >= tabBounds.Max(tab => tab.Bottom) - tolerance,
                "The inventory should use the reclaimed shell width and start below the navigation row.");
        }
        finally { account.Text = originalAccountText; }
    }

    private static Rect BoundsIn(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void VerifyVirtualization(MainWindow window, Size bounds, string command)
    {
        const int rowCount = 160;
        window.SetInventory(Enumerable.Range(1, rowCount).Select(index => Fixture($"Inventory fixture {index}", index, command)).ToArray());
        Layout((FrameworkElement)window.Content, bounds);
        var grid = Named<DataGrid>(window, "AppGrid");
        var realized = VisualDescendants(grid).OfType<DataGridRow>().Count();
        Require(grid.Items.Count == rowCount && realized is > 0 and < rowCount,
            "The inventory must virtualize offscreen rows when row heights are automatic.");
    }

    private static FormattedText MeasureText(TextBlock text) => new(text.Text, DeepUninstaller.Localization.Culture, text.FlowDirection,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
        null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);

    private static void Layout(FrameworkElement content, Size bounds)
    {
        content.Measure(bounds);
        content.Arrange(new Rect(bounds));
        content.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        content.UpdateLayout();
    }

    private static IInvokeProvider HeaderInvokeProvider(DataGrid grid, DataGridColumn column)
    {
        var owner = VisualDescendants(grid).OfType<DataGridColumnHeadersPresenter>().Single();
        var peer = new DataGridColumnHeaderItemAutomationPeer(column.Header, column, new DataGridColumnHeadersPresenterAutomationPeer(owner));
        return peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException($"The '{column.SortMemberPath}' header does not expose its sorting action.");
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
