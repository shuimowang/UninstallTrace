#Requires -Version 7.2
[CmdletBinding()]
param (
    [Parameter(Mandatory)][ValidateSet('SumatraPDF', 'WinMerge')][string] $Product,
    [Parameter(Mandatory)][string] $VerificationDll,
    [Parameter(Mandatory)][string] $ReferenceDll,
    [Parameter(Mandatory)][string] $ReportDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:UNINSTALLTRACE_DISPOSABLE_RUNNER -ne 'github-hosted' -or
    $env:RUNNER_ARCH -ne 'X64' -or ![Environment]::Is64BitProcess -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or !(Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
    throw 'Real installer tests require a disposable GitHub-hosted Windows x64 runner.'
}

function Get-ProductRegistrations([string] $NamePattern) {
    $uninstallPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall'
    $views = @(
        @{ Hive = 'CurrentUser'; View = 'Default'; Path = $uninstallPath }
        @{ Hive = 'CurrentUser'; View = 'Default'; Path = 'Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall' }
        @{ Hive = 'LocalMachine'; View = 'Registry64'; Path = $uninstallPath }
        @{ Hive = 'LocalMachine'; View = 'Registry32'; Path = $uninstallPath }
    )

    foreach ($view in $views) {
        $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey($view.Hive, $view.View)
        try {
            $root = $hive.OpenSubKey($view.Path)
            if ($null -eq $root) { continue }
            try {
                foreach ($subkeyName in $root.GetSubKeyNames()) {
                    $item = $root.OpenSubKey($subkeyName)
                    if ($null -eq $item) { continue }
                    try {
                        $name = [string]$item.GetValue('DisplayName', '')
                        if ($name -match $NamePattern) {
                            [pscustomobject]@{
                                Name = $name.Trim()
                                Version = [string]$item.GetValue('DisplayVersion', '')
                                Location = [string]$item.GetValue('InstallLocation', '')
                                Uninstall = [string]$item.GetValue('UninstallString', '')
                            }
                        }
                    } finally { $item.Dispose() }
                }
            } finally { $root.Dispose() }
        } finally { $hive.Dispose() }
    }
}

function Start-FixtureProcess([string] $Executable, [string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    [Diagnostics.Process]::Start($start)
}

function Wait-FixtureCondition([int] $Seconds, [scriptblock] $Test, [string] $Failure) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Test) { return }
        Start-Sleep -Milliseconds 350
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}

function Invoke-FixtureProcess([string] $Executable, [string[]] $Arguments, [int] $Seconds = 120) {
    $process = Start-FixtureProcess $Executable $Arguments
    try {
        if (!$process.WaitForExit($Seconds * 1000)) {
            $process.Kill($true)
            throw "Timed out waiting for $([IO.Path]::GetFileName($Executable))."
        }
        if ($process.ExitCode -ne 0) {
            throw "$([IO.Path]::GetFileName($Executable)) exited with code $($process.ExitCode)."
        }
    } finally { $process.Dispose() }
}

function Invoke-Verification([string] $Dll, [string[]] $Arguments) {
    & dotnet $Dll @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function New-ControlFile([string] $Path, [string] $Content) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write([Text.Encoding]::UTF8.GetBytes($Content)) }
    finally { $stream.Dispose() }
}

$VerificationDll = (Resolve-Path -LiteralPath $VerificationDll).ProviderPath
$ReferenceDll = (Resolve-Path -LiteralPath $ReferenceDll).ProviderPath
$reports = [IO.Path]::GetFullPath($ReportDirectory)
$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$roamingData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
$installParent = Join-Path $localData 'Programs'
$installPath = Join-Path $installParent $Product
$observedFiles = @()
$observedRegistryKeys = @()

