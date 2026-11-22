# Verify the actual WPF executable using the Windows UI Automation client.
[CmdletBinding()]param()
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
    $nav=ById 'Nav.ips'
    if(!$nav){throw 'IP navigation missing'}
    $nav.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
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
    Write-Host 'PASS packaged desktop startup, IP navigation, scan completion and saved labels'
}finally{
    if(!$p.HasExited){Stop-Process -Id $p.Id -ErrorAction Stop}
}
