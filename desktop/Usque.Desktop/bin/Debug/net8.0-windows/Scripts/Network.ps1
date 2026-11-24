param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Configure', 'Cleanup')][string]$Action,
    [Parameter(Mandatory)][string]$StatePath,
    [string]$Endpoint
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$adapterName = 'UsqueWARP'
$ruleName = 'UsqueDesktop-DNS-57d78a76'
$routeMetric = 3

function Save-State {
    $temporary = "$StatePath.tmp"
    $script:state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    if (Test-Path -LiteralPath $StatePath) { [System.IO.File]::Replace($temporary, $StatePath, [System.Management.Automation.Language.NullString]::Value) }
    else { [System.IO.File]::Move($temporary, $StatePath) }
}

function Add-OwnedRoute([string]$prefix, [int]$index, [string]$hop) {
    $existing = @(Get-NetRoute -PolicyStore ActiveStore |
        Where-Object { $_.DestinationPrefix -eq $prefix -and $_.InterfaceIndex -eq $index -and $_.NextHop -eq $hop })
    if ($existing.Count -gt 0) { return }
    # Journal the intent before changing Windows. Cleanup only removes the exact
    # route tuple we created, never a user's default routes or other VPN routes.
    $identity = (Get-NetAdapter -InterfaceIndex $index -IncludeHidden).InterfaceGuid.ToString()
    $script:state.Routes = @($script:state.Routes) + @(@{ Prefix = $prefix; Index = $index; InterfaceGuid = $identity; Hop = $hop; Metric = $routeMetric })
    Save-State
    New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $index -NextHop $hop -RouteMetric $routeMetric -PolicyStore ActiveStore | Out-Null
}

if (Test-Path -LiteralPath $StatePath) {
    $script:state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
} else {
    $script:state = [pscustomobject]@{ Version = 1; Routes = @(); DnsRule = $null }
}
if ($script:state.Version -ne 1) { throw 'Unsupported network recovery journal. No networking changes were made.' }

if ($Action -eq 'Cleanup') {
    # A fixed rule name also recovers a crash between NRPT creation and journaling.
    $rules = @(Get-DnsClientNrptRule | Where-Object { $_.DisplayName -eq $ruleName })
    foreach ($rule in $rules) { Remove-DnsClientNrptRule -Name $rule.Name -Force }
    foreach ($route in @($script:state.Routes)) {
        # Interface indexes can be reused after a crash. Never remove a route
        # on a different adapter that subsequently received the same index.
        $adapter = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.ifIndex -eq $route.Index -and $_.InterfaceGuid.ToString() -eq $route.InterfaceGuid })
        if ($adapter.Count -eq 0) { continue }
        $owned = @(Get-NetRoute -PolicyStore ActiveStore |
            Where-Object { $_.DestinationPrefix -eq $route.Prefix -and $_.InterfaceIndex -eq $route.Index -and $_.NextHop -eq $route.Hop -and $_.RouteMetric -eq $route.Metric -and $_.Protocol -eq 'NetMgmt' })
        foreach ($entry in $owned) { $entry | Remove-NetRoute -Confirm:$false }
    }
    Clear-DnsClientCache
    if (Test-Path -LiteralPath $StatePath) { Remove-Item -LiteralPath $StatePath }
    Write-Output 'Session routes and DNS policy restored.'
    return
}

if ($Action -eq 'Prepare') {
    $ip = $null
    if (-not [System.Net.IPAddress]::TryParse($Endpoint, [ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork') { throw 'Invalid IPv4 endpoint.' }
    if (@($script:state.Routes).Count -gt 0) { throw 'Previous session has not been cleaned up.' }
    $best = @(Find-NetRoute -RemoteIPAddress $Endpoint | Where-Object { $_.PSObject.Properties['DestinationPrefix'] }) | Select-Object -First 1
    if (-not $best) { throw 'No usable IPv4 route to the WARP endpoint.' }
    $adapter = Get-NetAdapter -InterfaceIndex $best.InterfaceIndex
    if ($adapter.Name -eq $adapterName) { throw 'The WARP endpoint is already routed into the tunnel.' }
    Save-State
    Add-OwnedRoute "$Endpoint/32" $best.InterfaceIndex $best.NextHop
    Write-Output "WARP endpoint pinned to $($adapter.Name)."
    return
}

if (-not (Test-Path -LiteralPath $StatePath)) { throw 'The network session has not been prepared.' }
$adapter = Get-NetAdapter -Name $adapterName
# Two /1 routes take precedence over defaults without deleting or rewriting them.
# Both families must succeed: do not silently leave IPv6 on the physical adapter.
Add-OwnedRoute '0.0.0.0/1' $adapter.ifIndex '0.0.0.0'
Add-OwnedRoute '128.0.0.0/1' $adapter.ifIndex '0.0.0.0'
Add-OwnedRoute '::/1' $adapter.ifIndex '::'
Add-OwnedRoute '8000::/1' $adapter.ifIndex '::'
Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses @('1.1.1.1', '1.0.0.1')
$script:state.DnsRule = $ruleName
Save-State
if (-not @(Get-DnsClientNrptRule | Where-Object { $_.DisplayName -eq $ruleName }).Count) {
    Add-DnsClientNrptRule -Namespace '.' -NameServers @('1.1.1.1', '1.0.0.1') -DisplayName $ruleName
}
Clear-DnsClientCache
Write-Output 'IPv4, IPv6 and DNS are routed through WARP.'
