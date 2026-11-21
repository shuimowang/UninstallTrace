using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeepUninstaller;
using DeepUninstaller.Services;

internal static class UpdateUiVerification
{
    public static void Run()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            VerifyManualChecks();
            VerifyBackgroundCheckAndDownload();
            VerifySilentBackgroundChecks();
            VerifyReleasePageFallback();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void VerifyManualChecks()
    {
        var window = new MainWindow();
        var closed = false;
        try
        {
            var settings = Named<Button>(window, "SettingsNav");
            settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, settings));
            var view = Named<Grid>(window, "SettingsView");
            Require(view.Visibility == Visibility.Visible, "The release links and update check need to be reachable through Settings.");

            var version = Named<TextBlock>(window, "AppVersionText");
            _ = Named<CheckBox>(window, "AutoUpdateCheckBox");
            var check = Named<Button>(window, "CheckUpdatesButton");
            var repository = Named<Button>(window, "OpenRepositoryButton");
            var releases = Named<Button>(window, "OpenReleasesButton");
            var status = Named<TextBlock>(window, "UpdateStatusText");
            var openUpdate = Named<Button>(window, "OpenUpdateButton");
            var current = UpdateChecker.CurrentVersion;
            Require(version.Text.Contains(current.ToString(3), StringComparison.Ordinal),
                "Settings must show the running application's version.");
            Require(check is { IsEnabled: true, Visibility: Visibility.Visible } &&
                repository is { IsEnabled: true, Visibility: Visibility.Visible, Tag: Uri repo } &&
                repo == new Uri("https://github.com/shuimowang/UninstallTrace") &&
                releases is { IsEnabled: true, Visibility: Visibility.Visible, Tag: Uri pages } &&
                pages == new Uri("https://github.com/shuimowang/UninstallTrace/releases"),
                "Settings must provide the check button and both official GitHub links.");
            Require(openUpdate.Visibility == Visibility.Collapsed, "Hide the version-specific link until a newer release is found.");

            var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = window.CheckForUpdateAsync(_ => pending.Task);
            Require(!check.IsEnabled && !string.IsNullOrWhiteSpace(status.Text) && !running.IsCompleted,
                "While the request runs, show progress and disable repeat checks.");
            var checkingMessage = status.Text;
            var duplicateInvoked = false;
            window.CheckForUpdateAsync(_ =>
            {
                duplicateInvoked = true;
                return pending.Task;
            }).GetAwaiter().GetResult();
            Require(!duplicateInvoked, "The UI must coalesce overlapping update checks.");

            var available = new Version(current.Major + 1, 0, 0);
            var releasePage = new Uri($"https://github.com/shuimowang/UninstallTrace/releases/tag/v{available}");
            pending.SetResult(new(available, true, releasePage));
            CompleteOnDispatcher(running);
            Require(check.IsEnabled && status.Text.Contains(available.ToString(), StringComparison.Ordinal) &&
                openUpdate is { Visibility: Visibility.Visible, Tag: Uri destination } && destination == releasePage,
                "A newer release must enable its official release page and show its version.");

            window.CheckForUpdateAsync(_ => Task.FromResult(new UpdateCheckResult(current, false, UpdateChecker.ReleasesPage)))
                .GetAwaiter().GetResult();
            var currentMessage = status.Text;
            Require(check.IsEnabled && currentMessage.Length > 0 && currentMessage != checkingMessage &&
                !currentMessage.Contains(available.ToString(), StringComparison.Ordinal) &&
                openUpdate is { Visibility: Visibility.Collapsed, Tag: null },
                "Checking the current version must replace an earlier availability message and clear its destination.");

            window.CheckForUpdateAsync(_ => Task.FromException<UpdateCheckResult>(new HttpRequestException("Synthetic offline failure.")))
                .GetAwaiter().GetResult();
            Require(check.IsEnabled && status.Text.Length > 0 && status.Text != currentMessage && status.Text != checkingMessage &&
                openUpdate is { Visibility: Visibility.Collapsed, Tag: null },
                "A failed check must show a distinct status and allow a retry without a stale link.");

            window.CheckForUpdateAsync(_ => Task.FromResult(new UpdateCheckResult(available, true, releasePage)))
                .GetAwaiter().GetResult();
            Require(check.IsEnabled && openUpdate is { Visibility: Visibility.Visible, Tag: Uri retryPage } && retryPage == releasePage,
                "A successful retry must recover the available release message and exact destination.");

