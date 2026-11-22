# Interactive installation regression. This leaves the tested application installed and open.
[CmdletBinding(DefaultParameterSetName='Verify')] param(
    [Parameter(ParameterSetName='Install',Mandatory)][switch]$InstallForCurrentUser,
    [Parameter(ParameterSetName='Verify')][switch]$VerifyInstalled
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$project=Split-Path $PSScriptRoot
$setup=Join-Path $project 'dist\Netcontroll-Setup.exe'
$root=Join-Path $env:LOCALAPPDATA 'Programs\Netcontroll'
$exe=Join-Path $root 'Netcontroll.exe'
$payload=Join-Path $project 'build\payload'

function Wait-Until($predicate,$failure){
    $end=(Get-Date).AddSeconds(20)
    do{$value=& $predicate;if($value){return $value};Start-Sleep -Milliseconds 150}while((Get-Date) -lt $end)
    throw $failure
}
function Control($window,$id){
    $property=[Windows.Automation.AutomationElement]::AutomationIdProperty
    $condition=[Windows.Automation.PropertyCondition]::new($property,$id)
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}
function ByName($window,$name){
    $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
    $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}

if($InstallForCurrentUser){
    if(Get-Process -Name Netcontroll -ErrorAction SilentlyContinue){throw 'Exit Netcontroll before testing an installation.'}
    $installer=Start-Process -FilePath $setup -PassThru
    $window=Wait-Until {
        $installer.Refresh()
        if($installer.HasExited){throw 'Installer stopped before presenting its UI.'}
        if($installer.MainWindowHandle -ne [IntPtr]::Zero){
            [Windows.Automation.AutomationElement]::FromHandle($installer.MainWindowHandle)
        }
    } 'Installer window did not appear.'
    $primary=Wait-Until {
        foreach($name in 'نصب برنامه','نصب مجدد / به‌روزرسانی'){
            $button=ByName $window $name
            if($button){return $button}
        }
    } 'Install button not found.'
    $edit=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit)
    $pathControl=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$edit)
    if($pathControl.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne $root){throw 'Incorrect destination in installer.'}
    $primary.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $open=Wait-Until {ByName $window 'باز کردن برنامه'} 'Installation did not reach the completion screen.'
}
foreach($file in Get-ChildItem -LiteralPath $payload -File -Recurse){
    $installed=Join-Path $root $file.FullName.Substring($payload.Length+1)
    if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash){throw "Installed mismatch: $($file.Name)"}
}
if((Get-FileHash $setup).Hash -ne (Get-FileHash (Join-Path $root 'Setup.exe')).Hash){throw 'Uninstaller copy mismatch.'}
$key=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Netcontroll'
if($key.InstallLocation -ne $root -or $key.DisplayVersion -ne [Diagnostics.FileVersionInfo]::GetVersionInfo($setup).ProductVersion){throw 'Installed apps registration mismatch.'}
$shell=New-Object -ComObject WScript.Shell
foreach($dir in 'Programs','DesktopDirectory'){
    $link=Join-Path ([Environment]::GetFolderPath($dir)) 'Netcontroll.lnk'
    if(!(Test-Path -LiteralPath $link) -or $shell.CreateShortcut($link).TargetPath -ne $exe){throw "Bad $dir shortcut."}
}
if($InstallForCurrentUser){
    $open.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if(!$installer.WaitForExit(6000) -or $installer.ExitCode -ne 0){throw 'Installer did not exit normally.'}
}else{
    Invoke-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Netcontroll.lnk')
}
$app=Wait-Until {
    Get-Process -Name Netcontroll -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -eq $exe -and $_.MainWindowHandle -ne [IntPtr]::Zero -and $_.MainWindowTitle -eq 'Netcontroll'
    } | Select-Object -First 1
} 'Installed application did not open.'
$main=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
if(!(Control $main 'Nav.ips')){throw 'Expected main navigation missing.'}
Write-Host ('PASS installed files, version '+$key.DisplayVersion+', both shortcuts and main application window.')
