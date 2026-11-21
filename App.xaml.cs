using System.Windows;
using DeepUninstaller.Services;

namespace DeepUninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (PortableUpdateInstaller.TryHandleStartup(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    internal void RefreshMainWindowLanguage(MainWindow previous)
    {
        var settingsWindow = new MainWindow(startInSettings: true)
        {
            WindowState = previous.WindowState,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = previous.RestoreBounds.Left,
            Top = previous.RestoreBounds.Top,
            Width = previous.RestoreBounds.Width,
            Height = previous.RestoreBounds.Height,
        };
        settingsWindow.RestoreAppListView(previous);
        settingsWindow.RestoreUpdateOffer(previous);
        MainWindow = settingsWindow;
        settingsWindow.Show();
        previous.Close();
    }
}
