using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Netcontroll
{
public sealed class Editor
{
    public static Rule Open(Window owner, string page, Rule old, PathMode initialMode = PathMode.Direct)
    {
        var result = old == null ? new Rule { Mode = initialMode } : old.Copy();
        bool app = page == "apps", ip = page == "ips";
        var w = new Window { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 500, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Title = old == null ? "افزودن قانون" : "ویرایش قانون", FlowDirection = FlowDirection.RightToLeft,
            FontFamily = owner.FontFamily, FontSize = 13, Resources = owner.Resources,
            UseLayoutRounding = true, Language = System.Windows.Markup.XmlLanguage.GetLanguage("fa-IR") };
        Netcontroll.Design.Theme.Attach(w, false);
        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
        w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(new TextBlock { Text = app ? "برنامه و مسیر اتصال" : ip ? "سرور و مسیر ویندوز" : "قانون اتصال سایت",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 139, 88)), FontSize = 11 });
        panel.Children.Add(new TextBlock { Text = w.Title, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 4) });
        panel.Children.Add(new TextBlock { Text = app ? "برنامه باید پراکسی HTTP را بپذیرد؛ مسیر انتخابی برای اتصال‌های جدید است."
            : ip ? "این قانون برای آدرس IPv4 در کل ویندوز اعمال می‌شود و دسترسی مدیر می‌خواهد."
            : "قانون سایت بین تب‌های همان دامنه مشترک است. برای جداسازی تب‌ها، پنجرهٔ مستقل باز کنید.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(170, 175, 187)),
            FontSize = 12, Margin = new Thickness(0, 0, 0, 10) });
        Action<string> label = text => panel.Children.Add(new TextBlock { Text = text, FontSize = 12,
            Margin = new Thickness(0, 10, 0, 5) });
        label(ip ? "نام سرور" : "نام نمایشی (اختیاری)");
        var name = new TextBox { Text = result.Name, MaxLength = 80 };
        System.Windows.Automation.AutomationProperties.SetName(name, "نام نمایشی قانون");
        panel.Children.Add(name);
        label(app ? "مسیر فایل برنامه" : ip ? "آدرس IPv4" : "دامنه یا نشانی سایت");
        var target = new TextBox { Text = result.Target, FlowDirection = FlowDirection.LeftToRight,
            MaxLength = 700, ToolTip = app ? @"C:\Program Files\App\App.exe" : ip ? "203.0.113.10" : "example.com" };
        System.Windows.Automation.AutomationProperties.SetName(target, app ? "مسیر فایل اجرایی" : ip ? "آدرس IPv4" : "نام دامنه");
        panel.Children.Add(target);
        if (app)
        {
            var browse = new Button { Content = "انتخاب فایل EXE", HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 7, 0, 0) };
            browse.Click += (s, e) =>
            {
                var file = new OpenFileDialog { Filter = "Windows applications|*.exe" };
                if (file.ShowDialog(w) == true)
                {
                    target.Text = file.FileName;
                    if (name.Text.Trim() == "") name.Text = Path.GetFileNameWithoutExtension(file.FileName);
                }
            };
            panel.Children.Add(browse);
        }
        label("مسیر درخواست‌های این مورد");
        var modes = new WrapPanel();
        PathMode selected = result.Mode;
        foreach (var option in ip ? new[] { PathMode.Direct, PathMode.Vpn } : new[] { PathMode.Direct, PathMode.Vpn, PathMode.System })
        {
            var choice = option;
            var radio = new RadioButton { Content = option == PathMode.Direct ? "اینترنت عادی" : option == PathMode.Vpn ? "فقط VPN" : "مسیر سیستم",
                IsChecked = selected == option, GroupName = "RouteMode" };
            radio.Checked += (sender, args) => selected = choice;
            modes.Children.Add(radio);
        }
        panel.Children.Add(modes);
        var sub = new CheckBox { Content = "زیردامنه‌ها هم شامل شوند", IsChecked = result.Subdomains,
            Margin = new Thickness(0, 14, 0, 0) };
        if (!app && !ip) panel.Children.Add(sub);
        var enabled = new CheckBox { Content = "قانون فعال باشد", IsChecked = result.Enabled,
            Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(enabled);
        var guard = new CheckBox { Content = "قفل فایروال برای مسیر مخالف این فایل EXE", IsChecked = result.Guard,
            Margin = new Thickness(0, 9, 0, 4) };
        if (app)
        {
            panel.Children.Add(guard);
            panel.Children.Add(new TextBlock { Text = "اختیاری و نیازمند مدیر ویندوز؛ قفل فقط محدودیت ایجاد می‌کند و جایگزین پشتیبانی برنامه از پراکسی نیست.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(170, 175, 187)),
                Margin = new Thickness(25, 0, 0, 0) });
        }
        var error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(241, 160, 179)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        panel.Children.Add(error);
        var buttons = new WrapPanel { Margin = new Thickness(-3, 10, -3, 0) };
        panel.Children.Add(buttons);
        var save = new Button { Content = "ذخیرهٔ قانون", IsDefault = true, MinWidth = 115 };
        save.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "انصراف", IsCancel = true, MinWidth = 80 });
        w.Loaded += (s, e) => { if (old == null) target.Focus(); else name.Focus(); };
        save.Click += (s, e) =>
        {
            try
            {
                string value = target.Text.Trim();
                if (ip) value = Settings.IPv4(value);
                else if (app)
                {
                    if (!Path.IsPathRooted(value) || !File.Exists(value) || !String.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("یک فایل اجرایی موجود انتخاب کنید.");
                    value = Path.GetFullPath(value);
                }
                else
                {
                    Uri pasted;
                    if (Uri.TryCreate(value, UriKind.Absolute, out pasted) && (pasted.Scheme == "https" || pasted.Scheme == "http")) value = pasted.Host;
                    value = Settings.Host(value);
                }
                if (app && guard.IsChecked == true && selected == PathMode.System)
                    throw new ArgumentException("برای قفل فایروال، اینترنت عادی یا VPN را مشخص کنید؛ مسیر سیستم جهت مشخصی ندارد.");
                result.Target = value;
                result.Name = String.IsNullOrWhiteSpace(name.Text) ? (app ? Path.GetFileNameWithoutExtension(value) : value) : name.Text.Trim();
                result.Enabled = enabled.IsChecked == true; result.Subdomains = sub.IsChecked == true;
                result.Guard = app && guard.IsChecked == true; result.Mode = selected;
                w.DialogResult = true;
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        return w.ShowDialog() == true ? result : null;
    }
}
}
