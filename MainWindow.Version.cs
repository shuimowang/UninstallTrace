using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using DeepUninstaller.Services;
using static DeepUninstaller.Localization;

namespace DeepUninstaller;

public partial class MainWindow
{
    private CancellationTokenSource? updateCheck;

    private async void OnCheckForUpdate(object sender, RoutedEventArgs e) =>
        await CheckForUpdateAsync(UpdateChecker.CheckAsync);

    internal async Task CheckForUpdateAsync(Func<CancellationToken, Task<UpdateCheckResult>> check)
    {
        if (updateCheck is not null)
            return;

        using var request = new CancellationTokenSource();
        updateCheck = request;
        CheckUpdatesButton.IsEnabled = false;
        OpenUpdateButton.Visibility = Visibility.Collapsed;
        OpenUpdateButton.Tag = null;
        SetUpdateStatus(Text("正在检查更新…", "Checking for updates…"));
        try
        {
            var result = await check(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (result.IsUpdateAvailable)
            {
                SetUpdateStatus(Text($"发现新版 v{result.LatestVersion}。", $"Version v{result.LatestVersion} is available."));
                OpenUpdateButton.Tag = result.ReleasePage;
                OpenUpdateButton.Visibility = Visibility.Visible;
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
            SetUpdateStatus(Text("连接 GitHub 超时。可以重试，或打开「发行记录」。",
                "The GitHub request timed out. Try again or open Release history."));
        }
        catch (HttpRequestException error)
        {
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
            SetUpdateStatus(Text("无法读取 GitHub 的发行版本。可以打开「发行记录」查看。",
                "Could not read the release version from GitHub. See Release history."));
        }
        finally
        {
            updateCheck = null;
            if (!request.IsCancellationRequested)
                CheckUpdatesButton.IsEnabled = true;
        }
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
