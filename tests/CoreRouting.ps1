[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler=(& $locator -latest -products '*' -find 'MSBuild\Current\Bin\Roslyn\csc.exe'|Select-Object -First 1)
if(!$compiler){throw 'Visual Studio Build Tools with MSBuild/Roslyn is required.'}
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references=@('mscorlib','System','System.Core','System.Management','System.Web.Extensions') | ForEach-Object { '/r:'+(Join-Path $framework ($_+'.dll')) }
$output=Join-Path $root 'build\CoreRoutingTests.exe'
[void][IO.Directory]::CreateDirectory((Split-Path $output))
$inputs=@('src\Model.cs','src\Network.cs','src\Proxy.cs','src\AppProfiles.cs','tests\CoreRouting.cs') | ForEach-Object { Join-Path $root $_ }
& $compiler /nologo /noconfig /nostdlib+ /langversion:7.3 /target:exe /codepage:65001 /utf8output @references ('/out:'+$output) @inputs
if($LASTEXITCODE){throw 'Core routing test compilation failed.'}
& $output
if($LASTEXITCODE){throw 'Core routing tests failed.'}
