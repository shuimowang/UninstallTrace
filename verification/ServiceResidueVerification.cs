using System.IO;
using DeepUninstaller.Services;
using Microsoft.Win32;
using ServiceRegistration = DeepUninstaller.Services.ServiceResidueScanner.ServiceRegistration;

internal static class ServiceResidueVerification
{
    public static void Run()
    {
        var name = $"UninstallTrace Service Fixture {Guid.NewGuid():N}";
        var productRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", name);
        var executable = Path.Combine(productRoot, "Product Worker.exe");
        var app = new InstalledApp
        {
            Name = name,
            Publisher = "UninstallTrace Test Publisher",
            Hive = RegistryHive.CurrentUser,
            View = RegistryView.Default,
            RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{name}",
            InstallLocation = productRoot,
            DisplayIcon = $"\"{Path.Combine(productRoot, "Product.exe")}\",0"
        };

        Check(!Directory.Exists(productRoot), "The test product directory must be absent to model a vendor uninstall.");
        var quoted = $"\"{executable}\" --service";
        ServiceRegistration[] registrations =
        [
            new("OwnedWorker", "Owned worker", 0x10, quoted),
            new("OwnedInteractive", "@unresolved.dll,-2", 0x110, quoted),
            new("UnsafeUnquoted", null, 0x10, $"{executable} --service"),
            new("UnseparatedQuote", null, 0x10, $"\"{executable}\"suffix"),
            new("ControlCharacter", null, 0x10, $"{quoted}\n"),
            new("RelativePath", null, 0x10, @"\relative\service.exe"),
            new("DotSegment", null, 0x10, $"\"{productRoot}\\..\\{name}\\Product Worker.exe\""),
            new("PrefixOnly", null, 0x10, $"\"{productRoot}-Other\\service.exe\""),
            new("SharedProcess", null, 0x20, quoted),
            new("Driver", null, 0x01, quoted),
            new("Interpreter", null, 0x10, $"\"{Path.Combine(productRoot, "powershell.exe")}\" -File service.ps1"),
            new("SystemWrapper", null, 0x10, $"\"{Path.Combine(Environment.SystemDirectory, "svchost.exe")}\" \"{executable}\""),
            new("UserVariable", null, 0x10, $"\"%LOCALAPPDATA%\\Programs\\{name}\\Product Worker.exe\"", ExpandImagePath: true)
        ];

        var report = ServiceResidueScanner.ScanRegistrations(app, [], registrations);
        Check(report.HasOwnershipEvidence && report.Warnings.Count == 0 &&
            report.Matches.Select(match => match.ServiceName).SequenceEqual(["OwnedInteractive", "OwnedWorker"]) &&
            report.Matches.All(match => match.ExecutablePath == executable) && report.Matches[0].DisplayName is null &&
            report.Matches[1].DisplayName == "Owned worker",
            "Only direct product-owned service images should match, including after the executable is removed.");

        var noMatches = ServiceResidueScanner.ScanRegistrations(app, [], []);
        Check(noMatches.HasOwnershipEvidence && noMatches.Matches.Count == 0,
            "No matching services must be distinguishable from unconfirmed app ownership.");

        var neighbor = new InstalledApp { Name = "UninstallTrace Service Fixture Neighbor", RegistryPath = app.RegistryPath + "-Neighbor" };
        foreach (var other in new[]
        {
            neighbor with { InstallLocation = Path.Combine(productRoot, "Nested") },
            neighbor with { InstallLocation = Path.GetDirectoryName(productRoot)! },
            neighbor with { DisplayIcon = $"\"{executable}\",0" },
            neighbor with { UninstallString = $"{executable} --uninstall" },
            neighbor with { QuietUninstallString = $"\"{executable}\" /quiet" }
        })
        {
            Check(!ServiceResidueScanner.ScanRegistrations(app, [other], registrations).HasOwnershipEvidence,
                "An overlapping install folder, icon, or uninstall executable must veto service attribution.");
        }

        Check(!ServiceResidueScanner.ScanRegistrations(app with { DisplayIcon = "" }, [], registrations).HasOwnershipEvidence,
            "The install location alone should not imply service ownership.");
        Check(!ServiceResidueScanner.ScanRegistrations(app with { IsSystemComponent = true }, [], registrations).HasOwnershipEvidence,
            "Protected apps must not be considered for service attribution.");

        var stale = ServiceResidueScanner.ScanRegistrations(app, [app with { Version = "New registration" }], registrations);
        Check(!stale.HasOwnershipEvidence && stale.Matches.Count == 0 && stale.Warnings.Count == 1,
            "A changed live registration with the saved app ID must prevent historical service attribution.");
        Check(ServiceResidueScanner.ScanRegistrations(app, [app], registrations).HasOwnershipEvidence,
            "An unchanged live registration should preserve ownership evidence.");
        var metadataUnavailable = ServiceResidueScanner.ScanRegistrations(app, [], registrations, _ => true);
        Check(!metadataUnavailable.HasOwnershipEvidence && metadataUnavailable.Warnings.Count == 1,
            "An exact registry key missing from the inventory must be reported as unverifiable.");
        var denied = ServiceResidueScanner.ScanRegistrations(app, [], registrations,
            _ => throw new UnauthorizedAccessException("Fixture access denied"));
        Check(!denied.HasOwnershipEvidence && denied.Warnings.Single().Contains("Fixture access denied", StringComparison.Ordinal),
            "A registry access failure must leave a diagnostic and skip service attribution.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
