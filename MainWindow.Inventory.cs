using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DeepUninstaller.Services;
using static DeepUninstaller.Localization;

namespace DeepUninstaller;

public partial class MainWindow
{
    private IReadOnlyDictionary<string, RegistrationLabel> registrationLabels = new Dictionary<string, RegistrationLabel>();
    private Dictionary<string, AppRow> inventoryRows = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? sizeRead;

    private async Task ReloadInventoryAsync()
    {
        var inventory = await Task.Run(InstalledApps.Read);
        var roles = await Task.Run(() => AppRoleClassifier.Classify(inventory));
        SetInventory(inventory, roles);
        _ = MeasureMissingSizesAsync(inventory);
    }

    internal void SetInventory(IReadOnlyList<InstalledApp> inventory, IReadOnlyDictionary<string, AppRoleAssessment>? roles = null)
    {
        sizeRead?.Cancel();
        apps = inventory;
        registrationLabels = RegistrationLabels.Create(inventory, roles);
        inventoryRows = inventory.ToDictionary(app => app.Id, app => new AppRow(app, registrationLabels[app.Id]),
            StringComparer.OrdinalIgnoreCase);
        UpdateSizeColumnMinimum();
        ApplyFilter();
    }

    protected override void OnClosed(EventArgs e)
    {
        sizeRead?.Cancel();
        updateCheck?.Cancel();
        base.OnClosed(e);
    }

