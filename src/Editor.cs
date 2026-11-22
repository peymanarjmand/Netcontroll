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
    public static Rule Open(Window owner, string page, Rule old)
    {
        var result = old == null ? new Rule() : old.Copy();
        bool app = page == "apps", ip = page == "ips";
        var w = new Window { Owner = owner,
                             WindowStartupLocation = WindowStartupLocation.CenterOwner,
                             Width = 455,
                             SizeToContent = SizeToContent.Height,
                             ResizeMode = ResizeMode.NoResize,
                             Title = old == null ? "افزودن قانون" : "ویرایش قانون",
                             FlowDirection = FlowDirection.RightToLeft,
                             FontFamily = owner.FontFamily,
                             Background = Brushes.White,
                             Resources = owner.Resources };
        var panel = new StackPanel { Margin = new Thickness(24) };
        w.Content = panel;
        panel.Children.Add(new TextBlock { Text = w.Title, FontSize = 23, FontWeight = FontWeights.SemiBold,
                                           Margin = new Thickness(0, 0, 0, 14) });
        Action<string> label = t =>
            panel.Children.Add(new TextBlock { Text = t, Margin = new Thickness(0, 9, 0, 4) });
        label(ip ? "نام سرور" : "نام نمایشی");
        var name = new TextBox { Text = result.Name, MaxLength = 80 };
        panel.Children.Add(name);
        label(app ? "مسیر فایل برنامه" : ip ? "آدرس IPv4" : "دامنه");
        var target =
            new TextBox { Text = result.Target, FlowDirection = FlowDirection.LeftToRight, MaxLength = 700 };
        panel.Children.Add(target);
        if (app)
        {
            var browse = new Button { Content = "انتخاب فایل EXE" };
            browse.Click += (s, e) =>
            {
                var f = new OpenFileDialog { Filter = "Windows applications|*.exe" };
                if (f.ShowDialog(w) == true)
                {
                    target.Text = f.FileName;
                    if (name.Text.Trim() == "")
                        name.Text = Path.GetFileNameWithoutExtension(f.FileName);
                }
            };
            panel.Children.Add(browse);
        }
        label("مسیر اتصال");
        var mode = new ComboBox { Padding = new Thickness(7),
                                  ItemsSource = ip ? new[] { "اینترنت عادی", "فقط VPN" }
                                                   : new[] { "اینترنت عادی", "فقط VPN", "مسیر سیستم" },
                                  SelectedIndex = result.Mode == PathMode.Direct ? 0
                                                  : result.Mode == PathMode.Vpn  ? 1
                                                                                 : 2 };
        panel.Children.Add(mode);
        var sub = new CheckBox { Content = "زیردامنه‌ها هم شامل شوند",
                                 IsChecked = result.Subdomains, Margin = new Thickness(0, 12, 0, 0) };
        if (!app && !ip)
            panel.Children.Add(sub);
        var enabled = new CheckBox { Content = "قانون فعال باشد", IsChecked = result.Enabled,
                                     Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(enabled);
        var guard = new CheckBox { Content = "مسیر مخالف برای این فایل EXE در فایروال بسته شود",
                                   IsChecked = result.Guard, Margin = new Thickness(0, 7, 0, 4) };
        if (app)
            panel.Children.Add(guard);
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(buttons);
        var save = new Button { Content = "ذخیره", IsDefault = true };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "انصراف", IsCancel = true });
        save.Click += (s, e) =>
        {
            try
            {
                string value = target.Text.Trim();
                if (ip)
                    value = Settings.IPv4(value);
                else if (app)
                {
                    if (!Path.IsPathRooted(value) || !File.Exists(value) ||
                        !String.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("یک فایل اجرایی موجود انتخاب کنید.");
                    value = Path.GetFullPath(value);
                }
                else
                    value = Settings.Host(value);
                result.Target = value;
                result.Name = String.IsNullOrWhiteSpace(name.Text)
                                  ? (app ? Path.GetFileNameWithoutExtension(value) : value)
                                  : name.Text.Trim();
                result.Enabled = enabled.IsChecked == true;
                result.Subdomains = sub.IsChecked == true;
                result.Guard = app && guard.IsChecked == true;
                result.Mode = mode.SelectedIndex == 0   ? PathMode.Direct
                              : mode.SelectedIndex == 1 ? PathMode.Vpn
                                                        : PathMode.System;
                w.DialogResult = true;
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
            }
        };
        return w.ShowDialog() == true ? result : null;
    }
}
}
