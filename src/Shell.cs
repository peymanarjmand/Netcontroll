using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Netcontroll
{
public sealed class Shell : IDisposable
{
    readonly Window w;
    readonly Router router;
    Settings data;
    string page = "chrome";
    bool busy;
    internal bool CanExitForUpdate
    {
        get {
            return !busy && w.IsEnabled;
        }
    }
    bool compact;
    List<Discovered> discovered = new List<Discovered>();
    bool showOther;
    string discoveryError = "";
    SavedRoute[] savedRoutes = new SavedRoute[0];
    bool hasScanned, scanning;
    readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
    readonly string[] keys = { "chrome", "ips", "docker", "apps", "activity" };
    readonly string[] titles = { "دامنه‌های Chrome", "آدرس‌های IP", "Docker Desktop",
                                 "برنامه‌ها", "فعالیت اتصال‌ها" };
    readonly string[] icons = { "\uE774", "\uE968", "\uE7B8", "\uECAA", "\uE9D9" };
    readonly string[] descriptions = {
        "مسیر اتصال وب را برای هر دامنه و زیردامنه انتخاب کنید.",
        "سرورها را با نام ذخیره کنید و مسیر آن‌ها را برای کل ویندوز " +
        "تعیین " +
            "کنید.",
        "مسیر دانلود ایمیج‌ها را برای Docker Desktop انتخاب کنید.",
        "برای برنامه‌های سازگار با پراکسی، مسیر جداگانه انتخاب " +
            "کنید.",
        "اتصال‌های اخیر پراکسی محلی و آدرس خروجی هر اتصال."
    };
    T C<T>(string key)
        where T : FrameworkElement
    {
        return (T)w.FindName(key);
    }
    static Brush B(string hex)
    {
        return (Brush) new BrushConverter().ConvertFromString(hex);
    }
    public Shell(Window window, Settings settings, Router engine)
    {
        w = window;
        Netcontroll.Design.Theme.Attach(w);
        w.SizeChanged += (s, e) =>
        {
            bool small = e.NewSize.Width < 820;
            if (small == compact)
                return;
            compact = small;
            C<Border>("Sidebar").Width = compact ? 76 : 206;
            C<TextBlock>("BrandName").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            C<StackPanel>("Credit").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            C<Grid>("Body").Margin = compact ? new Thickness(12) : new Thickness(27, 22, 27, 22);
            BuildNav();
        };
        data = settings;
        router = engine;
        C<TextBox>("Search").TextChanged += (s, e) => RenderRows();
        BuildNav();
        Render();
        timer.Tick += (s, e) =>
        {
            Links();
            if (page == "activity")
                RenderRows();
        };
        timer.Start();
    }
    public void Dispose()
    {
        timer.Stop();
    }
    static string Mode(PathMode m)
    {
        return m == PathMode.Direct ? "اینترنت عادی" : m == PathMode.Vpn ? "فقط VPN" : "مسیر سیستم";
    }
    void Tell(string text)
    {
        C<TextBlock>("Status").Text = text;
    }
    void Links()
    {
        foreach (var p in new[] { PathMode.Direct, PathMode.Vpn })
        {
            string text;
            try
            {
                var l = Network.Choose(p);
                text = l.Name + "   ·   " + l.Source;
            }
            catch
            {
                text = "در دسترس نیست";
            }
            var label = C<TextBlock>(p == PathMode.Direct ? "Direct" : "Vpn");
            label.Text = text;
            label.ToolTip = text;
        }
    }
    void BuildNav()
    {
        var nav = C<StackPanel>("Nav");
        nav.Children.Clear();
        for (int i = 0; i < keys.Length; i++)
        {
            string k = keys[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icons[i], FontFamily = new FontFamily("Segoe MDL2 Assets"),
                                             FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                                             Margin = new Thickness(0, 0, 9, 0) });
            if (!compact)
                row.Children.Add(new TextBlock { Text = titles[i] });
            var b = Button("", () =>
                               {
                                   page = k;
                                   C<TextBox>("Search").Clear();
                                   BuildNav();
                                   Render();
                               });
            b.Content = row;
            b.ToolTip = titles[i];
            System.Windows.Automation.AutomationProperties.SetName(b, titles[i]);
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "Nav." + k);
            b.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            b.Background = B(k == page ? "#343047" : "#181A26");
            b.Foreground = B(k == page ? "#D7CBFF" : "#BAC0D2");
            b.BorderBrush = B(k == page ? "#685A84" : "#181A26");
            b.Padding = compact ? new Thickness(1, 12, 1, 12) : new Thickness(12, 12, 12, 12);
            nav.Children.Add(b);
        }
    }
    Button Button(string label, Action act, bool primary = false)
    {
        var b = new Button { Content = label };
        if (primary)
        {
            b.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        }
        if (label.StartsWith("حذف"))
            b.Foreground = B("#F6AEC1");
        b.Click += (s, e) =>
        {
            try
            {
                act();
            }
            catch (Exception ex)
            {
                Tell(ex.GetBaseException().Message);
            }
        };
        return b;
    }
    List<Rule> Collection(Settings d)
    {
        return page == "ips" ? d.Addresses : page == "apps" ? d.Apps : d.Domains;
    }
    void Render()
    {
        int index = Array.IndexOf(keys, page);
        C<TextBlock>("Title").Text = titles[index];
        C<TextBlock>("Subtitle").Text = descriptions[index];
        Links();
        var bar = C<WrapPanel>("Actions");
        bar.Children.Clear();
        if (page == "chrome" || page == "ips" || page == "apps")
            bar.Children.Add(Button(page == "ips"      ? "＋  افزودن سرور"
                                    : page == "chrome" ? "＋  افزودن دامنه"
                                                       : "＋  افزودن برنامه",
                                    () => Edit(null), true));
        if (page == "chrome")
        {
            bar.Children.Add(Button("باز کردن Chrome مدیریت‌شده", () => LaunchChrome(false)));
            bar.Children.Add(Button("اتصال Chrome فعلی", () => LaunchChrome(true)));
        }
        if (page == "ips")
        {
            ReadWindows();
            bar.Children.Add(Button("اسکن قوانین ویندوز", () => Scan()));
            int unknown = discovered.Count(x => !x.Known && !data.Addresses.Any(r => r.Target == x.IP));
            if (unknown > 0)
                bar.Children.Add(
                    Button((showOther ? "پنهان کردن سایر مسیرها" : "سایر مسیرهای ویندوز") + " (" + unknown + ")",
                           () =>
                           {
                               showOther = !showOther;
                               Render();
                           }));
            bar.Children.Add(Button("اعمال در ویندوز", () => Commit(data.Clone(), true)));
        }
        if (page == "docker")
            bar.Children.Add(Button("اتصال Docker Desktop", DockerHelp, true));
        if (page == "activity")
            bar.Children.Add(Button("پاک کردن نمایش", () =>
                                                      {
                                                          Activity a;
                                                          while (router.History.TryDequeue(out a))
                                                          {
                                                          }
                                                          RenderRows();
                                                      }));
        Tell(page == "chrome" ? "قانون دقیق‌تر دامنه اولویت دارد. هر تب بر اساس میزبان هر درخواست " +
                                "مسیریابی " +
                                    "می‌شود."
             : page == "ips"
                 ? "تغییر مسیر سیستم با تأیید مدیر ویندوز اعمال می‌شود."
             : page == "apps" ? "برنامه را از همین صفحه اجرا کنید یا نشانی پراکسی را در تنظیمات خودش وارد کنید."
             : page == "docker"
                 ? "انتخاب مسیر برای اتصال‌های جدید پراکسی Docker اعمال " +
                       "می‌شود."
                 : "فقط در حافظهٔ این اجرا نگه‌داری می‌شود.");
        RenderRows();
        if (page == "ips" && discoveryError.Length > 0)
            Tell(discoveryError);
        if (page == "ips" && !hasScanned && !scanning)
            Scan();
    }
    async void Scan()
    {
        if (scanning)
            return;
        scanning = true;
        Tell("در حال خواندن قوانین ذخیره‌شده و جدول مسیر ویندوز…");
        try
        {
            savedRoutes = await Task.Run(() => Discovery.Persistent());
            hasScanned = true;
            if (page == "ips")
            {
                showOther = true;
                Render();
                if (discoveryError.Length == 0)
                    Tell("اسکن انجام شد   ·   " + discovered.Count(x => x.Known) +
                         " قانون شناخته‌شده   ·   " + discovered.Count(x => !x.Known) +
                         " مسیر دیگر");
            }
        }
        catch (Exception ex)
        {
            if (page == "ips")
                Tell("اسکن کامل نشد. " + ex.GetBaseException().Message);
        }
        finally
        {
            scanning = false;
        }
    }
    void ReadWindows()
    {
        try
        {
            var observed = Discovery.Read();
            foreach (var group in savedRoutes.GroupBy(x => x.IP))
            {
                var item = observed.FirstOrDefault(x => x.IP == group.Key);
                if (item == null)
                {
                    item = new Discovered { IP = group.Key };
                    observed.Add(item);
                }
                if (item.Known)
                    continue;
                item.Origin = group.Count() == 1 ? "مسیر ذخیره‌شدهٔ ویندوز"
                                                 : "چند مسیر ذخیره‌شدهٔ ویندوز";
                if (group.Count() == 1)
                    item.Saved = group.Single();
            }
            var next = data.Clone();
            bool changed = false;
            foreach (var item in observed.Where(x => x.Known))
            {
                if (next.Addresses.Any(x => x.Target == item.IP))
                    continue;
                next.Addresses.Add(new Rule { Target = item.IP, Name = item.IP, Mode = item.Mode });
                changed = true;
            }
            if (changed)
            {
                next.Save();
                data = next;
            }
            discovered = observed;
            discoveryError = "";
        }
        catch (Exception ex)
        {
            discoveryError = "بازخوانی مسیرها انجام نشد. " + ex.GetBaseException().Message;
        }
    }
    static TextBlock Line(string t, int size, string color, bool bold = false)
    {
        return new TextBlock { Text = t,
                               FontSize = size,
                               Foreground = B(color),
                               FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                               TextWrapping = TextWrapping.Wrap,
                               Margin = new Thickness(0, 2, 0, 2) };
    }
    void Card(string title, string detail, IEnumerable<Button> actions, string badge = null)
    {
        var border = new Border { Background = B("#1A1D2A"),
                                  CornerRadius = new CornerRadius(13),
                                  BorderBrush = B("#373B50"),
                                  BorderThickness = new Thickness(1),
                                  Padding = new Thickness(15, 11, 15, 11),
                                  Margin = new Thickness(0, 0, 0, 8) };
        var stack = new StackPanel();
        border.Child = stack;
        var top = new DockPanel();
        if (badge != null)
        {
            var tag = new Border { Background = B(badge == "فقط VPN"   ? "#373049"
                                                  : badge == "غیرفعال" ? "#303343"
                                                                       : "#243C38"),
                                   CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 2, 9, 2),
                                   VerticalAlignment = VerticalAlignment.Center,
                                   Child = Line(badge, 13,
                                                badge == "فقط VPN"   ? "#D9C9FF"
                                                : badge == "غیرفعال" ? "#C9CAD5"
                                                                     : "#A5EACF") };
            tag.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(tag, Dock.Right);
            top.Children.Add(tag);
        }
        top.Children.Add(Line(title, 16, "#F2F2FC", true));
        stack.Children.Add(top);
        if (!String.IsNullOrEmpty(detail))
            stack.Children.Add(Line(detail, 13, "#B3B8CC"));
        var group = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in actions)
            group.Children.Add(b);
        if (group.Children.Count > 0)
            stack.Children.Add(group);
        C<StackPanel>("Rows").Children.Add(border);
    }
    void RenderRows()
    {
        var list = C<StackPanel>("Rows");
        list.Children.Clear();
        string filter = C<TextBox>("Search").Text.Trim();
        Func<string, bool> match = s => s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        if (page == "activity")
        {
            foreach (var a in router.History.ToArray().Reverse().Where(
                         a => match(a.Source + " " + a.Host + " " + a.Local)))
                Card(a.Source + "   ·   " + a.Host,
                     a.Time.ToString("HH:mm:ss") + "   ·   " + Mode(a.Mode) + "   ·   " +
                         (a.Success ? a.Local : a.Message),
                     new Button[0], a.Success ? "متصل" : "ناموفق");
        }
        else if (page == "docker")
        {
            if (match("Docker Desktop"))
                Card("دانلود ایمیج و رجیستری", "پراکسی Docker   ·   http://127.0.0.1:" + Router.DockerPort,
                     new[] {
                         Button("اینترنت عادی", () => SetDocker(PathMode.Direct), data.Docker == PathMode.Direct),
                         Button("فقط VPN", () => SetDocker(PathMode.Vpn), data.Docker == PathMode.Vpn),
                         Button("مسیر سیستم", () => SetDocker(PathMode.System), data.Docker == PathMode.System),
                         Button("آزمون دانلود ایمیج", DockerTest)
                     },
                     Mode(data.Docker));
            var last = router.History.ToArray().LastOrDefault(a => a.Source == "Docker" && a.Success);
            if (last != null)
                Card("آخرین اتصال Docker",
                     last.Host + "   ·   " + last.Local + "   ·   " + last.Time.ToString("HH:mm:ss"),
                     new Button[0]);
        }
        else
        {
            foreach (var rule in Collection(data).Where(r => match(r.Name + " " + r.Target)))
            {
                var r = rule;
                var buttons = new List<Button>();
                if (page == "ips")
                    buttons.Add(Button("نام‌گذاری", () => Rename(r.Target, r)));
                buttons.Add(Button("ویرایش", () => Edit(r)));
                buttons.Add(Button(r.Enabled ? "غیرفعال" : "فعال", () => Toggle(r)));
                if (page == "chrome" || page == "ips")
                    buttons.Add(Button("تست اتصال", () => Probe(r)));
                if (page == "apps")
                {
                    buttons.Add(Button("اجرا", () => LaunchApp(r)));
                    buttons.Add(Button("کپی پراکسی", () => Copy("http://127.0.0.1:" + r.Port)));
                }
                buttons.Add(Button("حذف", () => Delete(r)));
                string detail =
                    r.Target +
                    (page == "chrome" && r.Subdomains ? "   ·   شامل زیردامنه‌ها" : "") +
                    (page == "apps" ? "   ·   localhost:" + r.Port + (r.Guard ? "   ·   قفل فایروال" : "") : "");
                if (page == "ips")
                {
                    var actual = discovered.FirstOrDefault(x => x.IP == r.Target);
                    detail += "\n" + (actual == null ? "مسیر اختصاصی مشاهده نشد" : actual.Description);
                }
                Card(r.Name, detail, buttons, r.Enabled ? Mode(r.Mode) : "غیرفعال");
            }
            if (page == "ips" && showOther)
                foreach (var item in discovered.Where(x => !x.Known && !data.Addresses.Any(r => r.Target == x.IP)))
                {
                    string label;
                    if (!data.Labels.TryGetValue(item.IP, out label))
                        label = item.IP;
                    if (!match(label + " " + item.IP + " " + item.Adapter))
                        continue;
                    var actions = new List<Button> { Button("نام‌گذاری", () => Rename(item.IP, null)) };
                    if (item.Saved != null)
                        actions.Add(Button("حذف مسیر ذخیره‌شده", () => DeleteSaved(item.Saved)));
                    Card(label, item.IP + "\n" + item.Description, actions,
                         item.Saved == null ? "مسیر سیستمی" : "ذخیره‌شده");
                }
        }
        C<Border>("EmptyPanel").Visibility = list.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Children.Count == 0)
        {
            string title = filter.Length > 0  ? "موردی پیدا نشد"
                           : page == "chrome" ? "اولین دامنه را اضافه کنید"
                           : page == "ips"    ? "اولین سرور را اضافه کنید"
                           : page == "apps"   ? "یک برنامه انتخاب کنید"
                                              : "هنوز اتصالی ثبت نشده";
            C<TextBlock>("EmptyIcon").Text = filter.Length == 0 ? icons[Array.IndexOf(keys, page)] : "\uE721";
            C<TextBlock>("EmptyTitle").Text = title;
            var detail = C<TextBlock>("EmptyDetail");
            detail.Text = page == "chrome" && filter.Length == 0
                              ? "نام دامنه و مسیر دلخواه را ثبت کنید، سپس Chrome را به برنامه وصل کنید."
                              : "";
            detail.Visibility = detail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    void Rename(string ip, Rule rule)
    {
        string previous;
        if (rule != null)
            previous = rule.Name;
        else if (!data.Labels.TryGetValue(ip, out previous))
            previous = "";
        var dialog = new Window { Title = "نام‌گذاری سرور",
                                  Owner = w,
                                  SizeToContent = SizeToContent.Height,
                                  Width = 390,
                                  WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                  FlowDirection = FlowDirection.RightToLeft,
                                  FontFamily = w.FontFamily,
                                  Resources = w.Resources,
                                  ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) };
        Netcontroll.Design.Theme.Attach(dialog, false);
        dialog.Content = panel;
        panel.Children.Add(Line(ip, 16, "#F2F2FC"));
        var box = new TextBox { Text = previous, MaxLength = 80, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(box);
        var ok = Button("ذخیرهٔ نام",
                        () =>
                        {
                            if (box.Text.Trim().Length > 0)
                                dialog.DialogResult = true;
                        },
                        true);
        ok.IsDefault = true;
        panel.Children.Add(ok);
        dialog.Loaded += (s, e) =>
        {
            box.Focus();
            box.SelectAll();
        };
        if (dialog.ShowDialog() != true)
            return;
        var next = data.Clone();
        string label = box.Text.Trim();
        if (rule != null)
            next.Addresses.Single(x => x.Id == rule.Id).Name = label;
        else
            next.Labels[ip] = label;
        Commit(next, false);
    }
    async void DeleteSaved(SavedRoute route)
    {
        if (busy || MessageBox.Show(
                        w,
                        route.IP + " /32   →   " + route.Gateway +
                            "\nمسیر ذخیره‌شدهٔ انتخابی از ویندوز حذف شود؟",
                        "حذف مسیر", MessageBoxButton.YesNo, MessageBoxImage.Question,
                        MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        busy = true;
        w.IsEnabled = false;
        try
        {
            await Task.Run(
                () =>
                {
                    string temp = Path.GetTempFileName();
                    try
                    {
                        File.WriteAllText(temp, Settings.Text(route));
                        var exe =
                            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                        string args = "-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"" +
                                      Path.Combine(Entry.Base, @"system\SavedRoutes.ps1") +
                                      "\" -DeleteSnapshot \"" + temp + "\"";
                        using (var p = Process.Start(
                                   new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas",
                                                                     WindowStyle = ProcessWindowStyle.Hidden }))
                        {
                            p.WaitForExit();
                            if (p.ExitCode != 0)
                                throw new IOException("مسیر تغییر کرده است یا مجوز حذف صادر نشد.");
                        }
                    }
                    finally
                    {
                        File.Delete(temp);
                    }
                });
            Scan();
        }
        catch (Exception ex)
        {
            Tell(ex.GetBaseException().Message);
        }
        finally
        {
            busy = false;
            w.IsEnabled = true;
        }
    }
    void Edit(Rule old)
    {
        var rule = Editor.Open(w, page, old);
        if (rule == null)
            return;
        var next = data.Clone();
        var list = Collection(next);
        if (list.Any(r => r.Id != rule.Id &&
                          String.Equals(r.Target, rule.Target, StringComparison.OrdinalIgnoreCase)))
        {
            Tell("این آدرس از قبل ثبت شده است.");
            return;
        }
        if (page == "apps" && old == null)
        {
            int port = Enumerable.Range(17900, 100).FirstOrDefault(n => !list.Any(r => r.Port == n));
            if (port == 0)
                throw new InvalidOperationException("پورت آزاد موجود نیست.");
            rule.Port = port;
        }
        if (old == null)
            list.Add(rule);
        else
            list[list.FindIndex(r => r.Id == old.Id)] = rule;
        bool policyChanged = old == null || old.Target != rule.Target || old.Mode != rule.Mode ||
                             old.Enabled != rule.Enabled || old.Guard != rule.Guard;
        Commit(next,
               policyChanged && (page == "ips" || (page == "apps" && (rule.Guard || (old != null && old.Guard)))));
    }
    void Toggle(Rule r)
    {
        var n = data.Clone();
        Collection(n).Single(x => x.Id == r.Id).Enabled = !r.Enabled;
        Commit(n, page == "ips" || (page == "apps" && r.Guard));
    }
    void Delete(Rule r)
    {
        if (MessageBox.Show(w, r.Target + "   ·   " + Mode(r.Mode) + "\nقانون «" + r.Name + "» حذف شود؟",
                            "Netcontroll", MessageBoxButton.YesNo, MessageBoxImage.Question,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var n = data.Clone();
        Collection(n).RemoveAll(x => x.Id == r.Id);
        Commit(n, page == "ips" || (page == "apps" && r.Guard));
    }
    void SetDocker(PathMode mode)
    {
        var n = data.Clone();
        n.Docker = mode;
        Commit(n, false);
    }
    async void Commit(Settings next, bool system)
    {
        if (busy)
            return;
        busy = true;
        w.IsEnabled = false;
        try
        {
            if (system)
            {
                bool takeover = false;
                string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                             "Netcontroll Routes", "routes.json");
                object adoptedValue;
                bool adopted = page == "ips" && File.Exists(config) &&
                               Settings.Json<Dictionary<string, object>>(File.ReadAllText(config))
                                   .TryGetValue("Adopted", out adoptedValue) &&
                               Convert.ToBoolean(adoptedValue);
                if (page == "ips" && !adopted &&
                    File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                             "DirectServerRoutes", "Update-Routes.ps1")))
                {
                    if (MessageBox.Show(w,
                                        "مدیریت مسیرهای قبلی این لپ‌تاپ به فهرست فعلی منتقل شود؟ فقط " +
                                        "IPهای " +
                                            "فعال همین فهرست حفظ خواهند شد.",
                                        "انتقال مدیریت مسیرها", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                        return;
                    takeover = true;
                }
                bool appsOnly = page == "apps";
                await Task.Run(() => ApplySystem(next, takeover, appsOnly));
            }
            router.Configure(next);
            try
            {
                next.Save();
            }
            catch
            {
                router.Configure(data);
                throw;
            }
            data = next;
            Render();
            Tell("تغییرات ذخیره و اعمال شد.");
        }
        catch (Exception ex)
        {
            Tell("تغییر ثبت نشد. " + ex.GetBaseException().Message);
        }
        finally
        {
            w.IsEnabled = true;
            busy = false;
        }
    }
    static void ApplySystem(Settings next, bool takeover, bool appsOnly)
    {
        string temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                temp, Settings.Text(
                          new { Rules = next.Addresses.Where(x => x.Enabled)
                                            .Select(x => new { IP = x.Target, Mode = x.Mode.ToString() })
                                            .ToArray(),
                                Apps = next.Apps.Where(x => x.Enabled && x.Guard && x.Mode != PathMode.System)
                                           .Select(x => new { x.Id, Program = x.Target, Mode = x.Mode.ToString() })
                                           .ToArray() }));
            var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var args = "-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"" +
                       Path.Combine(Entry.Base, @"system\Routes.ps1") + "\" -Action Apply -InputFile \"" + temp +
                       "\"" + (takeover ? " -TakeOver" : "") + (appsOnly ? " -AppsOnly" : "");
            using (var p =
                       Process.Start(new ProcessStartInfo(exe, args) { Verb = "runas", UseShellExecute = true,
                                                                       WindowStyle = ProcessWindowStyle.Hidden }))
            {
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException(
                        "ویندوز تغییر مسیر را نپذیرفت. مجوز مدیر یا مسیرهای موجود را بررسی کنید.");
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }
    async void Probe(Rule r)
    {
        string tab = page;
        var mode = tab == "ips" ? PathMode.System : data.Match(r.Target);
        Tell("در حال آزمون اتصال به پورت ۴۴۳…");
        try
        {
            string endpoint =
                await Task.Run(async () =>
                               {
                                   using (var c = await Network.Connect(
                                              r.Target, 443, mode)) return c.Client.LocalEndPoint.ToString();
                               });
            Tell(r.Name + "   ·   TCP 443 متصل شد   ·   خروجی محلی " + endpoint);
        }
        catch (Exception ex)
        {
            Tell(r.Name + "   ·   " + ex.GetBaseException().Message);
        }
    }
    void Copy(string value)
    {
        Clipboard.SetText(value);
        Tell("کپی شد   ·   " + value);
    }
    static string ChromeExe()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles,
                                       Environment.SpecialFolder.ProgramFilesX86,
                                       Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), @"Google\Chrome\Application\chrome.exe");
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException("Google Chrome پیدا نشد.");
    }
    void LaunchChrome(bool existing)
    {
        var exe = ChromeExe();
        if (existing)
        {
            string folder = Path.Combine(Entry.Base, "chrome");
            Copy(folder);
            Process.Start(exe, "chrome://extensions");
            Process.Start("explorer.exe", "\"" + folder + "\"");
            MessageBox.Show(w,
                            "در chrome://extensions حالت Developer mode را روشن کنید. Load unpacked را بزنید و " +
                                "پوشهٔ کپی‌شده را انتخاب کنید. سپس از دکمهٔ افزونه «اتصال این پروفایل» را " +
                                "بزنید. " +
                                "برای بازگرداندن پراکسی قبلی همان دکمه را دوباره بزنید.",
                            "اتصال پروفایل فعلی Chrome");
        }
        else
            Process.Start(exe,
                          "--user-data-dir=\"" + Path.Combine(Settings.Folder, "ChromeProfile") +
                              "\" --proxy-server=\"http://127.0.0.1:" + Router.ChromePort +
                              ("\" --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp " +
                               "--no-first-run --no-default-browser-check"));
    }
    void LaunchApp(Rule rule)
    {
        if (!rule.Enabled)
            throw new InvalidOperationException("ابتدا قانون را فعال کنید.");
        var start = new ProcessStartInfo(rule.Target) { UseShellExecute = false,
                                                        WorkingDirectory = Path.GetDirectoryName(rule.Target) };
        var proxy = "http://127.0.0.1:" + rule.Port;
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            start.EnvironmentVariables[key] = proxy;
        start.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
        Process.Start(start);
        Tell("اجرا شد. پذیرش پراکسی را در فعالیت اتصال‌ها بررسی " +
             "کنید.");
    }
    void DockerHelp()
    {
        Copy("http://127.0.0.1:" + Router.DockerPort);
        MessageBox.Show(w,
                        "در Docker Desktop ← Settings ← Resources ← Proxies برای Containers proxy حالت Manual " +
                            "را انتخاب کنید. نشانی کپی‌شده را در هر دو فیلد HTTP و HTTPS بگذارید و Apply " +
                            "را " +
                            "بزنید. در نسخه‌هایی که Containers proxy از host پیروی می‌کند، همین نشانی را " +
                            "برای " +
                            "Docker Desktop proxy هم وارد کنید. بعد از این مرحله مسیر را از همین صفحه عوض کنید.",
                        "اتصال Docker Desktop");
    }
    async void DockerTest()
    {
        if (busy)
            return;
        if (MessageBox.Show(w, "ایمیج کوچک hello-world:latest از Docker Hub دریافت شود؟", "تست Docker",
                            MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        var from = DateTime.Now;
        busy = true;
        Tell("در حال اجرای docker pull …");
        try
        {
            int code =
                await Task.Run(() =>
                               {
                                   using (var p = Process.Start(new ProcessStartInfo(
                                              "docker.exe", "pull hello-world:latest") { UseShellExecute = false,
                                                                                         CreateNoWindow = true }))
                                   {
                                       if (!p.WaitForExit(90000))
                                       {
                                           p.Kill();
                                           throw new TimeoutException("زمان دانلود تمام شد.");
                                       }
                                       return p.ExitCode;
                                   }
                               });
            bool seen = router.History.ToArray().Any(
                a => a.Time >= from && a.Source == "Docker" && a.Success &&
                     (a.Host == "registry-1.docker.io" || a.Host == "auth.docker.io"));
            Tell(code == 0 && seen ? "دانلود موفق بود و اتصال پراکسی Docker مشاهده شد."
                 : code == 0       ? "دانلود شد. عبور از پراکسی در این آزمایش تأیید نشد."
                                   : "دانلود ناموفق بود. تنظیمات پراکسی و دسترسی رجیستری را بررسی کنید.");
        }
        catch (Exception ex)
        {
            Tell(ex.GetBaseException().Message);
        }
        finally
        {
            busy = false;
        }
    }
}
}
