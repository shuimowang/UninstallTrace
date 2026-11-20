using System.IO;
using System.Text.Json;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class LockedQuarantineVerification
{
    public static void Run()
    {
        var name = $"UninstallTrace Locked Fixture {Guid.NewGuid():N}";
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var original = Path.GetFullPath(Path.Combine(localData, name));
        var storage = Path.GetFullPath(Path.Combine(localData, $"{name}-Vault"));
        Check(Path.GetDirectoryName(original) == localData && Path.GetDirectoryName(storage) == localData &&
            !Directory.Exists(original) && !Directory.Exists(storage), "Expected two new test directories within the user data folder.");

        var app = new InstalledApp
        {
            Name = name,
            Publisher = "UninstallTrace verification",
            InstallLocation = original,
            DisplayIcon = $"\"{Path.Combine(original, "Program.exe")}\",0",
            RegistryPath = $@"{InstalledApps.UninstallRegistryPath}\{name}",
            Hive = RegistryHive.CurrentUser,
            View = RegistryView.Default
        };
        var recovery = new RecoveryService(storage);
        var originalFile = Path.Combine(original, "held-open.txt");

        try
        {
            Directory.CreateDirectory(original);
            File.WriteAllText(originalFile, "original fixture data");
            var candidate = LeftoverScanner.Scan(app, InstalledApps.Read()).Items.Single(item =>
                item.Kind == LeftoverKind.Directory && string.Equals(item.Target, original, StringComparison.OrdinalIgnoreCase));
            using (File.Open(originalFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var session = recovery.Clean(app, [candidate], requireUnregistered: true);
                var entry = session.Entries.Single();
                Check(entry.State == CleanupState.OriginalInPlace && entry.OriginalPathPresent &&
                    entry.CopyStatus == RecoveryCopyStatus.Missing && !entry.CanRestore && !entry.CanDiscard,
                    "A move blocked by a locked child must leave the original path and advertise no recoverable copy.");
                var note = entry.LocalizedNote?.English ?? "";
                Check(note.Contains("No recovery copy was created.", StringComparison.Ordinal) &&
                    (note.Contains("error 5", StringComparison.Ordinal) || note.Contains("error 32", StringComparison.Ordinal)
                        || note.Contains("error 33", StringComparison.Ordinal)),
                    "A Windows sharing or access-denied move failure must give a specific action and an accurate note.");
                Check(recovery.ReadHistory().Single(item => item.Id == session.Id).App == app,
                    "A new cleanup record must preserve the exact app snapshot for a fresh scan.");
            }

            var legacy = new CleanupSession
            {
                AppName = name,
                Entries = [new CleanupEntry { Item = candidate, State = CleanupState.Failed, Note = "Legacy move error" }]
            };
            var journal = Path.Combine(storage, $"{legacy.Id:N}.json");
            File.WriteAllText(journal, JsonSerializer.Serialize(legacy));
            var before = File.ReadAllBytes(journal);
            var inspected = recovery.ReadHistory().Single(item => item.Id == legacy.Id);
            var oldEntry = inspected.Entries.Single();
            Check(inspected.App is null && oldEntry.State == CleanupState.Failed && oldEntry.Note == "Legacy move error" &&
                oldEntry.CopyStatus == RecoveryCopyStatus.Missing && oldEntry.OriginalPathPresent && !oldEntry.CanRestore && !oldEntry.CanDiscard,
                "A legacy failed journal remains unchanged while availability reflects an absent copy and an occupied original path.");
            Check(recovery.Restore(legacy.Id).Entries.Single().State == CleanupState.Failed &&
                recovery.Discard(legacy.Id).Entries.Single().State == CleanupState.Failed &&
                File.ReadAllBytes(journal).SequenceEqual(before), "Reading or retrying a missing legacy payload must not rewrite its history.");

            var rescanned = LeftoverScanner.Scan(app, InstalledApps.Read()).Items.Single(item =>
                item.Kind == LeftoverKind.Directory && string.Equals(item.Target, original, StringComparison.OrdinalIgnoreCase));
            var quarantined = recovery.Clean(app, [rescanned], requireUnregistered: true);
            Check(quarantined.Entries.Single() is { State: CleanupState.Quarantined, CanRestore: true, CanDiscard: true } &&
                !Directory.Exists(original), "A fresh scan can quarantine the folder after its file handle closes.");

            Directory.CreateDirectory(original);
            File.WriteAllText(originalFile, "replacement fixture data");
            var blockedRestore = recovery.Restore(quarantined.Id).Entries.Single();
            Check(blockedRestore is { State: CleanupState.Failed, CopyStatus: RecoveryCopyStatus.Available, CanRestore: true } &&
                File.ReadAllText(originalFile) == "replacement fixture data", "A blocked restore still has a real copy and preserves the replacement.");
        }
        finally
        {
            if (Directory.Exists(original))
                Directory.Delete(original, recursive: true);
            if (Directory.Exists(storage))
                Directory.Delete(storage, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
