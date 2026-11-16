# Import only pure function definitions; never invoke the package builder.
$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot '..\build.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($build, [ref]$null, [ref]$null)
foreach ($definition in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($definition.Name -in @('Get-PortableExecutableMachine', 'Assert-WintunArchitecture', 'Assert-OutputUnlocked')) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
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
    Write-Host '6/6 packaging-script tests passed. No executables were launched or drivers loaded.'
} finally { Remove-Item -LiteralPath $directory -Recurse -Force }
