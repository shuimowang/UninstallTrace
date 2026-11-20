using System.Windows;
using System.Windows.Controls;
using DeepUninstaller.Services;
using Microsoft.Win32;

namespace DeepUninstaller;

public partial class OrphanScanWindow : Window
{
    public OrphanScanWindow()
    {
        InitializeComponent();
        Title = Heading.Text = Localization.Text("扫描未登记的残留", "Scan orphaned leftovers");
        Help.Text = Localization.Text(
            "适用于已卸载或未出现在程序清单中的应用。输入完整的应用名称，以便按归属线索查找残留。",
            "Use this for software that was already removed or is missing from the program list. Enter its exact product name to look for attributable leftovers.");
        NameLabel.Content = Localization.Text("应用名称", "Product name");
        PublisherLabel.Content = Localization.Text("发布者（可选）", "Publisher (optional)");
        LocationLabel.Content = Localization.Text("原安装目录（可选）", "Previous install folder (optional)");
        BrowseButton.Content = Localization.Text("浏览", "Browse");
        CancelButton.Content = Localization.Text("取消", "Cancel");
        ScanButton.Content = Localization.Text("扫描残留", "Scan leftovers");
        ScanButton.IsEnabled = false;
        Loaded += (_, _) => AppName.Focus();
    }

    public InstalledApp Target => new()
    {
        Name = AppName.Text.Trim(),
        Publisher = Publisher.Text.Trim(),
        InstallLocation = Location.Text.Trim(),
        Hive = RegistryHive.CurrentUser,
        View = RegistryView.Default
    };

    private void OnNameChange(object sender, TextChangedEventArgs e)
    {
        if (ScanButton is not null)
            ScanButton.IsEnabled = AppName.Text.Count(char.IsLetterOrDigit) >= 2;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Localization.Text("选择原安装目录", "Choose the previous install folder") };
        if (dialog.ShowDialog(this) == true)
            Location.Text = dialog.FolderName;
    }

    private void OnScan(object sender, RoutedEventArgs e)
    {
        var target = Target;
        if (target.InstallLocation.Length > 0 &&
            !LeftoverScanner.TryValidateFileSystemTarget(target.InstallLocation, out var reason))
        {
            ErrorText.Text = reason;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
