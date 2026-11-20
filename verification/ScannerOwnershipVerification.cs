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

    private static InstalledApp Fixture(string name) => new()
    {
        Name = name,
        Hive = RegistryHive.CurrentUser,
        View = RegistryView.Default,
        RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{name}"
    };

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
