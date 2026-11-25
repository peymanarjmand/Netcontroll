# Portable behavioral tests. Every Windows network cmdlet is replaced by an in-memory fake.
[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('netcontroll-default-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$script:FakeRoutes=@();$script:Removed=@();$script:Created=0;$script:FailAfter=-1;$script:Guards=@{}
function Get-NetRoute {
    param($AddressFamily,$DestinationPrefix,$InterfaceIndex,$NextHop,$PolicyStore,$ErrorAction)
    $filter=$PSBoundParameters
    return @($script:FakeRoutes|Where-Object {
        (!$filter.ContainsKey('AddressFamily') -or $_.AddressFamily -eq $AddressFamily) -and
        (!$filter.ContainsKey('DestinationPrefix') -or $_.DestinationPrefix -eq $DestinationPrefix) -and
        (!$filter.ContainsKey('InterfaceIndex') -or $_.InterfaceIndex -eq $InterfaceIndex) -and
        (!$filter.ContainsKey('NextHop') -or $_.NextHop -eq $NextHop)
    })
}
function New-NetRoute {
    param($AddressFamily,$DestinationPrefix,$InterfaceIndex,$NextHop,$RouteMetric,$Protocol,$PolicyStore,$ErrorAction)
    if($PolicyStore -ne 'ActiveStore'){throw 'A test attempted persistent route mutation'}
    if($script:FailAfter -ge 0 -and $script:Created -ge $script:FailAfter){throw 'Injected route creation failure'}
    $script:Created++
    $script:FakeRoutes += [pscustomobject]@{AddressFamily=$AddressFamily;DestinationPrefix=$DestinationPrefix;InterfaceIndex=$InterfaceIndex;ifIndex=$InterfaceIndex;NextHop=$NextHop;RouteMetric=$RouteMetric;Protocol=$Protocol;State='Alive';InterfaceMetric=10}
}
function Remove-NetRoute {
    [CmdletBinding()]param([Parameter(ValueFromPipeline)]$InputObject,[switch]$Confirm)
    process {
        $script:Removed += $InputObject
        $script:FakeRoutes=@($script:FakeRoutes|Where-Object {$_ -ne $InputObject})
    }
}
function Set-Guard($kind,$ips,$aliases,$program='Any') {
    $script:Guards[$kind]=[pscustomobject]@{IPs=@($ips);Aliases=@($aliases);Program=$program}
}
. (Join-Path (Split-Path $PSScriptRoot) 'system/DefaultDirect.ps1')
$script:Pass=0
function Check($condition,$name){if(!$condition){throw ('FAIL '+$name)};$script:Pass++;Write-Host ('PASS '+$name)}
function Throws([scriptblock]$work,$name){$rejected=$false;try{& $work}catch{$rejected=$true};Check $rejected $name}
function Reset {
    $script:FakeRoutes=@();$script:Removed=@();$script:Created=0;$script:FailAfter=-1;$script:Guards=@{}
    if(Test-Path -LiteralPath $script:NcDefaultJournal){Remove-Item -LiteralPath $script:NcDefaultJournal}
}
$direct=[pscustomobject]@{ifIndex=7;NextHop='192.168.1.1'}
$direct6=[pscustomobject]@{ifIndex=7;NextHop='fe80::1'}
try {
    $v4=@(New-NcDefaultPlan $direct $null);$both=@(New-NcDefaultPlan $direct $direct6)
    Check ($v4.Count -eq 4 -and $both.Count -eq 8) 'four IPv4 and four IPv6 covering routes'
    Check (@($v4|Where-Object {$_.RouteMetric -ne 1732 -or $_.InterfaceIndex -ne 7}).Count -eq 0) 'owned route metric and selected interface'
    Sync-NcDefaultRoutes $v4
    Check ($script:FakeRoutes.Count -eq 4 -and @(Read-NcDefaultJournal).Count -eq 4) 'apply journals all created routes'
    Sync-NcDefaultRoutes $v4
    Check ($script:Created -eq 4) 'refresh is idempotent'
    $vpn=[pscustomobject]@{AddressFamily='IPv4';DestinationPrefix='0.0.0.0/1';InterfaceIndex=40;ifIndex=40;NextHop='10.8.0.1';RouteMetric=1;Protocol='NetMgmt'}
    $hostRoute=[pscustomobject]@{AddressFamily='IPv4';DestinationPrefix='192.0.2.1/32';InterfaceIndex=40;ifIndex=40;NextHop='10.8.0.1';RouteMetric=1731;Protocol='NetMgmt'}
    $script:FakeRoutes+=$vpn;$script:FakeRoutes+=$hostRoute
    $ethernet=[pscustomobject]@{ifIndex=9;NextHop='192.168.2.1'}
    Sync-NcDefaultRoutes @(New-NcDefaultPlan $ethernet $null)
    Check (@($script:FakeRoutes|Where-Object InterfaceIndex -eq 7).Count -eq 0) 'gateway switch removes old owned routes'
    Check ($script:FakeRoutes.Count -eq 6 -and $vpn -in $script:FakeRoutes -and $hostRoute -in $script:FakeRoutes) 'VPN defaults and explicit host rules are preserved'
    Sync-NcDefaultRoutes @()
    Check ($script:FakeRoutes.Count -eq 2 -and @(Read-NcDefaultJournal).Count -eq 0) 'disable removes only journaled routes'
    Reset
    $collision=[pscustomobject]@{AddressFamily='IPv4';DestinationPrefix='128.0.0.0/2';InterfaceIndex=7;NextHop='192.168.1.1';RouteMetric=4;Protocol='NetMgmt'}
    $script:FakeRoutes=@($collision)
    Throws {Sync-NcDefaultRoutes $v4} 'foreign matching tuple is rejected before mutation'
    Check ($script:Created -eq 0 -and $script:Removed.Count -eq 0 -and $script:FakeRoutes.Count -eq 1) 'collision never adopts or deletes existing route'
    $collision.InterfaceIndex=40;$collision.NextHop='10.8.0.1'
    Throws {Sync-NcDefaultRoutes $v4} 'equal prefix on another interface is rejected'
    Check ($script:Created -eq 0 -and $script:Removed.Count -eq 0) 'competing VPN route cannot silently win by metric'
    Reset
    $script:FailAfter=2
    Throws {Sync-NcDefaultRoutes $v4} 'partial creation failure is observable'
    Check (@(Read-NcDefaultJournal).Count -eq 3 -and $script:FakeRoutes.Count -eq 2) 'journal exists before failed create for crash recovery'
    Sync-NcDefaultRoutes @()
    Check ($script:FakeRoutes.Count -eq 0 -and @(Read-NcDefaultJournal).Count -eq 0) 'rollback removes partially created routes'
    Reset
    Write-NcDefaultJournal @($v4[0])
    Sync-NcDefaultRoutes $v4
    Check ($script:FakeRoutes.Count -eq 4) 'interrupted journal-before-create resumes correctly'
    $script:FakeRoutes[0].RouteMetric=22
    Sync-NcDefaultRoutes @()
    Check ($script:FakeRoutes.Count -eq 1 -and $script:FakeRoutes[0].RouteMetric -eq 22) 'externally modified owned route is never deleted'
    Reset
    $bad=[pscustomobject]@{AddressFamily='IPv4';DestinationPrefix='0.0.0.0/0';InterfaceIndex=7;NextHop='192.168.1.1';RouteMetric=1732}
    Write-NcDefaultJournal @($bad)
    Throws {Sync-NcDefaultRoutes @()} 'corrupted ownership journal fails before touching routes'
    Check ($script:Removed.Count -eq 0) 'corrupted journal cannot delete any route'
    Reset
    $physical=@([pscustomobject]@{ifIndex=7;Status='Up';Name='Wi-Fi'})
    $vpns=@([pscustomobject]@{ifIndex=40;Status='Up';Name='OpenVPN'})
    Set-NcDefaultDirect $true $physical $vpns $direct
    Check ($script:Guards['DefaultDirect-IPv6'].IPs[0] -eq '::/0' -and $script:Guards['DefaultDirect-IPv6'].Aliases[0] -eq 'OpenVPN') 'absent physical IPv6 prevents VPN IPv6 fallback'
    $script:FakeRoutes+=[pscustomobject]@{AddressFamily='IPv6';DestinationPrefix='::/0';InterfaceIndex=7;ifIndex=7;NextHop='fe80::1';RouteMetric=10;InterfaceMetric=10;State='Alive';Protocol='RouterAdvertisement'}
    Set-NcDefaultDirect $true $physical $vpns $direct
    Check (@($script:FakeRoutes|Where-Object RouteMetric -eq 1732).Count -eq 8) 'physical IPv6 receives its own routes'
    Check ($script:Guards['DefaultDirect-IPv6'].Aliases.Count -eq 0) 'physical IPv6 clears fallback firewall restriction'
    Set-NcDefaultDirect $false @() @() $null
    Check ($script:FakeRoutes.Count -eq 1 -and $script:FakeRoutes[0].DestinationPrefix -eq '::/0') 'disable works while disconnected and preserves physical default'
    Throws {Set-NcDefaultDirect $true $physical $vpns $null} 'enable without physical gateway is rejected'
    Write-NcRouteStatus ([pscustomobject]@{WindowsDefaultDirect=$true}) $false 'Injected failure'
    $report=Get-Content -LiteralPath (Join-Path $root 'route-status.json') -Raw|ConvertFrom-Json
    Check ($report.Success -eq $false -and $report.Message -eq 'Injected failure' -and $report.CheckedAtUtc) 'status distinguishes requested mode from successful application'
    Write-Host ("PASS all $script:Pass default-direct behavioral checks")
} finally {if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}}
