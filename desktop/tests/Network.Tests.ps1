# All Windows networking cmdlets are mocked. This test never calls the real
# NetTCPIP/DnsClient/NetAdapter implementations and does not need elevation.
$ErrorActionPreference = 'Stop'
$networkScript = Join-Path $PSScriptRoot '..\Usque.Desktop\Scripts\Network.ps1'
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('opencode\usque-network-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
$journal = Join-Path $temporary 'session.json'
$physicalGuid = [guid]::NewGuid()
$tunnelGuid = [guid]::NewGuid()
$global:MockAdapters = @(
    [pscustomobject]@{ Name = 'Ethernet'; ifIndex = 7; InterfaceIndex = 7; InterfaceGuid = $physicalGuid },
    [pscustomobject]@{ Name = 'UsqueWARP'; ifIndex = 42; InterfaceIndex = 42; InterfaceGuid = $tunnelGuid }
)
$global:MockRoutes = @([pscustomobject]@{ DestinationPrefix = '0.0.0.0/0'; InterfaceIndex = 7; NextHop = '192.0.2.1'; RouteMetric = 25; Protocol = 'NetMgmt' })
$global:MockDnsRules = @()
$global:MockFailPrefix = $null
$global:MockFailQuery = $false
$global:MockDnsInterfaces = @()

function Get-NetAdapter {
    param([uint32[]]$InterfaceIndex, [string]$Name, [switch]$IncludeHidden)
    $global:MockAdapters | Where-Object { (-not $InterfaceIndex -or $_.ifIndex -in $InterfaceIndex) -and (-not $Name -or $_.Name -eq $Name) }
}
function Find-NetRoute {
    param([string]$RemoteIPAddress)
    [pscustomobject]@{ IPAddress = '192.0.2.2' }
    $global:MockRoutes | Where-Object { $_.DestinationPrefix -eq '0.0.0.0/0' }
}
function Get-NetRoute {
    param([string]$PolicyStore)
    if ($global:MockFailQuery) { throw 'simulated route query failure' }
    $global:MockRoutes
}
function New-NetRoute {
    param([string]$DestinationPrefix, [int]$InterfaceIndex, [string]$NextHop, [int]$RouteMetric, [string]$PolicyStore)
    if ($DestinationPrefix -eq $global:MockFailPrefix) { throw 'simulated route creation failure' }
    $entry = [pscustomobject]@{ DestinationPrefix = $DestinationPrefix; InterfaceIndex = $InterfaceIndex; NextHop = $NextHop; RouteMetric = $RouteMetric; Protocol = 'NetMgmt' }
    $global:MockRoutes += $entry
    $entry
}
function Remove-NetRoute {
    [CmdletBinding(SupportsShouldProcess)]param([Parameter(ValueFromPipeline)]$InputObject)
    process { $global:MockRoutes = @($global:MockRoutes | Where-Object { $_ -ne $InputObject }) }
}
function Get-DnsClientNrptRule { $global:MockDnsRules }
function Add-DnsClientNrptRule {
    param([string[]]$Namespace, [string[]]$NameServers, [string]$DisplayName)
    $global:MockDnsRules += [pscustomobject]@{ Name = [guid]::NewGuid().ToString(); DisplayName = $DisplayName }
}
function Remove-DnsClientNrptRule {
    param([string]$Name, [switch]$Force)
    $global:MockDnsRules = @($global:MockDnsRules | Where-Object { $_.Name -ne $Name })
}
function Set-DnsClientServerAddress {
    param([int]$InterfaceIndex, [string[]]$ServerAddresses)
    $global:MockDnsInterfaces += $InterfaceIndex
}
function Clear-DnsClientCache { }
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }

try {
    & $networkScript -Action Prepare -StatePath $journal -Endpoint '162.159.198.1' | Out-Null
    & $networkScript -Action Configure -StatePath $journal | Out-Null
    Assert ($global:MockRoutes.Count -eq 6) 'Expected original default, endpoint route and four split defaults.'
    Assert ((Get-Content $journal -Raw | ConvertFrom-Json).Routes.Count -eq 5) 'All route intents must be journaled.'
    Assert ($global:MockDnsInterfaces.Count -eq 1 -and $global:MockDnsInterfaces[0] -eq 42) 'Physical DNS must remain unchanged.'
    & $networkScript -Action Configure -StatePath $journal | Out-Null
    Assert ($global:MockRoutes.Count -eq 6 -and $global:MockDnsRules.Count -eq 1) 'Configuration must be idempotent.'
    & $networkScript -Action Cleanup -StatePath $journal | Out-Null
    Assert ($global:MockRoutes.Count -eq 1 -and $global:MockRoutes[0].RouteMetric -eq 25) 'Cleanup must preserve original defaults.'
    Assert ($global:MockDnsRules.Count -eq 0 -and -not (Test-Path $journal)) 'Cleanup must remove only owned DNS and journal.'
    Write-Host 'PASS  Route/DNS setup, journaling, idempotence and cleanup'

    & $networkScript -Action Prepare -StatePath $journal -Endpoint '162.159.198.1' | Out-Null
    $global:MockFailPrefix = '8000::/1'
    $failed = $false
    try { & $networkScript -Action Configure -StatePath $journal | Out-Null } catch { $failed = $true }
    Assert $failed 'Partial setup should fail visibly.'
    $global:MockFailPrefix = $null
    & $networkScript -Action Cleanup -StatePath $journal | Out-Null
    Assert ($global:MockRoutes.Count -eq 1) 'Partial route setup must be recoverable.'
    Write-Host 'PASS  Partial IPv6 setup failure is recoverable'

    & $networkScript -Action Prepare -StatePath $journal -Endpoint '162.159.198.1' | Out-Null
    $global:MockFailQuery = $true
    $failed = $false
    try { & $networkScript -Action Cleanup -StatePath $journal | Out-Null } catch { $failed = $true }
    Assert ($failed -and (Test-Path $journal)) 'A failed query must not discard the recovery journal.'
    $global:MockFailQuery = $false
    & $networkScript -Action Cleanup -StatePath $journal | Out-Null
    Write-Host 'PASS  Cleanup query failure preserves recovery state'

    & $networkScript -Action Prepare -StatePath $journal -Endpoint '162.159.198.1' | Out-Null
    $global:MockAdapters[0].InterfaceGuid = [guid]::NewGuid()
    & $networkScript -Action Cleanup -StatePath $journal | Out-Null
    Assert ($global:MockRoutes.Count -eq 2) 'Cleanup must not touch a different adapter reusing the same index.'
    Write-Host 'PASS  Reused adapter indexes do not delete unrelated routes'
    Write-Host '4/4 network-script tests passed. No real networking was modified.'
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
