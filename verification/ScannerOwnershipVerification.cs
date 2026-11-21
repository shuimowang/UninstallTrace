using System.IO;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class ScannerOwnershipVerification
{
    public static void Run()
    {
        var productRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var token = $"UninstallTrace-OwnershipVerification-{Guid.NewGuid():N}";
        VerifySharedCommands(Path.Combine(productRoot, token), token);
        VerifyNameVariants(productRoot, token);
        VerifyCorroboratedInstallerName(productRoot, token);
    }

    private static void VerifySharedCommands(string root, string token)
    {
        var firstExecutable = Path.Combine(root, "Launcher.exe");
        var parent = Path.Combine(root, "Launcher.exe Folder");
        var ownerName = $"{token} Owner";
        var ownerPath = Path.Combine(parent, ownerName);
        var neighborExecutable = Path.Combine(ownerPath, "Neighbor Uninstaller.exe");
        var owner = Fixture(ownerName) with
        {
            InstallLocation = ownerPath,
            DisplayIcon = Path.Combine(ownerPath, "Owner.exe")
        };
        var neighbor = Fixture($"{token} Neighbor");
        Require(!Directory.Exists(root), "Ownership test directory must be new.");

        try
        {
            Directory.CreateDirectory(ownerPath);
            Require(FindDirectory(owner, ownerPath, [owner])?.IsRecommended == true,
                "The fixture has an attributable directory before another app claims an executable there.");

            var quoted = neighbor with { UninstallString = $"\"{neighborExecutable}\" /uninstall" };
            Require(FindDirectory(owner, ownerPath, [owner, quoted]) is null,
                "A quoted recorded uninstall executable protects its folder even when the file is missing.");

            var quiet = neighbor with { QuietUninstallString = $"{neighborExecutable} /quiet" };
            File.WriteAllBytes(firstExecutable, []);
            Require(FindDirectory(owner, ownerPath, [owner, quiet]) is null,
                "An unquoted quiet command protects all plausible executable prefixes, including its longer path.");

            var systemLauncher = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            var msi = neighbor with { UninstallString = $"\"{systemLauncher}\" /x \"{neighborExecutable}\"" };
            Require(FindDirectory(owner, ownerPath, [owner, msi])?.IsRecommended == true,
                "A common system launcher and its argument do not claim an independent app's install folder.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                File.Delete(firstExecutable);
                DeleteEmptyDirectory(ownerPath);
                DeleteEmptyDirectory(parent);
                Directory.Delete(root);
            }
        }
    }

    private static void VerifyNameVariants(string root, string token)
    {
        var variants = new[] { "", "++", "#" };
        var folders = variants.Select(variant => Path.Combine(root, $"{token}-Editor{variant}")).ToArray();
        Require(folders.All(path => !Directory.Exists(path)), "All name-variant test directories must be new.");
        try
        {
            foreach (var folder in folders)
                Directory.CreateDirectory(folder);

            foreach (var (variant, ownFolder) in new[] { ("++", folders[1]), ("＃", folders[2]) })
            {
                var app = Fixture($"{token} Editor{variant} (1.2)") with { Version = "1.2" };
                var scan = LeftoverScanner.Scan(app, [app]);
                Require(scan.Items.Any(item => IsDirectory(item, ownFolder) && item.Confidence == LeftoverConfidence.Possible),
                    "A punctuation-preserving version alias finds its own folder.");
                Require(folders.Where(folder => folder != ownFolder).All(folder => scan.Items.All(item => !IsDirectory(item, folder))),
                    "Name-only attribution excludes folders for other punctuation variants and the plain name.");
            }
        }
        finally
        {
            foreach (var folder in folders)
                DeleteEmptyDirectory(folder);
        }
    }

    private static void VerifyCorroboratedInstallerName(string localRoot, string token)
    {
        var installationParent = Path.Combine(localRoot, $"{token}-Installation");
        var installPath = Path.Combine(installationParent, token);
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), token);
        const string version = "2.16.60";
        var app = Fixture($"{token} x64 (Current user, 64-bit)") with
        {
            Version = version,
            InstallLocation = installPath,
            DisplayIcon = $"\"{Path.Combine(installPath, "Program.exe")}\",0"
        };
        Require(!Directory.Exists(installationParent) && !Directory.Exists(settingsPath),
            "Installer qualifier fixtures must use new directories.");

        try
        {
            // Model the post-uninstall scan. The original EXE and install folder no longer exist.
            Directory.CreateDirectory(settingsPath);
            Require(FindDirectory(app, settingsPath, [app]) is
                { Confidence: LeftoverConfidence.Possible, EvidenceKind: LeftoverEvidence.CorroboratedInstallerName },
                "Architecture and scope qualifiers can be removed when both recorded paths confirm the product name.");

            var versioned = app with { Name = $"{token} x64 v{version} (Current user, 64-bit)" };
            Require(FindDirectory(versioned, settingsPath, [versioned])?.EvidenceKind == LeftoverEvidence.CorroboratedInstallerName,
                "The alias can also omit the exact recorded version between installer qualifiers.");

            var noIcon = app with { DisplayIcon = "" };
            Require(FindDirectory(noIcon, settingsPath, [noIcon]) is null,
                "A matching install folder alone is not enough to broaden name-only attribution.");

            var unrelatedIcon = app with { DisplayIcon = Path.Combine(installationParent, "Unrelated.exe") };
            Require(FindDirectory(unrelatedIcon, settingsPath, [unrelatedIcon]) is null,
                "An icon executable outside the install folder does not corroborate the alias.");

            var edition = Fixture($"{token} x86 (All users, 32-bit)");
            Require(FindDirectory(app, settingsPath, [app, edition]) is null,
                "An installed edition with the same product name vetoes the alias without path metadata.");

            var plain = Fixture(token);
            Require(FindDirectory(plain, settingsPath, [plain])?.Confidence == LeftoverConfidence.Possible,
                "A unique undecorated product name matches its settings folder.");
            Require(FindDirectory(plain, settingsPath, [plain, edition]) is null,
                "A decorated installed edition also vetoes name-only attribution for an undecorated selection.");

            var reader = app with { Name = $"{token} Reader x64 (Current user, 64-bit)" };
            Require(FindDirectory(reader, settingsPath, [reader]) is null,
                "Product terms such as Reader are preserved when removing installer qualifiers.");
        }
        finally
        {
            DeleteEmptyDirectory(settingsPath);
        }
    }

    private static InstalledApp Fixture(string name) => new()
    {
        Name = name,
        Hive = RegistryHive.CurrentUser,
        View = RegistryView.Default,
        RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{name}"
    };

    internal static void RunStartupCases()
    {
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string version = "4.5.11";
        var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var token = $"UninstallTrace-OwnershipVerification-{Guid.NewGuid():N}";
        var verificationDirectory = Path.GetDirectoryName(typeof(ScannerOwnershipVerification).Assembly.Location)!;
        var installPath = Path.Combine(verificationDirectory, $"{token}-Install", token);
        var executable = Path.Combine(installPath, "Downloader.exe");
        var siblingExecutable = Path.Combine(installPath, "Unrelated.exe");
        var settingsPath = Path.Combine(localRoot, token);
        var runName = $"{token}-Run";
        var siblingRunName = $"{token}-SiblingRun";
        var app = Fixture($"{token} 版本 {version}") with
        {
            Version = version,
            InstallLocation = installPath,
            DisplayIcon = $"\"{executable}\",0"
        };

        Require(!Directory.Exists(settingsPath) && !File.Exists(executable) && !File.Exists(siblingExecutable),
            "The localized installer fixture needs fresh settings and non-existent startup executables.");
        Require(LeftoverScanner.TryValidateFileSystemTarget(installPath, out _),
            "The verification application needs to run from an unprotected local development directory.");
        var startupValues = new Dictionary<string, string?>
        {
            [runName] = $"\"{executable}\" --silence",
            [siblingRunName] = $"\"{siblingExecutable}\" --silence"
        };
        var emptyStartupValues = new Dictionary<string, string?>();

        try
        {
            Directory.CreateDirectory(settingsPath);
            var scan = ScanFixture(app, [app]);
            Require(scan.Items.Any(item => IsDirectory(item, settingsPath) && item is
                { Confidence: LeftoverConfidence.Possible, EvidenceKind: LeftoverEvidence.CorroboratedInstallerName }),
                "The exact localized version suffix can corroborate the app's name in an independent profile directory.");
            Require(scan.Items.Any(item => IsRunValue(item, runName) && item is
                { Confidence: LeftoverConfidence.Strong, EvidenceKind: LeftoverEvidence.StartupCommand }
                && string.Equals(item.EvidencePath, executable, StringComparison.OrdinalIgnoreCase)),
                "The exact corroborated display executable is attributable outside standard application roots.");
            Require(scan.Items.All(item => !IsRunValue(item, siblingRunName)),
                "An alias cannot claim other executables in the same custom directory.");

            var missingIcon = app with { DisplayIcon = "" };
            RequireExcluded(missingIcon, [missingIcon], "An install folder alone cannot support the localized alias.");
            var wrongVersion = app with { Version = "4.5.12" };
            RequireExcluded(wrongVersion, [wrongVersion], "The localized suffix must agree with the registered version.");
            var edition = Fixture($"{token} x64 (All users, 64-bit)");
            RequireExcluded(app, [app, edition], "A different registered edition vetoes both localized candidates.");
            var sharing = Fixture($"{token} Other product") with { UninstallString = $"\"{siblingExecutable}\" /uninstall" };
            RequireExcluded(app, [app, sharing], "Another app's registered executable makes the custom directory shared.");
        }
        finally
        {
            DeleteEmptyDirectory(settingsPath);
        }

        LeftoverScanResult ScanFixture(InstalledApp selected, InstalledApp[] inventory) =>
            LeftoverScanner.ScanWithStartupValues(selected, inventory, (hive, view, path) =>
                hive == RegistryHive.CurrentUser && view == RegistryView.Default
                && string.Equals(path, runPath, StringComparison.OrdinalIgnoreCase) ? startupValues : emptyStartupValues);

        bool IsRunValue(LeftoverItem item, string name) => item.Kind == LeftoverKind.RegistryValue
            && item.Hive == RegistryHive.CurrentUser && string.Equals(item.Target, runPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ValueName, name, StringComparison.OrdinalIgnoreCase);

        void RequireExcluded(InstalledApp selected, InstalledApp[] inventory, string message)
        {
            var scan = ScanFixture(selected, inventory);
            Require(scan.Items.All(item => !IsDirectory(item, settingsPath) && !IsRunValue(item, runName) && !IsRunValue(item, siblingRunName)), message);
        }
    }

    private static LeftoverItem? FindDirectory(InstalledApp app, string folder, InstalledApp[] inventory) =>
        LeftoverScanner.Scan(app, inventory).Items.SingleOrDefault(item => IsDirectory(item, folder));

    private static bool IsDirectory(LeftoverItem item, string path) =>
        item.Kind == LeftoverKind.Directory && string.Equals(item.Target, path, StringComparison.OrdinalIgnoreCase);

    private static void DeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
