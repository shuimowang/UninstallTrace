using System.IO;
using System.Text.Json;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class RegistryRestoreVerification
{
    public static void Run()
    {
        var fixture = $"UninstallTrace-RegistryRestoreVerification-{Guid.NewGuid():N}";
        var parentPath = $@"Software\{fixture}";
        var target = $@"{parentPath}\Original";
        var storage = Path.GetFullPath(Path.Combine(Path.GetTempPath(), fixture));
        using (var existing = Registry.CurrentUser.OpenSubKey(parentPath))
        {
            Check(existing is null && !Directory.Exists(storage), "The verification locations must be new.");
        }

        var correct = new RegistryTree(
            [new RegistryData("First", RegistryValueKind.String, ["original data"])],
            new() { ["Child"] = new([new RegistryData("Enabled", RegistryValueKind.DWord, ["1"])], new()) });
        var session = new CleanupSession
        {
            AppName = fixture,
            Entries =
            [
                new CleanupEntry
                {
                    Item = new LeftoverItem(LeftoverKind.RegistryKey, target, "Registry restore verification",
                        LeftoverConfidence.Strong, RegistryHive.CurrentUser, RegistryView.Default),
                    State = CleanupState.Quarantined,
                    RegistryBackup = correct with
                    {
                        Children = new() { ["Child"] = new([new RegistryData("Unsupported", RegistryValueKind.Unknown, [])], new()) }
                    }
                }
            ]
        };
        var journal = Path.Combine(storage, $"{session.Id:N}.json");
        Directory.CreateDirectory(storage);

        try
        {
            using var parent = Registry.CurrentUser.CreateSubKey(parentPath);
            var recovery = new RecoveryService(storage);
            File.WriteAllText(journal, JsonSerializer.Serialize(session));

            var failed = recovery.Restore(session.Id);
            var failedEntry = failed.Entries.Single();
            Check(failedEntry.State == CleanupState.Failed && failedEntry.RegistryBackup is not null,
                "A staging write failure must keep the snapshot available for retry.");
            Check(parent.GetSubKeyNames().Length == 0,
                "A staging write failure must leave neither a partially restored original key nor a staging key.");

            failedEntry.RegistryBackup = correct;
            File.WriteAllText(journal, JsonSerializer.Serialize(failed));
            using (var occupied = parent.CreateSubKey("Original"))
                occupied.SetValue("First", "replacement data");
            var collision = recovery.Restore(session.Id);
            using (var occupied = parent.OpenSubKey("Original"))
                Check(collision.Entries.Single().State == CleanupState.Failed && occupied?.GetValue("First") is "replacement data",
                    "An occupied original name must be left unchanged.");
            parent.DeleteSubKey("Original");

            var restored = recovery.Restore(session.Id);
            using var original = parent.OpenSubKey("Original");
            using var child = original?.OpenSubKey("Child");
            var restoredEntry = restored.Entries.Single();
            Check(restoredEntry.State == CleanupState.Restored && restoredEntry.RegistryBackup is null &&
                original?.GetValue("First") is "original data" && child?.GetValue("Enabled") is 1 &&
                parent.GetSubKeyNames().SequenceEqual(["Original"]),
                "Retry must publish the complete restored tree under its original name.");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(parentPath, throwOnMissingSubKey: false);
            File.Delete(journal);
            File.Delete(Path.Combine(storage, "operation.lock"));
            Directory.Delete(storage);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
