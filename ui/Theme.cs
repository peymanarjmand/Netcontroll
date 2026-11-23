using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;

namespace Netcontroll.Design
{
internal static class Theme
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static Window LoadWindow(string resource)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) return (
            Window)XamlReader.Load(stream);
    }

    public static void Attach(Window window, bool loadResources = true)
    {
        if (loadResources)
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("theme.xaml"))
                window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        window.SetResourceReference(Window.BackgroundProperty, "Canvas");
        window.SetResourceReference(Window.ForegroundProperty, "Ink");
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        if (!double.IsNaN(window.Width))
            window.Width = Math.Min(window.Width, area.Width);
        if (!double.IsNaN(window.Height))
            window.Height = Math.Min(window.Height, area.Height);
        if (window.SizeToContent != SizeToContent.Manual)
            window.MaxHeight = area.Height;
        window.SourceInitialized += (s, e) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
        };
    }
}
}
