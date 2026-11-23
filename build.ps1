[CmdletBinding()] param()
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$out=Join-Path $root 'build'
$payload=Join-Path $out 'payload'
$dist=Join-Path $root 'dist'
foreach($dir in $out,$payload,$dist){[void][IO.Directory]::CreateDirectory($dir)}
$locator=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler=(& $locator -latest -products '*' -find 'MSBuild\Current\Bin\Roslyn\csc.exe'|Select-Object -First 1)
if(!$compiler){throw 'Install Visual Studio Build Tools with MSBuild and the C# compiler.'}
$fw=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('mscorlib','System','System.Core','System.Drawing','System.Management','System.Windows.Forms','System.Web.Extensions','System.Xml','System.Xaml','System.IO.Compression','System.IO.Compression.FileSystem','Microsoft.CSharp')|ForEach-Object {'/r:'+(Join-Path $fw ($_+'.dll'))}
$refs+=@('WindowsBase','PresentationCore','PresentationFramework')|ForEach-Object {'/r:'+(Join-Path $fw ('WPF\'+$_+'.dll'))}
Add-Type -AssemblyName System.Drawing
$bmp=New-Object Drawing.Bitmap 64,64
$g=[Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode='AntiAlias';$g.Clear([Drawing.ColorTranslator]::FromHtml('#252337'))
$pen=New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#B9A8FF')),7
$pen.StartCap='Round';$pen.EndCap='Round'
$g.DrawLine($pen,15,49,15,15);$g.DrawLine($pen,15,15,49,49);$g.DrawLine($pen,49,49,49,15)
$ico=Join-Path $out 'netcontroll.ico'
$file=[IO.File]::Create($ico);try{[Drawing.Icon]::FromHandle($bmp.GetHicon()).Save($file)}finally{$file.Dispose();$g.Dispose();$bmp.Dispose();$pen.Dispose()}
$common=@('/nologo','/noconfig','/nostdlib+','/langversion:7.3','/platform:x64','/optimize+','/utf8output','/codepage:65001','/deterministic+','/target:winexe',('/win32icon:'+$ico),('/win32manifest:'+(Join-Path $root 'src\app.manifest')))+$refs
$common+=('/resource:'+(Join-Path $root 'ui\Theme.xaml')+',theme.xaml')
$themeCode=Join-Path $root 'ui\Theme.cs'
$inputs=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs'|ForEach-Object FullName)
& $compiler @common ('/out:'+(Join-Path $payload 'Netcontroll.exe')) @inputs $themeCode
if($LASTEXITCODE){throw 'Desktop build failed.'}
Copy-Item -LiteralPath (Join-Path $root 'src\Main.xaml'),(Join-Path $root 'README.md') -Destination $payload -Force
foreach($name in 'chrome','system'){
    $to=Join-Path $payload $name;[void][IO.Directory]::CreateDirectory($to)
    Get-ChildItem -LiteralPath (Join-Path $root $name) -File|Copy-Item -Destination $to -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=Join-Path $out 'payload.zip'
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip}
[IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip)
& $compiler @common ('/resource:'+$zip+',payload.zip') ('/resource:'+(Join-Path $root 'installer\Installer.xaml')+',installer.xaml') $themeCode ('/out:'+(Join-Path $dist 'Netcontroll-Setup.exe')) (Join-Path $root 'installer\Setup.cs') (Join-Path $root 'src\AssemblyInfo.cs')
if($LASTEXITCODE){throw 'Installer build failed.'}
Get-Item -LiteralPath (Join-Path $dist 'Netcontroll-Setup.exe')|Select-Object FullName,Length
