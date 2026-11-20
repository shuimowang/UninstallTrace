using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeepUninstaller.Services;

public enum LeftoverKind { File, Directory, RegistryKey, RegistryValue }

public enum LeftoverConfidence { Strong, Possible }

public enum LeftoverEvidence
{
    Legacy,
    InstallLocationWithIcon,
    InstallLocationMatchesName,
    IconDirectoryMatchesName,
    ProductFolderMatchesName,
    PublisherFolderMatchesName,
    UninstallRegistration,
    SoftwareKeyMatchesName,
    PublisherKeyMatchesName,
    AppPath,
    StartupCommand,
    Shortcut,
    CorroboratedInstallerName
}

public sealed record LeftoverItem(
    LeftoverKind Kind,
    string Target,
    [property: JsonPropertyName("Evidence")] string StoredEvidence,
    LeftoverConfidence Confidence,
    RegistryHive? Hive = null,
    RegistryView? View = null,
    string? ValueName = null,
    LeftoverEvidence EvidenceKind = LeftoverEvidence.Legacy,
    string? EvidencePath = null)
{
    internal LeftoverItem(LeftoverKind kind, string target, LeftoverEvidence evidence, LeftoverConfidence confidence,
        RegistryHive? hive = null, RegistryView? view = null, string? valueName = null, string? evidencePath = null)
        : this(kind, target, "", confidence, hive, view, valueName, evidence, evidencePath)
    {
        StoredEvidence = Evidence;
    }

    [JsonIgnore]
    public string Evidence => EvidenceKind switch
    {
        LeftoverEvidence.InstallLocationWithIcon => Localization.Text("安装登记路径与应用图标的程序位置一致",
            "The registered install location contains the executable referenced by the app icon."),
        LeftoverEvidence.InstallLocationMatchesName => Localization.Text("安装登记路径与应用名称精确匹配",
            "The registered install folder exactly matches the app name."),
        LeftoverEvidence.IconDirectoryMatchesName => Localization.Text("应用图标的程序位于与应用名称精确匹配的目录",
            "The executable referenced by the app icon is in a folder that exactly matches the app name."),
        LeftoverEvidence.ProductFolderMatchesName => Localization.Text("标准应用目录下的文件夹名称与应用精确匹配",
            "The folder name in a standard application directory exactly matches the app name."),
        LeftoverEvidence.PublisherFolderMatchesName => Localization.Text("发布者目录中的子目录与应用名称精确匹配",
            "A folder under the publisher's directory exactly matches the app name."),
        LeftoverEvidence.UninstallRegistration => Localization.Text("与应用安装登记完全一致",
            "This is the app's exact uninstall registration."),
        LeftoverEvidence.SoftwareKeyMatchesName => Localization.Text("软件设置项名称与应用精确匹配",
            "The software settings key exactly matches the app name."),
        LeftoverEvidence.PublisherKeyMatchesName => Localization.Text("发布者软件设置项下的子项与应用精确匹配",
            "A key under the publisher's software settings exactly matches the app name."),
        LeftoverEvidence.AppPath => Localization.Text($"程序路径登记指向 {EvidencePath}",
            $"The registered app path points to {EvidencePath}."),
        LeftoverEvidence.StartupCommand => Localization.Text($"启动命令直接指向 {EvidencePath}",
            $"The startup command directly points to {EvidencePath}."),
        LeftoverEvidence.Shortcut => Localization.Text($"快捷方式直接指向 {EvidencePath}",
            $"The shortcut directly points to {EvidencePath}."),
        LeftoverEvidence.CorroboratedInstallerName => Localization.Text(
            "名称与安装目录佐证的应用名称一致，已略去登记名称末尾的架构、安装范围或版本信息。",
            "The name matches the product name confirmed by the install folder after removing installer qualifiers."),
        _ => StoredEvidence
    };

    public bool IsRecommended => Confidence == LeftoverConfidence.Strong;
}

public sealed record LeftoverScanResult(IReadOnlyList<LeftoverItem> Items, IReadOnlyList<string> Warnings);

