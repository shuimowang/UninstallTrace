using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace DeepUninstaller.Services;

public enum InstallSizeStatus { Unknown, Measured, Partial }

public enum InstallSizeReason
{
    None,
    MissingInstallLocation,
    UnverifiedOwnership,
    SharedInstallLocation,
    ProtectedLocation,
    UnreadableLocation,
    IncompleteRead
}

public sealed record InstallSizeResult(InstallSizeStatus Status, InstallSizeReason Reason, long? AllocatedBytes,
    string? InstallDirectory, int SkippedEntries);

public sealed class InstallSizeEstimator
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint KnownFolderDontVerify = 0x4000;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid PublicDownloadsFolder = new("3D644C9B-1FB8-4F30-9B45-F670235F79C0");
    private static readonly Regex ExecutableEnd = new(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IconIndex = new(@",\s*-?\d+\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex PackageUninstallArgument = new(
        """(?:^|[\s"])(?:--?uninstall[-_]?app|uninstallapp|--(?:package(?:name|-name|-id)?|app-id))(?:[\s"=:]|$)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> SharedNames = new(Paths)
    {
        "app", "apps", "application", "applications", "bin", "cache", "common", "commonfiles", "data", "desktop",
        "documents", "download", "downloads", "game", "games", "local", "locallow", "packages", "programdata",
        "programfiles", "programfilesx86", "programs", "public", "roaming", "shared", "software", "system", "temp",
        "tools", "users", "windows", "workspace"
    };
    private static readonly HashSet<string> WrapperNames = new(Paths)
    {
        "bash.exe", "cmd.exe", "conhost.exe", "cscript.exe", "dllhost.exe", "dotnet.exe", "java.exe", "javaw.exe",
        "mshta.exe", "msiexec.exe", "node.exe", "powershell.exe", "pwsh.exe", "python.exe", "pythonw.exe",
        "regsvr32.exe", "rundll32.exe", "services.exe", "sh.exe", "svchost.exe", "wscript.exe", "wsl.exe",
        "ldconsole.exe", "dnconsole.exe", "noxconsole.exe", "mumumanager.exe", "hd-player.exe", "hd-runapp.exe",
        "androidemulator.exe", "gameassist.exe", "gameloop.exe"
    };
    private static readonly HashSet<string> AppHosts = new(Paths)
    {
        "gameassist", "gameloop", "ldplayer", "nox", "noxplayer", "bluestacks", "mumu", "mumuplayer"
    };

    private readonly RegisteredPaths[] registrations;
    private readonly string[] downloadFolders;

    public InstallSizeEstimator(IReadOnlyList<InstalledApp> installedApps)
        : this(installedApps, ReadDownloadFolders())
    {
    }

    internal InstallSizeEstimator(IReadOnlyList<InstalledApp> installedApps, IEnumerable<string> downloadFolders)
    {
        ArgumentNullException.ThrowIfNull(installedApps);
        registrations = installedApps.Select(app => new RegisteredPaths(app.Id, NormalizeLocalPath(app.InstallLocation),
            ReadExecutablePrefixes(app.UninstallString).Concat(ReadExecutablePrefixes(app.QuietUninstallString))
                .Append(ReadIconPath(app.DisplayIcon)).OfType<string>().Distinct(Paths).ToArray())).ToArray();
        this.downloadFolders = downloadFolders.Select(NormalizeLocalPath).OfType<string>().Distinct(Paths).ToArray();
    }

    // Run on a worker thread. These are allocated bytes for regular files in the corroborated directory.
    public InstallSizeResult Measure(InstalledApp app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        cancellationToken.ThrowIfCancellationRequested();
        if (app.IsProtected)
            return Unknown(InstallSizeReason.ProtectedLocation);

        var registered = !string.IsNullOrWhiteSpace(app.InstallLocation);
        var icon = ReadIconPath(app.DisplayIcon);
        var path = registered ? NormalizeLocalPath(app.InstallLocation) : InferDirectoryFromIcon(app, icon);
        if (!registered && path is null)
            return Unknown(InstallSizeReason.MissingInstallLocation);
        if (path is null || new DriveInfo(Path.GetPathRoot(path)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) ||
            SharedNames.Contains(Identity(Path.GetFileName(path)).Replace("+", "").Replace("#", "")) ||
            (!registered && path.Split(Path.DirectorySeparatorChar).Any(part => AppHosts.Contains(Identity(part)))) ||
            downloadFolders.Any(folder => IsWithin(path, folder) || IsWithin(folder, path)) ||
            !LeftoverScanner.TryValidateFileSystemTarget(path, out _))
        {
            return Unknown(InstallSizeReason.ProtectedLocation, path);
        }

        if (PackageUninstallArgument.IsMatch(app.UninstallString) || registrations.Any(other => !Paths.Equals(other.Id, app.Id) &&
            ((other.Directory is not null && (IsWithin(path, other.Directory) || IsWithin(other.Directory, path))) ||
                other.References.Any(reference => IsWithin(reference, path)))))
        {
            return Unknown(InstallSizeReason.SharedInstallLocation, path);
        }

        var topLevelFolder = Paths.Equals(Path.GetDirectoryName(path), Path.GetPathRoot(path));
        if ((topLevelFolder && !FolderMatchesName(app, path)) ||
            !HasProductEvidence(app, path, icon, needsSibling: !registered || topLevelFolder))
            return registered ? Unknown(InstallSizeReason.UnverifiedOwnership, path) : Unknown(InstallSizeReason.MissingInstallLocation);

        return MeasureDirectory(path, cancellationToken);
    }

    private static string? InferDirectoryFromIcon(InstalledApp app, string? icon)
    {
        if (icon is null || !IsExecutable(icon) || Path.GetDirectoryName(icon) is not { } directory)
            return null;

        return FolderMatchesName(app, directory) ? directory : null;
    }

    private static bool HasProductEvidence(InstalledApp app, string directory, string? icon, bool needsSibling)
    {
        if (icon is null || !IsExecutable(icon) || !IsWithin(icon, directory) || Paths.Equals(icon, directory) ||
            !LeftoverScanner.TryValidateFileSystemTarget(icon, out _) || !File.Exists(icon))
        {
            return false;
        }

        if (!needsSibling && FolderMatchesName(app, directory))
            return true;

        return Paths.Equals(Path.GetDirectoryName(icon), directory) &&
            UninstallCommand.TryCreate(app, out var command, out _) && !command.IsMsi &&
            !PackageUninstallArgument.IsMatch(command.Arguments) &&
            IsExecutable(command.FileName) && !Paths.Equals(command.FileName, icon) &&
            Paths.Equals(Path.GetDirectoryName(command.FileName), directory) &&
            LeftoverScanner.TryValidateFileSystemTarget(command.FileName, out _) &&
            (!needsSibling || AreDifferentFiles(icon, command.FileName));
    }

    private static bool AreDifferentFiles(string first, string second)
    {
        var nativePath = new StringBuilder(512);
        using var firstHandle = OpenMetadata(first);
        using var secondHandle = OpenMetadata(second);
        return IsPlainObject(firstHandle, first, nativePath, isDirectory: false, out _) &&
            IsPlainObject(secondHandle, second, nativePath, isDirectory: false, out _) &&
            ReadIdentity(firstHandle, FileInfoClass.Id, out var firstId, Marshal.SizeOf<FileIdentity>()) &&
            ReadIdentity(secondHandle, FileInfoClass.Id, out var secondId, Marshal.SizeOf<FileIdentity>()) && firstId != secondId;
    }

    private static bool FolderMatchesName(InstalledApp app, string directory)
    {
        var leaf = Identity(Path.GetFileName(directory));
        return leaf.Length > 2 && Paths.Equals(leaf, Identity(app.Name));
    }

    private static InstallSizeResult MeasureDirectory(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var seenLinks = new HashSet<FileIdentity>();
        var nativePath = new StringBuilder(512);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        long bytes = 0;
        var skipped = 0;
        var readAnything = false;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var directoryHandle = OpenMetadata(directory);
            if (!IsPlainObject(directoryHandle, directory, nativePath, isDirectory: true, out _))
            {
                skipped++;
                continue;
            }

            try
            {
                foreach (var child in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = child.Attributes;
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    {
                        skipped++;
                        continue;
                    }

                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(child.FullName);
                        continue;
                    }

                    if (!TryReadAllocation(child.FullName, nativePath, out var length, out var link) || length > long.MaxValue - bytes)
                    {
                        skipped++;
                        continue;
                    }

                    if (link is null || seenLinks.Add(link.Value))
                        bytes += length;
                    readAnything = true;
                }
                readAnything = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                skipped++;
            }
        }

        if (!readAnything)
            return Unknown(InstallSizeReason.UnreadableLocation, root);
        return new InstallSizeResult(skipped == 0 ? InstallSizeStatus.Measured : InstallSizeStatus.Partial,
            skipped == 0 ? InstallSizeReason.None : InstallSizeReason.IncompleteRead, bytes, root, skipped);
    }

    private static bool TryReadAllocation(string path, StringBuilder nativePath, out long allocated, out FileIdentity? link)
    {
        allocated = 0;
        link = null;
        using var handle = OpenMetadata(path);
        if (!IsPlainObject(handle, path, nativePath, isDirectory: false, out var attributes) ||
            !ReadStandardInfo(handle, FileInfoClass.Standard, out var standard, Marshal.SizeOf<FileStandardInfo>()) ||
            standard.DeletePending != 0)
        {
            return false;
        }

        if (standard.NumberOfLinks > 1)
        {
            if (!ReadIdentity(handle, FileInfoClass.Id, out var id, Marshal.SizeOf<FileIdentity>()))
                return false;
            link = id;
        }

        allocated = standard.AllocationSize;
        if ((attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
        {
            if (!ReadCompressionInfo(handle, FileInfoClass.Compression, out var compression, Marshal.SizeOf<FileCompressionInfo>()))
                return false;
            allocated = compression.CompressedFileSize;
        }

        return allocated >= 0;
    }

    private static bool IsPlainObject(SafeFileHandle handle, string expectedPath, StringBuilder nativePath,
        bool isDirectory, out FileAttributes attributes)
    {
        attributes = 0;
        if (handle.IsInvalid || !ReadAttributeTag(handle, FileInfoClass.AttributeTag, out var info, Marshal.SizeOf<FileAttributeTag>()))
            return false;

        attributes = (FileAttributes)info.Attributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != isDirectory)
            return false;

        // Opening the final component without reparse traversal is not enough if an intermediate directory changed.
        var count = GetFinalPathNameByHandle(handle, nativePath, (uint)nativePath.Capacity, 0);
        if (count >= nativePath.Capacity && count < 32768)
        {
            nativePath.EnsureCapacity((int)count + 1);
            count = GetFinalPathNameByHandle(handle, nativePath, (uint)nativePath.Capacity, 0);
        }

        return count > 0 && count < nativePath.Capacity && Paths.Equals(nativePath.ToString(), @"\\?\" + expectedPath);
    }

    private static SafeFileHandle OpenMetadata(string path) => CreateFile(@"\\?\" + path, FileReadAttributes,
        FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero, FileMode.Open,
        FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

    private static InstallSizeResult Unknown(InstallSizeReason reason, string? directory = null) =>
        new(InstallSizeStatus.Unknown, reason, null, directory, 0);

    private static string? ReadIconPath(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? NormalizeLocalPath(value[1..end]) : null;
        }

        return NormalizeLocalPath(IconIndex.Replace(value, ""));
    }

    private static IEnumerable<string> ReadExecutablePrefixes(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1 && NormalizeLocalPath(text[1..end]) is { } path)
                yield return path;
            yield break;
        }

        foreach (Match match in ExecutableEnd.Matches(text))
        {
            if (NormalizeLocalPath(text[..(match.Index + match.Length)]) is { } path)
                yield return path;
        }
    }

    private static string? NormalizeLocalPath(string path)
    {
        var text = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\' ||
            text.AsSpan(3).IndexOfAny(":*?\"<>|") >= 0 || text.Any(char.IsControl))
        {
            return null;
        }

        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static bool IsExecutable(string path) => Paths.Equals(Path.GetExtension(path), ".exe") &&
        !WrapperNames.Contains(Path.GetFileName(path));

    private static string Identity(string value) => new(value
        .Where(character => char.IsLetterOrDigit(character) || character is '+' or '#' or '＋' or '＃')
        .Select(character => character switch { '＋' => '+', '＃' => '#', _ => character }).ToArray());

    private static bool IsWithin(string path, string root) => Paths.Equals(path, root) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ReadDownloadFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length != 0)
            yield return Path.Combine(profile, "Downloads");

        foreach (var folder in new[] { DownloadsFolder, PublicDownloadsFolder })
        {
            var result = SHGetKnownFolderPath(in folder, KnownFolderDontVerify, IntPtr.Zero, out var pointer);
            try
            {
                if (result == 0 && Marshal.PtrToStringUni(pointer) is { } path)
                    yield return path;
            }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
    }

    private sealed record RegisteredPaths(string Id, string? Directory, string[] References);

    private enum FileInfoClass { Standard = 1, Compression = 8, AttributeTag = 9, Id = 18 }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct FileCompressionInfo { public long CompressedFileSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTag { public uint Attributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadStandardInfo(SafeFileHandle handle, FileInfoClass type, out FileStandardInfo info, int length);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadIdentity(SafeFileHandle handle, FileInfoClass type, out FileIdentity info, int length);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadCompressionInfo(SafeFileHandle handle, FileInfoClass type, out FileCompressionInfo info, int length);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributeTag(SafeFileHandle handle, FileInfoClass type, out FileAttributeTag info, int length);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token, out IntPtr path);
}
