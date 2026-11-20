# UninstallTrace

[简体中文](README.zh-CN.md) · [Download for Windows](https://github.com/shuimowang/UninstallTrace/releases/latest) · [Competitive analysis](docs/competitive-analysis.md)

UninstallTrace runs the original Windows uninstaller, checks what it left behind, and explains why each remaining item belongs to the app. You choose what to quarantine. Recovery history lets you restore that cleanup or free the space by permanently deleting its quarantine.

## Use it

1. Open `UninstallTrace.exe` from the release. The Windows x64 build includes its .NET runtime.
2. Search for the program by name, publisher, or version. Press **Ctrl+F** to focus search or **F5** to refresh the list.
3. Choose **Run uninstaller** to start the original remover or **Scan for leftovers** to inspect a damaged or stale registration. Use **Scan removed app** for a program missing from the list.
4. Check the location and evidence for the leftovers you want to quarantine. Open **Recovery history** to restore the selected cleanup at any time before discarding its backups.

The language setting supports English, Simplified Chinese, and automatic selection from Windows. **Batch uninstall** runs vendor removers sequentially, reviews leftovers per program, and pauses when a result needs attention. **Uninstall history** lets you resume a scan later or after a restart.

## What it checks

- Desktop uninstall registrations for the current user and machine, including 32-bit and 64-bit entries and verified MSI product codes.
- Dedicated install folders, application data locations, software registry keys, App Paths, Run and RunOnce, and directly attributable shortcuts.
- Shared locations, Windows and user folders, filesystem links, and ownership changes discovered when rechecking selected items.
- Current-user Store packages through Windows package management. System and protected packages are view-only. Windows manages package removal and its data.

Name matches alone require a manual selection. The recovery vault covers the files and registry data quarantined by UninstallTrace. Changes made by vendor uninstallers and Windows package removal are outside that vault. Locked or inaccessible items are recorded for review. Hidden artifacts owned by services, scheduled tasks, drivers, or other users, and changes that require installation-time tracing, are outside the current cleanup scope.

## Build and verify

Use the .NET 10 SDK on Windows. The integration harness uses temporary files and current-user registry fixtures to test scanning, ownership checks, quarantine, and restoration.

```powershell
dotnet build -c Release
dotnet run --project verification/DeepUninstaller.Verification.csproj -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release/win-x64
```

The portable executable is unsigned. Recovery and uninstall history are local to the Windows user profile. Published under the [MIT License](LICENSE).
