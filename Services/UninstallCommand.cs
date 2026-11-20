using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;

namespace DeepUninstaller.Services;

public sealed class UninstallCommand
{
    private static readonly Regex ExecutableSuffix = new(@"\.exe(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MsiProduct = new(
        """^[/\-](?<verb>uninstall|x|i)\s*(?<quote>"?)(?<product>\{[a-f0-9-]{36}\})\k<quote>(?=\s|$)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MsiPatch = new(@"\.msp\b|\bMSIPATCHREMOVE\s*=|\s[/\-]package(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RundllTarget = new(
        """^(?:"(?<module>[^"]+)"|(?<module>[^,"]+?))\s*,\s*(?<entry>[a-z_][a-z0-9_@$?]*|#[0-9]+)(?<tail>\s.*)?$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe"
    };

    private UninstallCommand(string fileName, string arguments, bool isMsi = false)
    {
        FileName = fileName;
        Arguments = arguments;
        IsMsi = isMsi;
    }

    public string FileName { get; }
    public string Arguments { get; }
    public string DisplayText => Arguments.Length == 0 ? $"\"{FileName}\"" : $"\"{FileName}\" {Arguments}";
    public bool IsMsi { get; }

    public static bool TryCreate(InstalledApp app, [NotNullWhen(true)] out UninstallCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        command = null;
        if (app.IsProtected)
        {
            error = Localization.Text("这条注册记录标记为系统组件、更新或禁止卸载。",
                "Windows marks this entry as a system component, update, or protected program.");
            return false;
        }

        var rawCommand = app.UninstallString.Trim();
        var hasRegisteredProduct = Guid.TryParseExact(app.KeyName, "B", out var registeredProduct);

        if (rawCommand.Length == 0)
        {
            if (app.IsMsi && hasRegisteredProduct)
                return TryCreateMsi(registeredProduct, out command, out error);

            error = app.QuietUninstallString.Length == 0
                ? Localization.Text("注册记录没有提供原厂卸载命令。", "The registration does not provide a vendor uninstall command.")
                : Localization.Text("注册记录只提供静默卸载命令。", "The registration provides only a silent uninstall command.");
            return false;
        }

        if (rawCommand.Length >= 32767 || rawCommand.Any(character => char.IsControl(character) && character != '\t') ||
            !TrySplitExecutable(rawCommand, out var executable, out var arguments))
        {
            error = Localization.Text("无法将注册的卸载命令解析为本地可执行文件。",
                "Could not resolve the registered uninstall command to a local executable.");
            return false;
        }

        var executableName = Path.GetFileName(executable);
        if (TryResolveSystemLauncher(executable, "msiexec", out _))
        {
            var match = MsiProduct.Match(arguments);
            if (!match.Success || MsiPatch.IsMatch(arguments) ||
                !Guid.TryParseExact(match.Groups["product"].Value, "B", out var product) ||
                (hasRegisteredProduct && product != registeredProduct) ||
                (match.Groups["verb"].Value.Equals("i", StringComparison.OrdinalIgnoreCase) &&
                    !app.IsMsi && !hasRegisteredProduct))
            {
                error = Localization.Text("无法从卸载命令确认对应的 MSI 产品编号。",
                    "Could not verify the MSI product code in the uninstall command.");
                return false;
            }

            return TryCreateMsi(product, out command, out error);
        }

        if (TryResolveSystemLauncher(executable, "rundll32", out var systemLauncher))
            return TryCreateRundll(systemLauncher, arguments, out command, out error);

        if (ScriptHosts.Contains(executableName))
        {
            error = Localization.Text("原厂卸载命令使用脚本宿主。请从 Windows 设置运行此卸载程序。",
                "The vendor uninstall command uses a script host. Run this uninstaller from Windows Settings.");
            return false;
        }

        if (!TryGetLocalPath(executable, ".exe", out var path) || !File.Exists(path))
        {
            error = Localization.Text("找不到登记的本地卸载程序。", "The registered local uninstaller could not be found.");
            return false;
        }

        command = new UninstallCommand(path, arguments);
        error = null;
        return true;
    }

    public Process Start()
    {
        // Opening the resolved EXE through Windows supports its native UAC manifest.
        var startInfo = new ProcessStartInfo
        {
            FileName = FileName,
            Arguments = Arguments,
            WorkingDirectory = Path.GetDirectoryName(FileName)!,
            UseShellExecute = true
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException(
            Localization.Text("Windows 未返回卸载进程。", "Windows did not return an uninstaller process."));
    }

    private static bool TryCreateMsi(Guid product, [NotNullWhen(true)] out UninstallCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        var installer = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        if (!File.Exists(installer))
        {
            command = null;
            error = Localization.Text("找不到 Windows Installer。", "Windows Installer could not be found.");
            return false;
        }

        // Registered MSI commands often use /I to open maintenance UI. /X targets the same product for removal.
        command = new UninstallCommand(installer, $"/x {product.ToString("B").ToUpperInvariant()} /norestart", isMsi: true);
        error = null;
        return true;
    }

    private static bool TryCreateRundll(string systemLauncher, string arguments,
        [NotNullWhen(true)] out UninstallCommand? command, [NotNullWhen(false)] out string? error)
    {
        command = null;
        var target = RundllTarget.Match(arguments);
        if (!target.Success || !File.Exists(systemLauncher))
        {
            error = Localization.Text("无法识别这个原厂卸载命令的 Rundll32 模块。",
                "Could not identify the Rundll32 module in the vendor uninstall command.");
            return false;
        }

        var module = target.Groups["module"].Value.Trim();
        if (IsBareFileName(module))
            module = Path.Combine(Path.GetDirectoryName(systemLauncher)!, module);

        if (!TryGetLocalPath(module, ".dll", out var modulePath) || !File.Exists(modulePath))
        {
            error = Localization.Text("找不到登记的本地卸载模块。", "The registered local uninstall module could not be found.");
            return false;
        }

        command = new UninstallCommand(systemLauncher,
            $"\"{modulePath}\",{target.Groups["entry"].Value}{target.Groups["tail"].Value}");
        error = null;
        return true;
    }

    private static bool TrySplitExecutable(string text, out string executable, out string arguments)
    {
        executable = arguments = "";
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end <= 1 || (end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1])))
                return false;

            executable = Environment.ExpandEnvironmentVariables(text[1..end]);
            arguments = text[(end + 1)..].TrimStart();
            return true;
        }

        var firstTokenEnd = 0;
        while (firstTokenEnd < text.Length && !char.IsWhiteSpace(text[firstTokenEnd]))
            firstTokenEnd++;

        var firstToken = text[..firstTokenEnd];
        if (IsExecutableName(firstToken, "msiexec") || IsExecutableName(firstToken, "rundll32"))
        {
            executable = firstToken;
            arguments = text[firstTokenEnd..].TrimStart();
            return true;
        }

        var foundFile = false;
        foreach (Match suffix in ExecutableSuffix.Matches(text))
        {
            var pathEnd = suffix.Index + suffix.Length;
            var candidate = Environment.ExpandEnvironmentVariables(text[..pathEnd]);
            if (!TryGetLocalPath(candidate, ".exe", out var path))
                continue;

            var exists = File.Exists(path);
            if (foundFile && exists)
                return false;

            if (executable.Length == 0 || exists)
            {
                executable = path;
                arguments = text[pathEnd..].TrimStart();
            }

            foundFile |= exists;
        }

        return executable.Length != 0;
    }

    private static bool TryResolveSystemLauncher(string executable, string name, [NotNullWhen(true)] out string? path)
    {
        path = null;
        var fileName = name + ".exe";
        var systemPath = Path.Combine(Environment.SystemDirectory, fileName);
        if (IsExecutableName(executable, name))
        {
            path = systemPath;
            return true;
        }

        if (!Path.GetFileName(executable).Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
            !TryGetLocalPath(executable, ".exe", out var recordedPath))
        {
            return false;
        }

        var systemX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        if (!recordedPath.Equals(systemPath, StringComparison.OrdinalIgnoreCase) &&
            (systemX86.Length == 0 ||
                !recordedPath.Equals(Path.Combine(systemX86, fileName), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        path = recordedPath;
        return true;
    }

    private static bool TryGetLocalPath(string text, string extension, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (text.Length < 4 || !char.IsAsciiLetter(text[0]) || text[1] != ':' ||
            (text[2] != '\\' && text[2] != '/') || text.AsSpan(2).IndexOfAny(':', '"', '\0') >= 0 ||
            !text.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(text);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool IsBareFileName(string text) => text.AsSpan().IndexOfAny('\\', '/', ':') < 0;

    private static bool IsExecutableName(string value, string name) =>
        value.Equals(name, StringComparison.OrdinalIgnoreCase) || value.Equals(name + ".exe", StringComparison.OrdinalIgnoreCase);
}
