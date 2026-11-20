using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeepUninstaller.Services;

public sealed record ServiceResidue(string ServiceName, string? DisplayName, string ExecutablePath);

public sealed record ServiceResidueReport(bool HasOwnershipEvidence, IReadOnlyList<ServiceResidue> Matches,
    IReadOnlyList<string> Warnings);

public static class ServiceResidueScanner
{
    private const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services";
    private const int Win32OwnProcess = 0x10;
    private const int InteractiveProcess = 0x100;
    private static readonly Regex EnvironmentVariable = new("%([^%]+)%", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> WrapperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe", "services.exe", "dllhost.exe", "rundll32.exe", "regsvr32.exe", "msiexec.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "cscript.exe", "wscript.exe", "mshta.exe", "conhost.exe",
        "bash.exe", "sh.exe", "wsl.exe", "python.exe", "pythonw.exe", "node.exe", "dotnet.exe",
        "java.exe", "javaw.exe", "nssm.exe", "winsw.exe", "srvany.exe"
    };

    internal sealed record ServiceRegistration(string Name, string? DisplayName, int Type, string ImagePath,
        bool ExpandImagePath = false);

    public static ServiceResidueReport Scan(InstalledApp app, IReadOnlyList<InstalledApp> installedApps,
        CancellationToken cancellationToken = default) =>
        Scan(app, installedApps, warnings => ReadRegistry(warnings, cancellationToken), InstalledApps.IsRegistered, cancellationToken);

    internal static ServiceResidueReport ScanRegistrations(InstalledApp app, IReadOnlyList<InstalledApp> installedApps,
        IReadOnlyList<ServiceRegistration> registrations, Func<InstalledApp, bool>? isRegistered = null) =>
        Scan(app, installedApps, _ => registrations, isRegistered, CancellationToken.None);

    private static ServiceResidueReport Scan(InstalledApp app, IReadOnlyList<InstalledApp> installedApps,
        Func<List<string>, IReadOnlyList<ServiceRegistration>> readRegistry, Func<InstalledApp, bool>? isRegistered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(installedApps);
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckRegistration(app, installedApps, isRegistered) is { } registrationWarning)
            return new ServiceResidueReport(false, [], [registrationWarning]);

        var owner = LeftoverScanner.FindCorroboratedInstallLocation(app, installedApps);
        if (owner is null)
            return new ServiceResidueReport(false, [], []);

        var prefix = owner + Path.DirectorySeparatorChar;
        var matches = new List<ServiceResidue>();
        var warnings = new List<string>();
        foreach (var registration in readRegistry(warnings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOwnProcess(registration.Type) || ReadExecutable(registration) is not { } path ||
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !LeftoverScanner.TryValidateFileSystemTarget(path, out _) || Directory.Exists(path))
            {
                continue;
            }

            var displayName = registration.DisplayName?.Trim();
            // Resource references can involve loading a service-supplied DLL. Keep the service's key name instead.
            if (string.IsNullOrEmpty(displayName) || displayName.StartsWith('@') || displayName.Any(char.IsControl))
                displayName = null;

            matches.Add(new ServiceResidue(registration.Name, displayName, path));
        }

