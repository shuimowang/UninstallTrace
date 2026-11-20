using System.ComponentModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using DeepUninstaller.Services;
using static DeepUninstaller.Localization;

namespace DeepUninstaller;

public partial class MainWindow : Window
{
    private readonly RecoveryService recovery = new();
    private IReadOnlyList<InstalledApp> apps = [];
    private readonly bool startInSettings;
    private bool busy;
    private bool initialized;
    private CancellationTokenSource? vendorWait;
    private string? workflowStatusPrefix;
    private string? restoredSelection;
    private MainView currentView;
    private AppSortColumn appSortColumn = AppSortColumn.InstalledOn;
    private ListSortDirection appSortDirection = ListSortDirection.Descending;
    private bool synchronizingAppSort;
    private enum MainView { Apps, Attempts, Recovery, Settings }
    private enum AppSortColumn { InstalledOn, Name, Publisher, Version, Size }

    public MainWindow() : this(startInSettings: false) { }

    internal MainWindow(bool startInSettings)
    {
        this.startInSettings = startInSettings;
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
        switch (Preference)
        {
            case AppLanguage.SimplifiedChinese: ChineseLanguage.IsChecked = true; break;
            case AppLanguage.English: EnglishLanguage.IsChecked = true; break;
            default: SystemLanguage.IsChecked = true; break;
        }
    }

