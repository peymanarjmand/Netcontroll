# Native packaged-app checks. Default run navigates and filters without changing settings.
# -ScanWindows also visits IP discovery, which can import known legacy routes into settings.
[CmdletBinding()]param([switch]$ScanWindows)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$project=Split-Path $PSScriptRoot
$exe=Join-Path $project 'build\payload\Netcontroll.exe'
if(Get-Process -Name Netcontroll -ErrorAction SilentlyContinue){throw 'Close Netcontroll before running the desktop smoke test'}
$p=Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
try{
    $timeout=(Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $p.Refresh()
        if($p.HasExited){throw 'Desktop process stopped before displaying the main window'}
    } while($p.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $timeout)
    if($p.MainWindowHandle -eq [IntPtr]::Zero){throw 'Main window missing'}
    $window=[Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $scope=[Windows.Automation.TreeScope]::Descendants
    function ById($id){
        $cond=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
        return $window.FindFirst($scope,$cond)
    }
    function Check($condition,$label){if(!$condition){throw $label};Write-Host "PASS $label"}
    function Navigate($key){
        $nav=ById ('Nav.'+$key)
        Check ($null -ne $nav) "navigation $key"
        $nav.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 250
    }
    foreach($key in 'overview','chrome','codex','docker','apps','ips','activity','about'){
        Check ($null -ne (ById ('Nav.'+$key))) "destination $key exists"
    }
    Check ((ById 'Title').Current.Name -eq 'نمای کلی شبکه') 'packaged app starts on the overview'
    Check ($null -ne (ById 'WindowsRouteStatus')) 'Windows apply status is distinct from the saved setting'
    Navigate 'chrome'
    foreach($mode in 'Direct','Vpn','System'){
        Check ($null -ne (ById ('ChromeDefault.'+$mode))) "browser default $mode selector"
    }
    Check ($null -ne (ById 'ChromeStatus')) 'extension acknowledgement visible'
    $search=ById 'Search'
    $pattern=$search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue('no-match-'+[guid]::NewGuid().ToString('N'))
    Start-Sleep -Milliseconds 200
    Check ((ById 'EmptyTitle').Current.Name -eq 'موردی با این فیلتر پیدا نشد') 'domain search empty state'
    $pattern.SetValue('')
    Navigate 'codex'
    Check ((ById 'Title').Current.Name -eq 'Codex Desktop') 'dedicated Codex Desktop page'
    Check ($null -ne (ById 'CodexExecutable') -and $null -ne (ById 'CodexObservedStatus')) 'Codex executable and observed activity fields'
    Navigate 'docker'
    Check ((ById 'Title').Current.Name -eq 'Docker Desktop') 'dedicated Docker page'
    Navigate 'apps'
    Check ($null -ne (ById 'Search')) 'application search'
    Navigate 'activity'
    Check ((ById 'Title').Current.Name -eq 'فعالیت اتصال‌ها') 'connection activity page'
    Navigate 'about'
    $email=ById 'DeveloperEmailField'
    Check ($null -ne $email -and $email.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'peymanarjmand308@gmail.com') 'exact developer email in the About page'
    if($ScanWindows){
        Navigate 'ips'
        $end=(Get-Date).AddSeconds(12)
        do{
            Start-Sleep -Milliseconds 350
            $status=ById 'Status'
        }while((!$status -or $status.Current.Name -notmatch 'اسکن انجام شد') -and (Get-Date) -lt $end)
        if(!$status -or $status.Current.Name -notmatch 'اسکن انجام شد'){throw ('Scan result unavailable: '+$status.Current.Name)}
        $private=Join-Path $env:LOCALAPPDATA 'Netcontroll\settings.json'
        if(Test-Path $private){
            $settings=Get-Content -LiteralPath $private -Raw -Encoding UTF8|ConvertFrom-Json
            foreach($entry in $settings.Addresses){
                $cond=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,[string]$entry.Name)
                if(!$window.FindFirst($scope,$cond)){throw 'Saved IP name missing from actual desktop list'}
            }
        }
        Write-Host 'PASS opt-in Windows route discovery and saved IP labels'
    }
    Write-Host 'PASS packaged desktop navigation, policy selectors, search, Codex preset and developer details'
}finally{
    if(!$p.HasExited){Stop-Process -Id $p.Id -ErrorAction Stop}
}