if ($Product -eq 'SumatraPDF') {
    # https://github.com/microsoft/winget-pkgs/blob/master/manifests/s/SumatraPDF/SumatraPDF/3.5.2/SumatraPDF.SumatraPDF.installer.yaml
    # https://www.sumatrapdfreader.org/docs/Installer-cmd-line-arguments
    $source = 'https://files2.sumatrapdfreader.org/software/sumatrapdf/rel/3.5.2/SumatraPDF-3.5.2-64-install.exe'
    $sha256 = '2BE4A27B83830EA07C6671C3557673D509544E5F70FC6B2DC8CC4388B302C1F2'
    $version = '3.5.2'
    $namePattern = '^SumatraPDF(?:\s|$)'
    $processNames = @('SumatraPDF', 'Sumatra-Uninstaller')
    $binary = Join-Path $installPath 'SumatraPDF.exe'
    $settingsDirectory = Join-Path $localData 'SumatraPDF'
    $settingsFile = Join-Path $settingsDirectory 'SumatraPDF-settings.txt'
    $installArguments = @('-install', '-silent', '-d', $installPath)
    $uninstallArguments = @('-uninstall', '-silent')
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The pinned SumatraPDF uninstaller relaunches elevated. The runner must already be elevated.'
    }
} else {
    # https://github.com/microsoft/winget-pkgs/blob/master/manifests/w/WinMerge/WinMerge/2.16.60/WinMerge.WinMerge.installer.yaml
    # https://manual.winmerge.org/en/Command_line.html
    # https://jrsoftware.org/inno-setup/documentation/topic_uninstcmdline.htm
    $source = 'https://github.com/WinMerge/winmerge/releases/download/v2.16.60/WinMerge-2.16.60-x64-PerUser-Setup.exe'
    $sha256 = '35217E6B3BB981E37B7CE3858E686E4EE3AFE5CB7820FF5960258829FEA0BA03'
    $version = '2.16.60'
    $namePattern = '^WinMerge(?:\s|\(|$)'
    $processNames = @('WinMergeU', 'WinMerge32BitPluginProxy')
    $binary = Join-Path $installPath 'WinMergeU.exe'
    $settingsDirectory = Join-Path $roamingData 'WinMerge'
    $settingsFile = Join-Path $settingsDirectory 'WinMerge-verification.ini'
    $settingsKey = 'Software\Thingamahoochie\WinMerge'
    $settingsRegistryPath = "HKCU:\$settingsKey"
    $installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NORESTARTAPPLICATIONS', '/NOICONS', "/DIR=$installPath")
    $uninstallArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if (Test-Path -LiteralPath $settingsRegistryPath) { throw 'WinMerge settings existed before verification.' }
}

if (@(Get-ProductRegistrations $namePattern).Count -gt 0 -or
    @(Get-Process -Name $processNames -ErrorAction SilentlyContinue).Count -gt 0 -or
    (Test-Path -LiteralPath $installPath) -or (Test-Path -LiteralPath $settingsDirectory)) {
    throw "Refusing to touch a pre-existing $Product installation, process or settings directory."
}

[void][IO.Directory]::CreateDirectory($reports)
$work = Join-Path $env:RUNNER_TEMP "UninstallTrace-$Product-$([Guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($work)
$installerPath = Join-Path $work ([IO.Path]::GetFileName([uri]::new($source).AbsolutePath))
Invoke-WebRequest -Uri $source -OutFile $installerPath -TimeoutSec 90
if ((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ne $sha256) {
    throw "$Product installer SHA-256 mismatch."
}
Write-Host "Verified $Product $version installer SHA-256."
Invoke-FixtureProcess $installerPath $installArguments

Wait-FixtureCondition 30 { @(Get-ProductRegistrations $namePattern).Count -gt 0 -and [IO.File]::Exists($binary) } 'Installed program or registration did not appear.'
$registrations = @(Get-ProductRegistrations $namePattern)
if ($registrations.Count -ne 1 -or $registrations[0].Version -notmatch "^$([regex]::Escape($version))(?:\.0)?$" -or
    [string]::IsNullOrWhiteSpace($registrations[0].Location) -or
    [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($registrations[0].Location.Trim('"'))) -ne $installPath) {
    throw "$Product did not register exactly one expected program and install location."
}
$registration = $registrations[0]
$snapshot = Join-Path $reports 'installed-app.json'
Invoke-Verification $VerificationDll @('--real-snapshot', $registration.Name, $snapshot)

if ($Product -eq 'SumatraPDF') {
    # 3.5.2 needs the appdata directory to exist when explicitly selecting it.
    [void][IO.Directory]::CreateDirectory($settingsDirectory)
    $process = Start-FixtureProcess $binary @('-appdata', $settingsDirectory)
    try {
        Wait-FixtureCondition 30 {
            $process.Refresh()
            if ($process.HasExited) { throw "SumatraPDF exited before writing its initial settings ($($process.ExitCode))." }
            [IO.File]::Exists($settingsFile) -and (Get-Item -LiteralPath $settingsFile).Length -gt 0
        } 'SumatraPDF did not write its initial settings.'
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.CloseMainWindow() -and $process.WaitForExit(15000)) {
            if ($process.ExitCode -ne 0) { throw "SumatraPDF exited with code $($process.ExitCode)." }
        }
    } finally {
        if (!$process.HasExited) {
            $process.Kill($true)
            if (!$process.WaitForExit(10000)) { throw 'The fixture application process did not exit.' }
        }
        $process.Dispose()
    }
} else {
    $left = Join-Path $work 'left.txt'
    $right = Join-Path $work 'right.txt'
    New-ControlFile $left 'UninstallTrace WinMerge comparison'
    New-ControlFile $right 'UninstallTrace WinMerge comparison'
    $compareArguments = @('/s-', '/noninteractive', '/xq', '/enableexitcode', '/cfg', 'Settings/DiffAlgorithm=3', $left, $right)
    Invoke-FixtureProcess $binary $compareArguments
    if (Test-Path -LiteralPath $settingsRegistryPath) { $observedRegistryKeys += $settingsKey }

    [void][IO.Directory]::CreateDirectory($settingsDirectory)
    Invoke-FixtureProcess $binary (@('/inifile', $settingsFile) + $compareArguments)
}
if ([IO.File]::Exists($settingsFile) -and (Get-Item -LiteralPath $settingsFile).Length -gt 0) { $observedFiles += $settingsFile }
if ($observedFiles.Count + $observedRegistryKeys.Count -lt 1) { throw "$Product did not generate a settings artifact." }
if (@(Get-Process -Name $processNames -ErrorAction SilentlyContinue).Count -gt 0) { throw "$Product still has a running application process." }