public static class LeftoverScanner
{
    private const string WowUserUninstallRoot = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string RunRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const uint ShellLinkRawPath = 4;
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex ExecutableEnd = new(@"\.exe(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string InstallerQualifier = @"(?:x64|x86|amd64|arm64|arm|(?:32|64)[ -]?bit|current\s+user|all\s+users|per[ -]user|per[ -]machine)";
    private static readonly Regex InstallerQualifierSuffix = new(
        $@"(?:\s*\(\s*{InstallerQualifier}(?:\s*,\s*{InstallerQualifier})*\s*\)|\s+{InstallerQualifier})+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> ReservedDosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
        "COM8", "COM9", "COM¹", "COM²", "COM³", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8",
        "LPT9", "LPT¹", "LPT²", "LPT³",
    };
    private static readonly string LocalAppData = Folder(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string RoamingAppData = Folder(Environment.SpecialFolder.ApplicationData);
    private static readonly string ProgramData = Folder(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string UserProfile = Folder(Environment.SpecialFolder.UserProfile);
    private static readonly string Windows = Folder(Environment.SpecialFolder.Windows);
    private static readonly string ProgramFiles = Folder(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Folder(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string LocalLow = Under(UserProfile, @"AppData\LocalLow");
    private static readonly string LocalPrograms = Under(LocalAppData, "Programs");
    private static readonly string[] ShortcutRoots = DistinctPaths(
        Folder(Environment.SpecialFolder.StartMenu), Folder(Environment.SpecialFolder.CommonStartMenu),
        Folder(Environment.SpecialFolder.Startup), Folder(Environment.SpecialFolder.CommonStartup),
        Folder(Environment.SpecialFolder.DesktopDirectory), Folder(Environment.SpecialFolder.CommonDesktopDirectory));
    private static readonly string[] ProductRoots = DistinctPaths(
        ProgramFiles, ProgramFilesX86, ProgramData, LocalAppData, RoamingAppData, LocalLow,
        LocalPrograms);
    private static readonly string[] ProfileProductRoots = DistinctPaths(LocalAppData, RoamingAppData, LocalLow);
    private static readonly string[] ProtectedTrees = DistinctPaths(
        Windows, Folder(Environment.SpecialFolder.System), Folder(Environment.SpecialFolder.SystemX86),
        Folder(Environment.SpecialFolder.CommonProgramFiles), Folder(Environment.SpecialFolder.CommonProgramFilesX86),
        Under(ProgramFiles, "WindowsApps"), Under(ProgramFilesX86, "WindowsApps"),
        Under(ProgramFiles, "ModifiableWindowsApps"), Under(ProgramFilesX86, "ModifiableWindowsApps"),
        Under(ProgramFiles, "Windows Defender"), Under(ProgramFilesX86, "Windows Defender"),
        Under(ProgramData, "Package Cache"), Under(ProgramData, "Microsoft"),
        Under(LocalAppData, "Packages"), Under(LocalAppData, "Temp"), Under(LocalAppData, "VirtualStore"),
        Under(LocalAppData, @"Microsoft\Windows"), Under(LocalAppData, @"Microsoft\Credentials"),
        Under(LocalAppData, @"Microsoft\TokenBroker"), Under(RoamingAppData, @"Microsoft\Windows"),
        Under(RoamingAppData, @"Microsoft\Protect"), Under(RoamingAppData, @"Microsoft\Credentials"),
        Under(RoamingAppData, @"Microsoft\Vault"), Under(RoamingAppData, @"Microsoft\Crypto"));
    private static readonly string[] ProtectedBoundaries = DistinctPaths(
        [.. ProductRoots, .. ProtectedTrees, .. ShortcutRoots, UserProfile,
            Folder(Environment.SpecialFolder.MyDocuments), Folder(Environment.SpecialFolder.MyPictures),
            Folder(Environment.SpecialFolder.MyMusic), Folder(Environment.SpecialFolder.MyVideos),
            Folder(Environment.SpecialFolder.CommonDocuments), Under(UserProfile, "Downloads")]);
    private static readonly string[] PersonalTrees = DistinctPaths(
        Folder(Environment.SpecialFolder.MyDocuments), Folder(Environment.SpecialFolder.MyPictures),
        Folder(Environment.SpecialFolder.MyMusic), Folder(Environment.SpecialFolder.MyVideos),
        Folder(Environment.SpecialFolder.CommonDocuments), Under(UserProfile, "Downloads"),
        Environment.GetEnvironmentVariable("OneDrive") ?? string.Empty);
    private static readonly HashSet<string> SharedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "app", "apps", "application", "applications", "bin", "classes", "clients", "common", "commonfiles",
        "currentversion", "data", "drivers", "local", "locallow", "microsoft", "packages", "policies", "program",
        "programdata", "programfiles", "programs", "public", "registeredapplications", "roaming", "shared",
        "system", "temp", "tools", "uninstall", "users", "windows", "windowsnt", "wow6432node",
    };

    public static LeftoverScanResult Scan(
        InstalledApp app, IReadOnlyList<InstalledApp> installedApps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(installedApps);
        cancellationToken.ThrowIfCancellationRequested();
        if (app.IsProtected)
        {
            return new LeftoverScanResult([], [Localization.Text("该条目属于系统、更新或禁止移除的组件。",
                "This entry is a system component, an update, or marked as protected from removal.")]);
        }

        return new ScanSession(app, installedApps, cancellationToken).Run();
    }

    public static bool TryValidateFileSystemTarget(string target, out string? reason)
    {
        var path = NormalizePath(target, expandEnvironment: false);
        if (path is null || PathComparer.Equals(path, Path.GetPathRoot(path)))
        {
            reason = Localization.Text("路径必须是本地磁盘上的独立文件或子目录。",
                "The path must identify an individual file or subfolder on a local disk.");
            return false;
        }

        if (ProtectedBoundaries.Any(boundary => IsAtOrUnder(boundary, path)))
        {
            reason = Localization.Text("路径包含系统、用户或通用应用目录。",
                "The path includes a system, user, or common application directory.");
            return false;
        }

        var shortcutFolder = ShortcutRoots.Any(root => IsAtOrUnder(path, root));
        if (shortcutFolder && !PathComparer.Equals(Path.GetExtension(path), ".lnk"))
        {
            reason = Localization.Text("桌面或开始菜单仅允许独立快捷方式。",
                "Only individual shortcuts can be cleaned from the desktop or Start menu.");
            return false;
        }

        var driveRoot = Path.GetPathRoot(path)!;
        var usersRoot = Under(Path.GetPathRoot(Windows) ?? driveRoot, "Users");
        var isProfilePath = IsAtOrUnder(path, UserProfile) || IsAtOrUnder(path, usersRoot);
        if (!shortcutFolder
            && (ProtectedTrees.Any(root => IsAtOrUnder(path, root))
                || PersonalTrees.Any(root => IsAtOrUnder(path, root))
                || (isProfilePath && !ProfileProductRoots.Any(root => IsAtOrUnder(path, root)))
                || IsAtOrUnder(path, Under(driveRoot, "$Recycle.Bin"))
                || IsAtOrUnder(path, Under(driveRoot, "System Volume Information"))))
        {
            reason = Localization.Text("路径位于受保护的系统区域、用户文件区域或共享组件目录。",
                "The path is in a protected system area, personal files area, or shared component directory.");
            return false;
        }

        if (!CheckPathComponents(path, out reason))
        {
            return false;
        }

        if (shortcutFolder && Directory.Exists(path))
        {
            reason = Localization.Text("桌面或开始菜单仅允许独立快捷方式。",
                "Only individual shortcuts can be cleaned from the desktop or Start menu.");
            return false;
        }

        return true;
    }

    private sealed class ScanSession
    {
        private readonly InstalledApp _app;
        private readonly CancellationToken _cancellationToken;
        private readonly HashSet<string> _names;
        private readonly string? _nameOnlyAlias;
        private readonly string? _publisher;
        private readonly string? _displayExecutable;
        private readonly string[] _otherLocations;
        private readonly string[] _otherExecutables;
        private readonly bool _ambiguousName;
        private readonly Dictionary<string, LeftoverItem> _items = new(PathComparer);
        private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
        private string? _installationCandidate;
        private string? _ownedLocation;
        private string? _iconInferredLocation;

        public ScanSession(InstalledApp app, IReadOnlyList<InstalledApp> installedApps, CancellationToken cancellationToken)
        {
            _app = app;
            _cancellationToken = cancellationToken;
            _names = IdentityNames(app);
            var publisher = Identity(app.Publisher);
            _publisher = publisher.Length > 2 && !_names.Contains(publisher)
                && (!IsSharedName(publisher) || publisher == "microsoft") ? publisher : null;
            _displayExecutable = ReadDisplayExecutable(app.DisplayIcon);
            var others = installedApps.Where(other => !PathComparer.Equals(other.Id, app.Id)).ToArray();
            _otherLocations = others.Select(other => NormalizePath(other.InstallLocation))
                .OfType<string>().Distinct(PathComparer).ToArray();
            _otherExecutables = others.SelectMany(other => ReadRecordedExecutables(other.UninstallString)
                    .Concat(ReadRecordedExecutables(other.QuietUninstallString))
                    .Append(ReadDisplayExecutable(other.DisplayIcon)))
                .OfType<string>().Distinct(PathComparer).ToArray();
            _ambiguousName = others.Any(other => _names.Overlaps(IdentityNames(other))
                || _names.Contains(Identity(UndecoratedName(other)))
                || _names.Contains(Identity(other.Publisher)));
            _nameOnlyAlias = _ambiguousName ? null : GetNameOnlyAlias(others, publisher);
        }

        public LeftoverScanResult Run()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ScanInstallLocation();
            ScanDisplayIconLocation();
            ScanProductFolders();
            ScanRegistry();
            ScanShortcuts();
            return new LeftoverScanResult(
                _items.Values.OrderByDescending(item => item.Confidence == LeftoverConfidence.Strong)
                    .ThenBy(item => item.Kind).ThenBy(item => item.Target, PathComparer).ToArray(),
                _warnings.OrderBy(warning => warning, StringComparer.CurrentCulture).ToArray());
        }

        private void ScanInstallLocation()
        {
            var path = NormalizePath(_app.InstallLocation);
            if (path is null)
            {
                if (!string.IsNullOrWhiteSpace(_app.InstallLocation))
                {
                    _warnings.Add(Localization.Text("安装登记中的路径无法确认为本地有效路径，已跳过。",
                        "Skipped the registered install location because it could not be verified as a valid local path."));
                }

                return;
            }

            var leaf = Identity(Path.GetFileName(path));
            var insideProductRoot = ProductRoots.Any(root => IsAtOrUnder(path, root));
            var iconInside = _displayExecutable is not null && IsAtOrUnder(_displayExecutable, path)
                && TryValidateFileSystemTarget(_displayExecutable, out _);
            var directDriveChild = PathComparer.Equals(Path.GetDirectoryName(path), Path.GetPathRoot(path));
            if (!TryValidateFileSystemTarget(path, out var reason) || IsSharedName(leaf)
                || IsShared(path) || (!_names.Contains(leaf) && !iconInside) || (!insideProductRoot && directDriveChild))
            {
                reason ??= Localization.Text("目录可能由多个应用共用，或缺少独占归属证据。",
                    "The folder may be shared by multiple apps or lack evidence that it belongs to this app exclusively.");
                _warnings.Add(Localization.Text($"已跳过安装位置 {path}。{reason}",
                    $"Skipped install location {path}. {reason}"));
                return;
            }

            _installationCandidate = path;
            var settingsRoot = IsAtOrUnder(path, ProgramData)
                || (ProfileProductRoots.Any(root => IsAtOrUnder(path, root)) && !IsAtOrUnder(path, LocalPrograms));
            // Keep the recorded location as evidence after the original uninstaller has removed it.
            if ((insideProductRoot && (!settingsRoot || iconInside)) || (_names.Contains(leaf) && iconInside))
            {
                _ownedLocation = path;
            }

            if (Directory.Exists(path))
            {
                Add(new LeftoverItem(LeftoverKind.Directory, path,
                    iconInside ? LeftoverEvidence.InstallLocationWithIcon : LeftoverEvidence.InstallLocationMatchesName,
                    _ownedLocation is null ? LeftoverConfidence.Possible : LeftoverConfidence.Strong));
            }
        }

        private void ScanDisplayIconLocation()
        {
            if (_ambiguousName || _displayExecutable is null || NormalizePath(_app.InstallLocation) is not null
                || !TryValidateFileSystemTarget(_displayExecutable, out _))
            {
                return;
            }

            for (var path = Path.GetDirectoryName(_displayExecutable);
                path is not null && ProductRoots.Any(root => IsAtOrUnder(path, root));
                path = Path.GetDirectoryName(path))
            {
                if (!_names.Contains(Identity(Path.GetFileName(path))) || !TryValidateFileSystemTarget(path, out _)
                    || IsShared(path))
                {
                    continue;
                }

                _iconInferredLocation = path;
                _installationCandidate = path;
                if (Directory.Exists(path))
                {
                    Add(new LeftoverItem(LeftoverKind.Directory, path,
                        LeftoverEvidence.IconDirectoryMatchesName, LeftoverConfidence.Possible));
                }

                break;
            }
        }

        private void ScanProductFolders()
        {
            if (_ambiguousName || (_names.Count == 0 && _nameOnlyAlias is null))
            {
                return;
            }

            foreach (var root in ProductRoots)
            {
                foreach (var directory in EnumerateDirectories(root))
                {
                    var name = Identity(Path.GetFileName(directory));
                    if (MatchesNameOnly(name))
                    {
                        AddProductFolder(directory, NameEvidence(name, LeftoverEvidence.ProductFolderMatchesName));
                    }

                    if (name == _publisher)
                    {
                        foreach (var child in EnumerateDirectories(directory))
                        {
                            var childName = Identity(Path.GetFileName(child));
                            if (MatchesNameOnly(childName))
                            {
                                AddProductFolder(child, NameEvidence(childName, LeftoverEvidence.PublisherFolderMatchesName));
                            }
                        }
                    }
                }
            }
        }

        private IEnumerable<string> EnumerateDirectories(string path)
        {
            if (!CheckPathComponents(path, out _) || !Directory.Exists(path))
            {
                return [];
            }

            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var directories = new List<string>();
                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    directories.Add(directory);
                }

                return directories;
            }
            catch (Exception exception) when (IsAccessError(exception))
            {
                _warnings.Add(Localization.Text($"无法读取目录 {path}。{exception.Message}",
                    $"Could not read folder {path}. {exception.Message}"));
                return [];
            }
        }

        private void AddProductFolder(string path, LeftoverEvidence evidence)
        {
            if (!TryValidateFileSystemTarget(path, out _) || IsShared(path)
                || (_installationCandidate is not null && !PathComparer.Equals(path, _installationCandidate)
                    && (IsAtOrUnder(_installationCandidate, path) || IsAtOrUnder(path, _installationCandidate))))
            {
                return;
            }

            Add(new LeftoverItem(LeftoverKind.Directory, path, evidence, LeftoverConfidence.Possible));
        }

        private void ScanRegistry()
        {
            if (_app.Hive is RegistryHive.CurrentUser or RegistryHive.LocalMachine)
            {
                using var registeredHive = RegistryKey.OpenBaseKey(_app.Hive, _app.View);
                ScanUninstallKey(registeredHive, _app.Hive, _app.View);
            }

            ScanRegistryView(RegistryHive.CurrentUser, RegistryView.Default, scanAppPaths: true);
            if (Environment.Is64BitOperatingSystem)
            {
                ScanRegistryView(RegistryHive.LocalMachine, RegistryView.Registry64, scanAppPaths: true);
                ScanRegistryView(RegistryHive.LocalMachine, RegistryView.Registry32, scanAppPaths: false);
            }
            else
            {
                ScanRegistryView(RegistryHive.LocalMachine, RegistryView.Default, scanAppPaths: true);
            }
        }

        private void ScanRegistryView(RegistryHive hive, RegistryView view, bool scanAppPaths)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            using var root = RegistryKey.OpenBaseKey(hive, view);
            if (!_ambiguousName && (_names.Count > 0 || _nameOnlyAlias is not null))
            {
                ScanSoftwareKeys(root, hive, view);
            }

            if (_ownedLocation is not null || _displayExecutable is not null)
            {
                // Windows shares App Paths in both machine views, while Run can be redirected.
                if (scanAppPaths)
                {
                    ScanAppPaths(root, hive, view);
                }

                ScanStartup(root, hive, view, RunRoot);
                ScanStartup(root, hive, view, RunOnceRoot);
            }
        }

        private void ScanUninstallKey(RegistryKey root, RegistryHive hive, RegistryView view)
        {
            var expectedPath = $"{InstalledApps.UninstallRegistryPath}\\{_app.KeyName}";
            var userWowPath = $"{WowUserUninstallRoot}\\{_app.KeyName}";
            if (_app.KeyName.Length == 0 || _app.KeyName.Contains('\\')
                || (!PathComparer.Equals(_app.RegistryPath, expectedPath)
                    && (hive != RegistryHive.CurrentUser || !PathComparer.Equals(_app.RegistryPath, userWowPath))))
            {
                return;
            }

            ReadRegistry(root, _app.RegistryPath, key =>
            {
                var currentName = key.GetValue("DisplayName") as string;
                if (PathComparer.Equals(currentName?.Trim(), _app.Name.Trim()))
                {
                    Add(new LeftoverItem(LeftoverKind.RegistryKey, _app.RegistryPath, LeftoverEvidence.UninstallRegistration,
                        LeftoverConfidence.Strong, hive, view));
                }
            });
        }

        private void ScanSoftwareKeys(RegistryKey root, RegistryHive hive, RegistryView view)
        {
            ReadRegistry(root, "SOFTWARE", software =>
            {
                foreach (var name in software.GetSubKeyNames())
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var identity = Identity(name);
                    var path = $"SOFTWARE\\{name}";
                    if (MatchesNameOnly(identity))
                    {
                        Add(new LeftoverItem(LeftoverKind.RegistryKey, path, NameEvidence(identity, LeftoverEvidence.SoftwareKeyMatchesName),
                            LeftoverConfidence.Possible, hive, view));
                    }

                    if (identity != _publisher)
                    {
                        continue;
                    }

                    ReadRegistry(root, path, publisher =>
                    {
                        foreach (var appKey in publisher.GetSubKeyNames())
                        {
                            _cancellationToken.ThrowIfCancellationRequested();
                            var keyName = Identity(appKey);
                            if (MatchesNameOnly(keyName))
                            {
                                Add(new LeftoverItem(LeftoverKind.RegistryKey, $"{path}\\{appKey}",
                                    NameEvidence(keyName, LeftoverEvidence.PublisherKeyMatchesName), LeftoverConfidence.Possible, hive, view));
                            }
                        }
                    });
                }
            });
        }