    internal void RestoreAppListView(MainWindow previous)
    {
        appSortColumn = previous.appSortColumn;
        appSortDirection = previous.appSortDirection;
        SearchInput.Text = previous.SearchInput.Text;
        IncludeProtected.IsChecked = previous.IncludeProtected.IsChecked;
        restoredSelection = (previous.AppGrid.SelectedItem as AppRow)?.App.Id;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized)
            return;
        initialized = true;
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        AccountText.Text = elevated ? Text("管理员权限", "Administrator") : Text("当前用户权限", "Current user");
        SetView(startInSettings ? MainView.Settings : MainView.Apps);
        await RunBusy(RefreshApps);
        if (startInSettings && currentView == MainView.Settings)
            SetStatus(Text("已更新界面语言。", "Display language updated."));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            System.Media.SystemSounds.Beep.Play();
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (busy)
            return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetView(MainView.Apps);
            SearchInput.Focus();
            SearchInput.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None && currentView != MainView.Settings)
        {
            e.Handled = true;
            await RunBusy(currentView switch
            {
                MainView.Attempts => RefreshAttempts,
                MainView.Recovery => () => RefreshHistory(),
                _ => RefreshApps,
            });
        }
    }

    private void ShowApps(object sender, RoutedEventArgs e) => SetView(MainView.Apps);

    private async void ShowAttempts(object sender, RoutedEventArgs e)
    {
        SetView(MainView.Attempts);
        await RunBusy(RefreshAttempts);
    }

    private async void ShowHistory(object sender, RoutedEventArgs e)
    {
        SetView(MainView.Recovery);
        await RunBusy(() => RefreshHistory());
    }

    private void ShowSettings(object sender, RoutedEventArgs e)
    {
        SetView(MainView.Settings);
        SetStatus(string.Empty);
    }

    private AppLanguage SelectedLanguage => ChineseLanguage.IsChecked == true ? AppLanguage.SimplifiedChinese
        : EnglishLanguage.IsChecked == true ? AppLanguage.English : AppLanguage.System;

    private void OnLanguageChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (ApplyLanguageButton is not null)
            ApplyLanguageButton.IsEnabled = !busy && SelectedLanguage != Preference;
    }

    private void OnApplyLanguage(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedLanguage == Preference)
            return;
        try
        {
            var previousLanguage = Current;
            SavePreference(SelectedLanguage);
            if (previousLanguage != Current)
                ((App)Application.Current).RefreshMainWindowLanguage(this);
            else
            {
                ApplyLanguageButton.IsEnabled = false;
                SetStatus(Text("已保存语言设置。", "Language preference saved."));
            }
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            UiPrompt.Show(this, exception.Message, Text("无法保存语言设置", "Could not save language preference"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetView(MainView view)
    {
        currentView = view;
        AppsView.Visibility = view == MainView.Apps ? Visibility.Visible : Visibility.Collapsed;
        AttemptsView.Visibility = view == MainView.Attempts ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = view == MainView.Recovery ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = view == MainView.Settings ? Visibility.Visible : Visibility.Collapsed;
        var active = (Brush)FindResource("AccentBackgroundBrush");
        AppsNav.Background = view == MainView.Apps ? active : Brushes.Transparent;
        AttemptsNav.Background = view == MainView.Attempts ? active : Brushes.Transparent;
        HistoryNav.Background = view == MainView.Recovery ? active : Brushes.Transparent;
        SettingsNav.Background = view == MainView.Settings ? active : Brushes.Transparent;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RunBusy(RefreshApps);

    private async Task RefreshApps()
    {
        SetStatus(Text("正在读取已安装程序…", "Loading installed apps…"));
        apps = await Task.Run(InstalledApps.Read);
        ApplyFilter();
        SetStatus(Text($"已读取 {apps.Count} 条卸载登记。", $"Loaded {apps.Count} uninstall registrations."));
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (initialized)
            ApplyFilter();
    }

    private void OnAppSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || synchronizingAppSort)
            return;

        var sort = AppSortSelector.SelectedIndex switch
        {
            0 => (AppSortColumn.InstalledOn, ListSortDirection.Descending),
            1 => (AppSortColumn.InstalledOn, ListSortDirection.Ascending),
            2 => (AppSortColumn.Name, ListSortDirection.Ascending),
            3 => (AppSortColumn.Name, ListSortDirection.Descending),
            _ => (appSortColumn, appSortDirection),
        };
        SetAppSort(sort.Item1, sort.Item2);
    }

    private void OnAppGridSorting(object sender, DataGridSortingEventArgs e)
    {
        if (!Enum.TryParse<AppSortColumn>(e.Column.SortMemberPath, out var column))
            return;

        e.Handled = true;
        var direction = column == AppSortColumn.InstalledOn ? ListSortDirection.Descending : ListSortDirection.Ascending;
        if (column == appSortColumn)
            direction = appSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        SetAppSort(column, direction);
    }

    private void SetAppSort(AppSortColumn column, ListSortDirection direction)
    {
        if (column == appSortColumn && direction == appSortDirection)
            return;

        appSortColumn = column;
        appSortDirection = direction;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selected = (AppGrid.SelectedItem as AppRow)?.App.Id ?? restoredSelection;
        restoredSelection = null;
        var query = SearchInput.Text.Trim();
        var matching = apps.Where(app => (IncludeProtected.IsChecked == true || !app.IsProtected) &&
            (query.Length == 0 || Contains(app.Name, query) || Contains(app.Publisher, query) || Contains(app.Version, query)));
        var visible = OrderApps(matching).Select(app => new AppRow(app)).ToArray();
        AppGrid.ItemsSource = visible;
        AppGrid.SelectedItem = visible.FirstOrDefault(row => row.App.Id == selected) ?? visible.FirstOrDefault();
        UpdateAppSortIndicators();
        InventoryCount.Text = Text($"{visible.Length} 个程序", $"{visible.Length} apps");
        InventoryEmpty.Text = query.Length > 0 ? Text("没有匹配的程序", "No apps match your search") : Text("没有可显示的程序", "No apps to display");
        InventoryEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private IOrderedEnumerable<InstalledApp> OrderApps(IEnumerable<InstalledApp> matching)
    {
        var comparer = StringComparer.Create(Culture, ignoreCase: true);
        var ordered = appSortColumn switch
        {
            AppSortColumn.InstalledOn => InstallDateOrder.Sort(matching, app => app.InstalledOn, appSortDirection),
            AppSortColumn.Publisher => By(app => app.Publisher, comparer),
            AppSortColumn.Version => By(app => app.Version, comparer),
            AppSortColumn.Size => By(app => app.EstimatedSizeBytes),
            _ => By(app => app.Name, comparer),
        };

        return ordered.ThenBy(app => app.Name, comparer).ThenBy(app => app.Publisher, comparer)
            .ThenBy(app => app.Id, StringComparer.OrdinalIgnoreCase);

        IOrderedEnumerable<InstalledApp> By<T>(Func<InstalledApp, T> selector, IComparer<T>? keyComparer = null) =>
            appSortDirection == ListSortDirection.Ascending
                ? matching.OrderBy(selector, keyComparer)
                : matching.OrderByDescending(selector, keyComparer);
    }

    private void UpdateAppSortIndicators()
    {
        var member = appSortColumn.ToString();
        var activeColumn = AppGrid.Columns.First(column => column.SortMemberPath == member);
        foreach (var column in AppGrid.Columns)
            column.SortDirection = column == activeColumn ? appSortDirection : null;

        var preset = appSortColumn switch
        {
            AppSortColumn.InstalledOn => appSortDirection == ListSortDirection.Descending ? 0 : 1,
            AppSortColumn.Name => appSortDirection == ListSortDirection.Ascending ? 2 : 3,
            _ => 4,
        };
        if (preset == 4)
        {
            var arrow = appSortDirection == ListSortDirection.Ascending ? "↑" : "↓";
            HeaderAppSort.Content = $"{activeColumn.Header} {arrow}";
            HeaderAppSort.Visibility = Visibility.Visible;
        }

        synchronizingAppSort = true;
        AppSortSelector.SelectedIndex = preset;
        synchronizingAppSort = false;
        if (preset != 4)
            HeaderAppSort.Visibility = Visibility.Collapsed;
    }

    private static bool Contains(string text, string query) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void OnAppSelected(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (!initialized)
            return;
        var app = (AppGrid.SelectedItem as AppRow)?.App;
        AppName.Text = app?.Name ?? Text("选择一个程序", "Select an app");
        AppPublisher.Text = app is null ? "" : string.Join("  ·  ", new[] { app.Publisher, app.Version }.Where(value => value.Length != 0));
        AppDetails.Visibility = app is null ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.IsEnabled = !busy && app is { IsProtected: false };
        UninstallButton.IsEnabled = false;
        if (app is null)
            return;

        var type = app.IsMsi ? "Windows Installer" : Text("桌面程序", "Desktop app");
        var protection = app.IsProtected ? Text("  ·  受保护", "  ·  Protected") : "";
        AppScope.Text = $"{app.Scope}  ·  {type}{protection}" +
            (app.InstalledOn is { } date ? $"\n{date.ToString("d", Culture)}" : "");
        AppPath.Text = app.InstallLocation.Length == 0 ? Text("未登记", "Not registered") : app.InstallLocation;
        if (UninstallCommand.TryCreate(app, out var command, out var error))
        {
            UninstallButton.IsEnabled = !busy;
            AppCommand.Text = command.DisplayText;
        }
        else
        {
            AppCommand.Text = error;
        }
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (AppGrid.SelectedItem is not AppRow row)
            return;
        var app = row.App;
        if (UiPrompt.Show(this, Text($"运行「{app.Name}」的原厂卸载程序？原厂卸载本身的改动由该程序处理。", $"Run the original uninstaller for {app.Name}? Its changes are managed by the original uninstaller."),
                Text("确认卸载", "Confirm uninstall"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        await RunBusy(() => UninstallApp(app));
    }

    private async Task<bool> UninstallApp(InstalledApp app)
    {
        var current = await Task.Run(() => InstalledApps.Read().FirstOrDefault(item => item.Id == app.Id));
        if (current is null || current.Name != app.Name || current.UninstallString != app.UninstallString
            || current.InstallLocation != app.InstallLocation || current.Version != app.Version || current.Publisher != app.Publisher)
            throw new InvalidOperationException(Text("程序登记已变化，请刷新列表后重新确认。", "The app registration changed. Refresh the list and confirm the operation again."));
        if (!UninstallCommand.TryCreate(current, out var command, out var error))
            throw new InvalidOperationException(error);

        var attempt = await Task.Run(() => UninstallHistory.Start(current));
        SetStatus(Text($"正在等待「{app.Name}」的原厂卸载程序退出…", $"Waiting for the original uninstaller for {app.Name} to exit…"));
        int exitCode;
        using var wait = new CancellationTokenSource();
        try
        {
            using var process = command.Start();
            vendorWait = wait;
            StopWaitButton.Visibility = Visibility.Visible;
            await process.WaitForExitAsync(wait.Token);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            SetStatus(Text("已停止等待，原厂卸载进程可继续运行。稍后可从卸载记录继续扫描。", "Stopped waiting. The original uninstaller may continue running. Resume the scan later from Uninstall history."));
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            await Task.Run(() => UninstallHistory.Update(attempt, UninstallOutcome.Canceled));
            SetStatus(Text("原厂卸载未运行，权限请求已取消。", "The permission request was canceled before the original uninstaller ran."));
            return false;
        }
        catch
        {
            await Task.Run(() => UninstallHistory.Update(attempt, UninstallOutcome.Failed));
            throw;
        }
        finally
        {
            vendorWait = null;
            StopWaitButton.Visibility = Visibility.Collapsed;
        }

        if (command.IsMsi && exitCode == 1602)
        {
            await Task.Run(() => UninstallHistory.Update(attempt, UninstallOutcome.Canceled, exitCode));
            SetStatus(Text("原厂卸载已取消。", "The original uninstall was canceled."));
            return false;
        }
        if (command.IsMsi && exitCode is 1641 or 3010)
        {
            await Task.Run(() => UninstallHistory.Update(attempt, UninstallOutcome.RestartRequired, exitCode));
            UiPrompt.Show(this, Text($"Windows Installer 返回 {exitCode}，需要重启才能完成卸载。你可以先预览残留。", $"Windows Installer returned {exitCode}. Restart to complete removal. You can preview leftovers now."),
                Text("卸载需要重启", "Restart required"), MessageBoxButton.OK, MessageBoxImage.Information);
            await ScanAndReview(current, Text("Windows Installer 正在等待重启。此处可以查看本次残留。", "Windows Installer is waiting for a restart. You can preview leftovers here."), readOnly: true);
            return false;
        }

        SetStatus(Text("正在核对 Windows 卸载登记…", "Checking the Windows uninstall registration…"));
        var stillRegistered = await WaitForRegistration(current, exitCode == 0);
        var completed = !stillRegistered && (exitCode == 0 || command.IsMsi && exitCode is 1605 or 1614);
        var outcome = completed ? UninstallOutcome.Removed :
            stillRegistered ? UninstallOutcome.RegistrationRemains : UninstallOutcome.Failed;
        await Task.Run(() => UninstallHistory.Update(attempt, outcome, exitCode));
        var summary = completed
            ? Text($"Windows 已移除卸载登记（退出码 {exitCode}）。", $"Windows removed the uninstall registration (exit code {exitCode}).")
            : stillRegistered
                ? Text($"原厂卸载进程已退出（退出码 {exitCode}），Windows 仍有该程序的登记。", $"The original uninstaller exited with code {exitCode}. The app is still registered in Windows.")
                : Text($"Windows 已移除登记，原厂卸载返回退出码 {exitCode}。", $"Windows removed the registration. The original uninstaller returned code {exitCode}.");
        await ScanAndReview(current, summary, registrationConfirmedGone: completed);
        return completed;
    }

    private void OnStopWait(object sender, RoutedEventArgs e) => vendorWait?.Cancel();

    private static async Task<bool> WaitForRegistration(InstalledApp app, bool poll)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!await Task.Run(() => InstalledApps.IsRegistered(app)))
                return false;
            if (!poll || attempt == 6)
                return true;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (AppGrid.SelectedItem is not AppRow row)
            return;
        await RunBusy(() => ScanAndReview(row.App, Text("核对程序登记、安装路径和残留的归属依据。", "Checking the app registration, install location, and evidence for each leftover.")));
    }

    private async Task ScanAndReview(InstalledApp app, string summary, bool registrationConfirmedGone = false, bool readOnly = false)
    {
        SetStatus(Text($"正在扫描「{app.Name}」的残留…", $"Scanning for leftovers from {app.Name}…"));
        var result = await Task.Run(() => LeftoverScanner.Scan(app, InstalledApps.Read()));
        var review = new ReviewWindow(app.Name, summary, result, registrationConfirmedGone, readOnly) { Owner = this };
        if (review.ShowDialog() == true)
        {
            SetStatus(Text($"正在隔离「{app.Name}」的选中残留…", $"Quarantining the selected leftovers from {app.Name}…"));
            var session = await Task.Run(() => recovery.Clean(app, review.SelectedItems, registrationConfirmedGone));
            SetView(MainView.Recovery);
            await RefreshHistory(session.Id);
            SetStatus(Text($"已隔离 {session.Entries.Count(entry => entry.State == CleanupState.Quarantined)} 项，", $"Quarantined {session.Entries.Count(entry => entry.State == CleanupState.Quarantined)} items. ")
                + Text($"待核对 {session.Entries.Count(entry => entry.State is CleanupState.Failed or CleanupState.Skipped)} 项。", $"{session.Entries.Count(entry => entry.State is CleanupState.Failed or CleanupState.Skipped)} need review."));
            apps = await Task.Run(InstalledApps.Read);
            ApplyFilter();
            return;
        }
        apps = await Task.Run(InstalledApps.Read);
        ApplyFilter();
        SetStatus(result.Items.Count == 0 ? Text("本次扫描未发现可归属的残留。", "No attributable leftovers were found.") : Text($"扫描发现 {result.Items.Count} 项，未执行清理。", $"Found {result.Items.Count} items. No items were cleaned."));
    }

    private async void OnAttemptsRefresh(object sender, RoutedEventArgs e) => await RunBusy(RefreshAttempts);

    private async Task RefreshAttempts()
    {
        var selected = (AttemptsGrid.SelectedItem as AttemptRow)?.Attempt.Id;
        SetStatus(Text("正在读取卸载记录…", "Loading uninstall history…"));
        var warnings = new List<string>();
        var attempts = await Task.Run(() => UninstallHistory.Read(warnings.Add));
        var rows = attempts.Select(attempt => new AttemptRow(attempt)).ToArray();
        AttemptsGrid.ItemsSource = rows;
        AttemptsGrid.SelectedItem = rows.FirstOrDefault(row => row.Attempt.Id == selected) ?? rows.FirstOrDefault();
        AttemptsEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(warnings.Count == 0 ? Text($"{rows.Length} 次原厂卸载记录。", $"{rows.Length} uninstall attempts.") :
            Text($"{rows.Length} 次原厂卸载记录。部分记录无法读取，详情可从状态提示查看。", $"{rows.Length} uninstall attempts. Some records could not load. Hover over this status for details."));
        StatusText.ToolTip = warnings.Count == 0 ? null : string.Join("\n", warnings);
    }

    private void OnAttemptSelected(object sender, SelectionChangedEventArgs e) => UpdateAttemptsButton();

    private void UpdateAttemptsButton()
    {
        if (initialized)
            ResumeScanButton.IsEnabled = !busy && AttemptsGrid.SelectedItem is AttemptRow { Attempt.App.IsProtected: false };
    }

    private async void OnResumeScan(object sender, RoutedEventArgs e)
    {
        if (AttemptsGrid.SelectedItem is not AttemptRow row)
            return;
        var attempt = row.Attempt;
        await RunBusy(async () =>
        {
            var readOnly = attempt.RequiresRestartNow;
            var verified = !readOnly && attempt.Outcome is UninstallOutcome.Removed or UninstallOutcome.RestartRequired
                && !await Task.Run(() => InstalledApps.IsRegistered(attempt.App));
            var summary = readOnly ? Text("Windows Installer 正在等待重启。此处可以查看残留。", "Windows Installer is waiting for a restart. You can preview leftovers here.") :
                verified ? Text("Windows 已移除卸载登记，正在按卸载记录扫描。", "Windows removed the uninstall registration. Scanning using the saved uninstall record.") :
                Text("正在按卸载记录扫描，请逐项核对与应用的归属关系。", "Scanning using the saved uninstall record. Verify that each selected item belongs to the app.");
            await ScanAndReview(attempt.App, summary, verified, readOnly);
        });
    }

    private async void OnHistoryRefresh(object sender, RoutedEventArgs e) => await RunBusy(() => RefreshHistory());

    private async Task RefreshHistory(Guid? selected = null)
    {
        selected ??= (HistoryGrid.SelectedItem as SessionRow)?.Session.Id;
        SetStatus(Text("正在读取清理恢复记录…", "Loading cleanup recovery records…"));
        var warnings = new List<string>();
        var history = await Task.Run(() => recovery.ReadHistory(warnings.Add));
        var rows = history.Select(session => new SessionRow(session)).ToArray();
        HistoryGrid.ItemsSource = rows;
        HistoryGrid.SelectedItem = rows.FirstOrDefault(row => row.Session.Id == selected) ?? rows.FirstOrDefault();
        HistoryEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(warnings.Count == 0 ? Text($"{rows.Length} 次清理记录。", $"{rows.Length} cleanup sessions.") :
            Text($"{rows.Length} 次清理记录。部分记录无法读取，详情可从状态提示查看。", $"{rows.Length} cleanup sessions. Some records could not load. Hover over this status for details."));
        StatusText.ToolTip = warnings.Count == 0 ? null : string.Join("\n", warnings);
    }

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized)
            return;
        var row = HistoryGrid.SelectedItem as SessionRow;
        EntryGrid.ItemsSource = row?.Session.Entries.Select(entry => new EntryRow(entry)).ToArray();
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        var row = HistoryGrid.SelectedItem as SessionRow;
        var available = !busy && row?.Session.Entries.Any(entry =>
            entry.State is CleanupState.Pending or CleanupState.Quarantined or CleanupState.Failed) == true;
        RestoreButton.IsEnabled = available;
        DiscardButton.IsEnabled = available || !busy && row?.Session.Entries.Any(entry => entry.State == CleanupState.DiscardInProgress) == true;
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SessionRow row ||
            UiPrompt.Show(this, Text($"恢复「{row.Session.AppName}」在本次清理中移走的项目？原位置有新内容时，该项目会继续保留在隔离区。", $"Restore items quarantined for {row.Session.AppName}? Items stay in quarantine if their original location is occupied."),
                Text("确认恢复", "Confirm restore"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        await RunBusy(async () =>
        {
            SetStatus(Text("正在恢复选中记录的隔离项…", "Restoring quarantined items for the selected session…"));
            var session = await Task.Run(() => recovery.Restore(row.Session.Id));
            await RefreshHistory(session.Id);
            SetStatus(Text($"该次记录已恢复 {session.Entries.Count(entry => entry.State == CleanupState.Restored)} 项，", $"Restored {session.Entries.Count(entry => entry.State == CleanupState.Restored)} items. ")
                + Text($"待核对 {new SessionRow(session).Failed} 项。", $"{new SessionRow(session).Failed} need review."));
        });
    }

    private async void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not SessionRow row ||
            UiPrompt.Show(this, Text($"永久删除「{row.Session.AppName}」在本次清理中保存的恢复副本？删除后无法通过本历史记录还原这些副本。", $"Permanently delete the recovery copies for {row.Session.AppName}? You will lose the ability to restore them from this history."),
                Text("确认永久删除", "Confirm permanent deletion"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        await RunBusy(async () =>
        {
            SetStatus(Text("正在删除选中的恢复副本…", "Deleting the selected recovery copies…"));
            var session = await Task.Run(() => recovery.Discard(row.Session.Id));
            await RefreshHistory(session.Id);
            SetStatus(Text($"该次记录已删除 {session.Entries.Count(entry => entry.State == CleanupState.Discarded)} 项恢复副本，", $"Deleted {session.Entries.Count(entry => entry.State == CleanupState.Discarded)} recovery copies. ")
                + Text($"待核对 {new SessionRow(session).Failed} 项。", $"{new SessionRow(session).Failed} need review."));
        });
    }

    private async Task RunBusy(Func<Task> work)
    {
        if (busy)
            return;
        busy = true;
        AppsNav.IsEnabled = AttemptsNav.IsEnabled = HistoryNav.IsEnabled = SettingsNav.IsEnabled = RefreshButton.IsEnabled = false;
        ApplyLanguageButton.IsEnabled = false;
        OnWorkflowBusyChanged(true);
        BusyProgress.Visibility = Visibility.Visible;
        UpdateSelection();
        UpdateHistoryButtons();
        UpdateAttemptsButton();
        try
        {
            await work();
        }
        catch (Exception exception)
        {
            SetStatus(Text($"操作中止。{exception.Message}", $"The operation stopped. {exception.Message}"));
            UiPrompt.Show(this, exception.Message, Text("操作中止", "Operation stopped"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            busy = false;
            AppsNav.IsEnabled = AttemptsNav.IsEnabled = HistoryNav.IsEnabled = SettingsNav.IsEnabled = RefreshButton.IsEnabled = true;
            ApplyLanguageButton.IsEnabled = SelectedLanguage != Preference;
            OnWorkflowBusyChanged(false);
            BusyProgress.Visibility = Visibility.Collapsed;
            UpdateSelection();
            UpdateHistoryButtons();
            UpdateAttemptsButton();
        }
    }

    private void SetStatus(string text)
    {
        StatusText.ToolTip = null;
        StatusText.Text = workflowStatusPrefix is { Length: > 0 } ? $"{workflowStatusPrefix}  {text}" : text;
    }

    private sealed record AppRow(InstalledApp App)
    {
        public string Name => App.Name;
        public string Publisher => App.Publisher.Length == 0 ? "—" : App.Publisher;
        public string Version => App.Version.Length == 0 ? "—" : App.Version;
        public string InstallDate => App.InstalledOn?.ToString("d", Culture) ?? Text("未登记", "Unknown");
        public string Size => App.EstimatedSizeBytes switch
        {
            >= 1L << 30 => $"{App.EstimatedSizeBytes / (double)(1L << 30):0.##} GB",
            >= 1L << 20 => $"{App.EstimatedSizeBytes / (double)(1L << 20):0.##} MB",
            > 0 => $"{App.EstimatedSizeBytes / 1024d:0.#} KB",
            _ => "—"
        };
    }

    private sealed record SessionRow(CleanupSession Session)
    {
        public string Time => Session.CreatedAt.LocalDateTime.ToString("g", Culture);
        public int Quarantined => Session.Entries.Count(entry => entry.State == CleanupState.Quarantined);
        public int Restored => Session.Entries.Count(entry => entry.State == CleanupState.Restored);
        public int Failed => Session.Entries.Count(entry => entry.State is CleanupState.Pending or CleanupState.Failed or CleanupState.Skipped or CleanupState.DiscardInProgress);
    }

    private sealed record AttemptRow(UninstallAttempt Attempt)
    {
        public string Time => Attempt.StartedAt.LocalDateTime.ToString("g", Culture);
        public string Outcome => Attempt.Outcome switch
        {
            UninstallOutcome.Started => Text("结果待核对", "Pending verification"),
            UninstallOutcome.Removed => Text("卸载登记已移除", "Registration removed"),
            UninstallOutcome.RegistrationRemains => Text("卸载登记仍存在", "Still registered"),
            UninstallOutcome.RestartRequired => Attempt.RequiresRestartNow ? Text("需要重启", "Restart required") : Text("重启后可扫描", "Ready to scan"),
            UninstallOutcome.Canceled => Text("已取消", "Canceled"),
            UninstallOutcome.Failed => Text("返回错误", "Returned an error"),
            _ => Text("结果待核对", "Pending verification")
        };
    }

    private sealed record EntryRow(CleanupEntry Entry)
    {
        public string Target => ReviewWindow.DescribeTarget(Entry.Item);
        public string State => Entry.State switch
        {
            CleanupState.Pending => Text("待重新核对", "Pending review"),
            CleanupState.Quarantined => Text("已隔离", "Quarantined"),
            CleanupState.Restored => Text("已恢复", "Restored"),
            CleanupState.Skipped => Text("已跳过", "Skipped"),
            CleanupState.Failed => Text("需处理", "Needs attention"),
            CleanupState.Discarded => Text("副本已删除", "Copy deleted"),
            CleanupState.DiscardInProgress => Text("待继续删除", "Retry deletion"),
            _ => Entry.State.ToString()
        };
    }
}
