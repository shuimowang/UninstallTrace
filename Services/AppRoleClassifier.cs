using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;

namespace DeepUninstaller.Services;

public enum AppRegistrationRole { Unknown, PcGame, Launcher, AndroidManagedApp }

public enum AppRoleEvidence
{
    None,
    GameAssistUninstall,
    EmulatorUninstall,
    GameRuntimeAndRegistration,
    GameAndSiblingLauncher,
    LauncherComponentAndMarker,
    LauncherAndSiblingGame,
}

public sealed record AppRoleAssessment(AppRegistrationRole Role, AppRoleEvidence EvidenceKind = AppRoleEvidence.None)
{
    public string Label => Role switch
    {
        AppRegistrationRole.PcGame => Localization.Text("PC 游戏本体", "PC game"),
        AppRegistrationRole.Launcher => Localization.Text("启动器", "Launcher"),
        AppRegistrationRole.AndroidManagedApp when EvidenceKind == AppRoleEvidence.GameAssistUninstall =>
            Localization.Text("手游助手内应用", "App in GameAssist"),
        AppRegistrationRole.AndroidManagedApp => Localization.Text("模拟器内应用", "App in Android emulator"),
        _ => string.Empty,
    };

    public string Evidence => EvidenceKind switch
    {
        AppRoleEvidence.GameAssistUninstall => Localization.Text("手游助手的卸载命令与此登记项的 Android 包名一致。",
            "The GameAssist uninstall command names the Android package registered by this entry."),
        AppRoleEvidence.EmulatorUninstall => Localization.Text("模拟器的卸载命令与此登记项的 Android 包名一致。",
            "The emulator uninstall command names the Android package registered by this entry."),
        AppRoleEvidence.GameRuntimeAndRegistration => Localization.Text("应用包含成套游戏引擎文件及独立的游戏安装标记。",
            "This app has packaged game engine files and separate game installation evidence."),
        AppRoleEvidence.GameAndSiblingLauncher => Localization.Text("本地安装包含游戏文件或卸载标记，同一产品还有独立的启动器目录。",
            "The local installation has game files or uninstall evidence and a separate launcher for the same product."),
        AppRoleEvidence.LauncherComponentAndMarker => Localization.Text("登记目录包含启动器组件及其本地端口标记文件。",
            "The registered installation has a launcher component and its local port marker."),
        AppRoleEvidence.LauncherAndSiblingGame => Localization.Text("登记目录中的启动器组件与同一产品的本地游戏安装相对应。",
            "The registered launcher component corresponds to a separate local game installation for the same product."),
        _ => string.Empty,
    };
}

