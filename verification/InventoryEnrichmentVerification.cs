using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DeepUninstaller;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class InventoryEnrichmentVerification
{
    public static void Run()
    {
        var game = Fixture("Example Arena", "native", RegistryHive.CurrentUser, RegistryView.Default, 500L << 20);
        var launcher = Fixture(game.Name, "launcher", RegistryHive.LocalMachine, RegistryView.Registry64);
        var managed = Fixture(game.Name, "managed", RegistryHive.LocalMachine, RegistryView.Registry32);
        var empty = Fixture("Empty Folder", "empty", RegistryHive.CurrentUser, RegistryView.Default);
        var unknown = Fixture("Unreported", "unreported", RegistryHive.CurrentUser, RegistryView.Default);
        InstalledApp[] inventory = [game, launcher, managed, empty, unknown];
        var roles = new Dictionary<string, AppRoleAssessment>
        {
            [game.Id] = new(AppRegistrationRole.PcGame, AppRoleEvidence.GameRuntimeAndRegistration),
            [launcher.Id] = new(AppRegistrationRole.Launcher, AppRoleEvidence.LauncherComponentAndMarker),
            [managed.Id] = new(AppRegistrationRole.Unknown),
        };
        var sizes = new Dictionary<string, InstallSizeResult>
        {
            [launcher.Id] = new(InstallSizeStatus.Measured, InstallSizeReason.None, 3L << 30, launcher.InstallLocation, 0),
            [managed.Id] = new(InstallSizeStatus.Partial, InstallSizeReason.IncompleteRead, 8L << 30, managed.InstallLocation, 1),
            [empty.Id] = new(InstallSizeStatus.Measured, InstallSizeReason.None, 0, empty.InstallLocation, 0),
            [unknown.Id] = new(InstallSizeStatus.Unknown, InstallSizeReason.MissingInstallLocation, null, null, 0),
        };
        var labels = RegistrationLabels.Create(inventory, roles);
        VerifyConsent([game, launcher, managed], labels);
        VerifyFullBatchConsent([game, launcher, managed], roles);
        VerifyInventory(inventory, roles, sizes);
        VerifyBatch([game, launcher, managed], labels, launcher, managed);
    }

    private static InstalledApp Fixture(string name, string variant, RegistryHive hive, RegistryView view, long? size = null) => new()
    {
        Name = name,
        Publisher = "Example Publisher",
        Version = "1.2.3",
        Hive = hive,
        View = view,
        KeyName = "SyntheticInventory-" + name,
        RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\SyntheticInventory-{name}",
        InstallLocation = $@"C:\Example Inventory Fixture\{variant}",
        UninstallString = $"\"{Environment.ProcessPath}\" --inventory-enrichment-fixture-{variant}",
        EstimatedSizeBytes = size,
    };

    private static void VerifyConsent(InstalledApp[] duplicates, IReadOnlyDictionary<string, RegistrationLabel> labels)
    {
        foreach (var app in duplicates)
        {
            Require(UninstallCommand.TryCreate(app, out var command, out _), "The inert command fixture must parse.");
            var target = RegistrationLabels.ConfirmationTarget(app, labels);
            Require(target.Contains(app.Name, StringComparison.Ordinal) && target.Contains(command!.DisplayText, StringComparison.Ordinal),
                "The consent target must show the exact selected registration's command.");
            foreach (var other in duplicates.Where(other => other.Id != app.Id))
                Require(!target.Contains(other.UninstallString, StringComparison.Ordinal),
                    "Consent must never identify a same-name sibling's uninstall command.");
        }
    }

    private static void VerifyFullBatchConsent(InstalledApp[] duplicates, IReadOnlyDictionary<string, AppRoleAssessment> roles)
    {
        var selected = duplicates.Concat(Enumerable.Range(1, 9).Select(index =>
            Fixture($"Batch Fixture {index}", $"batch-{index}", RegistryHive.CurrentUser, RegistryView.Default))).ToArray();
        var labels = RegistrationLabels.Create(selected, roles);
        var message = BatchWindow.ConfirmationMessage(selected, labels);
        var last = -1;
        foreach (var app in selected)
        {
            Require(UninstallCommand.TryCreate(app, out var command, out _), "The inert batch command fixture must parse.");
            var labelIndex = message.IndexOf(RegistrationLabels.NameFor(app, labels), last + 1, StringComparison.Ordinal);
            var commandIndex = labelIndex < 0 ? -1 : message.IndexOf(command!.DisplayText, labelIndex, StringComparison.Ordinal);
            Require(labelIndex > last && commandIndex > labelIndex, "Batch consent must include every label and command in queue order.");
            last = commandIndex;
        }
    }

    private static void VerifyInventory(InstalledApp[] inventory, IReadOnlyDictionary<string, AppRoleAssessment> roles,
        IReadOnlyDictionary<string, InstallSizeResult> sizes)
    {
        var window = new MainWindow();
        try
        {
            var (game, launcher, managed, empty, unknown) = (inventory[0], inventory[1], inventory[2], inventory[3], inventory[4]);
            window.SetInventory(inventory, roles);
            Layout(window);
            var grid = Named<DataGrid>(window, "AppGrid");
            var name = grid.Columns.Single(column => column.SortMemberPath == "Name");
            var size = grid.Columns.Single(column => column.SortMemberPath == "Size");
            var sameName = grid.Items.Cast<object>().Where(row => App(row).Name == game.Name).ToArray();
            Require(sameName.Length == 3 && sameName.Select(row => Cell(name, row).Text).Distinct().Count() == 3,
                "All same-name registrations need separate visible labels.");

            Select(grid, game);
            var reportedSizeLabel = DisplayText(Named<DependencyObject>(window, "AppSizeLabel"));
            Require(!string.IsNullOrWhiteSpace(reportedSizeLabel), "Installer-reported size needs a visible source label.");
            var roleEvidence = DisplayText(Named<DependencyObject>(window, "AppRoleEvidence"));
            Require(roleEvidence.Contains(roles[game.Id].Evidence, StringComparison.Ordinal), "The selected role needs its evidence.");

            Select(grid, launcher);
            var headers = Descendants(grid).OfType<DataGridColumnHeadersPresenter>().Single();
            var peer = new DataGridColumnHeaderItemAutomationPeer(size.Header, size, new DataGridColumnHeadersPresenterAutomationPeer(headers));
            var sort = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!;
            sort.Invoke();
            Layout(window);
            Require(size.SortDirection == ListSortDirection.Descending, "Size sorting should start in descending order.");
            Require(window.ApplyMeasuredSizes(inventory, sizes), "Enrichment from the current inventory must be applied.");
            Layout(window);
            Require(App(grid.SelectedItem).Id == launcher.Id, "Reordering enriched size rows must preserve exact selection.");
            var measuredSizeLabel = DisplayText(Named<DependencyObject>(window, "AppSizeLabel"));
            Require(!string.IsNullOrWhiteSpace(measuredSizeLabel) && measuredSizeLabel != reportedSizeLabel,
                "The details must distinguish measured allocation from installer-reported size.");
            Require(Cell(size, Row(grid, launcher)).Text == "3 GB" && Cell(size, Row(grid, game)).Text == "500 MB" &&
                    Cell(size, Row(grid, managed)).Text.StartsWith('≥') && Cell(size, Row(grid, empty)).Text == "0 B" &&
                    Cell(size, Row(grid, unknown)).Text == "—",
                "Reported, measured, partial, measured zero and unknown sizes should render distinctly.");

            Require(Order(grid).SequenceEqual([launcher.Id, game.Id, empty.Id, managed.Id, unknown.Id]),
                "Descending ordering must put complete sizes before partial and unknown data.");
            sort.Invoke();
            Layout(window);
            Require(size.SortDirection == ListSortDirection.Ascending &&
                    Order(grid).SequenceEqual([empty.Id, game.Id, launcher.Id, managed.Id, unknown.Id]),
                "Ascending ordering must also leave partial and unknown data after complete sizes.");

            var replacement = launcher with { Version = "2.4.6", InstallLocation = launcher.InstallLocation + " New" };
            var refreshed = inventory.Select(app => app.Id == launcher.Id ? replacement : app).ToArray();
            window.SetInventory(refreshed, roles);
            Require(!window.ApplyMeasuredSizes(inventory, sizes), "A superseded inventory measurement must be rejected.");
            Layout(window);
            Require(App(grid.SelectedItem) == replacement && Cell(size, Row(grid, replacement)).Text == "—",
                "Stale measurement must not affect a refreshed registration that reuses its exact ID.");
        }
        finally { window.Close(); }
    }

    private static void VerifyBatch(InstalledApp[] duplicates, IReadOnlyDictionary<string, RegistrationLabel> labels,
        InstalledApp launcher, InstalledApp managed)
    {
        var window = new BatchWindow(duplicates, labels);
        try
        {
            Layout(window);
            var choices = Named<DataGrid>(window, "Choices");
            var appColumn = Named<DataGridColumn>(window, "AppColumn");
            Require(choices.Items.Cast<object>().Select(row => Cell(appColumn, row).Text).Distinct().Count() == duplicates.Length,
                "Batch choices must visibly distinguish same-name registrations.");
            CheckBox Toggle(InstalledApp app)
            {
                var item = Row(choices, app);
                var container = (DataGridRow)choices.ItemContainerGenerator.ContainerFromItem(item);
                return Descendants(container).OfType<CheckBox>().Single();
            }

            Toggle(managed).IsChecked = true;
            Layout(window);
            Require(window.SelectedApps.SequenceEqual([managed]), "Selecting one duplicate must queue that exact registration only.");
            Toggle(launcher).IsChecked = true;
            Toggle(managed).IsChecked = false;
            Layout(window);
            Require(window.SelectedApps.SequenceEqual([launcher]), "Switching between duplicate rows must update the exact queued identity.");
        }
        finally { window.Close(); }
    }

    private static void Layout(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var bounds = new Size(1100, 645);
        content.Measure(bounds);
        content.Arrange(new Rect(bounds));
        content.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
        content.UpdateLayout();
    }

    private static T Named<T>(Window window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException($"Missing inventory control {name}.");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static TextBlock Cell(DataGridColumn column, object row) => column.GetCellContent(row) as TextBlock
        ?? throw new InvalidOperationException("The expected inventory cell has no rendered text.");

    private static string DisplayText(DependencyObject control) => control switch
    {
        TextBlock text => text.Text,
        TextBox box => box.Text,
        _ => throw new InvalidOperationException("The expected detail control does not display text."),
    };

    private static InstalledApp App(object row) => row.GetType().GetProperty("App")?.GetValue(row) as InstalledApp
        ?? throw new InvalidOperationException("Inventory row does not identify its original registration.");

    private static object Row(DataGrid grid, InstalledApp app) => grid.Items.Cast<object>().Single(row => App(row).Id == app.Id);

    private static IEnumerable<string> Order(DataGrid grid) => grid.Items.Cast<object>().Select(row => App(row).Id);

    private static void Select(DataGrid grid, InstalledApp app) => grid.SelectedItem = Row(grid, app);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
