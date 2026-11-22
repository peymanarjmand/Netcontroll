# Run from an elevated Windows PowerShell on a machine with an active OpenVPN tunnel.
[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$systemRoot=Join-Path $env:ProgramFiles 'Netcontroll Routes'
$task='Netcontroll - IPv4 routes'
if((Test-Path -LiteralPath (Join-Path $systemRoot 'routes.json')) -or (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)){throw 'Test requires no existing Netcontroll route configuration'}
$prefix='192.0.2.44/32'
if(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue){throw 'Test address already in use'}
$direct=Get-NetRoute -DestinationPrefix '0.0.0.0/0'|Where-Object InterfaceIndex -in @(Get-NetAdapter -Physical|ForEach-Object ifIndex)|Select-Object -First 1
$vpn=Get-NetRoute -DestinationPrefix '0.0.0.0/1'|Where-Object InterfaceIndex -in @(Get-NetAdapter|Where-Object InterfaceDescription -match 'OpenVPN'|ForEach-Object ifIndex)|Select-Object -First 1
if(!$direct -or !$vpn){throw 'Direct and OpenVPN test paths are required'}
$folder=Join-Path $env:TEMP ('Netcontroll-RouteTest-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$file=Join-Path $folder 'request.json'
$script=Join-Path (Split-Path $PSScriptRoot) 'system\Routes.ps1'
try{
    foreach($pair in @(@('Direct',$direct),@('Vpn',$vpn))){
        [IO.File]::WriteAllText($file,(@{Rules=@(@{IP='192.0.2.44';Mode=$pair[0]})}|ConvertTo-Json -Depth 4))
        & $script -Action Apply -InputFile $file
        $selected=Find-NetRoute -RemoteIPAddress '192.0.2.44'|Where-Object DestinationPrefix
        if($selected.DestinationPrefix -ne $prefix -or $selected.ifIndex -ne $pair[1].ifIndex){throw 'Selected interface mismatch'}
        $guards=Get-NetFirewallRule -Name ('Netcontroll.Routes.'+$pair[0]+'.*') -PolicyStore ActiveStore
        if($guards.Action -ne 'Block' -or $guards.Enabled -ne 'True'){throw 'Missing effective firewall guard'}
    }
    Remove-NetRoute -DestinationPrefix $prefix -InterfaceIndex $vpn.ifIndex -NextHop $vpn.NextHop -PolicyStore ActiveStore -Confirm:$false
    Start-ScheduledTask -TaskName $task
    $deadline=(Get-Date).AddSeconds(25)
    do{Start-Sleep -Milliseconds 350;$r=Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue}while(!$r -and (Get-Date) -lt $deadline)
    if(!$r -or $r.ifIndex -ne $vpn.ifIndex){throw 'SYSTEM task failed to re-create the missing rule'}
    $testId=[guid]::NewGuid().ToString('N')
    $testProgram=Join-Path $folder 'Netcontroll-curl.exe'
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\curl.exe') -Destination $testProgram
    try{
        [IO.File]::WriteAllText($file,(@{Rules=@();Apps=@(@{Id=$testId;Program=$testProgram;Mode='Direct'})}|ConvertTo-Json -Depth 5))
        & $script -Action Apply -InputFile $file -AppsOnly
        $rule=Get-NetFirewallRule -Name ('Netcontroll.Routes.App-'+$testId+'.*') -PolicyStore ActiveStore
        $actualFile=[Environment]::ExpandEnvironmentVariables(($rule|Get-NetFirewallApplicationFilter).Program)
        $actualAliases=@(($rule|Get-NetFirewallInterfaceFilter).InterfaceAlias)
        $sameFile=(Test-Path -LiteralPath $actualFile) -and [IO.Path]::GetFileName($actualFile) -eq [IO.Path]::GetFileName($testProgram) -and [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($actualFile)) -eq [IO.Path]::GetFileName($folder) -and (Get-FileHash -LiteralPath $actualFile).Hash -eq (Get-FileHash -LiteralPath $testProgram).Hash
        if(!$sameFile -or $vpn.InterfaceAlias -notin $actualAliases){throw "Executable firewall scope: sameFile=$sameFile; path=$actualFile; aliases=$actualAliases; expected adapter=$($vpn.InterfaceAlias)"}
        if(!(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue)){throw 'An application rule disturbed IP rules'}
        $attempt=Start-Process -FilePath $testProgram -ArgumentList '--silent --noproxy * --connect-timeout 3 --max-time 6 -o NUL https://api.ipify.org/' -WindowStyle Hidden -Wait -PassThru
        if($attempt.ExitCode -eq 0){throw 'Direct-only application used the default VPN route'}
    }finally{Remove-Item -LiteralPath $testProgram -ErrorAction SilentlyContinue}
    [IO.File]::WriteAllText($file,'{"Rules": []}')
    & $script -Action Apply -InputFile $file
    if(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue){throw 'Deleting the rule left a host route'}
    $savedScript=Join-Path (Split-Path $PSScriptRoot) 'system\SavedRoutes.ps1'
    $tuple=@{DestinationPrefix=$prefix;InterfaceIndex=$direct.ifIndex;NextHop=$direct.NextHop}
    New-NetRoute @tuple -RouteMetric 48|Out-Null
    try{
        $saved=@((& $savedScript | ConvertFrom-Json)|Where-Object IP -eq '192.0.2.44')
        if($saved.Count -ne 1 -or $saved[0].Metric -ne 48){throw 'Saved route scan missed its test fixture'}
        $saved[0].Metric=49
        [IO.File]::WriteAllText($file,($saved[0]|ConvertTo-Json))
        $rejected=$false;try{& $savedScript -DeleteSnapshot $file}catch{$rejected=$true}
        if(!$rejected){throw 'Stale deletion snapshot accepted'}
        $saved[0].Metric=48
        [IO.File]::WriteAllText($file,($saved[0]|ConvertTo-Json))
        & $savedScript -DeleteSnapshot $file
        if((Get-NetRoute @tuple -PolicyStore ActiveStore -ErrorAction SilentlyContinue) -or (Get-NetRoute @tuple -PolicyStore PersistentStore -ErrorAction SilentlyContinue)){throw 'Saved host route survived explicit removal'}
    }finally{
        foreach($store in 'PersistentStore','ActiveStore'){
            $left=Get-NetRoute @tuple -PolicyStore $store -ErrorAction SilentlyContinue
            if($left -and $left.RouteMetric -eq 48){Remove-NetRoute @tuple -PolicyStore $store -Confirm:$false}
        }
    }
}finally{
    & $script -Action Remove
    Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
    [IO.Directory]::Delete($folder,$false)
}
if(Get-NetFirewallRule -Name 'Netcontroll.Routes.*' -ErrorAction SilentlyContinue){throw 'Firewall rule left after test'}
Write-Host 'PASS system IP add / switch / firewall / scheduled re-create / delete / uninstall'