public static class AppRoleClassifier
{
    private const int DirectoryProbeLimit = 128;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex AndroidPackage = new(@"\A[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CommandExecutable = new(@"\A(?:""(?<path>[^""]+)""|(?<path>[A-Za-z]:[\\/][^""\r\n]*?\.exe))(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CommandArgument = new(@"\G\s*(?:""(?<argument>[^""]*)""|(?<argument>[^""\s]+))(?=\s|$)",
        RegexOptions.CultureInvariant);
    private static readonly Regex IconIndex = new(@",\s*-?\d+\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex LauncherPort = new(@"\Alauncher_[A-Za-z0-9_.-]+\.port\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] LauncherSuffixes = ["launcher", "启动器"];
    private static readonly HashSet<string> GenericFamilies = new(StringComparer.Ordinal)
    {
        "app", "apps", "application", "applications", "bin", "client", "game", "games", "launcher", "programs", "tools",
    };

    public static IReadOnlyDictionary<string, AppRoleAssessment> Classify(
        IReadOnlyList<InstalledApp> apps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apps);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new Dictionary<string, AppRoleAssessment>(PathComparer);
        foreach (var group in apps.Where(app => !string.IsNullOrWhiteSpace(app.Name))
                     .GroupBy(app => app.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var registrations = group.DistinctBy(app => app.Id, PathComparer).ToArray();
            if (registrations.Length < 2)
                continue;

            var facts = registrations.Select(app => Probe(app, cancellationToken)).ToArray();
            foreach (var fact in facts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[fact.App.Id] = Assess(fact, facts);
            }
        }

        return results;
    }

    private static RegistrationFacts Probe(InstalledApp app, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = ReadCommand(app.UninstallString);
        var emulator = ManagedAppEvidence(app, command);
        if (emulator != AppRoleEvidence.None)
            return new RegistrationFacts(app, null, null, null, false, false, false, emulator);

        var registered = NormalizePath(app.InstallLocation);
        var icon = ReadIconPath(app.DisplayIcon);
        var root = registered ?? (icon is null ? null : Path.GetDirectoryName(icon));
        if (root is null || !PlainEntryExists(root, isDirectory: true))
            return new RegistrationFacts(app, null, null, null, false, false, false, emulator);

        if (icon is null || !IsWithin(icon, root) || !PlainEntryExists(icon, isDirectory: false))
        {
            var candidate = Path.Combine(root, Path.GetFileName(root) + ".exe");
            icon = PlainEntryExists(candidate, isDirectory: false) ? candidate : null;
        }

        var launcher = FindLauncher(root, icon);
        var hasPort = launcher is not null && HasFile(root, name => LauncherPort.IsMatch(name), cancellationToken);
        var runtime = icon is not null && HasPackagedRuntime(icon, cancellationToken);
        var gameInstall = icon is not null && HasGameInstallEvidence(app, root, command);
        return new RegistrationFacts(app, root, icon, launcher, hasPort, runtime, gameInstall, emulator);
    }

    private static AppRoleAssessment Assess(RegistrationFacts candidate, RegistrationFacts[] group)
    {
        if (candidate.Emulator != AppRoleEvidence.None)
            return new(AppRegistrationRole.AndroidManagedApp, candidate.Emulator);

        var pairedLauncher = (candidate.HasRuntime || candidate.HasGameInstall)
            && group.Any(other => other != candidate && AreLocalSiblings(other, candidate));
        var pairedGame = candidate.Launcher is not null && group.Any(other => other != candidate
            && (other.HasRuntime || other.HasGameInstall) && AreLocalSiblings(candidate, other));
        var game = (candidate.HasRuntime && candidate.HasGameInstall) || pairedLauncher;
        var launcher = candidate.HasLauncherPort || pairedGame;
        if (game == launcher)
            return new(AppRegistrationRole.Unknown);

        if (game)
            return new(AppRegistrationRole.PcGame, candidate.HasRuntime && candidate.HasGameInstall
                ? AppRoleEvidence.GameRuntimeAndRegistration : AppRoleEvidence.GameAndSiblingLauncher);

        return new(AppRegistrationRole.Launcher, candidate.HasLauncherPort
            ? AppRoleEvidence.LauncherComponentAndMarker : AppRoleEvidence.LauncherAndSiblingGame);
    }

    private static AppRoleEvidence ManagedAppEvidence(InstalledApp app, RegisteredCommand? command)
    {
        var keyName = app.KeyName;
        if (command is null || !AndroidPackage.IsMatch(keyName) || !PathComparer.Equals(Path.GetFileName(app.RegistryPath), keyName))
            return AppRoleEvidence.None;

        var executable = Path.GetFileName(command.Executable);
        var parent = Path.GetDirectoryName(command.Executable)!;
        var arguments = command.Arguments;
        if (PathComparer.Equals(executable, "Uninstall.exe") && PathComparer.Equals(Path.GetFileName(parent), "Application")
            && PathComparer.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), "GameAssist")
            && arguments is [var verb, var package] && PathComparer.Equals(verb, "--uninstall-app") && package == keyName)
            return AppRoleEvidence.GameAssistUninstall;

        if ((PathComparer.Equals(executable, "ldconsole.exe") || PathComparer.Equals(executable, "dnconsole.exe"))
            && arguments is [var operation, var selector, var instance, var packageOption, var packageId]
            && PathComparer.Equals(operation, "uninstallapp") && PathComparer.Equals(packageOption, "--packagename")
            && (PathComparer.Equals(selector, "--name") && !string.IsNullOrWhiteSpace(instance)
                || PathComparer.Equals(selector, "--index") && int.TryParse(instance, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            && packageId == keyName)
            return AppRoleEvidence.EmulatorUninstall;

        return AppRoleEvidence.None;
    }

    private static LauncherComponent? FindLauncher(string root, string? executable)
    {
        var leaf = Path.GetFileName(root);
        if (LauncherFamily(leaf) is { } family)
            return new(root, family);

        // Build numbers or an Application subfolder may sit beneath the actual launcher component.
        if ((PathComparer.Equals(leaf, "Application") || leaf.Length > 0 && char.IsAsciiDigit(leaf[0])
                && leaf.All(character => char.IsAsciiDigit(character) || character == '.'))
            && Path.GetDirectoryName(root) is { } parent && LauncherFamily(Path.GetFileName(parent)) is { } parentFamily)
            return new(parent, parentFamily);

        return executable is not null && LauncherFamily(Path.GetFileNameWithoutExtension(executable)) is { } executableFamily
            ? new(root, executableFamily) : null;
    }

    private static bool AreLocalSiblings(RegistrationFacts launcher, RegistrationFacts game)
    {
        if (launcher.Launcher is not { } component || launcher.Executable is null
            || game.Root is not { } gameRoot || game.Executable is null
            || PathComparer.Equals(launcher.Executable, game.Executable) || PathComparer.Equals(launcher.Root, gameRoot))
            return false;

        var parent = Path.GetDirectoryName(component.Path);
        if (parent is null || !PathComparer.Equals(parent, Path.GetDirectoryName(gameRoot)) || PathComparer.Equals(parent, Path.GetPathRoot(parent)))
            return false;

        var gameName = Identity(Path.GetFileName(gameRoot));
        if (component.Family.Length > 0)
            return IsProductFamily(component.Family) && component.Family == gameName;

        var product = Identity(Path.GetFileName(parent));
        return IsProductFamily(product)
            && (Identity(game.App.Name) == product || Identity(Path.GetFileNameWithoutExtension(game.Executable)) == product);
    }

    private static bool HasGameInstallEvidence(InstalledApp app, string root, RegisteredCommand? command)
    {
        if (HasEntry(root, "GameToLauncherConfig.ini") && HasEntry(root, "launcherConfig.json"))
            return true;
        if (command is null || !IsWithin(command.Executable, root) || !LeftoverScanner.TryValidateFileSystemTarget(command.Executable, out _))
            return false;

        var uninstaller = Identity(Path.GetFileNameWithoutExtension(command.Executable));
        if (uninstaller is "uninstallgame" or "gameuninstaller" or "gameuninstall")
            return true;

        var name = Option(command.Arguments, "--name");
        var registryId = Option(command.Arguments, "--regpath");
        var cleanup = NormalizePath(Option(command.Arguments, "--cleanup"));
        return PathComparer.Equals(name?.Trim(), app.Name.Trim()) && !string.IsNullOrWhiteSpace(registryId)
            && registryId.Contains("launcher", StringComparison.OrdinalIgnoreCase)
            && cleanup is not null && IsWithin(cleanup, root) && PathComparer.Equals(Path.GetExtension(cleanup), ".exe");
    }

    private static bool HasPackagedRuntime(string executable, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(executable)!;
        var stem = Path.GetFileNameWithoutExtension(executable);
        if (HasEntry(folder, "UnityPlayer.dll") && HasEntry(folder, stem + "_Data", isDirectory: true))
            return true;
        if (HasEntry(folder, stem + ".pck") || HasEntry(folder, "data.pck"))
            return true;

        if (stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
            && PathComparer.Equals(Path.GetFileName(folder), "Win64")
            && Path.GetDirectoryName(folder) is { } binaries && PathComparer.Equals(Path.GetFileName(binaries), "Binaries")
            && Path.GetDirectoryName(binaries) is { } project)
            return ContainsCookedUnrealContent(project, cancellationToken);

        foreach (var projectRoot in new[] { folder, Path.Combine(folder, stem) })
        {
            var shipping = Path.Combine(projectRoot, "Binaries", "Win64", $"{stem}-Win64-Shipping.exe");
            if (PlainEntryExists(shipping, isDirectory: false) && ContainsCookedUnrealContent(projectRoot, cancellationToken))
                return true;
        }

        return false;
    }

    private static bool ContainsCookedUnrealContent(string projectRoot, CancellationToken cancellationToken) =>
        HasFile(Path.Combine(projectRoot, "Content", "Paks"), name =>
            Path.GetExtension(name).ToLowerInvariant() is ".pak" or ".ucas", cancellationToken);

    private static bool HasEntry(string root, string leaf, bool isDirectory = false) =>
        PlainEntryExists(Path.Combine(root, leaf), isDirectory);

    private static bool PlainEntryExists(string path, bool isDirectory)
    {
        if (!LeftoverScanner.TryValidateFileSystemTarget(path, out _))
            return false;
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.Directory) == isDirectory;
        }
        catch (Exception exception) when (IsAccessFailure(exception)) { return false; }
    }

