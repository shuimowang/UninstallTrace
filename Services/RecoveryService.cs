using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DeepUninstaller.Services;

public enum CleanupState { Pending, Quarantined, Restored, Skipped, Failed, Discarded, DiscardInProgress, OriginalInPlace }

public enum RecoveryCopyStatus { Unverified, Missing, Available }

public sealed record RecoveryNote(string Chinese, string English)
{
    [JsonIgnore]
    public string Text => Localization.Text(Chinese, English);
}

internal sealed class RecoveryIOException(string chinese, string english, Exception? inner = null)
    : IOException(Localization.Text(chinese, english), inner)
{
    public RecoveryNote Details { get; } = new(chinese, english);
}

public sealed class CleanupEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required LeftoverItem Item { get; init; }
    public CleanupState State { get; set; }
    public string? Note { get; set; }
    public RecoveryNote? LocalizedNote { get; set; }
    [JsonIgnore]
    public string? DisplayNote => LocalizedNote?.Text ?? Note;
    public RegistryTree? RegistryBackup { get; set; }

    [JsonIgnore]
    public RecoveryCopyStatus CopyStatus { get; internal set; }
    [JsonIgnore]
    public bool OriginalPathPresent { get; internal set; }
    [JsonIgnore]
    public string? CopyCheckError { get; internal set; }
    [JsonIgnore]
    public bool CanRestore => CopyStatus == RecoveryCopyStatus.Available &&
        State is CleanupState.Pending or CleanupState.Quarantined or CleanupState.Failed;
    [JsonIgnore]
    public bool CanDiscard => CanRestore || CopyStatus == RecoveryCopyStatus.Available && State == CleanupState.DiscardInProgress;
}

public sealed class CleanupSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string AppName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstalledApp? App { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required List<CleanupEntry> Entries { get; init; }
}

public sealed record RegistryTree(RegistryData[] Values, Dictionary<string, RegistryTree> Children);
public sealed record RegistryData(string Name, RegistryValueKind Kind, string[] Data)
{
    public static RegistryData Read(RegistryKey key, string name)
    {
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string[] data = value switch
        {
            string text when kind is RegistryValueKind.String or RegistryValueKind.ExpandString => [text],
            string[] strings when kind == RegistryValueKind.MultiString => strings,
            int number when kind == RegistryValueKind.DWord => [number.ToString(CultureInfo.InvariantCulture)],
            long number when kind == RegistryValueKind.QWord => [number.ToString(CultureInfo.InvariantCulture)],
            byte[] bytes when kind is RegistryValueKind.Binary or RegistryValueKind.None => [Convert.ToBase64String(bytes)],
            _ => throw new RecoveryIOException($"无法保存注册表值 {name}（{kind}）。",
                $"Could not save registry value {name} ({kind}).")
        };
        return new RegistryData(name, kind, data);
    }

    public void Write(RegistryKey key)
    {
        object value = Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => Data[0],
            RegistryValueKind.MultiString => Data,
            RegistryValueKind.DWord => int.Parse(Data[0], CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(Data[0], CultureInfo.InvariantCulture),
            RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(Data[0]),
            _ => throw new RecoveryIOException($"无法恢复注册表值 {Name}（{Kind}）。",
                $"Could not restore registry value {Name} ({Kind}).")
        };
        key.SetValue(Name, value, Kind);
    }
}