        return new ServiceResidueReport(true,
            matches.OrderBy(match => match.ServiceName, StringComparer.OrdinalIgnoreCase).ToArray(), warnings.ToArray());
    }

    private static string? CheckRegistration(InstalledApp app, IReadOnlyList<InstalledApp> installedApps,
        Func<InstalledApp, bool>? isRegistered)
    {
        var live = installedApps.FirstOrDefault(candidate => string.Equals(candidate.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        if (live is not null)
            return live == app ? null : Localization.Text("程序当前的卸载登记与扫描时保存的信息不同，请重新扫描。",
                "The app's current registration differs from the saved scan information. Scan again.");

        try
        {
            return app.RegistryPath.Length != 0 && isRegistered?.Invoke(app) == true
                ? Localization.Text("该卸载登记仍然存在，但无法从当前清单核对其信息。",
                    "The uninstall registration still exists, but its details could not be verified in the current inventory.")
                : null;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return Localization.Text($"无法核对程序的当前卸载登记。{exception.Message}",
                $"Could not verify the app's current uninstall registration. {exception.Message}");
        }
    }

    private static IReadOnlyList<ServiceRegistration> ReadRegistry(List<string> warnings, CancellationToken cancellationToken)
    {
        var registrations = new List<ServiceRegistration>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var services = root.OpenSubKey(ServiceRegistryPath, writable: false);
            if (services is null)
            {
                warnings.Add(Localization.Text("找不到 Windows 服务登记。", "The Windows service registrations could not be found."));
                return registrations;
            }

            var unreadable = 0;
            var missingImagePath = 0;
            foreach (var name in services.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var service = services.OpenSubKey(name, writable: false);
                    if (service?.GetValue("Type") is not int type || !IsOwnProcess(type))
                    {
                        continue;
                    }

                    if (service.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string imagePath
                        || string.IsNullOrWhiteSpace(imagePath))
                    {
                        missingImagePath++;
                        continue;
                    }

                    var displayName = service.GetValue("DisplayName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    registrations.Add(new ServiceRegistration(name, displayName, type, imagePath,
                        service.GetValueKind("ImagePath") == RegistryValueKind.ExpandString));
                }
                catch (Exception exception) when (IsReadFailure(exception))
                {
                    unreadable++;
                }
            }

            if (unreadable != 0)
                warnings.Add(Localization.Text($"有 {unreadable} 条服务登记无法读取。", $"Could not read {unreadable} service registrations."));
            if (missingImagePath != 0)
                warnings.Add(Localization.Text($"有 {missingImagePath} 条独立进程服务登记缺少可读的可执行文件路径。",
                    $"No executable path was available for {missingImagePath} own-process service registrations."));
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            warnings.Add(Localization.Text($"无法读取 Windows 服务登记。{exception.Message}",
                $"Could not read the Windows service registrations. {exception.Message}"));
        }

        return registrations;
    }

    private static string? ReadExecutable(ServiceRegistration registration)
    {
        var raw = registration.ImagePath;
        if (raw.Length >= 32767 || raw.Any(character => char.IsControl(character) && character != '\t'))
            return null;
        var command = raw.Trim(' ', '\t');
        if (command.Length == 0)
            return null;

        var quoted = command[0] == '"';
        string path;
        if (quoted)
        {
            var closing = command.IndexOf('"', 1);
            if (closing < 2 || (closing + 1 < command.Length && command[closing + 1] is not (' ' or '\t')))
                return null;
            path = command[1..closing];
        }
        else
        {
            // SCM requires quotes around paths containing spaces. An unquoted prefix is the only unambiguous target.
            var separator = command.AsSpan().IndexOfAny(' ', '\t');
            path = separator < 0 ? command : command[..separator];
        }

        if (registration.ExpandImagePath)
            path = EnvironmentVariable.Replace(path, match => MachineFolder(match.Groups[1].Value) ?? match.Value);

        path = path.Replace('/', '\\');
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.Contains('%') || (!quoted && path.Any(char.IsWhiteSpace)) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || WrapperNames.Contains(Path.GetFileName(path)))
        {
            return null;
        }

        // Match only normalized, unambiguous path spellings. The scanner validates existing components separately.
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(full, path, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static string? MachineFolder(string variable) => variable.ToUpperInvariant() switch
    {
        "WINDIR" or "SYSTEMROOT" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "PROGRAMFILES" or "PROGRAMW6432" when Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem
            => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "PROGRAMFILES(X86)" when Environment.Is64BitOperatingSystem
            => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "PROGRAMDATA" or "ALLUSERSPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        _ => null
    };

    private static bool IsOwnProcess(int type) => (type & ~InteractiveProcess) == Win32OwnProcess;

    private static bool IsReadFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or SecurityException;
}