        private void ScanAppPaths(RegistryKey root, RegistryHive hive, RegistryView view)
        {
            ReadRegistry(root, AppPathsRoot, appPaths =>
            {
                foreach (var name in appPaths.GetSubKeyNames())
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var path = $"{AppPathsRoot}\\{name}";
                    ReadRegistry(root, path, key =>
                    {
                        var executable = ReadDirectExecutable(key.GetValue(string.Empty) as string);
                        if (OwnsExecutable(executable) && PathComparer.Equals(name, Path.GetFileName(executable)))
                        {
                            Add(new LeftoverItem(LeftoverKind.RegistryKey, path, LeftoverEvidence.AppPath,
                                LeftoverConfidence.Strong, hive, view, evidencePath: executable));
                        }
                    });
                }
            });
        }

        private void ScanStartup(RegistryKey root, RegistryHive hive, RegistryView view, string path)
        {
            ReadRegistry(root, path, key =>
            {
                foreach (var name in key.GetValueNames())
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var executable = ReadCommandExecutable(key.GetValue(name) as string);
                    if (OwnsExecutable(executable))
                    {
                        Add(new LeftoverItem(LeftoverKind.RegistryValue, path, LeftoverEvidence.StartupCommand,
                            LeftoverConfidence.Strong, hive, view, name, evidencePath: executable));
                    }
                }
            });
        }

        private void ScanShortcuts()
        {
            if ((_ownedLocation is null && _displayExecutable is null) || _ambiguousName || _names.Count == 0)
            {
                return;
            }

            try
            {
                foreach (var root in ShortcutRoots)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var startup = PathComparer.Equals(root, Folder(Environment.SpecialFolder.Startup))
                        || PathComparer.Equals(root, Folder(Environment.SpecialFolder.CommonStartup));
                    var directories = new List<(string Path, bool Named)> { (root, startup) };
                    var startMenu = PathComparer.Equals(root, Folder(Environment.SpecialFolder.StartMenu))
                        || PathComparer.Equals(root, Folder(Environment.SpecialFolder.CommonStartMenu));
                    if (startMenu)
                    {
                        var programs = Under(root, "Programs");
                        directories.Add((programs, false));
                        foreach (var path in EnumerateDirectories(programs))
                        {
                            var name = Identity(Path.GetFileName(path));
                            if (_names.Contains(name))
                            {
                                directories.Add((path, true));
                            }
                            else if (name == _publisher)
                            {
                                directories.AddRange(EnumerateDirectories(path)
                                    .Where(child => _names.Contains(Identity(Path.GetFileName(child))))
                                    .Select(child => (child, true)));
                            }
                        }
                    }

                    foreach (var directory in directories)
                    {
                        if (!CheckPathComponents(directory.Path, out _) || !Directory.Exists(directory.Path))
                        {
                            continue;
                        }

                        foreach (var file in Directory.EnumerateFiles(directory.Path, "*.lnk"))
                        {
                            _cancellationToken.ThrowIfCancellationRequested();
                            if ((!directory.Named && !_names.Contains(Identity(Path.GetFileNameWithoutExtension(file))))
                                || !TryValidateFileSystemTarget(file, out _))
                            {
                                continue;
                            }

                            ReadShortcut(file);
                        }
                    }
                }
            }
            catch (Exception exception) when (IsAccessError(exception) || exception is COMException)
            {
                _warnings.Add(Localization.Text($"部分快捷方式无法扫描。{exception.Message}",
                    $"Some shortcuts could not be scanned. {exception.Message}"));
            }
        }

        private void ReadShortcut(string path)
        {
            object? shortcut = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ShellLinkClassId);
                shortcut = type is null ? null : Activator.CreateInstance(type);
                if (shortcut is not IShellLinkPath shellLink || shortcut is not IPersistFile persistFile)
                {
                    return;
                }

                // Read the stored path without resolving the link or starting its target.
                persistFile.Load(path, 0);
                var buffer = new StringBuilder(260);
                var result = shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, ShellLinkRawPath);
                var executable = result == 0 ? ReadDirectExecutable(buffer.ToString()) : null;
                if (OwnsExecutable(executable))
                {
                    Add(new LeftoverItem(LeftoverKind.File, path, LeftoverEvidence.Shortcut,
                        LeftoverConfidence.Strong, evidencePath: executable));
                }
            }
            catch (Exception exception) when (IsAccessError(exception) || exception is COMException)
            {
                _warnings.Add(Localization.Text($"无法读取快捷方式 {path}。{exception.Message}",
                    $"Could not read shortcut {path}. {exception.Message}"));
            }
            finally
            {
                if (shortcut is not null && Marshal.IsComObject(shortcut))
                {
                    Marshal.ReleaseComObject(shortcut);
                }
            }
        }

        private void ReadRegistry(RegistryKey root, string path, Action<RegistryKey> action)
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                using var key = root.OpenSubKey(path, writable: false);
                if (key is not null)
                {
                    action(key);
                }
            }
            catch (Exception exception) when (IsAccessError(exception))
            {
                var keyPath = $"{root.Name}\\{path}";
                _warnings.Add(Localization.Text($"无法读取注册表 {keyPath}。{exception.Message}",
                    $"Could not read registry key {keyPath}. {exception.Message}"));
            }
        }

        private bool OwnsExecutable(string? path)
        {
            if (path is null || !TryValidateFileSystemTarget(path, out _) || IsShared(path))
            {
                return false;
            }

            if (_ownedLocation is not null && IsAtOrUnder(path, _ownedLocation))
            {
                return true;
            }

            return !_ambiguousName && PathComparer.Equals(path, _displayExecutable)
                && ProductRoots.Any(root => IsAtOrUnder(path, root))
                && (_iconInferredLocation is not null
                    || _names.Contains(Identity(Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty))));
        }

        private bool IsShared(string path) =>
            _otherLocations.Any(other => IsAtOrUnder(other, path) || IsAtOrUnder(path, other))
            || _otherExecutables.Any(executable => IsAtOrUnder(executable, path));

        private string? GetNameOnlyAlias(InstalledApp[] others, string publisher)
        {
            var alias = Identity(UndecoratedName(_app));
            if (_names.Contains(alias) || !IsUsableName(alias, publisher)
                || NormalizePath(_app.InstallLocation) is not { } installPath
                || Identity(Path.GetFileName(installPath)) != alias
                || _displayExecutable is null || PathComparer.Equals(_displayExecutable, installPath)
                || !IsAtOrUnder(_displayExecutable, installPath)
                || !TryValidateFileSystemTarget(installPath, out _) || !TryValidateFileSystemTarget(_displayExecutable, out _)
                || IsShared(installPath))
            {
                return null;
            }

            // Other editions can own the product's settings even when their install paths are unregistered.
            return others.Any(other => Identity(UndecoratedName(other)) == alias || Identity(other.Name) == alias
                || Identity(other.Publisher) == alias) ? null : alias;
        }

        private bool MatchesNameOnly(string name) => name == _nameOnlyAlias || _names.Contains(name);

        private LeftoverEvidence NameEvidence(string name, LeftoverEvidence exact) =>
            name == _nameOnlyAlias ? LeftoverEvidence.CorroboratedInstallerName : exact;

        private void Add(LeftoverItem item)
        {
            var key = $"{item.Kind}|{item.Hive}|{item.View}|{item.Target}|{item.ValueName}";
            if (!_items.TryGetValue(key, out var current) || (item.IsRecommended && !current.IsRecommended))
            {
                _items[key] = item;
            }
        }
    }

    private static HashSet<string> IdentityNames(InstalledApp app)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var publisher = Identity(app.Publisher);
        void AddName(string name)
        {
            var token = Identity(name);
            if (IsUsableName(token, publisher))
            {
                result.Add(token);
            }
        }

        var name = app.Name.Trim();
        AddName(name);
        if (WithoutVersionSuffix(name, app.Version.Trim()) is { } alias)
        {
            AddName(alias);
        }

        return result;
    }

    private static bool IsUsableName(string token, string publisher) =>
        token.Count(char.IsLetterOrDigit) >= (token.Any(character => character > 127) ? 2 : 3)
        && !IsSharedName(token) && token != publisher && token.Any(char.IsLetter);

    private static string UndecoratedName(InstalledApp app)
    {
        var name = InstallerQualifierSuffix.Replace(app.Name.Trim(), "");
        var withoutVersion = WithoutVersionSuffix(name, app.Version.Trim());
        return withoutVersion is null ? name : InstallerQualifierSuffix.Replace(withoutVersion, "");
    }

    private static string? WithoutVersionSuffix(string name, string version)
    {
        if (version.Length == 0)
            return null;

        foreach (var suffix in new[] { $" {version}", $" v{version}", $" ({version})" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        return null;
    }

    private static string Identity(string value) => new(value
        .Where(character => char.IsLetterOrDigit(character) || character is '+' or '#' or '＋' or '＃')
        .Select(character => character switch { '＋' => '+', '＃' => '#', _ => char.ToLowerInvariant(character) })
        .ToArray());

    private static bool IsSharedName(string identity) => SharedNames.Contains(identity.Replace("+", "").Replace("#", ""));

    private static string? ReadDisplayExecutable(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? ReadDirectExecutable(value[1..end]) : null;
        }

        return ReadDirectExecutable(Regex.Replace(value, @",\s*-?\d+\s*$", string.Empty));
    }

    private static string? ReadDirectExecutable(string? text)
    {
        var path = NormalizePath(text);
        return path is not null && PathComparer.Equals(Path.GetExtension(path), ".exe") ? path : null;
    }

    private static string? ReadCommandExecutable(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? ReadDirectExecutable(value[1..end]) : null;
        }

        var endOfPath = value.AsSpan().IndexOfAny(' ', '\t');
        return ReadDirectExecutable(endOfPath < 0 ? value : value[..endOfPath]);
    }

    private static IEnumerable<string> ReadRecordedExecutables(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1 && (end + 1 == text.Length || char.IsWhiteSpace(text[end + 1]))
                && ReadDirectExecutable(text[1..end]) is { } executable)
            {
                yield return executable;
            }

            yield break;
        }

        // Each absolute prefix is a veto only. Unquoted spaces can leave more than one plausible executable.
        foreach (Match suffix in ExecutableEnd.Matches(text))
        {
            if (ReadDirectExecutable(text[..(suffix.Index + suffix.Length)]) is { } executable)
            {
                yield return executable;
            }
        }
    }

    private static string? NormalizePath(string? value, bool expandEnvironment = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var path = value.Trim().Trim('"');
            var expanded = (expandEnvironment ? Environment.ExpandEnvironmentVariables(path) : path).Replace('/', '\\');
            if (expanded.Length < 3 || !char.IsAsciiLetter(expanded[0]) || expanded[1] != ':' || expanded[2] != '\\'
                || expanded.AsSpan(3).IndexOfAny(":*?\"<>|") >= 0)
            {
                return null;
            }

            var parts = expanded[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(char.IsControl) || ReservedDosNames.Contains(part.Split('.', 2)[0].TrimEnd())
                || Regex.IsMatch(part, @"~\d+(?:\.|$)")))
            {
                return null;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static bool CheckPathComponents(string path, out string? reason)
    {
        var root = Path.GetPathRoot(path);
        if (root is null || !IsDirectDisk(root))
        {
            reason = Localization.Text("路径所在盘符必须直接对应本地文件系统。",
                "The drive letter must map directly to a local file system.");
            return false;
        }

        var components = new Stack<string>();
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            components.Push(current);
        }

        foreach (var component in components)
        {
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = Localization.Text($"路径经过重解析点 {component}。", $"The path crosses a reparse point at {component}.");
                    return false;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
            catch (Exception exception) when (IsAccessError(exception))
            {
                reason = Localization.Text($"无法确认路径安全 {component}。{exception.Message}",
                    $"Could not verify the safety of path {component}. {exception.Message}");
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool IsDirectDisk(string root)
    {
        var driveType = new DriveInfo(root).DriveType;
        if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
        {
            return false;
        }

        var target = new StringBuilder(2048);
        if (QueryDosDevice(root[..2], target, target.Capacity) == 0)
        {
            return false;
        }

        return !target.ToString().StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int maxChars);

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkPath
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
    }

    private static bool IsAtOrUnder(string path, string root) => root.Length > 0
        && (PathComparer.Equals(path, root) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + '\\',
            StringComparison.OrdinalIgnoreCase));

    private static string Folder(Environment.SpecialFolder folder) =>
        NormalizePath(Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify),
            expandEnvironment: false) ?? string.Empty;

    private static string Under(string parent, string child) => parent.Length > 0 ? Path.Combine(parent, child) : string.Empty;

    private static string[] DistinctPaths(params string[] paths) => paths.Where(path => path.Length > 0)
        .Distinct(PathComparer).ToArray();

    private static bool IsAccessError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException;
}
