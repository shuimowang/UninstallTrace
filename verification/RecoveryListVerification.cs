using System.Windows;
using System.Windows.Controls;
using DeepUninstaller;
using DeepUninstaller.Services;

internal static class RecoveryListVerification
{
    public static void Run(MainWindow window)
    {
        var discarded = Record(CleanupState.Discarded, RecoveryCopyStatus.Missing, removable: true);
        var oldFailure = Record(CleanupState.Failed, RecoveryCopyStatus.Missing, removable: true);
        var needsRescan = Record(CleanupState.OriginalInPlace, RecoveryCopyStatus.Missing, removable: true, originalPresent: true);
        var hasCopy = Record(CleanupState.Quarantined, RecoveryCopyStatus.Available, removable: false);
        var grid = (DataGrid)window.FindName("HistoryGrid");
        var toggle = (CheckBox)window.FindName("ShowCompletedHistory");
        var remove = (Button)window.FindName("RemoveHistoryButton");
        var restore = (Button)window.FindName("RestoreButton");
        var empty = (TextBlock)window.FindName("HistoryEmpty");

        window.SetRecoveryHistory([discarded, oldFailure], []);
        Check(grid.Items.Count == 0 && empty.Visibility == Visibility.Visible,
            "Finished and no-copy historical failures should be hidden by default.");
        toggle.IsChecked = true;
        Check(grid.Items.Count == 2 && remove is { Visibility: Visibility.Visible, IsEnabled: true } &&
              !restore.IsEnabled, "A finished record can be deleted without offering restore.");

        window.SetRecoveryHistory([discarded, oldFailure, needsRescan, hasCopy], [], hasCopy.Id);
        Check(restore.IsEnabled && !remove.IsEnabled, "A record with a copy must remain available for recovery.");
        toggle.IsChecked = false;
        Check(grid.Items.Count == 2 && restore.IsEnabled,
            "Filtering must keep the current selection and show records that can be recovered or rescanned.");
        grid.SelectedIndex = 0;
        Check(((Button)window.FindName("RecoveryRescanButton")).Visibility == Visibility.Visible,
            "An unresolved original location should remain actionable.");
    }

    private static CleanupSession Record(CleanupState state, RecoveryCopyStatus copy, bool removable, bool originalPresent = false) =>
        new()
        {
            AppName = "UI fixture",
            CanRemoveRecord = removable,
            Entries = [new CleanupEntry
            {
                Item = new LeftoverItem(LeftoverKind.File, "sample.file", "UI fixture", LeftoverConfidence.Possible),
                CopyStatus = copy, State = state, OriginalPathPresent = originalPresent
            }]
        };

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