    private async Task MeasureMissingSizesAsync(IReadOnlyList<InstalledApp> inventory)
    {
        using var request = new CancellationTokenSource();
        sizeRead = request;
        var token = request.Token;
        try
        {
            var selectedId = (AppGrid.SelectedItem as AppRow)?.App.Id;
            var pending = inventory.Where(app => !app.IsProtected && app.EstimatedSizeBytes is not > 0)
                .OrderByDescending(app => app.Id == selectedId).ThenByDescending(app => registrationLabels[app.Id].IsDuplicate)
                .ToArray();
            if (pending.Length == 0)
                return;
            ShowProgress(0);
            // One worker walks one directory at a time. Publish batches so input and sorting remain responsive.
            await Task.Run(async () =>
            {
                var estimator = new InstallSizeEstimator(inventory);
                var batch = new Dictionary<string, InstallSizeResult>(StringComparer.OrdinalIgnoreCase);
                var elapsed = Stopwatch.StartNew();
                for (var index = 0; index < pending.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var app = pending[index];
                    try { batch[app.Id] = estimator.Measure(app, token); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        batch[app.Id] = new(InstallSizeStatus.Unknown, InstallSizeReason.UnreadableLocation, null, null, 0);
                    }
                    var count = index + 1;
                    if (count != pending.Length && elapsed.ElapsedMilliseconds < 160)
                        continue;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && ApplyMeasuredSizes(inventory, batch))
                            ShowProgress(count);
                    }, System.Windows.Threading.DispatcherPriority.Background, token);
                    batch.Clear();
                    elapsed.Restart();
                }
            }, token);

            void ShowProgress(int count) => SizeProgressText.Text =
                Text($"正在核算大小 {count}/{pending.Length}", $"Measuring sizes {count}/{pending.Length}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            if (!token.IsCancellationRequested && ReferenceEquals(apps, inventory))
            {
                var remaining = inventoryRows.Values.Where(row => !row.App.IsProtected && row.ByteCount is null && row.Measurement is null);
                ApplyMeasuredSizes(inventory, remaining.ToDictionary(row => row.App.Id, _ =>
                    new InstallSizeResult(InstallSizeStatus.Unknown, InstallSizeReason.UnreadableLocation, null, null, 0)));
            }
        }
        finally
        {
            if (ReferenceEquals(sizeRead, request))
            {
                sizeRead = null;
                SizeProgressText.Text = "";
            }
        }
    }

    internal bool ApplyMeasuredSizes(IReadOnlyList<InstalledApp> inventory, IReadOnlyDictionary<string, InstallSizeResult> results)
    {
        if (!ReferenceEquals(apps, inventory))
            return false;

        var changedSize = false;
        var changedSelection = false;
        foreach (var (id, result) in results)
        {
            if (!inventoryRows.TryGetValue(id, out var row) || row.App.EstimatedSizeBytes is > 0 || row.Measurement == result)
                continue;
            var previous = row.ByteCount;
            var previousRank = row.SizeRank;
            row.SetMeasurement(result);
            changedSize |= previous != row.ByteCount || previousRank != row.SizeRank;
            changedSelection |= ReferenceEquals(row, AppGrid.SelectedItem);
        }
        if (changedSize)
            UpdateSizeColumnMinimum();
        if (changedSize && appSortColumn == AppSortColumn.Size)
            ApplyFilter();
        else if (changedSelection)
            UpdateSelection();
        return true;
    }

    private sealed class AppRow(InstalledApp app, RegistrationLabel label) : INotifyPropertyChanged
    {
        public InstalledApp App => app;
        public RegistrationLabel Label => label;
        public string Name => label.Name;
        public string Publisher => app.Publisher.Length == 0 ? "—" : app.Publisher;
        public string Version => app.Version.Length == 0 ? "—" : app.Version;
        public string InstallDate => app.InstalledOn?.ToString("d", Culture) ?? Text("未登记", "Unknown");
        public InstallSizeResult? Measurement { get; private set; }
        private bool Reported => app.EstimatedSizeBytes is > 0;
        private bool Partial => Measurement?.Status == InstallSizeStatus.Partial;
        public long? ByteCount => Reported ? app.EstimatedSizeBytes : Measurement?.AllocatedBytes;
        public int SizeRank => ByteCount is null ? 2 : Partial ? 1 : 0;
        public string Size => (Partial ? "≥ " : "") + (ByteCount == 0 ? "0 B" : FormatSize(ByteCount));

        public string SizeLabel => Reported ? Text("安装程序登记的估算大小", "Installer-reported estimate") :
            Partial ? Text("安装目录占用（部分）", "Size on disk (partial)") :
            Measurement?.Status == InstallSizeStatus.Measured ? Text("安装目录占用", "Installation size on disk") :
            Text("大小", "Size");

        public string SizeHint => ByteCount.HasValue && !Partial ? SizeLabel : SizeExplanation;
        private string SizeExplanation => (app.IsProtected ? InstallSizeReason.ProtectedLocation : Measurement?.Reason) switch
        {
            InstallSizeReason.MissingInstallLocation or InstallSizeReason.SharedInstallLocation when Label.Role == AppRegistrationRole.AndroidManagedApp =>
                Text("该应用由模拟器管理，未找到可单独计量的安装目录。", "This emulator-managed app has no identifiable separate installation folder to measure."),
            InstallSizeReason.MissingInstallLocation => Text("未找到可单独计量的安装目录。", "No separate installation folder could be identified."),
            InstallSizeReason.UnverifiedOwnership => Text("无法确认此程序独占安装目录。", "Could not verify that this program owns the installation folder."),
            InstallSizeReason.SharedInstallLocation => Text("多条登记使用此位置，无法单独计量。", "Multiple registrations use this location. Its size cannot be attributed to this entry."),
            InstallSizeReason.ProtectedLocation => Text("系统、用户或受保护的位置未计量。", "System, user-data and protected locations are excluded."),
            InstallSizeReason.IncompleteRead => Text($"已跳过 {Measurement!.SkippedEntries} 个条目，仅显示可核实的下限。",
                $"Skipped {Measurement!.SkippedEntries} entries. The value is a verified lower bound."),
            InstallSizeReason.UnreadableLocation => Text("无法读取安装目录。", "The installation folder could not be read."),
            _ => Text("未登记，等待核算。", "No installer estimate. Awaiting measurement."),
        };

        public string SizeDetail
        {
            get
            {
                if (ByteCount is not long bytes)
                    return SizeExplanation;
                var exact = bytes.ToString("N0", Culture);
                var detail = Size + Environment.NewLine + (Partial ? "≥ " : "") + Text($"{exact} 字节", $"{exact} bytes");
                if (!Reported && Measurement?.InstallDirectory is string measuredDirectory &&
                    !string.Equals(app.InstallLocation.Trim().Trim('"').TrimEnd('\\'), measuredDirectory, StringComparison.OrdinalIgnoreCase))
                    detail += Environment.NewLine + measuredDirectory;
                return Partial ? detail + Environment.NewLine + SizeExplanation : detail;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetMeasurement(InstallSizeResult result)
        {
            Measurement = result;
            PropertyChanged?.Invoke(this, new(nameof(Size)));
            PropertyChanged?.Invoke(this, new(nameof(SizeHint)));
        }
    }
}
