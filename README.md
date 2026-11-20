# UninstallTrace

[简体中文](README.zh-CN.md) · [Download for Windows](https://github.com/shuimowang/UninstallTrace/releases/latest) · [Competitive analysis](docs/competitive-analysis.md)

UninstallTrace runs the original Windows uninstaller, checks what it left behind, and explains why each remaining item belongs to the app. You choose what to quarantine. Cleanup recovery lets you restore quarantined files and registry data or permanently delete their copies to free space.

## Use it

1. Open `UninstallTrace.exe` from the release. The Windows x64 build includes its .NET runtime.
2. Programs with a recorded install date appear newest first. Unknown dates appear last. Click the install-date column header to reverse date order. Press **Ctrl+F** to search by name, publisher, or version and **F5** to refresh.
3. Choose **Run uninstaller** to start the original remover or **Scan for leftovers** to inspect a damaged or stale registration. Use **Scan removed app** for a program missing from the list.
4. Check the location and evidence for the leftovers you want to quarantine. Open **Cleanup recovery** to restore the files and registry entries from a cleanup before discarding its backups. To use the removed program again, reinstall it with its installer or through the Store.

The language setting supports English, Simplified Chinese, and automatic selection from Windows. **Batch uninstall** runs vendor removers sequentially, reviews leftovers per program, and pauses when a result needs attention. **Uninstall history** lets you resume a scan later or after a restart.

Desktop install dates come from uninstall registrations. Some installers omit the date. Applying an MSI patch or performing a repair can update it. Store apps use the package installation or last update date reported by Windows. [Microsoft's MSI date documentation](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)

## What it checks

- Desktop uninstall registrations for the current user and machine, including 32-bit and 64-bit entries and verified MSI product codes.
- Dedicated install folders, application data locations, software registry keys, App Paths, Run and RunOnce, and directly attributable shortcuts.
- Shared locations, Windows and user folders, filesystem links, and ownership changes discovered when rechecking selected items.
- Current-user Store packages through Windows package management. System and protected packages are view-only. Windows manages package removal and its data.

Name matches alone require a manual selection. The recovery vault covers the files and registry data quarantined by UninstallTrace. Changes made by vendor uninstallers and Windows package removal are outside that vault. Locked or inaccessible items are recorded for review. Hidden artifacts owned by services, scheduled tasks, drivers, or other users, and changes that require installation-time tracing, are outside the current cleanup scope.

## Build and verify

Use the .NET 10 SDK on Windows. The integration harness uses temporary files and current-user registry fixtures to test scanning, ownership checks, quarantine, and restoration.

The [real Windows uninstall checks](.github/workflows/real-apps.yml) use separate disposable Windows machines for SumatraPDF and WinMerge. They compare the scanner with v0.1.3 on the same post-uninstall state and verify quarantine and restoration against known files, registry data, and shared-folder controls. [Method and results](docs/uninstall-verification.md)

```powershell
dotnet build -c Release
dotnet run --project verification/DeepUninstaller.Verification.csproj -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release/win-x64
```

The portable executable is unsigned. Recovery and uninstall history are local to the Windows user profile. Published under the [MIT License](LICENSE).
