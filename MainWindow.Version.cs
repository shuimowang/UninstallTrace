using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using DeepUninstaller.Services;
using static DeepUninstaller.Localization;

namespace DeepUninstaller;

public partial class MainWindow
{
    private readonly bool supportsPortableUpdate;
    private CancellationTokenSource? updateCheck;
    private CancellationTokenSource? updateDownload;
    private UpdateCheckResult? updateOffer;
    private PortableUpdateStaging? stagedUpdate;
    private bool autoCheckStarted;
    private bool updateNoticeDismissed;
    private bool preparingUpdate;
    private bool updateWindowClosed;

    private async void OnCheckForUpdate(object sender, RoutedEventArgs e) =>
        await CheckForUpdateAsync(UpdateChecker.CheckAsync);

    internal Task CheckForUpdatesAtStartupAsync(bool enabled, Func<CancellationToken, Task<UpdateCheckResult>> check)
    {
        if (autoCheckStarted)
            return Task.CompletedTask;
        autoCheckStarted = true;
        return enabled ? CheckForUpdateAsync(check, automatically: true) : Task.CompletedTask;
    }

    internal async Task CheckForUpdateAsync(Func<CancellationToken, Task<UpdateCheckResult>> check, bool automatically = false)
    {
        if (updateCheck is not null || updateDownload is not null || stagedUpdate is not null)
            return;

        using var request = new CancellationTokenSource();
        updateCheck = request;
        updateOffer = null;
        RefreshUpdateActions();
        if (!automatically)
            SetUpdateStatus(Text("正在检查更新…", "Checking for updates…"));
        try
        {
            var result = await check(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (result.IsUpdateAvailable)
            {
                updateOffer = result;
                updateNoticeDismissed = false;
                ShowOffer();
            }
            else
            {
                var sameVersion = result.LatestVersion == UpdateChecker.CurrentVersion;
                SetUpdateStatus(sameVersion ? Text("已是最新正式版。", "You have the latest stable release.") :
                    Text($"当前版本高于最新正式版 v{result.LatestVersion}。", $"Your version is newer than the latest stable release, v{result.LatestVersion}."));
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (request.IsCancellationRequested && error is (HttpRequestException or TimeoutException or InvalidDataException)) { }
        catch (TimeoutException)
        {
            if (!automatically)
                SetUpdateStatus(Text("连接 GitHub 超时。可以重试，或打开「发行记录」。",
                    "The GitHub request timed out. Try again or open Release history."));
        }
        catch (HttpRequestException error)
        {
            if (!automatically)
                SetUpdateStatus(error.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => Text(
                        "GitHub 暂时拒绝查询。可以稍后重试，或打开「发行记录」。",
                        "GitHub rejected the request. Try again later or open Release history."),
                    HttpStatusCode.NotFound => Text("GitHub 未返回正式发行版。可以打开「发行记录」查看。",
                        "GitHub did not return a stable release. See Release history."),
                    _ => Text("连接 GitHub 失败。可以重试，或打开「发行记录」。",
                        "Could not reach GitHub. Try again or open Release history."),
                });
        }
        catch (InvalidDataException)
        {
            if (!automatically)
                SetUpdateStatus(Text("无法读取 GitHub 的发行版本。可以打开「发行记录」查看。",
                    "Could not read the release version from GitHub. See Release history."));
        }
        finally
        {
            updateCheck = null;
            if (!request.IsCancellationRequested)
                RefreshUpdateActions();
        }
    }

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        var enabled = AutoUpdateCheckBox.IsChecked == true;
        try { UpdatePreferences.SaveAutoCheck(enabled); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            AutoUpdateCheckBox.IsChecked = UpdatePreferences.ReadAutoCheck();
            SetUpdateStatus(Text("无法保存自动检查更新设置。", "Could not save the automatic update check preference."));
        }
    }

    private async void OnDownloadUpdate(object sender, RoutedEventArgs e) =>
        await DownloadUpdateAsync(UpdateDownload.DownloadAsync);

    internal async Task DownloadUpdateAsync(Func<UpdateAsset, string, IProgress<long>, CancellationToken, Task> download,
        Func<PortableUpdateStaging>? createStaging = null)
    {
        if (!supportsPortableUpdate || updateOffer?.Asset is not { } asset || updateDownload is not null || stagedUpdate is not null)
            return;

        using var request = new CancellationTokenSource();
        updateDownload = request;
        updateNoticeDismissed = false;
        ApplyLanguageButton.IsEnabled = false;
        SetUpdateNotice(Text("正在下载更新…", "Downloading update…"));
        UpdateBannerProgress.Value = 0;
        RefreshUpdateActions();
        PortableUpdateStaging? staging = null;
        try
        {
            staging = await Task.Run(createStaging ?? PortableUpdateInstaller.CreateStaging);
            request.Token.ThrowIfCancellationRequested();
            var progress = new Progress<long>(received =>
            {
                if (updateDownload != request || request.IsCancellationRequested)
                    return;
                UpdateBannerProgress.Value = 100d * received / asset.Size;
                var done = received == 0 ? "0 B" : FormatSize(received);
                var total = FormatSize(asset.Size);
                SetUpdateNotice(Text($"正在下载更新　{done} / {total}", $"Downloading update · {done} / {total}"));
            });
            await download(asset, staging.DownloadPath, progress, request.Token);
            request.Token.ThrowIfCancellationRequested();
            stagedUpdate = staging;
            staging = null;
            ShowOffer();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!updateWindowClosed)
                SetUpdateNotice(Text("已取消下载。可以重新下载更新。", "Download canceled. You can try again."));
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or InvalidDataException or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or Win32Exception)
        {
            if (!updateWindowClosed)
            {
                var message = error switch
                {
                    InvalidDataException => Text("更新包校验失败。请重试下载。", "The update could not be verified. Download it again."),
                    HttpRequestException or TimeoutException => Text("更新下载中断。请重试。", "The update download was interrupted. Try again."),
                    _ => Text("无法保存更新包。请检查磁盘空间和文件夹权限。", "Could not save the update. Check free space and folder permissions."),
                };
                SetUpdateNotice(request.IsCancellationRequested ? Text("已取消下载。可以重新下载更新。", "Download canceled. You can try again.") : message);
            }
        }
        finally
        {
            staging?.Dispose();
            updateDownload = null;
            if (!updateWindowClosed)
            {
                ApplyLanguageButton.IsEnabled = !busy && SelectedLanguage != Preference;
                RefreshUpdateActions();
            }
        }
    }

