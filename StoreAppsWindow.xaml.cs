using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DeepUninstaller.Services;

namespace DeepUninstaller;

public partial class StoreAppsWindow : Window
{
    private StoreRow[] rows = [];
    private CancellationTokenSource? inventoryRead;
    private bool removing;
    private bool closed;
    private ListSortDirection? dateSort = ListSortDirection.Descending;
    private bool Busy => inventoryRead is not null || removing;

    public StoreAppsWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(string? priorResult = null)
    {
        if (Busy || closed)
            return;

        using var cancellation = new CancellationTokenSource();
        inventoryRead = cancellation;
        StoreStatus.Text = Localization.Text("正在读取当前账户的应用包…", "Reading current-account packages…");
        UpdateControls();
        try
        {
            var packages = await StoreApps.ReadAsync(cancellation.Token);
            if (closed)
                return;

            rows = packages.Select(app => new StoreRow(app)).OrderBy(row => row.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.App.FamilyName, StringComparer.OrdinalIgnoreCase).ToArray();
            ApplyFilter();
            StoreStatus.Text = priorResult ?? Localization.Text(
                $"可卸载 {rows.Count(row => row.App.CanRemove)} 个，共 {rows.Length} 个包。",
                $"{rows.Count(row => row.App.CanRemove)} eligible for removal out of {rows.Length} packages.");
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception exception) when (IsOperationError(exception))
        {
            if (closed)
                return;

            rows = [];
            ApplyFilter();
            var error = Localization.Text("无法读取商店应用。", "Could not read Store apps.");
            StoreStatus.Text = priorResult is null ? error : $"{priorResult} {error}";
            ShowError(exception.Message);
        }
        finally
        {
            inventoryRead = null;
            if (!closed)
                UpdateControls();
        }
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Busy || StoreGrid.SelectedItem is not StoreRow { App.CanRemove: true } selected)
            return;

        var prompt = Localization.Text(
            $"从当前 Windows 账户卸载“{selected.App.DisplayName}”？\n\n{selected.App.FullName}\n\nWindows 可能删除该应用的数据。此操作无法从清理恢复记录中撤销。",
            $"Uninstall “{selected.App.DisplayName}” from the current Windows account?\n\n{selected.App.FullName}\n\nWindows may remove this app’s data. The cleanup history cannot undo this operation.");
        if (UiPrompt.Show(this, prompt, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        removing = true;
        StoreStatus.Text = Localization.Text("正在等待 Windows 完成卸载…", "Waiting for Windows to finish removing the package…");
        UpdateControls();
        string result;
        try
        {
            result = await StoreApps.RemoveAsync(selected.App.FullName) switch
            {
                StoreRemovalResult.Removed => Localization.Text("已从当前账户卸载该应用。", "The app was removed from the current account."),
                StoreRemovalResult.SelectionChanged => Localization.Text("应用包已更新或移除，请核对刷新后的清单。", "The package changed or was removed. Review the refreshed list."),
                StoreRemovalResult.Protected => Localization.Text("Windows 应用包的状态已变化，当前受到保护。", "The package status changed and it is now protected."),
                _ => Localization.Text("Windows 已返回，但当前账户仍登记了该应用包。", "Windows returned, but this package family is still registered for the current account.")
            };
        }
        catch (Exception exception) when (IsOperationError(exception))
        {
            result = Localization.Text("无法确认商店应用已卸载。", "Could not confirm Store app removal.");
            ShowError(exception.Message);
        }
        finally
        {
            removing = false;
            UpdateControls();
        }

        await RefreshAsync(result);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (removing)
        {
            e.Cancel = true;
            return;
        }

        closed = true;
        inventoryRead?.Cancel();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (StoreGrid is not null)
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        var previous = (StoreGrid.SelectedItem as StoreRow)?.App.FullName;
        var query = StoreSearch.Text.Trim();
        var matches = rows.Where(row => (IncludeProtected.IsChecked == true || row.App.CanRemove) && row.Matches(query));
        var visible = (dateSort is { } direction ? InstallDateOrder.Sort(matches, row => row.App.InstalledOn, direction) : matches).ToArray();
        StoreGrid.ItemsSource = visible;
        StoreDateColumn.SortDirection = dateSort;
        StoreGrid.SelectedItem = visible.FirstOrDefault(row => string.Equals(row.App.FullName, previous, StringComparison.OrdinalIgnoreCase));
        EmptyText.Text = query.Length > 0
            ? Localization.Text("没有匹配的应用包。", "No matching app packages.")
            : Localization.Text("没有可显示的应用包。", "No app packages to display.");
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Column != StoreDateColumn)
        {
            dateSort = null;
            StoreDateColumn.SortDirection = null;
            return;
        }

        e.Handled = true;
        dateSort = dateSort == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        ApplyFilter();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = StoreGrid.SelectedItem as StoreRow;
        StoreFullName.Visibility = selection is null ? Visibility.Collapsed : Visibility.Visible;
        StoreFullName.Text = selection?.App.FullName ?? "";
        UpdateControls();
    }

    private void UpdateControls()
    {
        StoreProgress.Visibility = Busy ? Visibility.Visible : Visibility.Collapsed;
        StoreRefresh.IsEnabled = !Busy;
        StoreGrid.IsEnabled = !removing;
        StoreClose.IsEnabled = !removing;
        StoreRemove.IsEnabled = !Busy && StoreGrid.SelectedItem is StoreRow { App.CanRemove: true };
    }

    private void ShowError(string message) => UiPrompt.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool IsOperationError(Exception exception) => exception is IOException or TimeoutException or UnauthorizedAccessException
        or JsonException or Win32Exception or InvalidOperationException or ArgumentException;

    private sealed class StoreRow(StoreApp app)
    {
        public StoreApp App { get; } = app;
        public string InstalledDate => App.InstalledOn?.LocalDateTime.ToString("d", Localization.Culture) ?? "—";

        public string State => App.Protection switch
        {
            "Component" => Localization.Text("受保护的组件", "Protected component"),
            "Signature" => Localization.Text("非商店签名", "Non-Store signature"),
            "Status" => Localization.Text("应用包未就绪", "Package unavailable"),
            _ when App.CanRemove => Localization.Text("可卸载", "Eligible"),
            _ => Localization.Text("应用包标识无效", "Invalid package identity")
        };

        public bool Matches(string query) => query.Length == 0 || App.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || App.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || App.FullName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
