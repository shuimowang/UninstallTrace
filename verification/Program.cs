using System.ComponentModel;
using System.IO;
using DeepUninstaller.Services;
using Microsoft.Win32;

const string FixturePrefix = "DeepUninstallerVerification-";
const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
const string ProofName = "VerificationToken";
const string ProofText = "fixture command argument with spaces";

if (args is ["--fixture-uninstall", var childId, ProofText])
    return HostedStartupVerification.Allow("The integration fixture") ? RunFixtureUninstaller(childId) : 2;
if (args is ["--verify-ui"])
{
    ThemeVerification.Run();
    Checkpoint("Window themes, size-column layout, numeric ordering, and selectable details");
    return 0;
}
if (args is ["--verify-startup"])
    return HostedStartupVerification.Run();
if (args is ["--probe-path-attributes", var manifest])
    return PathAttributeProbe.Run(manifest);
if (args.Length != 0 && args[0].StartsWith("--real-", StringComparison.Ordinal))
    return HostedStartupVerification.Allow("Real-application verification") ? RealAppVerification.Run(args) : 2;
if (args.Length != 0 && args[0] == "--benchmark-scan")
    return HostedStartupVerification.Allow("The complete scanner benchmark") ? ScannerBenchmark.Run(args) : 2;
var integration = args is ["--verify-integration"];
if (args.Length != 0 && !integration)
    return 1;
if (integration && !HostedStartupVerification.Allow("Integration verification"))
    return 2;

ThemeVerification.Run();
Checkpoint("Window themes, size-column layout, numeric ordering, and selectable details");

(string Name, DateTime? Date)[] datedRows =
[
    ("missing A", null), ("newer A", new(2026, 8, 2)), ("older", new(2024, 10, 1)),
    ("newer B", new(2026, 8, 2)), ("missing B", null)
];
var newest = SortOrder.KnownFirst(datedRows, row => row.Date, ListSortDirection.Descending);
var oldest = SortOrder.KnownFirst(datedRows, row => row.Date, ListSortDirection.Ascending);
Require(newest.Select(row => row.Name).SequenceEqual(["newer A", "newer B", "older", "missing A", "missing B"]) &&
        oldest.Select(row => row.Name).SequenceEqual(["older", "newer A", "newer B", "missing A", "missing B"]),
    "Date sorting should keep unknown dates last and preserve the order of equal dates.");
Checkpoint("Newest and oldest sorting with unknown install dates");

if (!integration)
    return 0;

ScannerOwnershipVerification.Run();
Checkpoint("Shared uninstallers and punctuation-sensitive app names");
LockedQuarantineVerification.Run();
Checkpoint("Locked quarantine targets and recovery-copy availability");
ServiceResidueVerification.Run();
Checkpoint("Read-only service attribution and shared host exclusions");
RegistryRestoreVerification.Run();
Checkpoint("Atomic registry restore and conflict handling");
HistoryRemovalVerification.Run();
Checkpoint("Remove only disposable recovery history records");

var id = Guid.NewGuid().ToString("N");
var name = FixturePrefix + id;
var uninstallKey = @$"{UninstallRoot}\{name}";
var neighborKey = uninstallKey + "-Neighbor";
var softwareKey = @$"Software\{name}";
var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var installPath = OwnedChild(localData, name);
var tempPath = OwnedChild(Path.GetTempPath(), name);
var appHost = Environment.ProcessPath ?? throw new InvalidOperationException("No app host path.");
var leftoverFile = Path.Combine(installPath, "retained.txt");

Require(!string.Equals(Path.GetFileName(appHost), "dotnet.exe", StringComparison.OrdinalIgnoreCase),
    "Run the verification app host, for example with dotnet run --project verification.");
Require(!EqualPath(installPath, tempPath) && !Directory.Exists(installPath) && !Directory.Exists(tempPath),
    "Fixture directories must be distinct and new.");
