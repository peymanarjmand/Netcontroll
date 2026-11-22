[CmdletBinding()]param([string]$DeleteSnapshot)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
# Enumerate before filtering so an empty persistent store is a normal result.
$all=@(Get-NetRoute -PolicyStore PersistentStore -ErrorAction SilentlyContinue | Where-Object {
    $_.AddressFamily -eq 'IPv4' -and $_.DestinationPrefix -match '/32$' -and $_.NextHop -ne '0.0.0.0'
})
if(!$DeleteSnapshot){
    $items=@($all|ForEach-Object {
        [pscustomobject]@{IP=$_.DestinationPrefix.Split('/')[0];Gateway=$_.NextHop;Index=[int]$_.ifIndex;Metric=[int]$_.RouteMetric}
    })
    ConvertTo-Json -InputObject $items -Compress
    return
}
$picked=Get-Content -LiteralPath $DeleteSnapshot -Raw -Encoding UTF8|ConvertFrom-Json
$ip=[IPAddress]::Parse([string]$picked.IP)
if($ip.AddressFamily -ne 'InterNetwork' -or $ip.ToString() -cne $picked.IP){throw 'Invalid IPv4 address'}
$prefix=$ip.ToString()+'/32'
$routes=@($all|Where-Object DestinationPrefix -eq $prefix)
if($routes.Count -ne 1 -or $routes[0].ifIndex -ne $picked.Index -or $routes[0].NextHop -ne $picked.Gateway -or $routes[0].RouteMetric -ne $picked.Metric){throw 'The saved route changed. Scan again.'}
$tuple=@{DestinationPrefix=$prefix;InterfaceIndex=$routes[0].ifIndex;NextHop=$routes[0].NextHop}
Remove-NetRoute @tuple -PolicyStore PersistentStore -Confirm:$false
try{
    $active=Get-NetRoute @tuple -PolicyStore ActiveStore -ErrorAction SilentlyContinue
    if($active -and $active.RouteMetric -eq $picked.Metric){Remove-NetRoute @tuple -PolicyStore ActiveStore -Confirm:$false}
}catch{
    $reason=$_
    $existing=Get-NetRoute @tuple -PolicyStore PersistentStore -ErrorAction SilentlyContinue
    if(!$existing){New-NetRoute @tuple -PolicyStore PersistentStore -RouteMetric $picked.Metric|Out-Null}
    throw $reason
}
