# Build and update this user's installation without deleting their routing settings.
[CmdletBinding()]param([switch]$SkipPull,[switch]$BuildOnly)
$ErrorActionPreference='Stop'
$repo=$PSScriptRoot
if(!$SkipPull){
    if(!(Get-Command git -ErrorAction SilentlyContinue)){throw 'Install Git, or use -SkipPull after downloading the current source.'}
    $top=(& git -C $repo rev-parse --show-toplevel 2>$null)
    if($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($top) -ne [IO.Path]::GetFullPath($repo)){throw 'Run update.ps1 from the Netcontroll Git checkout; ZIP source requires -SkipPull.'}
    $branch=(& git -C $repo branch --show-current)
    if($LASTEXITCODE -ne 0 -or $branch -ne 'main'){throw 'Switch this checkout to main before updating.'}
    $remote=(& git -C $repo remote get-url origin)
    if($LASTEXITCODE -ne 0 -or $remote -notmatch '^(https://github\.com/|git@github\.com:)peymanarjmand/Netcontroll(?:\.git)?/?$'){
        throw 'origin is not peymanarjmand/Netcontroll. Verify the checkout before updating.'
    }
    $changes=@(& git -C $repo status --porcelain)
    if($LASTEXITCODE -ne 0){throw 'Cannot read repository status.'}
    if($changes.Count){throw 'This checkout has local changes. Commit or move them before updating; nothing was overwritten.'}
    & git -C $repo pull --ff-only origin main
    if($LASTEXITCODE -ne 0){throw 'Fast-forward update failed. Resolve the Git state before installing.'}
}
& (Join-Path $repo 'build.ps1')
$installer=Join-Path $repo 'dist\Netcontroll-Setup.exe'
if(!(Test-Path -LiteralPath $installer)){throw 'Build completed without an installer.'}
Get-FileHash -LiteralPath $installer -Algorithm SHA256|Format-List
if($BuildOnly){Write-Host ('Installer ready: '+$installer);return}
$settings=Join-Path $env:LOCALAPPDATA 'Netcontroll\settings.json'
if(Test-Path -LiteralPath $settings){
    $backup=Join-Path $env:LOCALAPPDATA 'Netcontroll\backups'
    [void][IO.Directory]::CreateDirectory($backup)
    $name='settings-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6)+'.json'
    Copy-Item -LiteralPath $settings -Destination (Join-Path $backup $name)
    Write-Host ('Settings backup: '+(Join-Path $backup $name))
}
Write-Host 'Choose Update in the installer. Your existing rules and browser profiles stay in place.'
$install=Start-Process -FilePath $installer -PassThru -Wait
if($install.ExitCode -ne 0){throw ('Installer exited with code '+$install.ExitCode)}
Write-Host 'After installation, reload Netcontroll at chrome://extensions and reconnect the extension.'
Write-Host 'Close Codex Desktop completely and launch it from the new Codex Desktop page.'
