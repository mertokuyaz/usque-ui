param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$DownloadWintun,
    [string]$WintunDll,
    [string]$OutputDirectory,
    [switch]$FrameworkDependent,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = if (-not $OutputDirectory) { Join-Path $root 'dist\desktop' }
    elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) { [System.IO.Path]::GetFullPath($OutputDirectory) }
    else { [System.IO.Path]::GetFullPath((Join-Path $root $OutputDirectory)) }
$engine = Join-Path $output 'engine'
function Assert-Exit([string]$operation) { if ($LASTEXITCODE -ne 0) { throw "$operation failed (exit $LASTEXITCODE)." } }
function Get-PortableExecutableMachine([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid Windows executable header.' }
        $stream.Position = 60
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt ($stream.Length - 6)) { throw 'Invalid Windows PE header offset.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Invalid Windows PE signature.' }
        return $reader.ReadUInt16()
    } finally { $reader.Dispose() }
}
function Assert-WintunArchitecture([string]$Path, [string]$TargetRuntime) {
    $expected = if ($TargetRuntime -eq 'win-arm64') { 0xAA64 } elseif ($TargetRuntime -eq 'win-x64') { 0x8664 } else { throw 'Unsupported Windows runtime.' }
    if ((Get-PortableExecutableMachine $Path) -ne $expected) {
        throw "wintun.dll does not match $TargetRuntime. Re-run with -DownloadWintun or supply a matching -WintunDll."
    }
}
function Assert-OutputUnlocked([string]$Directory) {
    foreach ($relative in @('Usque.Desktop.exe', 'Usque.Desktop.dll', 'Usque.Core.dll', 'engine\usque.exe', 'engine\wintun.dll')) {
        $path = Join-Path $Directory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        try {
            $file = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $file.Dispose()
        } catch {
            throw "The output is in use or read-only: $path. Use Exit Usque before rebuilding it, or choose a different -OutputDirectory. The running VPN will not be stopped automatically."
        }
    }
}
Push-Location $root
try {
    Assert-OutputUnlocked $output
    if (-not $SkipTests) {
        & go test ./...
        Assert-Exit 'Go tests'
        & dotnet run --project desktop/Usque.Tests -c Release
        Assert-Exit 'Desktop tests'
        & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File desktop/tests/Network.Tests.ps1
        Assert-Exit 'Network-script tests'
        & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File desktop/tests/Build.Tests.ps1
        Assert-Exit 'Packaging-script tests'
    }
    Assert-OutputUnlocked $output
    & dotnet publish desktop/Usque.Desktop -c Release -r $Runtime --self-contained (-not $FrameworkDependent).ToString().ToLowerInvariant() -o $output
    Assert-Exit 'Desktop publish'
    New-Item -ItemType Directory -Path $engine -Force | Out-Null
    $oldArch = $env:GOARCH; $oldOs = $env:GOOS; $oldCgo = $env:CGO_ENABLED
    try {
        $env:GOOS = 'windows'
        $env:GOARCH = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'amd64' }
        $env:CGO_ENABLED = '0'
        & go build -trimpath '-ldflags=-s -w' -o (Join-Path $engine 'usque.exe') .
        Assert-Exit 'VPN engine build'
    } finally { $env:GOARCH = $oldArch; $env:GOOS = $oldOs; $env:CGO_ENABLED = $oldCgo }
    if ($DownloadWintun) {
        # Hash published by https://www.wintun.net/ for the official signed DLLs.
        $temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('usque-wintun-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporary | Out-Null
        $archive = Join-Path $temporary 'wintun.zip'
        try {
            Invoke-WebRequest 'https://www.wintun.net/builds/wintun-0.14.1.zip' -OutFile $archive -UseBasicParsing
            if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51') {
                throw 'Wintun download checksum mismatch. Refusing to package it.'
            }
            Expand-Archive -LiteralPath $archive -DestinationPath $temporary
            $arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'amd64' }
            Copy-Item (Join-Path $temporary "wintun\bin\$arch\wintun.dll") (Join-Path $engine 'wintun.dll')
            Copy-Item (Join-Path $temporary 'wintun\LICENSE.txt') (Join-Path $output 'WINTUN-LICENSE.txt')
        } finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
    } elseif ($WintunDll) {
        Assert-WintunArchitecture (Resolve-Path $WintunDll).Path $Runtime
        Copy-Item -LiteralPath (Resolve-Path $WintunDll) -Destination (Join-Path $engine 'wintun.dll')
    }
    if (Test-Path (Join-Path $engine 'wintun.dll')) { Assert-WintunArchitecture (Join-Path $engine 'wintun.dll') $Runtime }
    Copy-Item (Join-Path $root 'LICENSE.md') (Join-Path $output 'USQUE-LICENSE.md')
    Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $output 'README.md')
    Copy-Item (Join-Path $PSScriptRoot 'CHANGELOG.md') (Join-Path $output 'CHANGELOG.md')
    Copy-Item (Join-Path $PSScriptRoot 'Usque.Desktop\UsqueDesktop.ico') (Join-Path $output 'UsqueDesktop.ico') -Force
    Copy-Item (Join-Path $PSScriptRoot 'Usque.Desktop\UsqueDesktop.png') (Join-Path $output 'UsqueDesktop.png') -Force
    if (-not (Test-Path (Join-Path $engine 'wintun.dll'))) {
        Write-Warning 'wintun.dll is missing. Re-run with -DownloadWintun, or supply -WintunDll <path>.'
    }
    Write-Host "Ready: $output\Usque.Desktop.exe"
} finally { Pop-Location }
