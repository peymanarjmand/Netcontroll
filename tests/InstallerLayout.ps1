# Exercise the actual installer at desktop DPI without performing an install.
[CmdletBinding()]param([string]$Screenshot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,PresentationCore,PresentationFramework
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LayoutNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x,int y,int w,int h,uint flags);
}
'@
[void][LayoutNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$exe=Join-Path (Split-Path $PSScriptRoot) 'dist\Netcontroll-Setup.exe'
$proc=Start-Process -FilePath $exe -PassThru
try{
    $until=(Get-Date).AddSeconds(12)
    do{Start-Sleep -Milliseconds 150;$proc.Refresh()}while($proc.MainWindowHandle -eq [IntPtr]::Zero -and !$proc.HasExited -and (Get-Date) -lt $until)
    if($proc.HasExited -or $proc.MainWindowHandle -eq [IntPtr]::Zero){throw 'Installer window missing'}
    $handle=$proc.MainWindowHandle
    $dpi=[LayoutNative]::GetDpiForWindow($handle)
    $scale=$dpi/96
    $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
    function Get-Control($id){
        $property=[Windows.Automation.AutomationElement]::AutomationIdProperty
        $window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new($property,$id))
    }
    foreach($layout in @(@(830,610),@(560,420),@(400,350))){
        $ok=[LayoutNative]::SetWindowPos($handle,[IntPtr]::Zero,0,0,[int]($layout[0]*$scale),[int]($layout[1]*$scale),0x16)
        if(!$ok){throw 'Window resize failed'}
        Start-Sleep -Milliseconds 250
        $frame=$window.Current.BoundingRectangle
        $button=Get-Control 'PrimaryAction';$close=Get-Control 'CloseAction'
        if(!$button -or !$close){throw 'Footer controls missing'}
        $b=$button.Current.BoundingRectangle
        $c=$close.Current.BoundingRectangle
        $typeface=[Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Segoe UI Variable Text, Segoe UI, Tahoma'),[Windows.FontStyles]::Normal,[Windows.FontWeights]::SemiBold,[Windows.FontStretches]::Normal)
        $label=[Windows.Media.FormattedText]::new($button.Current.Name,[Globalization.CultureInfo]::GetCultureInfo('fa-IR'),[Windows.FlowDirection]::RightToLeft,$typeface,17,[Windows.Media.Brushes]::White,$scale)
        if(!$frame.Contains($b) -or !$frame.Contains($c) -or $b.IntersectsWith($c) -or $b.Height/$scale -lt 48 -or $b.Width/$scale -lt $label.Width+40){
            throw "Footer at $($layout -join 'x'): window=$frame primary=$b close=$c textWidth=$($label.Width) DPI=$dpi"
        }
        if($Screenshot -and $layout[0] -eq 830){
            $image=[Drawing.Bitmap]::new([int]$frame.Width,[int]$frame.Height)
            $g=[Drawing.Graphics]::FromImage($image);$dc=$g.GetHdc()
            try{if(![LayoutNative]::PrintWindow($handle,$dc,2)){throw 'Cannot capture installer window'}}finally{$g.ReleaseHdc($dc);$g.Dispose()}
            try{$image.Save($Screenshot,[Drawing.Imaging.ImageFormat]::Png)}finally{$image.Dispose()}
        }
        Write-Host "PASS $($layout -join 'x') at $dpi DPI; primary $([int]($b.Width/$scale)) x $([int]($b.Height/$scale)) DIP"
    }
    (Get-Control 'CloseAction').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if(!$proc.WaitForExit(4000)){throw 'Installer did not close'}
}finally{if(!$proc.HasExited){[void]$proc.CloseMainWindow()}}
