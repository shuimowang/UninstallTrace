using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using DeepUninstaller.Services;
using Microsoft.Win32;
using static DeepUninstaller.Localization;

namespace DeepUninstaller;

public partial class ReviewWindow : Window
{
    private readonly ReviewRow[] rows;
    private readonly bool needsConsent;
    private readonly bool readOnly;
    private readonly InstalledApp app;
    private CancellationTokenSource? serviceRead;
    public int? RelatedServices { get; private set; }

    public ReviewWindow(InstalledApp app, string summary, LeftoverScanResult result, bool registrationConfirmedGone, bool readOnly, string? displayName = null)
    {
        this.app = app;
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(Culture.IetfLanguageTag);
        this.readOnly = readOnly;
        needsConsent = !registrationConfirmedGone && !readOnly;
        ProductText.Text = displayName ?? app.Name;
        SummaryText.Text = summary;
        rows = result.Items.OrderBy(item => item.Confidence).ThenBy(item => item.Kind).ThenBy(item => item.Target, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new ReviewRow(item, registrationConfirmedGone && !readOnly && item.IsRecommended))
            .ToArray();
        foreach (var row in rows)
            row.PropertyChanged += (_, _) => UpdateCount();
        ReviewGrid.ItemsSource = rows;
        ReviewGrid.SelectedItem = rows.FirstOrDefault();
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionTools.Visibility = readOnly || rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChoiceColumn.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        CleanButton.Visibility = readOnly || rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ManualConsent.Visibility = needsConsent && rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanupHint.Visibility = readOnly || rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (result.Warnings.Count > 0)
        {
            Warnings.Header = Text($"扫描提示（{result.Warnings.Count}）", $"Scan notes ({result.Warnings.Count})");
            WarningText.Text = string.Join("\n", result.Warnings);
            Warnings.Visibility = Visibility.Visible;
        }
        UpdateCount();
    }

    public IReadOnlyList<LeftoverItem> SelectedItems => rows.Where(row => row.IsSelected).Select(row => row.Item).ToArray();

    private async void OnServicesExpanded(object sender, RoutedEventArgs e)
    {
        using var request = new CancellationTokenSource();
        serviceRead = request;
        RelatedServices = null;
        UpdateCount();
        ServicesText.Text = Text("正在检查服务登记…", "Reading service registrations…");
        try
        {
            var report = await Task.Run(() => ServiceResidueScanner.Scan(app, InstalledApps.Read(), request.Token), request.Token);
            if (request.IsCancellationRequested)
                return;
            ApplyServiceReport(report);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!request.IsCancellationRequested)
                ServicesText.Text = Text($"无法完成服务检查。{exception.Message}", $"Could not complete the service check. {exception.Message}");
        }
        finally
        {
            if (serviceRead == request)
            {
                serviceRead = null;
                UpdateCount();
            }
        }
    }

    internal void ApplyServiceReport(ServiceResidueReport report)
    {
        var lines = new List<string>();
        if (!report.HasOwnershipEvidence)
            lines.Add(Text("现有安装登记不足以确认独占安装目录。", "There is insufficient registration evidence for an exclusive install folder."));
        else if (report.Matches.Count == 0)
            lines.Add(Text("在可读取的登记中，未找到直接指向此目录的独立服务。", "No readable own-process service registration points directly into this folder."));
        else
        {
            lines.Add(Text("以下服务仍有登记。包含其程序路径的候选项需手动勾选。", "These services are registered. Items containing their executable paths require manual selection."));
            foreach (var match in report.Matches)
            {
                var name = string.IsNullOrEmpty(match.DisplayName) || match.ServiceName == match.DisplayName
                    ? match.ServiceName : $"{match.DisplayName} ({match.ServiceName})";
                lines.Add($"{name}  —  {match.ExecutablePath}");
            }
        }
        foreach (var row in rows)
            row.SetServiceMatch(report.Matches.Any(service => row.ContainsExecutable(service.ExecutablePath)));
        lines.AddRange(report.Warnings);
        RelatedServices = report.HasOwnershipEvidence ? report.Matches.Count : null;
        ServicesText.Text = string.Join(Environment.NewLine, lines);
    }

    private void OnServicesCollapsed(object sender, RoutedEventArgs e) => serviceRead?.Cancel();

    protected override void OnClosed(EventArgs e)
    {
        serviceRead?.Cancel();
        base.OnClosed(e);
    }

    public static string DescribeTarget(LeftoverItem item)
    {
        if (item.Hive is null)
            return item.Target;
        var hive = item.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
        var view = item.View switch
        {
            RegistryView.Registry32 => Text("32 位", "32-bit"),
            RegistryView.Registry64 => Text("64 位", "64-bit"),
            _ => Text("默认", "Default")
        };
        var value = item.ValueName is null ? "" : $"  →  {(item.ValueName.Length == 0 ? Text("（默认）", "(Default)") : item.ValueName)}";
        return Text($"{hive}（{view}）\\{item.Target}{value}", $"{hive} ({view})\\{item.Target}{value}");
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = ReviewGrid.SelectedItem as ReviewRow;
        ItemDetail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        LocationText.Text = row?.Target ?? "";
        EvidenceText.Text = row?.Item.Evidence ?? "";
    }

    private void OnReviewGridKeyDown(object sender, KeyEventArgs e)
    {
        if (!readOnly && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None
            && e.OriginalSource is not CheckBox && ReviewGrid.SelectedItem is ReviewRow row)
        {
            row.IsSelected = !row.IsSelected;
            e.Handled = true;
        }
    }

    private void OnSelectStrong(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows)
            row.IsSelected = row.RecommendSelection;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows)
            row.IsSelected = false;
    }

    private void OnConsent(object sender, RoutedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        var selected = rows.Count(row => row.IsSelected);
        ReviewCount.Text = readOnly ? Text($"找到 {rows.Length} 项", $"{rows.Length} items found") : Text($"找到 {rows.Length} 项，已选 {selected} 项", $"{rows.Length} found, {selected} selected");
        CleanButton.IsEnabled = serviceRead is null && !readOnly && selected > 0 && (!needsConsent || ManualConsent.IsChecked == true);
    }

    private void OnClean(object sender, RoutedEventArgs e)
    {
        if (CleanButton.IsEnabled)
            DialogResult = true;
    }

    private sealed class ReviewRow(LeftoverItem item, bool selected) : INotifyPropertyChanged
    {
        private bool isSelected = selected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public LeftoverItem Item { get; } = item;
        public string Target => DescribeTarget(Item);
        private bool containsService;
        public bool RecommendSelection => Item.IsRecommended && !containsService;
        public string Confidence => containsService ? Text("关联服务", "Service path") :
            Item.IsRecommended ? Text("强归属", "Strong match") : Text("需核对", "Needs review");
        public bool ContainsExecutable(string path) =>
            (Item.Kind == LeftoverKind.File && string.Equals(path, Item.Target, StringComparison.OrdinalIgnoreCase)) ||
            (Item.Kind == LeftoverKind.Directory && path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(Item.Target) +
                System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        public void SetServiceMatch(bool matched)
        {
            if (matched) IsSelected = false;
            if (containsService == matched) return;
            containsService = matched;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Confidence)));
        }
        public string Kind => Item.Kind switch
        {
            LeftoverKind.File => Text("文件", "File"),
            LeftoverKind.Directory => Text("文件夹", "Folder"),
            LeftoverKind.RegistryKey => Text("注册表项", "Registry key"),
            LeftoverKind.RegistryValue => Text("注册表值", "Registry value"),
            _ => Item.Kind.ToString()
        };
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                    return;
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
