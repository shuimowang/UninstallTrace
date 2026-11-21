using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DeepUninstaller.Services;

namespace DeepUninstaller;

public partial class BatchWindow : Window
{
    private BatchRow[] rows;
    private ListSortDirection dateDirection = ListSortDirection.Descending;

    public BatchWindow(IReadOnlyList<InstalledApp> apps)
    {
        rows = apps.Select(app => new BatchRow(app)).ToArray();
        InitializeComponent();
        Title = Localization.Text("批量卸载", "Batch uninstall");
        HelpText.Text = Localization.Text(
            "按清单顺序逐个运行原厂卸载。每个程序的残留都可独立审查，遇到取消、重启或结果不明时暂停。",
            "Run vendor uninstallers one at a time in list order. Review each program’s leftovers. The queue pauses on cancellation, a restart request, or an unconfirmed result.");
        AutomationProperties.SetName(Search, Localization.Text("搜索队列候选程序", "Search available programs"));
        Search.ToolTip = AutomationProperties.GetName(Search);
        AppColumn.Header = Localization.Text("程序", "Program");
        DateColumn.Header = Localization.Text("安装日期", "Installed");
        PublisherColumn.Header = Localization.Text("发布者", "Publisher");
        VersionColumn.Header = Localization.Text("版本", "Version");
        ScopeColumn.Header = Localization.Text("安装范围", "Install scope");
        Empty.Text = Localization.Text("没有匹配的可卸载程序", "No matching uninstallable programs");
        CancelButton.Content = Localization.Text("取消", "Cancel");
        StartButton.Content = Localization.Text("启动队列", "Start queue");
        foreach (var row in rows)
            row.PropertyChanged += (_, _) => UpdateCount();
        SortRows();
        UpdateCount();
        Loaded += (_, _) => Search.Focus();
    }

    public IReadOnlyList<InstalledApp> SelectedApps => rows.Where(row => row.Selected).Select(row => row.App).ToArray();

    private void OnSort(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column != DateColumn)
            return;
        dateDirection = dateDirection == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        SortRows();
    }

    private void SortRows()
    {
        rows = SortOrder.KnownFirst(rows, row => row.App.InstalledOn, dateDirection)
            .ThenBy(row => row.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.App.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        Filter();
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (Choices is not null && Empty is not null)
            Filter();
    }

    private void Filter()
    {
        var text = Search.Text.Trim();
        var result = rows.Where(row => row.App.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                       row.App.Publisher.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        Choices.ItemsSource = result;
        DateColumn.SortDirection = dateDirection;
        Empty.Visibility = result.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCount()
    {
        var selected = rows.Count(row => row.Selected);
        CountText.Text = Localization.Text($"已选 {selected} 个程序", $"{selected} selected");
        StartButton.IsEnabled = selected > 0;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApps;
        if (selected.Count == 0)
            return;
        var names = string.Join("\n", selected.Take(8).Select(app => $"• {app.Name}"));
        if (selected.Count > 8)
            names += $"\n{Localization.Text($"另有 {selected.Count - 8} 个程序", $"Plus {selected.Count - 8} more programs")}";
        var consent = UiPrompt.Show(this,
            Localization.Text($"为以下程序启动原厂卸载队列？\n\n{names}", $"Start the vendor uninstall queue for these programs?\n\n{names}"),
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (consent == MessageBoxResult.OK)
            DialogResult = true;
    }

    private sealed class BatchRow(InstalledApp app) : INotifyPropertyChanged
    {
        private bool selected;
        public InstalledApp App { get; } = app;
        public string DateLabel => App.InstalledOn?.ToString("d", Localization.Culture) ?? Localization.Text("未登记", "Unknown");
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value)
                    return;
                selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }
}
