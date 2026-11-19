<#
.SYNOPSIS
Builds a clean, verified Usque Desktop package and its Inno Setup installer in one step.

.DESCRIPTION
1. Reads the version from Usque.Desktop.csproj (the single source of truth).
2. Recreates the package directory under dist\ so files from older builds can never
   leak into the installer, then runs build.ps1 (tests, publish, engine, Wintun).
3. Verifies that every runtime file the app needs is present, including Wintun.
4. Finds ISCC.exe (parameter, PATH, Inno Setup registry entries, default folders),
   compiles installer.iss with the matching version/architecture, and writes a
   SHA-256 file next to the installer.
It never installs drivers, changes networking, or stops a running VPN.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\desktop\build-installer.ps1
powershell -ExecutionPolicy Bypass -File .\desktop\build-installer.ps1 -Suffix bugfixes
powershell -ExecutionPolicy Bypass -File .\desktop\build-installer.ps1 -WintunDll C:\path\wintun.dll -SkipTests
#>
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    # Optional label for test builds, e.g. "bugfixes" -> UsqueDesktop-Setup-1.3.2-bugfixes.exe
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,31}$')][string]$Suffix,
    # Use a local, already-trusted Wintun DLL instead of downloading the verified official build.
    [string]$WintunDll,
    [string]$Iscc,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Get-DesktopVersion([string]$ProjectPath) {
    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Usque.Desktop.csproj must declare a <Version> like 1.2.3 (found '$version')." }
    return $version
}

function Find-InnoCompiler([string]$Explicit) {
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit -PathType Leaf)) { throw "ISCC.exe was not found at $Explicit." }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $candidates = New-Object System.Collections.Generic.List[string]
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { $candidates.Add($command.Source) }
    # Per-machine (both registry views) and per-user installs register an uninstall entry.
    foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                       'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                       'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
        $location = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).InstallLocation
        if ($location) { $candidates.Add((Join-Path $location 'ISCC.exe')) }
    }
    foreach ($folder in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if ($folder) { $candidates.Add((Join-Path $folder 'Inno Setup 6\ISCC.exe')) }
    }
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    throw 'Inno Setup 6 (ISCC.exe) was not found. Install it, e.g. "winget install JRSoftware.InnoSetup --scope user", or pass -Iscc <path>.'
}

function Assert-PackageComplete([string]$Directory) {
    # Mirrors the runtime files the app validates before connecting.
    $required = @('Usque.Desktop.exe', 'Usque.Desktop.dll', 'Usque.Core.dll', 'Usque.Desktop.deps.json',
        'Usque.Desktop.runtimeconfig.json', 'engine\usque.exe', 'engine\wintun.dll', 'Scripts\Network.ps1',
        'USQUE-LICENSE.md', 'UsqueDesktop.ico')
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Directory $_) -PathType Leaf) })
    if ($missing.Count -gt 0) { throw "The package is incomplete; refusing to build an installer. Missing: $($missing -join ', ')" }
    # (-Include is unreliable with -LiteralPath in Windows PowerShell 5.1; filter by name.)
    $private = @(Get-ChildItem -LiteralPath $Directory -Recurse -File |
        Where-Object { $_.Name -in @('config.json', 'settings.json', 'network-session.json') })
    if ($private.Count -gt 0) { throw "The package contains private state files: $($private.FullName -join ', ')" }
}

function Reset-PackageDirectory([string]$Directory, [string]$DistRoot) {
    $full = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $dist = [System.IO.Path]::GetFullPath($DistRoot).TrimEnd('\') + '\'
    # Only ever delete a build output inside dist\, never the dist root itself.
    if (-not $full.StartsWith($dist, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to clean a directory outside dist: $full" }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

$version = Get-DesktopVersion (Join-Path $PSScriptRoot 'Usque.Desktop\Usque.Desktop.csproj')
$arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$label = $version + $(if ($arch -ne 'x64') { "-$arch" }) + $(if ($Suffix) { "-$Suffix" })
$dist = Join-Path $root 'dist'
$package = Join-Path $dist "desktop-$label"
$installerName = "UsqueDesktop-Setup-$label"
$compiler = Find-InnoCompiler $Iscc   # fail before a multi-minute build, not after
Write-Host "Usque Desktop $version ($Runtime) -> $installerName.exe"
Write-Host "Inno Setup: $compiler"

# Reuse build.ps1's lock check before deleting anything: a running copy is never stopped.
$buildAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'build.ps1'), [ref]$null, [ref]$null)
$lockCheck = $buildAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Assert-OutputUnlocked' }, $false) | Select-Object -First 1
. ([scriptblock]::Create($lockCheck.Extent.Text))
Assert-OutputUnlocked $package
Reset-PackageDirectory $package $dist

$build = @{ Runtime = $Runtime; OutputDirectory = $package }
if ($WintunDll) { $build.WintunDll = $WintunDll } else { $build.DownloadWintun = $true }
if ($SkipTests) { $build.SkipTests = $true }
& (Join-Path $PSScriptRoot 'build.ps1') @build
Assert-PackageComplete $package

$arguments = @('/Qp', "/DMyAppVersion=$version", "/DPackageDir=$package", "/DArch=$arch",
    "/DOutputBaseName=$installerName", "/O$dist", (Join-Path $PSScriptRoot 'installer.iss'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit $LASTEXITCODE)." }

$installer = Join-Path $dist "$installerName.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Inno Setup did not produce $installer." }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
# Same "<hash>  <file>" format as sha256sum, so `sha256sum -c` can verify it.
[System.IO.File]::WriteAllText("$installer.sha256", "$hash  $installerName.exe`n", (New-Object System.Text.UTF8Encoding($false)))
$size = [math]::Round((Get-Item -LiteralPath $installer).Length / 1MB, 1)
Write-Host ''
Write-Host "Installer: $installer ($size MB)"
Write-Host "SHA-256:   $hash"
Write-Host "Package:   $package"
