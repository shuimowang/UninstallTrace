using System.Windows;
using DeepUninstaller.Services;

namespace DeepUninstaller;

public partial class MainWindow
{
    private void OnWorkflowBusyChanged(bool isBusy)
    {
        BatchButton.IsEnabled = !isBusy;
        OrphanButton.IsEnabled = !isBusy;
        StoreButton.IsEnabled = !isBusy;
    }

    private void OnStoreApps(object sender, RoutedEventArgs e) => new StoreAppsWindow { Owner = this }.ShowDialog();

    private async void OnBatchUninstall(object sender, RoutedEventArgs e)
    {
        var candidates = apps.Where(app => !app.IsProtected && UninstallCommand.TryCreate(app, out _, out _)).ToArray();
        var picker = new BatchWindow(candidates) { Owner = this };
        if (picker.ShowDialog() != true)
            return;

        var queue = picker.SelectedApps;
        await RunBusy(async () =>
        {
            var completed = 0;
            foreach (var app in queue)
            {
                workflowStatusPrefix = $"[{completed + 1}/{queue.Count}]";
                bool removed;
                try { removed = await UninstallApp(app); }
                finally { workflowStatusPrefix = null; }
                if (!removed)
                {
                    SetStatus(Localization.Text(
                        $"队列已暂停（{completed}/{queue.Count}）。请在卸载记录核对当前程序的结果，再继续剩余程序。",
                        $"Queue paused ({completed}/{queue.Count}). Check the current program in uninstall history before continuing."));
                    return;
                }
                completed++;
            }
            SetStatus(Localization.Text(
                $"队列完成，已确认卸载 {completed} 个程序。",
                $"Queue complete. Confirmed {completed} removals."));
        });
    }

    private async void OnOrphanScan(object sender, RoutedEventArgs e)
    {
        var prompt = new OrphanScanWindow { Owner = this };
        if (prompt.ShowDialog() != true)
            return;
        var app = prompt.Target;
        await RunBusy(async () =>
        {
            var isListed = await Task.Run(() => InstalledApps.Read().Any(
                listed => string.Equals(listed.Name.Trim(), app.Name, StringComparison.OrdinalIgnoreCase)));
            if (isListed)
                throw new InvalidOperationException(Localization.Text(
                    "这个名称仍有卸载登记。请从已安装程序清单中选择对应的项目来扫描。",
                    "This name is still registered. Select the matching program in the installed programs list to scan it."));
            await ScanAndReview(app, Localization.Text(
                "这是手动指定的目标。请核对残留归属，选中需要隔离的项目。",
                "This target was entered manually. Verify ownership before selecting items to quarantine."));
        });
    }
}
