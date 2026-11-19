# Import only pure function definitions; never invoke the package builder.
$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot '..\build.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($build, [ref]$null, [ref]$null)
foreach ($definition in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($definition.Name -in @('Get-PortableExecutableMachine', 'Assert-WintunArchitecture', 'Assert-OutputUnlocked')) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
}
$installerBuild = Join-Path $PSScriptRoot '..\build-installer.ps1'
$installerAst = [System.Management.Automation.Language.Parser]::ParseFile($installerBuild, [ref]$null, [ref]$null)
foreach ($definition in $installerAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($definition.Name -in @('Get-DesktopVersion', 'Find-InnoCompiler', 'Assert-PackageComplete', 'Reset-PackageDirectory')) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}
$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('opencode\usque-build-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
function Write-Sample([string]$path, [uint16]$machine) {
    $bytes = New-Object byte[] 70
    $bytes[0] = 0x4D; $bytes[1] = 0x5A; $bytes[60] = 64
    $bytes[64] = 0x50; $bytes[65] = 0x45
    $bytes[68] = $machine -band 255; $bytes[69] = ($machine -shr 8) -band 255
    [System.IO.File]::WriteAllBytes($path, $bytes)
}
try {
    $path = Join-Path $directory 'sample.dll'
    Write-Sample $path 0x8664
    Assert-WintunArchitecture $path 'win-x64'
    Write-Host 'PASS  x64 PE architecture is accepted'
    Write-Sample $path 0xAA64
    Assert-WintunArchitecture $path 'win-arm64'
    Write-Host 'PASS  ARM64 PE architecture is accepted'
    $failed = $false
    try { Assert-WintunArchitecture $path 'win-x64' } catch { $failed = $true }
    if (-not $failed) { throw 'A wrong-architecture Wintun DLL was accepted.' }
    Write-Host 'PASS  Wrong-architecture driver is rejected'
    [System.IO.File]::WriteAllBytes($path, [byte[]]@(1, 2, 3))
    $failed = $false
    try { Get-PortableExecutableMachine $path | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw 'A truncated PE file was accepted.' }
    Write-Host 'PASS  Truncated executable is rejected'
    $output = Join-Path $directory 'output'
    New-Item -ItemType Directory -Path $output | Out-Null
    $binary = Join-Path $output 'Usque.Core.dll'
    [System.IO.File]::WriteAllText($binary, 'unchanged sample')
    Assert-OutputUnlocked $output
    if ([System.IO.File]::ReadAllText($binary) -ne 'unchanged sample') { throw 'Output preflight modified the binary.' }
    Write-Host 'PASS  Output preflight does not modify unlocked files'
    $lock = [System.IO.File]::Open($binary, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    try {
        $failed = $false
        try { Assert-OutputUnlocked $output } catch { $failed = $true }
        if (-not $failed) { throw 'Locked output was not rejected before publishing.' }
    } finally { $lock.Dispose() }
    Write-Host 'PASS  Locked output is rejected without stopping an application'

    $project = Join-Path $directory 'Sample.csproj'
    Set-Content -LiteralPath $project -Value '<Project><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup><PropertyGroup><Version>4.5.6</Version></PropertyGroup></Project>'
    if ((Get-DesktopVersion $project) -ne '4.5.6') { throw 'The project version was not read.' }
    Set-Content -LiteralPath $project -Value '<Project><PropertyGroup><Version>1.2-beta</Version></PropertyGroup></Project>'
    Assert-Throws { Get-DesktopVersion $project } 'A malformed project version was accepted.'
    Write-Host 'PASS  Installer version comes from a well-formed project version'

    $compiler = Join-Path $directory 'ISCC.exe'
    Assert-Throws { Find-InnoCompiler $compiler } 'A missing explicit ISCC path was accepted.'
    [System.IO.File]::WriteAllText($compiler, '')
    if ((Find-InnoCompiler $compiler) -ne $compiler) { throw 'An explicit ISCC path was ignored.' }
    Write-Host 'PASS  Explicit Inno Setup compiler path is validated'

    $dist = Join-Path $directory 'dist'
    $package = Join-Path $dist 'desktop-test'
    foreach ($relative in @('Usque.Desktop.exe', 'Usque.Desktop.dll', 'Usque.Core.dll', 'Usque.Desktop.deps.json',
        'Usque.Desktop.runtimeconfig.json', 'engine\usque.exe', 'engine\wintun.dll', 'Scripts\Network.ps1',
        'USQUE-LICENSE.md', 'UsqueDesktop.ico')) {
        $path = Join-Path $package $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [System.IO.File]::WriteAllText($path, 'sample')
    }
    Assert-PackageComplete $package
    Remove-Item -LiteralPath (Join-Path $package 'engine\wintun.dll')
    Assert-Throws { Assert-PackageComplete $package } 'A package without Wintun was accepted.'
    [System.IO.File]::WriteAllText((Join-Path $package 'engine\wintun.dll'), 'sample')
    [System.IO.File]::WriteAllText((Join-Path $package 'settings.json'), '{}')
    Assert-Throws { Assert-PackageComplete $package } 'A package containing private settings was accepted.'
    Write-Host 'PASS  Incomplete packages and private state are rejected before compiling'

    Assert-Throws { Reset-PackageDirectory $directory $dist } 'A directory outside dist was cleaned.'
    Assert-Throws { Reset-PackageDirectory $dist $dist } 'The dist root itself was cleaned.'
    Reset-PackageDirectory $package $dist
    if ((Test-Path -LiteralPath $package) -or -not (Test-Path -LiteralPath $project)) { throw 'Package cleanup removed the wrong files.' }
    Write-Host 'PASS  Package cleanup is confined to its own directory under dist'
    Write-Host '10/10 packaging-script tests passed. No executables were launched or drivers loaded.'
} finally { Remove-Item -LiteralPath $directory -Recurse -Force }
