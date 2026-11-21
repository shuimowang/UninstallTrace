using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using DeepUninstaller.Services;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class InstallSizeVerification
{
    public static void Run()
    {
        var parent = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var fixture = Path.GetFullPath(Path.Combine(parent, $"UninstallTrace-SizeVerification-{Guid.NewGuid():N}"));
        Check(Path.GetDirectoryName(fixture) == parent && !Directory.Exists(fixture), "The fixture must be a new direct child.");
        var root = Path.Combine(fixture, "Product App");
        var executable = Path.Combine(root, "Product.exe");
        var uninstaller = Path.Combine(root, "uninstall.exe");
        var external = Path.Combine(fixture, "external");
        var link = Path.Combine(root, "linked directory");
        var app = new InstalledApp
        {
            Name = "Product App",
            InstallLocation = root,
            DisplayIcon = $"\"{executable}\",0",
            UninstallString = $"\"{uninstaller}\" /uninstall",
            Hive = RegistryHive.CurrentUser,
            RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{Guid.NewGuid():N}"
        };

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(external);
            File.WriteAllBytes(executable, new byte[8193]);
            File.WriteAllBytes(uninstaller, new byte[6147]);
            File.WriteAllBytes(Path.Combine(external, "outside.bin"), new byte[1_048_576]);

            var estimator = new InstallSizeEstimator([app], []);
            var baseline = estimator.Measure(app);
            Check(baseline is { Status: InstallSizeStatus.Measured, AllocatedBytes: > 0, SkippedEntries: 0 } &&
                baseline.InstallDirectory == root, "The registered directory should report measured allocated bytes.");

            if (!CreateHardLink(Path.Combine(root, "same-data.exe"), executable, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            Check(estimator.Measure(app) == baseline, "A second hard link to the same file must not increase allocation.");
            VerifySparseAllocation(estimator, app, root, baseline);

            FileSystemInfo? linkInfo = null;
            try { linkInfo = Directory.CreateSymbolicLink(link, external); }
            catch (Exception exception) when (exception is UnauthorizedAccessException ||
                exception is IOException && (exception.HResult & 0xffff) == 1314)
            {
                // Some Windows test accounts cannot create symlinks. The path guard cases below still run.
            }
            if (linkInfo is not null)
            {
                var partial = estimator.Measure(app);
                Check(partial.Status == InstallSizeStatus.Partial && partial.AllocatedBytes == baseline.AllocatedBytes && partial.SkippedEntries == 1,
                    "A reparse subtree is skipped and makes the measured result partial.");
                File.WriteAllBytes(Path.Combine(external, "outside.exe"), [1]);
                File.WriteAllBytes(Path.Combine(external, "uninstall.exe"), [1]);
                var reparseApp = app with
                {
                    Name = Path.GetFileName(link), InstallLocation = "", DisplayIcon = Path.Combine(link, "outside.exe"),
                    UninstallString = $"\"{Path.Combine(link, "uninstall.exe")}\" /uninstall"
                };
                Check(estimator.Measure(reparseApp).Reason == InstallSizeReason.ProtectedLocation,
                    "A candidate inferred through a reparse directory is excluded before measuring.");
                Directory.Delete(link);
            }

            var peer = app with { Name = "Other app", RegistryPath = app.RegistryPath + "-Other", DisplayIcon = "", UninstallString = "" };
            foreach (var sharing in new[]
            {
                peer with { InstallLocation = root },
                peer with { InstallLocation = Path.Combine(root, "Subfolder") },
                peer with { InstallLocation = fixture },
                peer with { InstallLocation = "", DisplayIcon = Path.Combine(root, "Other.ico") },
                peer with { InstallLocation = "", QuietUninstallString = $"{Path.Combine(root, "Other.exe")} /quiet" }
            })
            {
                Check(new InstallSizeEstimator([app, sharing], []).Measure(app).Reason == InstallSizeReason.SharedInstallLocation,
                    "Another registration's overlapping folder, icon or uninstaller must block attribution.");
            }

            var distinctEdition = peer with { Name = app.Name, InstallLocation = Path.Combine(fixture, "Other edition") };
            var hostedApp = app with { UninstallString = $"\"{uninstaller}\" --uninstall-app com.example.product" };
            Check(new InstallSizeEstimator([hostedApp], []).Measure(hostedApp).Reason == InstallSizeReason.SharedInstallLocation,
                "A package uninstall command cannot attribute its host directory even with an explicit matching install folder.");
            Check(new InstallSizeEstimator([app, distinctEdition], []).Measure(app) == baseline,
                "A distinct registration of the same name with a separate install folder does not share ownership.");

            var alternateName = app with { Name = "Different display name" };
            Check(new InstallSizeEstimator([alternateName], []).Measure(alternateName).Status == InstallSizeStatus.Measured,
                "Two different registered executables in the exact install folder corroborate a distinct directory name.");
            Check(new InstallSizeEstimator([app], [fixture]).Measure(app).Reason == InstallSizeReason.ProtectedLocation,
                "A redirected Downloads folder excludes descendants even when metadata otherwise agrees.");
            Check(new InstallSizeEstimator([app], [Path.Combine(root, "Downloads")]).Measure(app).Reason == InstallSizeReason.ProtectedLocation,
                "A candidate containing the redirected Downloads folder is also excluded.");
            VerifyInferredDirectory(app, peer, root, fixture, baseline);

            var scriptHost = Path.Combine(root, "cmd.exe");
            File.WriteAllBytes(scriptHost, new byte[2048]);
            Check(estimator.Measure(app with { DisplayIcon = scriptHost }).Reason == InstallSizeReason.UnverifiedOwnership,
                "A shell or script host cannot corroborate product ownership.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { estimator.Measure(app, cancellation.Token); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("A canceled measurement must stop before scanning.");
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void VerifyInferredDirectory(InstalledApp app, InstalledApp peer, string root, string fixture,
        InstallSizeResult baseline)
    {
        var inferred = app with { InstallLocation = "" };
        var estimator = new InstallSizeEstimator([inferred], []);
        Check(estimator.Measure(inferred) == baseline,
            "Two distinct existing sibling executables in a folder matching the full registered name can establish a missing location.");

        var iconExecutable = Path.Combine(root, "Product.exe");
        var uninstaller = Path.Combine(root, "uninstall.exe");
        foreach (var invalid in new[]
        {
            inferred with { Name = $"{app.Name} (x64)" },
            inferred with { DisplayIcon = Path.Combine(root, "app.ico") },
            inferred with { DisplayIcon = Path.Combine(root, "Missing.exe") },
            inferred with { UninstallString = "" },
            inferred with { UninstallString = $"\"{iconExecutable}\" /uninstall" },
            inferred with { UninstallString = $"\"{Path.Combine(root, "same-data.exe")}\" /uninstall" },
            inferred with { UninstallString = $"\"{Path.Combine(root, "Nonexistent.exe")}\" /uninstall" },
            inferred with { UninstallString = $"\"{uninstaller}\" --uninstall-app com.example.product" },
            inferred with { UninstallString = $"\"{uninstaller}\" uninstallapp --name Instance --packagename com.example.product" }
        })
        {
            Check(estimator.Measure(invalid) is { Status: InstallSizeStatus.Unknown, AllocatedBytes: null },
                "Incomplete, mismatched and package-host evidence cannot infer a private install folder.");
        }

        var fromSharedHost = Path.Combine(fixture, "GameAssist", app.Name);
        Directory.CreateDirectory(fromSharedHost);
        File.WriteAllBytes(Path.Combine(fromSharedHost, "Product.exe"), [1]);
        File.WriteAllBytes(Path.Combine(fromSharedHost, "uninstall.exe"), [1]);
        var host = inferred with
        {
            DisplayIcon = Path.Combine(fromSharedHost, "Product.exe"),
            UninstallString = $"\"{Path.Combine(fromSharedHost, "uninstall.exe")}\" /uninstall"
        };
        Check(estimator.Measure(host).Reason == InstallSizeReason.ProtectedLocation,
            "An inferred folder under a known shared emulator host is excluded.");
        Check(new InstallSizeEstimator([inferred], [fixture]).Measure(inferred).Reason == InstallSizeReason.ProtectedLocation,
            "Inferred folders still obey the redirected Downloads restrictions.");

        var sharing = peer with { InstallLocation = "", DisplayIcon = Path.Combine(root, "Neighbor.ico") };
        Check(new InstallSizeEstimator([inferred, sharing], []).Measure(inferred).Reason == InstallSizeReason.SharedInstallLocation,
            "Another registration's directory evidence also vetoes an inferred folder.");
    }

    private static void VerifySparseAllocation(InstallSizeEstimator estimator, InstalledApp app, string root, InstallSizeResult baseline)
    {
        const uint setSparse = 0x000900C4;
        const int apparentSize = 8 * 1024 * 1024;
        var path = Path.Combine(root, "sparse-fixture.bin");
        using (var file = File.Create(path))
        {
            if (!DeviceIoControl(file.SafeFileHandle, setSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            file.SetLength(apparentSize);
            file.Position = 6 * 1024 * 1024;
            file.WriteByte(1);
            file.Flush(flushToDisk: true);
        }

        var measured = estimator.Measure(app);
        var growth = measured.AllocatedBytes - baseline.AllocatedBytes;
        Check(measured.Status == InstallSizeStatus.Measured && growth is > 0 and < apparentSize,
            $"A sparse file must contribute allocated data without counting holes. Status={measured.Status}, growth={growth}, skipped={measured.SkippedEntries}.");
        File.Delete(path);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
