using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepUninstaller.Services;

public sealed record StoreApp(string Name, string FamilyName, string FullName, string Version, string DisplayName, string Protection)
{
    public bool CanRemove => Protection is "" && StoreApps.IsValidPackageFullName(FullName);
    public DateTimeOffset? InstalledOn { get; init; }
}

public enum StoreRemovalResult { Removed, SelectionChanged, Protected, StillRegistered }

public static partial class StoreApps
{
    private const string Setup = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $env:PSModulePath = "$PSHOME\Modules"
        $PSModuleAutoLoadingPreference = 'None'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

        function Read-PackageGroups {
            Appx\Get-AppxPackage -PackageTypeFilter Main,Bundle |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_.PackageFamilyName) } |
                Group-Object -Property PackageFamilyName
        }

        function Get-PreferredPackage($group) {
            $group.Group |
                Sort-Object -Property @{Expression='IsBundle';Descending=$true}, @{Expression='Version';Descending=$true}, PackageFullName |
                Select-Object -First 1
        }

        function Get-Protection($group) {
            foreach ($package in $group.Group) {
                if ($package.Name -match '^(Microsoft\.(WindowsStore|StorePurchaseApp|DesktopAppInstaller|WinGet\.Client|SecHealthUI|Windows\.SecHealthUI|WindowsDefender|WindowsSecurity|SmartScreen|WindowsBackup)(\.|$)|Microsoft\.(WindowsAppRuntime|VCLibs|NET\.Native|UI\.Xaml)\.|MicrosoftWindows\.Client\.)') {
                    return 'Component'
                }
                if ($package.NonRemovable -ne $false -or $package.IsFramework -ne $false -or $package.IsResourcePackage -ne $false) {
                    return 'Component'
                }
                if ([string]$package.SignatureKind -ne 'Store') { return 'Signature' }
                if ($package.IsPartiallyStaged -ne $false -or [string]$package.Status -ne 'Ok') { return 'Status' }
            }
            return ''
        }

        try {
            Import-Module "$PSHOME\Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1"
            Import-Module "$PSHOME\Modules\Appx\Appx.psd1"

        """;

    private const string ReadBody = """
            $names = @{}
            try {
                Import-Module "$PSHOME\Modules\StartLayout\StartLayout.psd1"
                foreach ($startApp in (StartLayout\Get-StartApps)) {
                    $appId = [string]$startApp.AppID
                    $separator = $appId.IndexOf('!')
                    if ($separator -gt 0) {
                        $family = $appId.Substring(0, $separator)
                        if (-not $names.ContainsKey($family) -and -not [string]::IsNullOrWhiteSpace($startApp.Name)) {
                            $names[$family] = [string]$startApp.Name
                        }
                    }
                }
            } catch {
                # Start menu names are optional. The package name is always available.
            }

            $dates = @{}
            try {
                $manager = [Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime]::new()
                $packageTypes = [Windows.Management.Deployment.PackageTypes,Windows.Management.Deployment,ContentType=WindowsRuntime]
                $appTypes = $packageTypes::Main -bor $packageTypes::Bundle
                $epoch = [DateTimeOffset]::FromFileTime(0)
                foreach ($registered in $manager.FindPackagesForUserWithPackageTypes('', $appTypes)) {
                    try {
                        $date = $registered.InstalledDate
                        if ($date -is [DateTimeOffset] -and $date -gt $epoch) {
                            $dates[[string]$registered.Id.FullName] = $date.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
                        }
                    } catch {
                        # Some individual packages cannot provide deployment metadata.
                    }
                }
            } catch {
                # Optional dates must not prevent use of the Appx inventory or removal workflow.
            }

            $rows = foreach ($group in (Read-PackageGroups)) {
                $package = Get-PreferredPackage $group
                $name = [string]$package.Name
                $family = [string]$package.PackageFamilyName
                $fullName = [string]$package.PackageFullName
                $version = [string]$package.Version
                $displayName = $names[$family]
                if ([string]::IsNullOrWhiteSpace($displayName)) { $displayName = $name }
                $installedOn = $dates[$fullName]
                if ($null -eq $installedOn -and $package.IsBundle -eq $true) {
                    $main = @($group.Group | Where-Object {
                        $_.IsBundle -eq $false -and $_.PackageFamilyName -eq $family -and [string]$_.Version -eq $version
                    })
                    if ($main.Count -eq 1) { $installedOn = $dates[[string]$main[0].PackageFullName] }
                }
                [pscustomobject]@{
                    Name = $name
                    FamilyName = $family
                    FullName = $fullName
                    Version = $version
                    DisplayName = $displayName
                    Protection = Get-Protection $group
                    InstalledOn = $installedOn
                }
            }
            [Console]::WriteLine((ConvertTo-Json -InputObject @($rows) -Depth 2 -Compress))

        """;

    private const string RemoveBody = """
            $requested = [Console]::ReadLine()
            if ([string]::IsNullOrWhiteSpace($requested)) { throw 'Missing package identity.' }
            $selected = $null
            $family = $null
            foreach ($group in (Read-PackageGroups)) {
                $candidate = Get-PreferredPackage $group
                if ([string]::Equals($candidate.PackageFullName, $requested, [StringComparison]::OrdinalIgnoreCase)) {
                    if ((Get-Protection $group) -ne '') { [Console]::WriteLine('PROTECTED'); return }
                    $selected = $candidate
                    $family = [string]$candidate.PackageFamilyName
                    break
                }
            }
            if ($null -eq $selected) { [Console]::WriteLine('CHANGED'); return }

            Appx\Remove-AppxPackage -Package $selected.PackageFullName -Confirm:$false
            foreach ($group in (Read-PackageGroups)) {
                if ([string]::Equals($group.Name, $family, [StringComparison]::OrdinalIgnoreCase)) {
                    [Console]::WriteLine('STILL_REGISTERED')
                    return
                }
            }
            [Console]::WriteLine('REMOVED')

        """;

    private const string End = """
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    public static async Task<IReadOnlyList<StoreApp>> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var json = await RunAsync(Setup + ReadBody + End, input: null, timeout.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<StoreApp[]>(json)
                ?? throw new IOException(Localization.Text("Windows 未返回应用包清单。", "Windows returned no package inventory."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(Localization.Text("读取商店应用超时。", "Reading Store apps timed out."));
        }
    }

    public static async Task<StoreRemovalResult> RemoveAsync(string packageFullName)
    {
        if (!IsValidPackageFullName(packageFullName))
            throw new ArgumentException(Localization.Text("应用包标识无效。", "The package identity is invalid."), nameof(packageFullName));

        var outcome = await RunAsync(Setup + RemoveBody + End, packageFullName, CancellationToken.None).ConfigureAwait(false);
        return outcome.Trim() switch
        {
            "REMOVED" => StoreRemovalResult.Removed,
            "CHANGED" => StoreRemovalResult.SelectionChanged,
            "PROTECTED" => StoreRemovalResult.Protected,
            "STILL_REGISTERED" => StoreRemovalResult.StillRegistered,
            _ => throw new IOException(Localization.Text("Windows 返回了无法识别的卸载结果。", "Windows returned an unrecognized removal result."))
        };
    }

    internal static bool IsValidPackageFullName(string? value) => value is not null && PackageIdentityPattern().IsMatch(value);

    [GeneratedRegex(@"\A[A-Za-z0-9.-]{3,50}_(?:[0-9]{1,5}\.){3}[0-9]{1,5}_(?:x86|x64|arm|arm64|neutral|x86a64)_(?:[A-Za-z0-9.-]{0,30}|~)_[A-Za-z0-9]{13}\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PackageIdentityPattern();

    private static async Task<string> RunAsync(string script, string? input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative")
            : Environment.SystemDirectory;
        var shell = Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(shell))
            throw new FileNotFoundException(Localization.Text("未找到系统 Windows PowerShell。", "The system Windows PowerShell executable was not found."));

        var start = new ProcessStartInfo(shell)
        {
            WorkingDirectory = system,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new IOException(Localization.Text("无法启动系统 Windows PowerShell。", "Windows PowerShell could not be started."));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (input is not null)
            await process.StandardInput.WriteLineAsync(input).ConfigureAwait(false);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            throw;
        }

        var outputText = await output.ConfigureAwait(false);
        var errorText = (await errors.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new IOException(errorText.Length == 0
                ? Localization.Text($"Windows PowerShell 退出码为 {process.ExitCode}。", $"Windows PowerShell exited with code {process.ExitCode}.")
                : errorText);
        }
        return outputText;
    }
}
