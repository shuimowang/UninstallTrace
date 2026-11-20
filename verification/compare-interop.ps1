# Compare one Win32 operation. Neither probe launches a UI or runs a leftover scan.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DeepUninstaller.Verification.csproj'
$dll = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/DeepUninstaller.Verification.dll'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("UninstallTrace-interop-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($fixture)
try {
    $paths = foreach ($index in 0..255) {
        $path = Join-Path $fixture ("entry-{0:d3}" -f $index)
        switch ($index % 4) {
            0 { [void][IO.Directory]::CreateDirectory($path) }
            1 { [IO.File]::WriteAllText($path, 'probe') }
            2 { [IO.File]::WriteAllText($path, 'probe') }
        }
        $path
    }
    $manifest = Join-Path $fixture 'paths.txt'
    [IO.File]::WriteAllLines($manifest, [string[]]$paths)
    $native = Join-Path $fixture 'probe.exe'
    $rustVersion = (rustc --version)
    $dotnetVersion = (dotnet --version)
    rustc --edition=2021 -C opt-level=3 (Join-Path $PSScriptRoot 'path_attribute_probe.rs') -o $native
    if ($LASTEXITCODE) { throw 'Rust build failed.' }
    dotnet build $project -c Release -warnaserror
    if ($LASTEXITCODE) { throw '.NET build failed.' }
    $samples = @{ Rust = [Collections.Generic.List[double]]::new(); CSharp = [Collections.Generic.List[double]]::new() }
    $checksum = $null
    foreach ($language in @('CSharp', 'Rust', 'Rust', 'CSharp', 'Rust', 'CSharp', 'CSharp', 'Rust')) {
        $lines = if ($language -eq 'Rust') { & $native $manifest } else { dotnet $dll --probe-path-attributes $manifest }
        if ($LASTEXITCODE -or $lines.Count -ne 15) { throw "$language probe failed." }
        foreach ($line in $lines) {
            $ms, $result = $line.Split(',')
            if ($null -eq $checksum) { $checksum = $result }
            if ($result -ne $checksum) { throw 'The two programs saw different file attributes.' }
            $samples[$language].Add([double]::Parse($ms, [Globalization.CultureInfo]::InvariantCulture))
        }
    }
    $summary = @("Warm GetFileAttributesW comparison, 2048 calls per timed batch, $dotnetVersion and $rustVersion.")
    foreach ($language in @('CSharp','Rust')) {
        $sorted = @($samples[$language] | Sort-Object)
        $mid = [int]($sorted.Count / 2)
        $median = ($sorted[$mid - 1] + $sorted[$mid]) / 2
        $p95 = $sorted[[int][Math]::Ceiling(0.95 * $sorted.Count) - 1]
        $summary += ('{0}: {1} samples, median {2:N3} ms, P95 {3:N3} ms, attribute sum {4}.' -f $language,$sorted.Count,$median,$p95,$checksum)
    }
    $summary | Write-Output
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
}
finally {
    # A fresh uniquely named fixture owns only top-level empty directories and these files.
    if ([IO.Directory]::Exists($fixture)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($fixture)) { [IO.File]::Delete($file) }
        foreach ($directory in [IO.Directory]::EnumerateDirectories($fixture)) { [IO.Directory]::Delete($directory) }
        [IO.Directory]::Delete($fixture)
    }
}
