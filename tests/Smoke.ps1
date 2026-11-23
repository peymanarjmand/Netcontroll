[CmdletBinding()]param([switch]$Live,[switch]$Container)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$area=Join-Path (Join-Path $root 'build') ('smoke-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($area)
$payload=Join-Path $area 'installed'
$setup=Join-Path $root 'dist\Netcontroll-Setup.exe'
$p=Start-Process -FilePath $setup -ArgumentList ('--extract "'+$payload+'"') -Wait -PassThru -WindowStyle Hidden
if($p.ExitCode -ne 0){throw 'Installer extraction failed'}
foreach($f in Get-ChildItem $payload -Recurse -File){
    $relative=$f.FullName.Substring($payload.Length+1)
    if((Get-FileHash $f.FullName).Hash -ne (Get-FileHash (Join-Path (Join-Path $root 'build\payload') $relative)).Hash){throw "Payload mismatch: $relative"}
}
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml,System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom((Join-Path $payload 'Netcontroll.exe'))
function Check($condition,$label){if(!$condition){throw $label};Write-Host "PASS $label"}
function Rule($hostName,$mode){
    $r=New-Object Netcontroll.Rule;$r.Target=[Netcontroll.Settings]::Host($hostName);$r.Name=$hostName;$r.Mode=$mode;return $r
}
$s=New-Object Netcontroll.Settings
$parent=Rule 'example.net' Direct
$child=Rule 'assets.example.net' Vpn
$s.Domains.Add($parent);$s.Domains.Add($child)
Check ($s.Match('cdn.assets.example.net') -eq 'Vpn') 'nested longest domain match'
Check ($s.Match('falseexample.net') -eq 'System') 'domain boundary'
$child.Enabled=$false
Check ($s.Match('assets.example.net') -eq 'Direct') 'disabled override'
$parent.Subdomains=$false
Check ($s.Match('assets.example.net') -eq 'System') 'exact host setting'
Check ([Netcontroll.Settings]::IPv4('203.0.113.14') -eq '203.0.113.14') 'IPv4 validation'
$bad=$false;try{[void][Netcontroll.Settings]::IPv4('127.1')}catch{$bad=$true};Check $bad 'reject noncanonical IPv4'
$ip=New-Object Netcontroll.Rule;$ip.Name='Test server';$ip.Target='203.0.113.14';$s.Addresses.Add($ip)
Check ($s.Clone().Addresses[0].Name -eq 'Test server') 'server label persists'
$discovered=@([Netcontroll.Discovery]::Read() | ForEach-Object {$_})
$legacyPath=Join-Path $env:ProgramFiles 'DirectServerRoutes\Update-Routes.ps1'
if((Test-Path -LiteralPath $legacyPath) -and !(Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'Netcontroll Routes\routes.json'))){
    $intent=Get-Content -LiteralPath $legacyPath|Where-Object {$_ -match '^\$prefixes = @\('}|Select-Object -First 1
    $expected=@([regex]::Matches($intent,'([\d.]+)/32')|ForEach-Object {$_.Groups[1].Value})
    $known=@($discovered|Where-Object Known)
    Check ($expected.Count -gt 0 -and @($expected|Where-Object {$_ -notin $known.IP}).Count -eq 0) "auto-discovery of all $($expected.Count) legacy rules"
    Check (@($known|Where-Object {$_.Exact -and $_.Adapter}).Count -eq $expected.Count) 'discovered rules show live Windows host routes'
}
$router=New-Object Netcontroll.Router
try{
    $router.Configure($s)
    $endpoint='http://127.0.0.1:17831/chrome-rules/'
    $client=New-Object Net.WebClient
    try{
        $client.Proxy=$null;$client.Headers.Add('X-Netcontroll-Client','chrome-v1')
        $policy=$client.DownloadString($endpoint)|ConvertFrom-Json
        Check ($policy.rules.Count -eq 1 -and $policy.rules[0].host -eq 'example.net') 'existing Chrome profile receives only active domain rules'
        $watch=$client.DownloadStringTaskAsync($endpoint+$policy.revision)
        $parent.Mode='System';$router.Configure($s)
        Check ($watch.Wait(4000) -and !(($watch.Result|ConvertFrom-Json).rules[0].route)) 'live extension rule notification'
        $parent.Mode='Direct';$router.Configure($s)
    }finally{$client.Dispose()}
    $app=New-Object System.Windows.Application
    $inputFile=[IO.File]::OpenRead((Join-Path $payload 'Main.xaml'))
    try{$window=[Windows.Markup.XamlReader]::Load($inputFile)}finally{$inputFile.Dispose()}
    $shell=New-Object Netcontroll.Shell($window,$s,$router)
    try{
        $window.ShowInTaskbar=$false;$window.ShowActivated=$false;$window.WindowStartupLocation='Manual';$window.Left=-15000;$window.Top=0;$window.Show();$window.UpdateLayout()
        $window.Content.Background=$window.Background
        Check ($window.FindName('Nav').Children.Count -eq 5 -and $window.FindName('Rows').Children.Count -eq 2) 'WPF navigation and domain rows'
        $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap([int][Math]::Ceiling($window.Content.ActualWidth),[int][Math]::Ceiling($window.Content.ActualHeight),96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window.Content)
        $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder;$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $shot=[IO.File]::Create((Join-Path $area 'dashboard.png'));try{$encoder.Save($shot)}finally{$shot.Dispose()}
    }finally{$shell.Dispose();$window.Hide()}
    if($Live){
        $chrome=Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'
        $hostName='api.ipify.org'
        $rule=Rule $hostName Direct;$s.Domains.Add($rule)
        foreach($mode in 'Direct','Vpn'){
            $link=[Netcontroll.Network]::Choose($mode);$rule.Mode=$mode;$router.Configure($s)
            $profile=Join-Path $area ('chrome-'+$mode)
            $html=Join-Path $area ($mode+'.html')
            $argsList='--headless --disable-quic --disable-background-networking --no-first-run --no-default-browser-check --proxy-server="http://127.0.0.1:17831" --user-data-dir="'+$profile+'" --dump-dom https://'+$hostName+'/?format=json'
            $started=Get-Date
            $proc=Start-Process -FilePath $chrome -ArgumentList $argsList -RedirectStandardOutput $html -RedirectStandardError (Join-Path $area ($mode+'.err')) -WindowStyle Hidden -PassThru
            if(!$proc.WaitForExit(35000)){Stop-Process -Id $proc.Id -ErrorAction SilentlyContinue;throw "Chrome $mode timed out"}
            $page=Get-Content -Raw -LiteralPath $html
            $events=@($router.History.ToArray() | Where-Object {$_.Time -ge $started -and $_.Source -eq 'Chrome' -and $_.Host -eq $hostName -and $_.Success -and $_.Mode -eq $mode -and $_.Local -eq $link.Source.ToString()})
            Check ($events.Count -gt 0) "Chrome $mode socket on selected adapter"
            $match=[regex]::Match([string]$page,'"ip"\s*:\s*"([0-9a-fA-F.:]+)"')
            Check $match.Success "Chrome $mode TLS egress response"
            $external=$match.Groups[1].Value
            if($mode -eq 'Direct'){$firstExternal=$external}else{Check ($firstExternal -ne $external) 'distinct public egress on real Chrome requests'}
        }
        $tool=New-Object Netcontroll.Rule;$tool.Name='curl';$tool.Target=(Get-Command curl.exe).Source;$tool.Port=17900;$tool.Mode='Direct';$s.Apps.Add($tool);$router.Configure($s)
        & curl.exe -sS -f --noproxy 'invalid.local' --proxy http://127.0.0.1:17900 --max-time 15 -o NUL https://api.ipify.org/
        Check ($LASTEXITCODE -eq 0 -and @($router.History.ToArray()|Where-Object {$_.Source -eq 'curl' -and $_.Success}).Count -gt 0) 'separate application proxy'
        $s.Apps.Clear();$s.Domains.Clear();$router.Configure($s)
        Check ($s.Match($hostName) -eq 'System') 'remove rule restores default'
    }
    if($Container){
        $javascript="const n=require('net');const s=n.connect(17832,'host.docker.internal',()=>s.write('CONNECT one.one.one.one:443 HTTP/1.1\r\nHost: one.one.one.one:443\r\n\r\n'));s.setTimeout(10000,()=>process.exit(2));s.once('data',b=>{console.log(b.toString().split('\r\n')[0]);process.exit(b.toString().includes('200 Connection Established')?0:3)});s.on('error',()=>process.exit(4))"
        foreach($mode in 'Direct','Vpn'){
            $s.Docker=$mode;$router.Configure($s)
            $javascript | & docker run --rm -i --pull never node:24.16.0-bookworm-slim node
            Check ($LASTEXITCODE -eq 0 -and @($router.History.ToArray()|Where-Object {$_.Source -eq 'Docker' -and $_.Mode -eq $mode -and $_.Success}).Count -gt 0) "live container via Docker proxy in $mode mode"
        }
    }
    Write-Host "Output: $area"
}finally{$router.Dispose()}