    private void OnCancelUpdateDownload(object sender, RoutedEventArgs e) => updateDownload?.Cancel();

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (busy || preparingUpdate || stagedUpdate is not { } staging || updateOffer?.Asset is not { } asset)
            return;

        preparingUpdate = true;
        IsEnabled = false;
        SetUpdateNotice(Text("正在准备重启…", "Preparing to restart…"));
        RefreshUpdateActions();
        try
        {
            await staging.StartInstallAsync(asset);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
                                      InvalidOperationException or Win32Exception or TimeoutException or OperationCanceledException or InvalidDataException)
        {
            preparingUpdate = false;
            IsEnabled = true;
            SetUpdateNotice(Text("未能应用更新。可以重试，或打开「发行记录」。", "Could not apply the update. Try again or open Release history."));
            UpdateBannerText.ToolTip = error.Message;
            RefreshUpdateActions();
            return;
        }

        Application.Current.Shutdown();
    }

    internal void RestoreUpdateOffer(MainWindow previous)
    {
        updateOffer = previous.updateOffer;
        stagedUpdate = previous.stagedUpdate;
        previous.stagedUpdate = null;
        updateNoticeDismissed = previous.updateNoticeDismissed;
        if (updateOffer is not null)
            ShowOffer();
        RefreshUpdateActions();
    }

    private void OnDismissUpdate(object sender, RoutedEventArgs e)
    {
        updateNoticeDismissed = true;
        RefreshUpdateActions();
    }

    private void OnShowUpdate(object sender, RoutedEventArgs e)
    {
        updateNoticeDismissed = false;
        RefreshUpdateActions();
    }

    private void ShowOffer()
    {
        if (updateOffer is not { } offer)
            return;
        var version = $"v{offer.LatestVersion}";
        var message = stagedUpdate is not null ? Text($"{version} 已下载并通过校验。", $"{version} is downloaded and verified.") :
            supportsPortableUpdate && offer.Asset is { } asset
                ? Text($"发现新版 {version}（{FormatSize(asset.Size)}）。", $"Version {version} is available ({FormatSize(asset.Size)}).")
                : Text($"发现新版 {version}。请打开发行页查看。", $"Version {version} is available. See the release page.");
        SetUpdateNotice(message);
    }

    private void SetUpdateNotice(string message)
    {
        UpdateBannerText.ToolTip = null;
        UpdateBannerText.Text = message;
        SetUpdateStatus(message);
    }

    private void RefreshUpdateActions()
    {
        var offered = updateOffer is not null;
        var canDownload = supportsPortableUpdate && updateOffer?.Asset is not null;
        var downloading = updateDownload is not null;
        var ready = stagedUpdate is not null;
        UpdateBanner.Visibility = Show(offered && !updateNoticeDismissed);
        DownloadUpdateButton.Visibility = Show(canDownload && !downloading && !ready);
        InstallUpdateButton.Visibility = Show(ready);
        InstallUpdateButton.IsEnabled = !busy && !preparingUpdate;
        CancelUpdateDownloadButton.Visibility = Show(downloading);
        UpdateBannerProgress.Visibility = Show(downloading);
        DismissUpdateButton.Visibility = Show(!downloading && !preparingUpdate);
        OpenBannerReleaseButton.Visibility = Show(offered && !canDownload);
        OpenBannerReleaseButton.Tag = updateOffer?.ReleasePage;
        ShowUpdateButton.Visibility = Show(offered && updateNoticeDismissed);
        OpenUpdateButton.Visibility = Show(offered);
        OpenUpdateButton.Tag = updateOffer?.ReleasePage;
        CheckUpdatesButton.IsEnabled = updateCheck is null && !downloading && !ready;

        static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenProjectPage(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Uri uri })
            return;
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            SetUpdateStatus(Text($"无法打开浏览器。请在浏览器中打开 {uri.AbsoluteUri}",
                $"Could not open your browser. Visit {uri.AbsoluteUri}"));
        }
    }

    private void SetUpdateStatus(string message)
    {
        UpdateStatusText.Text = message;
        UpdateStatusText.Visibility = Visibility.Visible;
    }
}