public sealed class RecoveryService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 256 };
    private readonly string storageRoot;

    public RecoveryService(string? storageRoot = null)
    {
        this.storageRoot = Path.GetFullPath(storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeepUninstaller", "Recovery"));
    }

    public IReadOnlyList<CleanupSession> ReadHistory() => ReadHistory(null);

    public IReadOnlyList<CleanupSession> ReadHistory(Action<string>? reportUnreadable)
    {
        if (!Directory.Exists(storageRoot))
            return [];
        EnsurePlainPath(storageRoot);
        var sessions = new List<CleanupSession>();
        foreach (var path in Directory.EnumerateFiles(storageRoot, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                continue;
            try { sessions.Add(Load(id)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                reportUnreadable?.Invoke(Localization.Text($"无法读取恢复记录 {id:N}。{exception.Message}",
                    $"Could not read recovery record {id:N}. {exception.Message}"));
            }
        }
        return sessions.OrderByDescending(session => session.CreatedAt).ToArray();
    }

    public CleanupSession Clean(InstalledApp app, IReadOnlyList<LeftoverItem> selected, bool requireUnregistered = false)
    {
        if (app.IsProtected)
            throw new InvalidOperationException(Localization.Text("受保护的应用只能查看信息。",
                "Only information can be viewed for protected apps."));
        if (selected.Count == 0)
            throw new InvalidOperationException(Localization.Text("请选择要清理的项目。", "Select items to clean."));

        using var operationLock = EnterOperation();
        var inventory = InstalledApps.Read();
        if (inventory.FirstOrDefault(candidate => candidate.Id == app.Id) is { } registration && registration != app)
            throw new InvalidOperationException(Localization.Text("程序登记已变化，请重新扫描。",
                "The app's registration has changed. Scan again."));
        if (requireUnregistered && InstalledApps.IsRegistered(app))
            throw new InvalidOperationException(Localization.Text("程序重新登记了卸载信息，请重新扫描。",
                "The app has registered an uninstall entry again. Scan again."));
        var current = LeftoverScanner.Scan(app, inventory).Items;
        var requested = selected.DistinctBy(ItemIdentity, PathComparer)
            .OrderBy(item => IsRegistration(item, app))
            .ToArray();
        var session = new CleanupSession
        {
            AppName = app.Name,
            App = string.IsNullOrWhiteSpace(app.RegistryPath) ? null : app,
            Entries = requested.Select(item => new CleanupEntry
            {
                Item = current.FirstOrDefault(candidate => ItemMatches(item, candidate)) ?? item
            }).ToList()
        };
        Save(session);

        foreach (var entry in session.Entries)
        {
            if (!current.Any(candidate => ItemMatches(entry.Item, candidate)))
            {
                entry.State = CleanupState.Skipped;
                var note = new RecoveryNote("清理前重新核对时，项目已经没有可用的归属依据。",
                    "When checked before cleanup, the item no longer had evidence linking it to the app.");
                entry.LocalizedNote = note;
                entry.Note = note.Text;
                Save(session);
                continue;
            }
            ExecuteEntry(session, entry, () =>
            {
                if (requireUnregistered && InstalledApps.IsRegistered(app))
                    throw new RecoveryIOException("程序重新登记了卸载信息，已停止清理该项。",
                        "Cleanup stopped for this item because the app registered an uninstall entry again.");
                if (IsRegistration(entry.Item, app) && session.Entries.Any(other =>
                        other != entry && other.State is CleanupState.Failed or CleanupState.Skipped or CleanupState.OriginalInPlace))
                    throw new RecoveryIOException("存在未清理的选中项，本次保留卸载登记，便于重试。",
                        "The uninstall entry was kept so you can retry cleanup for the selected items that remain.");
                Quarantine(session, entry);
            }, CleanupState.Quarantined);
        }
        return session;
    }

    public CleanupSession Restore(Guid id)
    {
        using var operationLock = EnterOperation();
        var session = Load(id);
        foreach (var entry in session.Entries.AsEnumerable().Reverse())
        {
            if (!entry.CanRestore)
                continue;
            ExecuteEntry(session, entry, () => RestoreEntry(session, entry), CleanupState.Restored);
        }
        return session;
    }

    public CleanupSession Discard(Guid id)
    {
        using var operationLock = EnterOperation();
        var session = Load(id);
        foreach (var entry in session.Entries)
        {
            if (!entry.CanDiscard && !(entry.State == CleanupState.DiscardInProgress && entry.CopyStatus == RecoveryCopyStatus.Missing))
                continue;
            ExecuteEntry(session, entry, () =>
            {
                if (IsFileSystem(entry.Item))
                    DeleteQuarantine(session, entry);
                entry.RegistryBackup = null;
            }, CleanupState.Discarded);
        }
        return session;
    }

    private FileStream EnterOperation()
    {
        EnsurePlainPath(storageRoot);
        Directory.CreateDirectory(storageRoot);
        var lockPath = Path.Combine(storageRoot, "operation.lock");
        EnsurePlainPath(lockPath);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new RecoveryIOException("无法锁定恢复记录。请等待其他清理窗口完成操作后重试。",
                "Could not lock the recovery records. Wait for the other cleanup window to finish, then try again.", exception);
        }
    }

    private void ExecuteEntry(CleanupSession session, CleanupEntry entry, Action action, CleanupState success)
    {
        try
        {
            action();
            entry.State = success;
            entry.Note = null;
            entry.LocalizedNote = null;
        }
        catch (Exception exception) when (IsEntryFailure(exception))
        {
            if (entry.State is not (CleanupState.DiscardInProgress or CleanupState.OriginalInPlace))
                entry.State = CleanupState.Failed;
            entry.Note = exception.Message;
            entry.LocalizedNote = (exception as RecoveryIOException)?.Details;
        }
        Save(session);
        InspectCopy(session, entry);
    }

    private void Quarantine(CleanupSession session, CleanupEntry entry)
    {
        var item = entry.Item;
        if (IsFileSystem(item))
        {
            ValidateOriginal(item.Target);
            EnsurePlainTree(item.Target, item.Kind == LeftoverKind.Directory);
            var destination = QuarantinePath(session, entry);
            EnsurePlainPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                Move(item, item.Target, destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                InspectCopy(session, entry);
                var originalInPlace = entry.CopyStatus == RecoveryCopyStatus.Missing && entry.OriginalPathPresent;
                if (originalInPlace)
                    entry.State = CleanupState.OriginalInPlace;
                throw DescribeMoveFailure(exception, originalInPlace);
            }
            return;
        }

        using var hive = OpenHive(item);
        EnsurePlainRegistryPath(hive, item.Target, item.View!.Value);
        using (var source = hive.OpenSubKey(item.Target, writable: item.Kind == LeftoverKind.RegistryValue)
               ?? throw new RecoveryIOException("注册表项已不存在。", "The registry key no longer exists."))
        {
            entry.RegistryBackup = item.Kind == LeftoverKind.RegistryValue
                ? new RegistryTree([ReadVerifiedRegistryValue(source, item)], new())
                : Snapshot(source, item.View.Value);
            Save(session);
            if (item.Kind == LeftoverKind.RegistryValue)
            {
                var saved = entry.RegistryBackup.Values.Single();
                var live = RegistryData.Read(source, item.ValueName!);
                if (saved.Kind != live.Kind || !saved.Data.SequenceEqual(live.Data))
                {
                    entry.RegistryBackup = null;
                    throw new RecoveryIOException("注册表值在备份期间发生变化，已跳过。",
                        "Skipped the registry value because it changed while being backed up.");
                }
                source.DeleteValue(item.ValueName!);
                return;
            }
            if (!TreesEqual(entry.RegistryBackup, Snapshot(source, item.View.Value)))
                throw new RecoveryIOException("注册表项在备份期间发生变化，已跳过。",
                    "Skipped the registry key because it changed while being backed up.");
        }
        EnsurePlainRegistryPath(hive, item.Target, item.View.Value);
        hive.DeleteSubKeyTree(item.Target);
    }

    internal static RegistryData ReadVerifiedRegistryValue(RegistryKey source, LeftoverItem item)
    {
        var saved = RegistryData.Read(source, item.ValueName!);
        if (item.EvidenceKind != LeftoverEvidence.StartupCommand)
            return saved;

        var command = saved.Kind switch
        {
            RegistryValueKind.String => saved.Data[0],
            RegistryValueKind.ExpandString => Environment.ExpandEnvironmentVariables(saved.Data[0]),
            _ => null
        };
        if (!LeftoverScanner.StartupCommandStillMatches(command, item.EvidencePath))
            throw new RecoveryIOException("自启动项的程序路径已变化，请重新扫描。",
                "The startup entry now points to a different program. Scan again.");
        return saved;
    }

    private void RestoreEntry(CleanupSession session, CleanupEntry entry)
    {
        var item = entry.Item;
        if (IsFileSystem(item))
        {
            var source = QuarantinePath(session, entry);
            EnsurePlainPath(source);
            if (GetAttributes(source) is null)
            {
                if (entry.State == CleanupState.Pending && GetAttributes(item.Target) is not null)
                    throw new RecoveryIOException("该项目仍在原处，且没有发现隔离副本。",
                        "The item is still in its original location and no quarantined copy was found.");
                throw new RecoveryIOException("找不到隔离副本。", "The quarantined copy could not be found.");
            }
            ValidateOriginal(item.Target);
            if (GetAttributes(item.Target) is not null)
                throw new RecoveryIOException("原位置已被占用，隔离副本仍保留。",
                    "The original location is occupied. The quarantined copy was kept.");
            EnsurePlainTree(source, item.Kind == LeftoverKind.Directory);
            Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
            Move(item, source, item.Target);
            return;
        }

        var backup = entry.RegistryBackup ?? throw new RecoveryIOException("没有保存该项目的注册表数据。",
            "No registry data was saved for this item.");
        using var hive = OpenHive(item);
        EnsurePlainRegistryPath(hive, item.Target, item.View!.Value);
        if (item.Kind == LeftoverKind.RegistryValue)
        {
            using var key = hive.OpenSubKey(item.Target, writable: true)
                ?? throw new RecoveryIOException("注册表值的父项已不存在。", "The parent key for the registry value no longer exists.");
            if (key.GetValueNames().Contains(item.ValueName!, PathComparer))
                throw new RecoveryIOException("原注册表值已被占用，备份仍保留。",
                    "The original registry value is occupied. The backup was kept.");
            backup.Values.Single().Write(key);
        }
        else
        {
            RestoreRegistryTree(hive, item.Target, item.View.Value, backup);
        }
        entry.RegistryBackup = null;
    }

    private static void RestoreRegistryTree(RegistryKey hive, string target, RegistryView view, RegistryTree backup)
    {
        using (var existing = hive.OpenSubKey(target))
        {
            if (existing is not null)
                throw new RecoveryIOException("原注册表项已被占用，备份仍保留。",
                    "The original registry key is occupied. The backup was kept.");
        }

        var separator = target.LastIndexOf('\\');
        var parentPath = target[..separator];
        var targetName = target[(separator + 1)..];
        using var parent = hive.CreateSubKey(parentPath, writable: true);
        EnsurePlainRegistryPath(hive, parentPath, view);

        const int createdNewKey = 1;
        var stagingName = $"UninstallTrace-Restore-{Guid.NewGuid():N}";
        var access = (int)(RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.Delete) | (int)view;
        var result = RegCreateKeyEx(parent.Handle, stagingName, 0, null, 0,
            access, IntPtr.Zero, out var handle, out var disposition);
        using (handle)
        {
            if (result != 0)
            {
                var detail = new Win32Exception(result).Message;
                throw new RecoveryIOException($"无法创建注册表恢复暂存项。{detail}",
                    $"Could not create the registry staging key for restore. {detail}");
            }
            if (disposition != createdNewKey)
                throw new RecoveryIOException("暂存注册表项已存在，请重试。",
                    "The registry staging key already exists. Try again.");

            using var staging = RegistryKey.FromHandle(handle, view);
            var renamed = false;
            try
            {
                WriteTree(staging, backup);
                if (!TreesEqual(backup, Snapshot(staging, view)))
                    throw new RecoveryIOException("暂存内容与备份不一致，原位置未改动。",
                        "The staged registry data did not match the backup. The original location was left alone.");

                // Windows refuses to rename onto an existing key, including a key created during staging.
                result = RegRenameKey(handle, null, targetName);
                if (result != 0)
                {
                    var detail = new Win32Exception(result).Message;
                    throw new RecoveryIOException($"无法还原原注册表项，备份仍保留。{detail}",
                        $"Could not restore the original registry key. The backup was kept. {detail}");
                }
                renamed = true;
            }
            finally
            {
                if (!renamed)
                    RemoveOwnedStagingKey(handle, view, stagingName);
            }
        }
    }

    private static void RemoveOwnedStagingKey(SafeRegistryHandle key, RegistryView view, string stagingName)
    {
        // Keep the original handle so cleanup cannot delete a replacement that happens to use the same name.
        var result = RegDeleteTree(key, null);
        if (result == 0)
            result = RegDeleteKeyEx(key, "", (int)view, 0);
        if (result != 0)
            Trace.TraceWarning($"Registry restore staging key {stagingName} could not be removed (Windows error {result}).");
    }

    private void DeleteQuarantine(CleanupSession session, CleanupEntry entry)
    {
        var path = QuarantinePath(session, entry);
        EnsurePlainPath(path);
        if (GetAttributes(path) is null)
            return;
        var isDirectory = entry.Item.Kind == LeftoverKind.Directory;
        EnsurePlainTree(path, isDirectory);
        // Recursive deletion may remove some children before encountering a locked file.
        // Persist intent first so a retry or restart cannot offer a partial tree for restore.
        entry.State = CleanupState.DiscardInProgress;
        Save(session);
        if (isDirectory)
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }

    private string JournalPath(Guid id) => Path.Combine(storageRoot, $"{id:N}.json");

    private CleanupSession Load(Guid id)
    {
        var path = JournalPath(id);
        EnsurePlainPath(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var session = JsonSerializer.Deserialize<CleanupSession>(stream, JsonOptions);
        if (session is null || session.Id != id || session.Entries is null ||
            session.Entries.Any(entry => entry?.Item is null || !Enum.IsDefined(entry.Item.Kind)))
            throw new RecoveryIOException("恢复记录无效。", "The recovery record is invalid.");
        foreach (var entry in session.Entries)
            InspectCopy(session, entry);
        return session;
    }

    private void InspectCopy(CleanupSession session, CleanupEntry entry)
    {
        entry.CopyStatus = RecoveryCopyStatus.Unverified;
        entry.OriginalPathPresent = false;
        entry.CopyCheckError = null;
        if (entry.State is CleanupState.Restored or CleanupState.Skipped or CleanupState.Discarded)
            return;

        if (!IsFileSystem(entry.Item))
        {
            entry.CopyStatus = entry.RegistryBackup is null ? RecoveryCopyStatus.Missing : RecoveryCopyStatus.Available;
            return;
        }

        try
        {
            var vaultPath = QuarantinePath(session, entry);
            EnsurePlainPath(vaultPath);
            if (GetAttributes(vaultPath) is { } attributes)
            {
                if (MatchesItemType(entry.Item, attributes))
                    entry.CopyStatus = RecoveryCopyStatus.Available;
                else
                    entry.CopyCheckError = Localization.Text("隔离位置的项目类型与记录不符。",
                        "The item in quarantine does not match the recorded item type.");
                return;
            }

            entry.CopyStatus = RecoveryCopyStatus.Missing;
            entry.OriginalPathPresent = GetAttributes(entry.Item.Target) is { } original && MatchesItemType(entry.Item, original);
        }
        catch (Exception exception) when (IsEntryFailure(exception))
        {
            entry.CopyCheckError = Localization.Text($"无法核对隔离副本的位置。{exception.Message}",
                $"Could not verify the recovery copy location. {exception.Message}");
        }
    }

    private static bool MatchesItemType(LeftoverItem item, FileAttributes attributes) =>
        !attributes.HasFlag(FileAttributes.ReparsePoint) &&
        attributes.HasFlag(FileAttributes.Directory) == (item.Kind == LeftoverKind.Directory);

    private static bool IsEntryFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or InvalidOperationException or ArgumentException;

    private static Exception DescribeMoveFailure(Exception exception, bool originalInPlace)
    {
        var result = (uint)exception.HResult;
        var code = result >> 16 == 0x8007 ? result & 0xffff : result;
        var (chinese, english) = code switch
        {
            32 or 33 => ($"Windows 报告文件正在被占用（错误 {code}）。请正常退出正在使用它的程序，再重新扫描残留。",
                $"Windows reported a file sharing or lock violation (error {code}). Close the app using it, then scan for leftovers again."),
            5 => ("Windows 拒绝移动该项目（错误 5）。请先退出可能正在使用它的程序。若仍失败，请核对目录权限后重新扫描。",
                "Windows denied the move (error 5). Close any app that may be using the item. If the issue persists, check the folder permissions, then scan again."),
            _ when originalInPlace => ($"无法将项目移入隔离区。{exception.Message}",
                $"Could not move the item to quarantine. {exception.Message}"),
            _ => (null, null),
        };
        if (chinese is null || english is null)
            return exception;

        if (originalInPlace)
        {
            chinese = $"项目仍在原位置，本次没有隔离副本。{chinese}";
            english = $"The item remains at its original path. No recovery copy was created. {english}";
        }
        return new RecoveryIOException(chinese, english, exception);
    }

    private void Save(CleanupSession session)
    {
        var target = JournalPath(session.Id);
        var temporary = Path.Combine(storageRoot, $"{session.Id:N}.{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                JsonSerializer.Serialize(stream, session, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        catch when (ownsTemporary)
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private string QuarantinePath(CleanupSession session, CleanupEntry entry)
    {
        var original = entry.Item.Target;
        ValidateOriginal(original);
        var storage = PathComparer.Equals(Path.GetPathRoot(original), Path.GetPathRoot(storageRoot))
            ? Path.Combine(storageRoot, "files")
            : Path.Combine(Path.GetDirectoryName(original)!, ".DeepUninstaller-Recovery");
        return Path.Combine(storage, session.Id.ToString("N"), entry.Id.ToString("N"));
    }

    private void ValidateOriginal(string target)
    {
        if (!LeftoverScanner.TryValidateFileSystemTarget(target, out var reason))
            throw new IOException(reason ?? Localization.Text("此位置超出安全清理范围。",
                "This location is outside the safe cleanup area."));
        var full = Path.GetFullPath(target);
        if (IsAtOrBelow(full, storageRoot) || IsAtOrBelow(storageRoot, full)
            || full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains(".DeepUninstaller-Recovery", PathComparer))
            throw new RecoveryIOException("无法操作恢复数据所在的位置。",
                "This operation cannot modify the location where recovery data is stored.");
    }

    private static bool IsAtOrBelow(string path, string directory) =>
        PathComparer.Equals(path, directory) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Move(LeftoverItem item, string from, string to)
    {
        if (item.Kind == LeftoverKind.Directory)
            Directory.Move(from, to);
        else
            File.Move(from, to, overwrite: false);
    }

    private static void EnsurePlainPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (GetAttributes(current) is { } attributes && attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new RecoveryIOException($"路径包含链接或挂载点：{current}",
                    $"The path contains a link or mount point at {current}.");
        }
    }

    private static void EnsurePlainTree(string path, bool directory)
    {
        var attributes = GetAttributes(path) ?? throw new FileNotFoundException(
            Localization.Text("项目已不存在。", "The item no longer exists."), path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new RecoveryIOException($"项目类型已变化或包含链接：{path}",
                $"The item type has changed or is a link at {path}.");
        if (!directory)
            return;

        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var parent))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(parent))
            {
                var childAttributes = File.GetAttributes(child);
                if (childAttributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new RecoveryIOException($"目录内包含链接或挂载点：{child}",
                        $"The folder contains a link or mount point at {child}.");
                if (childAttributes.HasFlag(FileAttributes.Directory))
                    pending.Push(child);
            }
        }
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static RegistryKey OpenHive(LeftoverItem item)
    {
        if (item.Hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine)
            || item.View is null || !Enum.IsDefined(item.View.Value)
            || !item.Target.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase)
            || item.Target.Split('\\').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..")
            || item.Target.Equals(@"Software\Microsoft", StringComparison.OrdinalIgnoreCase)
            || item.Kind == LeftoverKind.RegistryValue && item.ValueName is null)
            throw new RecoveryIOException("项目超出已支持的注册表范围。",
                "The item is outside the supported scope of the registry.");
        return RegistryKey.OpenBaseKey(item.Hive.Value, item.View.Value);
    }

    private static RegistryTree Snapshot(RegistryKey key, RegistryView view, int depth = 0)
    {
        if (depth >= 80)
            throw new RecoveryIOException("注册表结构过深，无法安全备份。",
                "The registry tree is too deep to back up safely.");
        var values = key.GetValueNames().Order(PathComparer).Select(name => RegistryData.Read(key, name)).ToArray();
        var children = new Dictionary<string, RegistryTree>(PathComparer);
        foreach (var name in key.GetSubKeyNames())
        {
            EnsurePlainRegistryPath(key, name, view);
            using var child = key.OpenSubKey(name) ?? throw new RecoveryIOException("注册表项在读取过程中发生变化。",
                "The registry key changed while being read.");
            children.Add(name, Snapshot(child, view, depth + 1));
        }
        return new RegistryTree(values, children);
    }

    private static bool TreesEqual(RegistryTree first, RegistryTree second) =>
        first.Values.Length == second.Values.Length &&
        first.Children.Count == second.Children.Count &&
        first.Values.Zip(second.Values).All(pair => PathComparer.Equals(pair.First.Name, pair.Second.Name)
            && pair.First.Kind == pair.Second.Kind && pair.First.Data.SequenceEqual(pair.Second.Data)) &&
        first.Children.All(pair => second.Children.TryGetValue(pair.Key, out var child) && TreesEqual(pair.Value, child));

    private static void WriteTree(RegistryKey key, RegistryTree tree)
    {
        foreach (var value in tree.Values)
            value.Write(key);
        foreach (var (name, subtree) in tree.Children)
        {
            using var child = key.CreateSubKey(name, writable: true);
            WriteTree(child, subtree);
        }
    }

    private static bool IsFileSystem(LeftoverItem item) => item.Kind is LeftoverKind.File or LeftoverKind.Directory;

    private static bool IsRegistration(LeftoverItem item, InstalledApp app) =>
        item.Kind == LeftoverKind.RegistryKey && item.Hive == app.Hive && item.View == app.View &&
        PathComparer.Equals(item.Target, app.RegistryPath);

    private static bool ItemMatches(LeftoverItem first, LeftoverItem second) =>
        PathComparer.Equals(ItemIdentity(first), ItemIdentity(second)) &&
        first.Confidence == second.Confidence && first.EvidenceKind == second.EvidenceKind &&
        PathComparer.Equals(first.EvidencePath, second.EvidencePath) &&
        (first.EvidenceKind != LeftoverEvidence.Legacy || PathComparer.Equals(first.StoredEvidence, second.StoredEvidence));

    private static string ItemIdentity(LeftoverItem item) => $"{item.Kind}|{item.Hive}|{item.View}|{item.Target}|{item.ValueName}";

    private static void EnsurePlainRegistryPath(RegistryKey root, string path, RegistryView view)
    {
        const int openLink = 8;
        const int queryValue = 1;
        const int registryLink = 6;
        var prefix = "";
        foreach (var component in path.Split('\\'))
        {
            prefix = prefix.Length == 0 ? component : $@"{prefix}\{component}";
            var result = RegOpenKeyEx(root.Handle, prefix, openLink, queryValue | (int)view, out var key);
            using (key)
            {
                if (result is 2 or 3)
                    return;
                if (result != 0)
                {
                    var detail = new Win32Exception(result).Message;
                    throw new RecoveryIOException($"无法验证注册表项 {prefix}。{detail}",
                        $"Could not verify registry key {prefix}. {detail}");
                }
                uint size = 0;
                var queryResult = RegQueryValueEx(key, "SymbolicLinkValue", IntPtr.Zero, out var kind, IntPtr.Zero, ref size);
                if (queryResult == 0 && kind == registryLink)
                    throw new RecoveryIOException($"注册表路径包含符号链接：{prefix}",
                        $"The registry path contains a symbolic link at {prefix}.");
                if (queryResult is not (0 or 2))
                {
                    var detail = new Win32Exception(queryResult).Message;
                    throw new RecoveryIOException($"无法验证注册表项 {prefix}。{detail}",
                        $"Could not verify registry key {prefix}. {detail}");
                }
            }
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, int options, int access, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out int type, IntPtr data, ref uint size);

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string name, int reserved, string? keyClass,
        int options, int access, IntPtr security, out SafeRegistryHandle result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegRenameKey(SafeRegistryHandle key, string? oldName, string newName);

    [DllImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteTree(SafeRegistryHandle key, string? name);

    [DllImport("advapi32.dll", EntryPoint = "RegDeleteKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteKeyEx(SafeRegistryHandle key, string name, int view, int reserved);
}
