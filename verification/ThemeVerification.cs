using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using DeepUninstaller;
using DeepUninstaller.Services;

internal static class ThemeVerification
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyOnUiThread(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("WPF theme verification timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyOnUiThread()
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var windowStyle = app.FindResource("DarkWindowStyle");
        var background = ((SolidColorBrush)app.FindResource("WindowBrush")).Color;
        var panel = ((SolidColorBrush)app.FindResource("PanelBrush")).Color;
        var text = ((SolidColorBrush)app.FindResource("TextBrush")).Color;
        var muted = ((SolidColorBrush)app.FindResource("MutedBrush")).Color;

        Window[] windows =
        [
            new MainWindow(),
            new BatchWindow([]),
            new ReviewWindow(new() { Name = "Theme fixture" }, "Theme fixture", new([], []), false, false),
            new OrphanScanWindow(),
            new StoreAppsWindow()
        ];
        try
        {
            foreach (var window in windows)
            {
                if (!ReferenceEquals(window.Style, windowStyle) ||
                    window.Background is not SolidColorBrush windowBackground || windowBackground.Color != background ||
                    window.Foreground is not SolidColorBrush windowText || windowText.Color != text)
                    throw new InvalidOperationException($"{window.GetType().Name} did not apply the shared dark window style.");
            }
            VerifyContrast("Window text", text, background);
            VerifyContrast("Panel text", text, panel);
            VerifyContrast("Muted text", muted, panel);
            VerifyContrast("Selected row text", text, GetColor("AccentBackgroundBrush"));
            VerifyContrast("Primary button text", GetColor("AccentTextBrush"), GetColor("AccentBrush"));
            VerifyContrast("Disabled button text", GetColor("DisabledTextBrush"), GetColor("DisabledSurfaceBrush"));
            InventoryLayoutVerification.Run(windows.OfType<MainWindow>().Single());
            InventoryEnrichmentVerification.Run();
            RecoveryListVerification.Run(windows.OfType<MainWindow>().Single());
            VerifyServiceReview();
        }
        finally
        {
            foreach (var window in windows)
                window.Close();
            app.Shutdown();
        }

        Color GetColor(string key) => ((SolidColorBrush)app.FindResource(key)).Color;
    }

    private static void VerifyServiceReview()
    {
        var folder = new LeftoverItem(LeftoverKind.Directory, @"C:\Program Files\Example", "test", LeftoverConfidence.Strong);
        var sibling = folder with { Target = @"C:\Program Files\Example Tools" };
        var review = new ReviewWindow(new InstalledApp { Name = "Example" }, "", new([folder, sibling], []), true, false);
        try
        {
            review.ApplyServiceReport(new(true, [new("ExampleSvc", "Example service", folder.Target + @"\svc.exe")], []));
            if (!review.SelectedItems.SequenceEqual([sibling]))
                throw new InvalidOperationException("The service's install folder must require explicit selection.");
        }
        finally { review.Close(); }
    }

    private static void VerifyContrast(string name, Color foreground, Color background)
    {
        var foregroundLuminance = Luminance(foreground);
        var backgroundLuminance = Luminance(background);
        var ratio = (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
            (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
        if (ratio < 4.5)
            throw new InvalidOperationException($"{name} contrast is {ratio:0.00}:1. Expected at least 4.5:1.");
    }

    private static double Luminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