# Use only the native uninstaller registered by this fixture and the product's fixed unattended switches.
if ($registration.Uninstall -notmatch '^\s*"?(?<path>[a-z]:\\.+?\.exe)"?(?:\s|$)') {
    throw "$Product registered an unsupported uninstall command."
}
$uninstaller = [IO.Path]::GetFullPath($Matches.path)
if ([IO.Path]::GetDirectoryName($uninstaller) -ne $installPath -or ![IO.File]::Exists($uninstaller) -or
    ($Product -eq 'SumatraPDF' -and ($uninstaller -ne $binary -or $registration.Uninstall -notmatch '(?:^|\s)-uninstall(?:\s|$)')) -or
    ($Product -eq 'WinMerge' -and [IO.Path]::GetFileName($uninstaller) -notmatch '^unins\d{3}\.exe$')) {
    throw "$Product registered an unexpected native uninstaller."
}
Invoke-FixtureProcess $uninstaller $uninstallArguments
Wait-FixtureCondition 60 {
    @(Get-ProductRegistrations $namePattern).Count -eq 0 -and ![IO.File]::Exists($binary) -and ![IO.File]::Exists($uninstaller) -and
    @(Get-Process -Name $processNames -ErrorAction SilentlyContinue).Count -eq 0
} 'Vendor uninstall did not clear the fixture registration, binary and application processes.'
Write-Host 'Confirmed native uninstall completed.'

# Keep the positive seed independent of settings written by the real program.
$seeded = Join-Path $installPath 'UninstallTrace-seeded-obsolete.txt'
$similarSibling = Join-Path (Join-Path $installParent "$Product-Shared-Fixture") 'unrelated.txt'
$sharedParent = Join-Path $localData 'UninstallTraceVerificationShared'
$shared = Join-Path $sharedParent 'shared.txt'
New-ControlFile $seeded 'Bounded cleanup confirmation seed'
New-ControlFile $similarSibling 'Similar name, unrelated fixture'
New-ControlFile $shared 'Unrelated shared fixture'

$expectations = Join-Path $reports 'expectations.json'
[ordered]@{
    observedFiles = @($observedFiles)
    observedRegistryKeys = @($observedRegistryKeys)
    seededFiles = @($seeded)
    protectedFiles = @($similarSibling, $shared)
    protectedRegistryKeys = @()
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $expectations -Encoding utf8NoBOM

Invoke-Verification $ReferenceDll @('--real-report', $snapshot, $expectations, (Join-Path $reports 'baseline-report.json'))
Invoke-Verification $VerificationDll @('--real-scan', $snapshot, $expectations, (Join-Path $reports 'candidate-report.json'))
