[CmdletBinding()]
param([ValidateSet('Apply','Refresh','Remove')][string]$Action='Refresh',[string]$InputFile,[switch]$TakeOver,[switch]$AppsOnly)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'Netcontroll Routes'
$config=Join-Path $root 'routes.json'
$agent=Join-Path $root 'Routes.ps1'
$taskName='Netcontroll - IPv4 routes'
$legacyName='Direct IPv4 routes for four servers'
$legacyGuard='DirectServerRoutes-BlockOpenVPN'
$metric=1731
. (Join-Path $PSScriptRoot 'DefaultDirect.ps1')
$lock=New-Object Threading.Mutex($false,'Global\Netcontroll.RouteSettings.v1')
try { $entered=$lock.WaitOne(20000) } catch [Threading.AbandonedMutexException] { $entered=$true }
if(!$entered){throw 'Route manager is busy'}
function Read-Config {
    if(Test-Path -LiteralPath $config){return (Get-Content -LiteralPath $config -Raw -Encoding UTF8 | ConvertFrom-Json)}
    return [pscustomobject]@{Rules=@();Apps=@();Tracked=@();Legacy=@();Adopted=$false;WindowsDefaultDirect=$false}
}
function Write-Config($data) {
    $temp=Join-Path $root ([Guid]::NewGuid().ToString('N')+'.tmp')
    [IO.File]::WriteAllText($temp,($data|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    if([IO.File]::Exists($config)){[IO.File]::Replace($temp,$config,(Join-Path $root 'previous.json'))}else{[IO.File]::Move($temp,$config)}
}
function Set-Guard($kind,$ips,$aliases,$program='Any') {
    $name='Netcontroll.Routes.'+$kind
    $ips=@($ips|Sort-Object -Unique)
    $aliases=@($aliases|Sort-Object -Unique)
    $existing=@(Get-NetFirewallRule -Name ($name+'.*') -ErrorAction SilentlyContinue)
    if($ips.Count -and $aliases.Count){
        $sha=[Security.Cryptography.SHA256]::Create()
        try{$digest=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((@($ips)+@($aliases)+@($program) -join '|')))).Replace('-','').Substring(0,20)}finally{$sha.Dispose()}
        $current=$name+'.'+$digest
        if(!($existing | Where-Object Name -eq $current)){
            New-NetFirewallRule -Name $current -DisplayName ('Netcontroll route - '+$kind) -Direction Outbound -Action Block -Protocol Any -Profile Any -Program $program -RemoteAddress $ips -InterfaceAlias $aliases | Out-Null
        }
        $existing=@($existing|Where-Object Name -ne $current)
    }
    foreach($rule in $existing){Remove-NetFirewallRule -Name $rule.Name}
}
function Reconcile($state){
    $adapters=@(Get-NetAdapter)
    $physical=@($adapters|Where-Object {$_.HardwareInterface -or $_.PnPDeviceID -match '^(USB|BTH|BTHENUM)\\'})
    $vpns=@($adapters|Where-Object {$_.InterfaceDescription -match 'OpenVPN|Wintun|TAP-Windows|WireGuard'})
    $all=@(Get-NetRoute -AddressFamily IPv4 -PolicyStore ActiveStore)
    $default=@($all|Where-Object {$_.State -ne 'Dead' -and $_.DestinationPrefix -in '0.0.0.0/0','0.0.0.0/1','128.0.0.0/1'} | Sort-Object @{Expression={[long]$_.RouteMetric+[long]$_.InterfaceMetric}},ifIndex,NextHop)
    $direct=$default|Where-Object {$_.DestinationPrefix -eq '0.0.0.0/0' -and $_.ifIndex -in @($physical|Where-Object Status -eq 'Up'|ForEach-Object ifIndex)}|Select-Object -First 1
    $vpn=$default|Where-Object {$_.ifIndex -in @($vpns|Where-Object Status -eq 'Up'|ForEach-Object ifIndex)}|Select-Object -First 1
    Set-NcDefaultDirect ([bool]$state.WindowsDefaultDirect) $physical $vpns $direct
    Set-Guard 'Direct' @($state.Rules|Where-Object Mode -eq 'Direct'|ForEach-Object IP) @($vpns|ForEach-Object Name)
    Set-Guard 'VPN' @($state.Rules|Where-Object Mode -eq 'Vpn'|ForEach-Object IP) @($physical|ForEach-Object Name)
    $keep=@()
    foreach($appRule in $state.Apps){
        $key='App-'+$appRule.Id
        $denied=if($appRule.Mode -eq 'Direct'){$vpns}else{$physical}
        Set-Guard $key @('Any') @($denied|ForEach-Object Name) $appRule.Program
        $keep+=('Netcontroll.Routes.'+$key)
    }
    foreach($guard in @(Get-NetFirewallRule -Name 'Netcontroll.Routes.App-*' -ErrorAction SilentlyContinue)){
        if($guard.Name.Substring(0,$guard.Name.LastIndexOf('.')) -notin $keep){Remove-NetFirewallRule -Name $guard.Name}
    }
    foreach($ip in @($state.Tracked)){
        $prefix=$ip+'/32'
        $wanted=$state.Rules|Where-Object IP -eq $ip|Select-Object -First 1
        $gateway=if($wanted){if($wanted.Mode -eq 'Direct'){$direct}else{$vpn}}
        $owned=@($all|Where-Object {$_.DestinationPrefix -eq $prefix -and $_.RouteMetric -eq $metric -and $_.Protocol -eq 'NetMgmt'})
        foreach($r in $owned){
            if(!$gateway -or $r.ifIndex -ne $gateway.ifIndex -or $r.NextHop -ne $gateway.NextHop){
                Remove-NetRoute -DestinationPrefix $prefix -InterfaceIndex $r.ifIndex -NextHop $r.NextHop -PolicyStore ActiveStore -Confirm:$false
            }
        }
        if($gateway -and !($owned|Where-Object {$_.ifIndex -eq $gateway.ifIndex -and $_.NextHop -eq $gateway.NextHop})){
            New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $gateway.ifIndex -NextHop $gateway.NextHop -RouteMetric $metric -PolicyStore ActiveStore | Out-Null
        }
    }
}
function Register-Agent {
    $file=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $start=(Get-Date).AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ss')
    $xml=@"
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger><EventTrigger><Enabled>true</Enabled><Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="Microsoft-Windows-NetworkProfile/Operational"&gt;&lt;Select Path="Microsoft-Windows-NetworkProfile/Operational"&gt;*[System[(EventID=10000 or EventID=10001 or EventID=4004)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription><Delay>PT2S</Delay></EventTrigger><TimeTrigger><Repetition><Interval>PT2M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>$start</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
<Principals><Principal id="route"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT1M</ExecutionTimeLimit></Settings><Actions Context="route"><Exec><Command>$file</Command><Arguments>-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "$agent" -Action Refresh</Arguments><WorkingDirectory>$root</WorkingDirectory></Exec></Actions></Task>
"@
    Register-ScheduledTask -TaskName $taskName -Xml $xml -Force | Out-Null
}
function Resume-Legacy {
    Enable-NetFirewallRule -Name $legacyGuard -ErrorAction SilentlyContinue
    Enable-ScheduledTask -TaskName $legacyName | Out-Null
    Start-ScheduledTask -TaskName $legacyName
}
try {
    $old=Read-Config
    if($Action -eq 'Refresh'){
        try { Reconcile $old; Write-NcRouteStatus $old $true '' }
        catch { Write-NcRouteStatus $old $false $_.Exception.Message; throw }
        return
    }
    if($Action -eq 'Remove'){
        $empty=[pscustomobject]@{Rules=@();Apps=@();Tracked=$old.Tracked;WindowsDefaultDirect=$false}
        Reconcile $empty
        if($old.Adopted){Resume-Legacy}
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        foreach($item in 'previous.json','routes.json','Routes.ps1','DefaultDirect.ps1','default-direct-owned.json','route-status.json'){Remove-Item -LiteralPath (Join-Path $root $item) -ErrorAction SilentlyContinue}
        return
    }
    $data=Get-Content -LiteralPath $InputFile -Raw -Encoding UTF8|ConvertFrom-Json
    $rules=@()
    foreach($r in $data.Rules){
        $ip=[ipaddress]::Parse([string]$r.IP)
        if($ip.AddressFamily -ne 'InterNetwork' -or $ip.ToString() -cne $r.IP -or [ipaddress]::IsLoopback($ip) -or $ip.GetAddressBytes()[0] -eq 0 -or $ip.GetAddressBytes()[0] -ge 224 -or $r.Mode -notin 'Direct','Vpn' -or $r.IP -in $rules.IP){throw 'Invalid host route'}
        $rules+= [pscustomobject]@{IP=$ip.ToString();Mode=[string]$r.Mode}
    }
    if($AppsOnly){$rules=@($old.Rules)}
    $apps=@()
    foreach($a in $data.Apps){
        if($a.Id -notmatch '^[0-9a-f]{32}$' -or $a.Mode -notin 'Vpn','Direct' -or $a.Program -notmatch '^[a-zA-Z]:\\' -or [IO.Path]::GetExtension($a.Program) -ne '.exe' -or $a.Id -in $apps.Id){throw 'Invalid application rule'}
        $apps+=[pscustomobject]@{Id=$a.Id;Program=[IO.Path]::GetFullPath($a.Program);Mode=$a.Mode}
    }
    $defaultDirect=[bool]$old.WindowsDefaultDirect
    if (!$AppsOnly -and $data.PSObject.Properties['WindowsDefaultDirect']) {
        if ($data.WindowsDefaultDirect -isnot [bool]) { throw 'WindowsDefaultDirect must be a JSON boolean' }
        $defaultDirect=$data.WindowsDefaultDirect
    }
    $legacy=@($old.Legacy);$adopting=$false
    if($TakeOver -and !$old.Adopted){
        $legacyFile=Join-Path $env:ProgramFiles 'DirectServerRoutes\Update-Routes.ps1'
        $previousTask=Get-ScheduledTask -TaskName $legacyName
        if($previousTask.Actions.Count -ne 1 -or !$previousTask.Actions[0].Arguments.Contains($legacyFile)){throw 'Unexpected legacy task'}
        $line=Get-Content -LiteralPath $legacyFile|Where-Object {$_ -match '^\$prefixes = @\('}|Select-Object -First 1
        $legacy=@([regex]::Matches($line,'([\d.]+)/32')|ForEach-Object {$_.Groups[1].Value})
        if(!$legacy.Count){throw 'Legacy addresses not found'}
        $adopting=$true
    }
    $next=[pscustomobject]@{Rules=$rules;Apps=$apps;Tracked=@(@($old.Tracked)+@($rules|ForEach-Object IP)+$legacy|Sort-Object -Unique);Legacy=$legacy;Adopted=($adopting -or $old.Adopted);WindowsDefaultDirect=$defaultDirect}
    [void][IO.Directory]::CreateDirectory($root)
    if((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected route agent folder'}
    if ([IO.Path]::GetFullPath($PSCommandPath) -ne [IO.Path]::GetFullPath($agent)) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DefaultDirect.ps1') -Destination (Join-Path $root 'DefaultDirect.ps1') -Force
        Copy-Item -LiteralPath $PSCommandPath -Destination $agent -Force
    }
    try {
        if($adopting){
            Disable-ScheduledTask -TaskName $legacyName | Out-Null
            Stop-ScheduledTask -TaskName $legacyName
            foreach($ip in $legacy){
                Get-NetRoute -DestinationPrefix ($ip+'/32') -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object RouteMetric -eq 17|Remove-NetRoute -Confirm:$false
            }
        }
        Register-Agent
        Reconcile $next
        if($adopting){Disable-NetFirewallRule -Name $legacyGuard}
        Write-Config $next
        Write-NcRouteStatus $next $true ''
    }catch{
        $cause=$_
        $restore=[pscustomobject]@{Rules=$old.Rules;Apps=$old.Apps;Tracked=$next.Tracked;WindowsDefaultDirect=[bool]$old.WindowsDefaultDirect}
        try{Reconcile $restore;Write-Config $old;Write-NcRouteStatus $old $false $cause.Exception.Message}finally{if($adopting){Resume-Legacy}}
        throw $cause
    }
}finally{$lock.ReleaseMutex();$lock.Dispose()}