Require(!KeyExists(uninstallKey) && !KeyExists(neighborKey) && !KeyExists(softwareKey), "Fixture registry keys must be new.");

try
{
    Directory.CreateDirectory(installPath);
    Directory.CreateDirectory(tempPath);
    File.WriteAllText(leftoverFile, "fixture residue");

    using (var key = Registry.CurrentUser.CreateSubKey(uninstallKey))
    {
        key.SetValue("DisplayName", name);
        key.SetValue("DisplayVersion", "1.2.3");
        key.SetValue("InstallDate", "20240229");
        key.SetValue("Publisher", "DeepUninstaller Verification");
        key.SetValue("InstallLocation", installPath);
        key.SetValue("UninstallString", $"\"{appHost}\" --fixture-uninstall {id} \"{ProofText}\"");
        key.SetValue(ProofName, id);
    }

    using (var key = Registry.CurrentUser.CreateSubKey(softwareKey))
    {
        key.SetValue("Expandable", @"%LOCALAPPDATA%\Verification", RegistryValueKind.ExpandString);
        key.SetValue("Enabled", 1, RegistryValueKind.DWord);
        key.SetValue("Labels", new[] { "first", "second" }, RegistryValueKind.MultiString);
        using var child = key.CreateSubKey("Settings");
        child.SetValue("Payload", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
    }

    var inventory = InstalledApps.Read();
    var app = inventory.Single(entry => entry.Name == name);
    Require(app.Hive == RegistryHive.CurrentUser, "Inventory preserved the per-user scope.");
    Require(app.Version == "1.2.3" && app.InstallLocation == installPath, "Inventory preserved application metadata.");
    Require(app.InstalledOn == new DateTime(2024, 2, 29), "Inventory preserved the recorded install date.");
    Require(InstalledApps.IsRegistered(app), "The fixture is registered before uninstall.");
    var initialScan = LeftoverScanner.Scan(app, inventory);
    Require(initialScan.Items.Any(item => item.Kind == LeftoverKind.RegistryKey && item.Hive == RegistryHive.CurrentUser
        && EqualPath(item.Target, uninstallKey)), "The exact per-user uninstall registration was found.");

    VerifyMsiCommand(app);
    var executableWithSpaces = Path.Combine(tempPath, "verification command.exe");
    File.Copy(appHost, executableWithSpaces);
    var unquoted = app with { UninstallString = $"{executableWithSpaces} \"{ProofText}\"" };
    Require(UninstallCommand.TryCreate(unquoted, out var parsed, out _) && parsed.FileName == executableWithSpaces
        && parsed.Arguments == $"\"{ProofText}\"", "An unquoted executable path with spaces was resolved without modifying its arguments.");
    Require(UninstallCommand.TryCreate(app, out var command, out var error), $"Vendor command could not be read: {error}");
    Require(command is not null && string.Equals(command.FileName, appHost, StringComparison.OrdinalIgnoreCase),
        "The command must target the verification app host.");
    using (var process = command!.Start())
    {
        Require(process.WaitForExit(20_000), "Verification command timed out.");
        Require(process.ExitCode == 0, $"Verification command exited with code {process.ExitCode}.");
    }
    Require(File.Exists(Path.Combine(tempPath, "vendor-ran.txt")), "The command preserved its arguments.");
    Require(!InstalledApps.IsRegistered(app), "The verification uninstaller removed its own registration.");
    Checkpoint("Inventory and vendor uninstall");

    var neighbors = inventory.Where(entry => entry.Id != app.Id).ToArray();
    var scan = LeftoverScanner.Scan(app, neighbors);
    var owned = scan.Items.Where(item => IsOwnedCandidate(item, installPath, softwareKey)).ToArray();
    var ownedDirectory = owned.Single(item => item.Kind == LeftoverKind.Directory && EqualPath(item.Target, installPath));
    Require(owned.Any(item => item.Kind == LeftoverKind.RegistryKey), "The fixture's application registry key was found.");
    Require(File.Exists(leftoverFile) && CheckRegistry(softwareKey), "Scanning left the fixtures intact.");

    var shared = app with { Name = name + "-Neighbor", RegistryPath = neighborKey };
    var sharedScan = LeftoverScanner.Scan(app, [.. neighbors, shared]);
    Require(!sharedScan.Items.Any(item => (item.Kind is LeftoverKind.Directory or LeftoverKind.File) &&
        IsOwnedCandidate(item, installPath, softwareKey)), "A directory owned by a second app was excluded.");
    Checkpoint("Leftover scan and shared ownership guard");

    var recovery = new RecoveryService(Path.Combine(tempPath, "Quarantine"));
    using (var key = Registry.CurrentUser.CreateSubKey(neighborKey))
    {
        key.SetValue("DisplayName", shared.Name);
        key.SetValue("InstallLocation", installPath);
    }
    var staleSelection = recovery.Clean(app, [ownedDirectory]);
    Require(staleSelection.Entries.Single().State == CleanupState.Skipped && File.Exists(leftoverFile),
        "A new app registration sharing the folder invalidated the earlier selection.");
    Registry.CurrentUser.DeleteSubKeyTree(neighborKey);

    var session = recovery.Clean(app, owned);
    Require(session.Entries.Count == owned.Length && session.Entries.All(entry => entry.State == CleanupState.Quarantined),
        $"The selected fixtures were quarantined: {Describe(session)}");
    Require(!Directory.Exists(installPath) && !KeyExists(softwareKey), "Cleanup removed the original fixture residue.");
    Require(recovery.ReadHistory().Any(entry => entry.Id == session.Id), "The cleanup session is present in history.");
    Checkpoint("Quarantine and durable history");

    Directory.CreateDirectory(installPath);
    File.WriteAllText(leftoverFile, "replacement content");
    var occupied = recovery.Restore(session.Id);
    Require(occupied.Entries.Single(entry => entry.Item.Kind == LeftoverKind.Directory).State == CleanupState.Failed
        && File.ReadAllText(leftoverFile) == "replacement content", "Restore did not overwrite an occupied target.");
    File.Delete(leftoverFile);
    Directory.Delete(installPath);

    var restored = recovery.Restore(session.Id);
    Require(restored.Entries.All(entry => entry.State == CleanupState.Restored),
        $"The session was restored: {Describe(restored)}");
    Require(File.ReadAllText(leftoverFile) == "fixture residue", "The original file was restored without modification.");
    Require(CheckRegistry(softwareKey), "Registry values, value types, and subkeys were restored.");
    Checkpoint("File and registry restore");
    File.WriteAllText(Path.Combine(installPath, "sibling.txt"), "second file");
    var toDiscard = recovery.Clean(app, [ownedDirectory]);
    Require(toDiscard.Entries.Single().State == CleanupState.Quarantined, "The disposal fixture was quarantined.");
    var payload = Directory.EnumerateFiles(tempPath, "retained.txt", SearchOption.AllDirectories).Single();
    using (File.Open(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var interrupted = recovery.Discard(toDiscard.Id);
        Require(interrupted.Entries.Single().State == CleanupState.DiscardInProgress,
            "An interrupted discard must keep its irreversible state.");
        Require(recovery.Restore(toDiscard.Id).Entries.Single().State == CleanupState.DiscardInProgress &&
                !Directory.Exists(installPath), "A partial backup must not be presented as a full recovery.");
    }
    Require(recovery.Discard(toDiscard.Id).Entries.Single().State == CleanupState.Discarded && !File.Exists(payload),
        "Discarding should succeed when retried after unlocking the file.");
    Checkpoint("Interrupted permanent deletion and retry");
    return 0;
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false);
    Registry.CurrentUser.DeleteSubKeyTree(neighborKey, throwOnMissingSubKey: false);
    Registry.CurrentUser.DeleteSubKeyTree(softwareKey, throwOnMissingSubKey: false);
    if (Directory.Exists(installPath))
        Directory.Delete(installPath, recursive: true);
    if (Directory.Exists(tempPath))
        Directory.Delete(tempPath, recursive: true);
}

static int RunFixtureUninstaller(string id)
{
    if (!Guid.TryParseExact(id, "N", out var fixtureId))
        return 2;

    var name = FixturePrefix + fixtureId.ToString("N");
    var path = @$"{UninstallRoot}\{name}";
    using (var key = Registry.CurrentUser.OpenSubKey(path))
    {
        if (!string.Equals(key?.GetValue(ProofName) as string, id, StringComparison.Ordinal))
            return 3;
    }

    var markerDirectory = OwnedChild(Path.GetTempPath(), name);
    if (!Directory.Exists(markerDirectory))
        return 4;

    File.WriteAllText(Path.Combine(markerDirectory, "vendor-ran.txt"), ProofText);
    Registry.CurrentUser.DeleteSubKeyTree(path);
    return 0;
}

static void VerifyMsiCommand(InstalledApp fixture)
{
    var productCode = Guid.NewGuid().ToString("B");
    var app = fixture with
    {
        KeyName = productCode,
        RegistryPath = @$"{UninstallRoot}\{productCode}",
        UninstallString = $"MsiExec.exe /I{productCode}",
        IsMsi = true
    };
    Require(UninstallCommand.TryCreate(app, out var command, out var error), $"MSI plan could not be built: {error}");
    Require(command is { IsMsi: true } && string.Equals(Path.GetFileName(command.FileName), "msiexec.exe", StringComparison.OrdinalIgnoreCase)
        && command.Arguments.Contains("/x", StringComparison.OrdinalIgnoreCase)
        && command.Arguments.Contains(productCode, StringComparison.OrdinalIgnoreCase), "The MSI command explicitly requests uninstall.");
    Checkpoint("MSI uninstall planning");
}

static bool IsOwnedCandidate(LeftoverItem item, string installPath, string softwareKey)
{
    if (item.Kind is LeftoverKind.File or LeftoverKind.Directory)
        return EqualPath(item.Target, installPath) || item.Target.StartsWith(installPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    return item.Hive == RegistryHive.CurrentUser && item.Kind == LeftoverKind.RegistryKey &&
        (EqualPath(item.Target, softwareKey) || EqualPath(item.Target, @$"HKEY_CURRENT_USER\{softwareKey}"));
}

static string OwnedChild(string parent, string name)
{
    var canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
    var child = Path.GetFullPath(Path.Combine(canonicalParent, name));
    Require(EqualPath(Path.GetDirectoryName(child) ?? "", canonicalParent) && Path.GetFileName(child) == name,
        "The fixture path must be a direct child with the generated name.");
    return child;
}

static bool CheckRegistry(string path)
{
    using var key = Registry.CurrentUser.OpenSubKey(path);
    using var subkey = key?.OpenSubKey("Settings");
    return key?.GetValue("Expandable", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw &&
        raw == @"%LOCALAPPDATA%\Verification" && key.GetValueKind("Expandable") == RegistryValueKind.ExpandString &&
        key.GetValue("Enabled") is 1 && key.GetValueKind("Enabled") == RegistryValueKind.DWord &&
        key.GetValue("Labels") is string[] labels && labels.SequenceEqual(new[] { "first", "second" }) &&
        subkey?.GetValue("Payload") is byte[] payload && payload.SequenceEqual(new byte[] { 1, 2, 3 });
}

static bool KeyExists(string path)
{
    using var key = Registry.CurrentUser.OpenSubKey(path);
    return key is not null;
}

static string Describe(CleanupSession session) => string.Join(", ", session.Entries.Select(item => $"{item.Item.Target} = {item.State} ({item.Note})"));

static bool EqualPath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Checkpoint(string message) => Console.WriteLine($"PASS {message}");