            var onClose = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lifetime = default(CancellationToken);
            var canceled = window.CheckForUpdateAsync(token =>
            {
                lifetime = token;
                return onClose.Task;
            });
            window.Close();
            closed = true;
            Require(lifetime.IsCancellationRequested, "Closing the window must cancel an in-flight update check.");
            onClose.SetResult(new(available, true, releasePage));
            CompleteOnDispatcher(canceled);
            Require(openUpdate is { Visibility: Visibility.Collapsed, Tag: null },
                "A response that races window closure must not present an available release.");
        }
        finally
        {
            if (!closed)
                window.Close();
        }
    }

    private static void VerifyBackgroundCheckAndDownload()
    {
        var window = new MainWindow(startInSettings: false, supportsPortableUpdate: true);
        var appBytes = new byte[128 * 1024];
        var available = new Version(UpdateChecker.CurrentVersion.Major + 1, 0, 0);
        var repository = "https://github.com/shuimowang/UninstallTrace";
        var asset = new UpdateAsset(new Uri($"{repository}/releases/download/v{available}/UninstallTrace.exe"),
            appBytes.Length, Convert.ToHexString(SHA256.HashData(appBytes)));
        var release = new UpdateCheckResult(available, true, new Uri($"{repository}/releases/tag/v{available}"), asset);
        var stagingFolders = new List<string>();
        string? stagedPayload = null;
        try
        {
            var banner = Named<FrameworkElement>(window, "UpdateBanner");
            var message = Named<TextBlock>(window, "UpdateBannerText");
            var download = Named<Button>(window, "DownloadUpdateButton");
            var progress = Named<ProgressBar>(window, "UpdateBannerProgress");
            var cancel = Named<Button>(window, "CancelUpdateDownloadButton");
            var install = Named<Button>(window, "InstallUpdateButton");
            Require(banner.Visibility == Visibility.Collapsed, "Opening the main window should not show an empty update notification.");

            var checkResponse = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var checkedCount = 0;
            Task<UpdateCheckResult> Check(CancellationToken token)
            {
                checkedCount++;
                return checkResponse.Task.WaitAsync(token);
            }

            var backgroundCheck = window.CheckForUpdatesAtStartupAsync(true, Check);
            CompleteOnDispatcher(window.CheckForUpdatesAtStartupAsync(true, Check));
            Require(checkedCount == 1 && banner.Visibility == Visibility.Collapsed,
                "One background check should run for an open window, with the banner hidden while awaiting its response.");

            checkResponse.SetResult(release);
            CompleteOnDispatcher(backgroundCheck);
            Require(banner.Visibility == Visibility.Visible && message.Text.Contains(available.ToString(), StringComparison.Ordinal) &&
                download.IsEnabled && download.Visibility == Visibility.Visible &&
                install.Visibility == Visibility.Collapsed && progress.Visibility == Visibility.Collapsed,
                "An available release should appear on the main window and require an explicit download.");
            Require(Named<Grid>(window, "SettingsView").Visibility == Visibility.Collapsed,
                "The main-window update notification should not redirect to Settings.");

            var failing = window.DownloadUpdateAsync(
                (_, _, _, _) => Task.FromException(new HttpRequestException("Synthetic interrupted connection.")), CreateStaging);
            CompleteOnDispatcher(failing);
            Require(banner.Visibility == Visibility.Visible && download.Visibility == Visibility.Visible && download.IsEnabled &&
                install.Visibility == Visibility.Collapsed && !Directory.Exists(stagingFolders[^1]),
                "A download failure must clean up its staging folder and show a retry without allowing installation.");

            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellation = default(CancellationToken);
            var canceled = window.DownloadUpdateAsync((nextAsset, path, report, token) =>
            {
                Require(nextAsset == asset && path == stagedPayload,
                    "Download must use the offered release and its own staging path.");
                cancellation = token;
                File.WriteAllBytes(path, appBytes.AsSpan(0, appBytes.Length / 2).ToArray());
                report.Report(appBytes.Length / 2);
                return paused.Task.WaitAsync(token);
            }, CreateStaging);
            WaitForDispatcher(() => progress.Value >= 49.5 || canceled.IsCompleted,
                "The transfer did not publish progress on the WPF dispatcher.");
            Require(progress.Visibility == Visibility.Visible && Math.Abs(progress.Value - 50) < 0.5 &&
                cancel.IsEnabled && cancel.Visibility == Visibility.Visible && install.Visibility == Visibility.Collapsed,
                "An active transfer should show byte-derived progress and allow cancellation before installation.");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancel));
            CompleteOnDispatcher(canceled);
            Require(cancellation.IsCancellationRequested && !Directory.Exists(stagingFolders[^1]) &&
                download.IsEnabled && download.Visibility == Visibility.Visible && install.Visibility == Visibility.Collapsed,
                "Canceling must stop the transfer, remove partial files, and allow retry.");

            CompleteOnDispatcher(window.DownloadUpdateAsync((_, path, _, _) => File.WriteAllBytesAsync(path, appBytes), CreateStaging));
            Require(install.IsEnabled && install.Visibility == Visibility.Visible &&
                cancel.Visibility == Visibility.Collapsed && File.Exists(stagedPayload),
                "A completed download must leave the staged payload for the explicit restart-and-install action.");
        }
        finally { window.Close(); }
        Require(stagingFolders.All(path => !Directory.Exists(path)),
            "Closing the window without applying the update must remove the staged installer.");

        PortableUpdateStaging CreateStaging()
        {
            var path = Path.Join(Path.GetTempPath(), $"UninstallTrace-TestUpdate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            stagingFolders.Add(path);
            var staging = new PortableUpdateStaging(path);
            stagedPayload = staging.DownloadPath;
            return staging;
        }
    }

    private static void VerifySilentBackgroundChecks()
    {
        var disabledWindow = new MainWindow();
        try
        {
            var called = false;
            CompleteOnDispatcher(disabledWindow.CheckForUpdatesAtStartupAsync(false, _ =>
            {
                called = true;
                return Task.FromResult(new UpdateCheckResult(UpdateChecker.CurrentVersion, false, UpdateChecker.ReleasesPage));
            }));
            Require(!called, "Disabling the automatic option must suppress the background request.");
        }
        finally { disabledWindow.Close(); }

        foreach (var check in new Func<CancellationToken, Task<UpdateCheckResult>>[]
        {
            _ => Task.FromException<UpdateCheckResult>(new HttpRequestException("Synthetic offline fixture.")),
            _ => Task.FromResult(new UpdateCheckResult(UpdateChecker.CurrentVersion, false, UpdateChecker.ReleasesPage)),
        })
        {
            var window = new MainWindow();
            try
            {
                CompleteOnDispatcher(window.CheckForUpdatesAtStartupAsync(true, check));
                Require(Named<FrameworkElement>(window, "UpdateBanner").Visibility == Visibility.Collapsed,
                    "Current releases and failed background requests must leave the main window undisturbed.");
            }
            finally { window.Close(); }
        }
    }

    private static void VerifyReleasePageFallback()
    {
        var next = new Version(UpdateChecker.CurrentVersion.Major + 1, 0, 0);
        var page = new Uri($"{UpdateChecker.ReleasesPage}/tag/v{next}");
        var window = new MainWindow(startInSettings: false, supportsPortableUpdate: true);
        try
        {
            var release = new UpdateCheckResult(next, true, page);
            CompleteOnDispatcher(window.CheckForUpdatesAtStartupAsync(true, _ => Task.FromResult(release)));
            Require(Named<Button>(window, "DownloadUpdateButton").Visibility == Visibility.Collapsed &&
                Named<Button>(window, "OpenBannerReleaseButton") is { Visibility: Visibility.Visible, Tag: Uri destination } && destination == page,
                "A new release without verifiable binary metadata must offer the official release page instead of downloading.");
            var later = Named<Button>(window, "DismissUpdateButton");
            later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, later));
            Require(Named<FrameworkElement>(window, "UpdateBanner").Visibility == Visibility.Collapsed,
                "The update notification must be dismissible while leaving its Settings link available.");
            var settings = Named<Button>(window, "SettingsNav");
            settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, settings));
            Require(Named<Button>(window, "OpenUpdateButton") is { Visibility: Visibility.Visible, Tag: Uri saved } && saved == page,
                "Dismissal must preserve access to the release from Settings.");
            var show = Named<Button>(window, "ShowUpdateButton");
            Require(show.Visibility == Visibility.Visible, "A dismissed notification can be brought back from Settings.");
            show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, show));
            Require(Named<FrameworkElement>(window, "UpdateBanner").Visibility == Visibility.Visible,
                "Show update must bring back the dismissed version without another request.");
        }
        finally { window.Close(); }
    }

    private static void CompleteOnDispatcher(Task task)
    {
        WaitForDispatcher(() => task.IsCompleted, "The synthetic update must complete on the WPF dispatcher.");
        task.GetAwaiter().GetResult();
    }

    private static void WaitForDispatcher(Func<bool> ready, string message)
    {
        if (ready())
            return;

        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) => frame.Continue = !ready() && elapsed.Elapsed < TimeSpan.FromSeconds(3);
        poll.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { poll.Stop(); }
        Require(ready(), message);
    }

    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing update control {name}.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
