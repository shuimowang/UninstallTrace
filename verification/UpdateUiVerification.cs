using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeepUninstaller;
using DeepUninstaller.Services;

internal static class UpdateUiVerification
{
    public static void Run()
    {
        var window = new MainWindow();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var closed = false;
        try
        {
            var settings = Named<Button>(window, "SettingsNav");
            settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, settings));
            var view = Named<Grid>(window, "SettingsView");
            Require(view.Visibility == Visibility.Visible, "The release links and update check need to be reachable through Settings.");

            var version = Named<TextBlock>(window, "AppVersionText");
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
            Require(openUpdate.Visibility == Visibility.Collapsed, "Hide the direct download link until a newer release is found.");

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
            // Let the pending WPF continuation finish on this STA thread without showing a window.
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            Require(running.IsCompleted, "Completing the request should publish its result on the WPF dispatcher.");
            running.GetAwaiter().GetResult();
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
            window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
            Require(canceled.IsCompletedSuccessfully && openUpdate is { Visibility: Visibility.Collapsed, Tag: null },
                "A response that races window closure must not present an available release.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (!closed)
                window.Close();
        }
    }

    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing update control {name}.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
