# Loaded by Routes.ps1 inside its elevated, serialized transaction.
# Preserve VPN routes so IP_UNICAST_IF can still select the VPN interface.
# More-specific existing host, LAN and corporate routes remain authoritative.
$script:NcDefaultMetric = 1732
$script:NcDefaultJournal = Join-Path $root 'default-direct-owned.json'

function Write-NcRouteStatus($state,$success,$message) {
    if (!(Test-Path -LiteralPath $root)) { return }
    $file = Join-Path $root 'route-status.json'
    $temp = $file + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $report = [pscustomobject]@{
        WindowsDefaultDirect=[bool]$state.WindowsDefaultDirect; Success=[bool]$success
        CheckedAtUtc=[datetime]::UtcNow.ToString('o'); Message=[string]$message
    }
    try {
        [IO.File]::WriteAllText($temp,($report|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($file)) { [IO.File]::Replace($temp,$file,[NullString]::Value) }
        else { [IO.File]::Move($temp,$file) }
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
}

function Get-NcRouteKey($route) {
    return ('{0}|{1}|{2}|{3}' -f $route.AddressFamily,$route.DestinationPrefix,$route.InterfaceIndex,$route.NextHop)
}
function Read-NcDefaultJournal {
    if (!(Test-Path -LiteralPath $script:NcDefaultJournal)) { return @() }
    $items = @(Get-Content -LiteralPath $script:NcDefaultJournal -Raw -Encoding UTF8 | ConvertFrom-Json)
    foreach ($item in $items) {
        if ($item.AddressFamily -notin 'IPv4','IPv6' -or
            $item.DestinationPrefix -notin '0.0.0.0/2','64.0.0.0/2','128.0.0.0/2','192.0.0.0/2','::/2','4000::/2','8000::/2','c000::/2' -or
            [int]$item.InterfaceIndex -le 0 -or [int]$item.RouteMetric -ne $script:NcDefaultMetric) {
            throw 'Invalid Netcontroll default-route ownership journal. No route was removed.'
        }
        [void][ipaddress]::Parse([string]$item.NextHop)
    }
    return $items
}
function Write-NcDefaultJournal($items) {
    $temp = $script:NcDefaultJournal + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temp,(ConvertTo-Json -InputObject @($items) -Depth 5),[Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($script:NcDefaultJournal)) {
            [IO.File]::Replace($temp,$script:NcDefaultJournal,[NullString]::Value)
        } else { [IO.File]::Move($temp,$script:NcDefaultJournal) }
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
}
function New-NcDefaultPlan($direct4,$direct6) {
    $plan = @()
    foreach ($family in 'IPv4','IPv6') {
        $gateway = if ($family -eq 'IPv4') { $direct4 } else { $direct6 }
        if (!$gateway) { continue }
        $prefixes = if ($family -eq 'IPv4') {
            @('0.0.0.0/2','64.0.0.0/2','128.0.0.0/2','192.0.0.0/2')
        } else { @('::/2','4000::/2','8000::/2','c000::/2') }
        foreach ($prefix in $prefixes) {
            $plan += [pscustomobject]@{
                AddressFamily=$family; DestinationPrefix=$prefix
                InterfaceIndex=[int]$gateway.ifIndex; NextHop=[string]$gateway.NextHop
                RouteMetric=$script:NcDefaultMetric
            }
        }
    }
    return $plan
}
function Get-NcExactDefaultRoute($item) {
    return @(Get-NetRoute -AddressFamily $item.AddressFamily -DestinationPrefix $item.DestinationPrefix `
        -InterfaceIndex $item.InterfaceIndex -NextHop $item.NextHop -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
}
function Remove-NcOwnedDefaultRoute($item) {
    # A changed metric/protocol means the route is no longer ours to remove.
    $matches = @(Get-NcExactDefaultRoute $item | Where-Object {
        $_.RouteMetric -eq $script:NcDefaultMetric -and $_.Protocol -eq 'NetMgmt'
    })
    foreach ($match in $matches) { $match | Remove-NetRoute -Confirm:$false -ErrorAction Stop }
}
function Sync-NcDefaultRoutes($plan) {
    $owned = @(Read-NcDefaultJournal)
    $keys = @($plan | ForEach-Object { Get-NcRouteKey $_ })
    # Validate the entire plan first; never adopt an existing route.
    foreach ($item in $plan) {
        $key = Get-NcRouteKey $item
        $competitors = @(Get-NetRoute -AddressFamily $item.AddressFamily -DestinationPrefix $item.DestinationPrefix `
            -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
        foreach ($actual in $competitors) {
            $actualKey = Get-NcRouteKey $actual
            $wasOurs = @($owned | Where-Object { (Get-NcRouteKey $_) -eq $actualKey }).Count -gt 0
            if (!$wasOurs -or $actual.RouteMetric -ne $script:NcDefaultMetric -or $actual.Protocol -ne 'NetMgmt') {
                throw ('An existing route conflicts with the managed default: '+$item.DestinationPrefix+'. Nothing was adopted.')
            }
        }
    }
    foreach ($item in @($owned)) {
        if ((Get-NcRouteKey $item) -notin $keys) {
            Remove-NcOwnedDefaultRoute $item
            $key = Get-NcRouteKey $item
            $owned = @($owned | Where-Object { (Get-NcRouteKey $_) -ne $key })
            Write-NcDefaultJournal $owned
        }
    }
    foreach ($item in $plan) {
        $key = Get-NcRouteKey $item
        if (!@($owned | Where-Object { (Get-NcRouteKey $_) -eq $key }).Count) {
            # Journal before mutation: a crash after creation stays recoverable.
            $owned += $item
            Write-NcDefaultJournal $owned
        }
        if (!@(Get-NcExactDefaultRoute $item).Count) {
            New-NetRoute -AddressFamily $item.AddressFamily -DestinationPrefix $item.DestinationPrefix `
                -InterfaceIndex $item.InterfaceIndex -NextHop $item.NextHop -RouteMetric $script:NcDefaultMetric `
                -Protocol NetMgmt -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
        }
    }
}
function Set-NcDefaultDirect($enabled,$physical,$vpns,$direct4) {
    if (!$enabled) {
        Sync-NcDefaultRoutes @()
        Set-Guard 'DefaultDirect-IPv6' @() @()
        return
    }
    if (!$direct4) { throw 'No active physical IPv4 gateway. Connect Wi-Fi or Ethernet before enabling the Windows direct default.' }
    $up = @($physical | Where-Object Status -eq 'Up' | ForEach-Object ifIndex)
    $direct6 = Get-NetRoute -AddressFamily IPv6 -DestinationPrefix '::/0' -PolicyStore ActiveStore -ErrorAction SilentlyContinue |
        Where-Object { $_.State -ne 'Dead' -and $_.ifIndex -in $up } |
        Sort-Object @{Expression={[long]$_.RouteMetric+[long]$_.InterfaceMetric}},ifIndex,NextHop |
        Select-Object -First 1
    $plan = @(New-NcDefaultPlan $direct4 $direct6)
    # Netcontroll's explicit VPN sockets use IPv4. Without a physical IPv6
    # gateway, keep unselected IPv6 traffic from falling through the VPN.
    $denied6 = @(if (!$direct6) { $vpns | ForEach-Object Name })
    Set-Guard 'DefaultDirect-IPv6' @('::/0') $denied6
    Sync-NcDefaultRoutes $plan
}