    private static bool HasFile(string folder, Func<string, bool> matches, CancellationToken cancellationToken)
    {
        if (!PlainEntryExists(folder, isDirectory: true))
            return false;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder).Take(DirectoryProbeLimit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (matches(Path.GetFileName(entry)) && PlainEntryExists(entry, isDirectory: false))
                    return true;
            }
        }
        catch (Exception exception) when (IsAccessFailure(exception)) { }
        return false;
    }

    private static RegisteredCommand? ReadCommand(string text)
    {
        var expanded = Environment.ExpandEnvironmentVariables(text.Trim());
        var executable = CommandExecutable.Match(expanded);
        if (!executable.Success || NormalizePath(executable.Groups["path"].Value) is not { } path
            || !PathComparer.Equals(Path.GetExtension(path), ".exe"))
            return null;

        var tail = expanded[executable.Length..];
        var arguments = new List<string>();
        var consumed = 0;
        foreach (Match match in CommandArgument.Matches(tail))
        {
            arguments.Add(match.Groups["argument"].Value);
            consumed = match.Index + match.Length;
        }
        return tail.AsSpan(consumed).Trim().IsEmpty ? new(path, arguments.ToArray()) : null;
    }

    private static string? Option(string[] arguments, string name)
    {
        string? value = null;
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (!PathComparer.Equals(arguments[index], name))
                continue;
            if (value is not null || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                return null;
            value = arguments[++index];
        }
        return value;
    }

    private static string? ReadIconPath(string icon)
    {
        var value = icon.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            value = end > 1 ? value[1..end] : string.Empty;
        }
        else
            value = IconIndex.Replace(value, string.Empty);
        var path = NormalizePath(value);
        return path is not null && PathComparer.Equals(Path.GetExtension(path), ".exe") ? path : null;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
        if (expanded.Length < 3 || !char.IsAsciiLetter(expanded[0]) || expanded[1] != ':' || expanded[2] != '\\'
            || expanded.AsSpan(3).IndexOfAny(":*?\"<>|") >= 0)
            return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase);

    private static string? LauncherFamily(string name)
    {
        var identity = Identity(name);
        var suffix = LauncherSuffixes.FirstOrDefault(part => identity.EndsWith(part, StringComparison.Ordinal));
        return suffix is not null ? identity[..^suffix.Length] : null;
    }

    private static string Identity(string name) => new(name.Where(character => char.IsLetterOrDigit(character) || character is '+' or '#')
        .Select(char.ToLowerInvariant).ToArray());

    private static bool IsProductFamily(string family) => family.Length >= (family.Any(character => character > 127) ? 2 : 3)
        && family.Any(char.IsLetter) && !GenericFamilies.Contains(family);

    private static bool IsAccessFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;

    private sealed record RegisteredCommand(string Executable, string[] Arguments);
    private sealed record LauncherComponent(string Path, string Family);
    private sealed record RegistrationFacts(InstalledApp App, string? Root, string? Executable, LauncherComponent? Launcher,
        bool HasLauncherPort, bool HasRuntime, bool HasGameInstall, AppRoleEvidence Emulator);
}
