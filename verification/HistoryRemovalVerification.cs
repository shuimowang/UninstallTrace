using System.IO;
using System.Text.Json;
using DeepUninstaller.Services;
using Microsoft.Win32;

internal static class HistoryRemovalVerification
{
    public static void Run()
    {
        var parent = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var fixture = Path.GetFullPath(Path.Combine(parent, $"UninstallTrace-HistoryRemovalVerification-{Guid.NewGuid():N}"));
        Check(Path.GetDirectoryName(fixture) == parent && !Directory.Exists(fixture), "The test location must be a new direct child.");

        var storage = Path.Combine(fixture, "history");
        var original = Path.Combine(fixture, "original.txt");
        var service = new RecoveryService(storage);
        Directory.CreateDirectory(storage);
        File.WriteAllText(original, "unmodified original");

        try
        {
            VerifyMissingCopies(service, storage, original);
            VerifyFreshCheck(service, storage, original);
            VerifyMixedAndUnlistedCopies(service, storage, original);
            VerifyUnverifiableCopy(service, storage);
            Check(File.ReadAllText(original) == "unmodified original", "Removing records must leave the original data alone.");
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void VerifyMissingCopies(RecoveryService service, string storage, string original)
    {
        foreach (var state in new[] { CleanupState.Restored, CleanupState.Discarded, CleanupState.Failed,
                     CleanupState.Pending, CleanupState.OriginalInPlace })
        {
            var session = WriteRecord(storage, FileEntry(original, state));
            var journal = Journal(storage, session);
            var bytes = File.ReadAllBytes(journal);
            var loaded = ReadRecord(service, session);
            Check(loaded.CanRemoveRecord && loaded.Entries.Single().State == state &&
                File.ReadAllBytes(journal).SequenceEqual(bytes), "A record without a recovery copy can be removed without rewriting it first.");
            service.RemoveRecord(session.Id);
            Check(!File.Exists(journal), "Removal must delete the specific journal file.");
        }
    }

    private static void VerifyFreshCheck(RecoveryService service, string storage, string original)
    {
        foreach (var state in new[] { CleanupState.Restored, CleanupState.Discarded })
        {
            var entry = FileEntry(original, state);
            var session = WriteRecord(storage, entry);
            Check(ReadRecord(service, session).CanRemoveRecord, "The journal was initially removable.");

            var payload = Path.Combine(Vault(storage, session), entry.Id.ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            File.WriteAllText(payload, "recoverable payload");

            Check(!ReadRecord(service, session).CanRemoveRecord, "A physical copy takes precedence over a terminal status.");
            ExpectKept(service, storage, session);
            Check(File.ReadAllText(payload) == "recoverable payload", "Removal must leave the actual payload unchanged.");
        }
    }

    private static void VerifyMixedAndUnlistedCopies(RecoveryService service, string storage, string original)
    {
        var absent = FileEntry(original, CleanupState.Failed);
        var copiedRegistryValue = new CleanupEntry
        {
            Item = new LeftoverItem(LeftoverKind.RegistryValue, @"Software\UninstallTrace-Test", "Test value",
                LeftoverConfidence.Strong, RegistryHive.CurrentUser, RegistryView.Default, "Value"),
            State = CleanupState.Discarded,
            RegistryBackup = new RegistryTree([new RegistryData("Value", RegistryValueKind.String, ["backup"])], new())
        };
        var mixed = WriteRecord(storage, absent, copiedRegistryValue);
        ExpectKept(service, storage, mixed);

        var orphanSession = WriteRecord(storage, FileEntry(original, CleanupState.Discarded));
        var vault = Vault(storage, orphanSession);
        Directory.CreateDirectory(vault);
        var orphan = Path.Combine(vault, "unlisted");
        File.WriteAllText(orphan, "unlisted backup");
        ExpectKept(service, storage, orphanSession);
        Check(File.ReadAllText(orphan) == "unlisted backup", "The record must remain when its vault contains an unlisted file.");
    }

    private static void VerifyUnverifiableCopy(RecoveryService service, string storage)
    {
        // A path inside recovery storage is rejected when validating an original filesystem location.
        var unverified = WriteRecord(storage, FileEntry(Path.Combine(storage, "unsafe-original.txt"), CleanupState.Restored));
        Check(ReadRecord(service, unverified).Entries.Single().CopyStatus == RecoveryCopyStatus.Unverified,
            "The test must exercise an unverifiable location, even with a terminal journal state.");
        ExpectKept(service, storage, unverified);
    }

    private static CleanupEntry FileEntry(string target, CleanupState state) => new()
    {
        Item = new LeftoverItem(LeftoverKind.File, target, "Test file", LeftoverConfidence.Strong),
        State = state,
    };

    private static CleanupSession WriteRecord(string storage, params CleanupEntry[] entries)
    {
        var session = new CleanupSession { AppName = "Test app", Entries = entries.ToList() };
        File.WriteAllText(Journal(storage, session), JsonSerializer.Serialize(session));
        return session;
    }

    private static CleanupSession ReadRecord(RecoveryService service, CleanupSession session) =>
        service.ReadHistory().Single(record => record.Id == session.Id);

    private static string Journal(string storage, CleanupSession session) => Path.Combine(storage, $"{session.Id:N}.json");

    private static string Vault(string storage, CleanupSession session) => Path.Combine(storage, "files", $"{session.Id:N}");

    private static void ExpectKept(RecoveryService service, string storage, CleanupSession session)
    {
        var path = Journal(storage, session);
        var originalJournal = File.ReadAllBytes(path);
        Check(!ReadRecord(service, session).CanRemoveRecord, "The record should not offer removal.");
        try
        {
            service.RemoveRecord(session.Id);
        }
        catch (IOException)
        {
            Check(File.ReadAllBytes(path).SequenceEqual(originalJournal), "A blocked record must remain unchanged.");
            return;
        }

        throw new InvalidOperationException("Removal must reject the record while it contains a potential recovery copy.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
