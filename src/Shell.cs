using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Netcontroll
{
public sealed class Shell : IDisposable
{
    readonly Window w;
    readonly Router router;
    Settings data;
    string page = "overview";
    string routeFilter = "all";
    bool busy, compact, navigating;
    volatile bool disposed, linksRefreshing;
    internal bool CanExitForUpdate { get { return !busy && w.IsEnabled; } }
    List<Discovered> discovered = new List<Discovered>();
    bool showOther;
    string discoveryError = "";
    SavedRoute[] savedRoutes = new SavedRoute[0];
    bool hasScanned, scanning;
    TextBlock overviewConnections, overviewProfiles, windowsStatus;
    readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
    readonly string[] keys = { "overview", "chrome", "codex", "docker", "apps", "ips", "activity", "about" };
    readonly string[] titles = { "نمای کلی شبکه", "مرورگر و دامنه‌ها", "Codex Desktop", "Docker Desktop",
                                 "سایر برنامه‌ها", "سرورها و آدرس‌های IP", "فعالیت اتصال‌ها", "راهنما و درباره" };
    readonly string[] icons = { "\uE80F", "\uE774", "\uE943", "\uE7B8", "\uECAA", "\uE968", "\uE9D9", "\uE946" };
    readonly string[] descriptions = {
        "مسیرهای در دسترس، سیاست مرورگر و برنامه‌های انتخاب‌شده در یک نگاه.",
        "برای هر سایت قانون بسازید یا گروهی از تب‌ها را در یک پنجرهٔ مستقل باز کنید.",
        "پروفایل آماده برای اجرای نسخهٔ دسکتاپ Codex از پراکسی اختصاصی.",
        "کنترل مسیر ارتباط Docker Desktop با رجیستری و دانلود ایمیج‌ها.",
        "اجرای برنامه‌های سازگار با پراکسی، با مسیر مستقل برای هر برنامه.",
        "مدیریت مسیر IPv4 سرورها در ویندوز و مشاهدهٔ قوانین ذخیره‌شده.",
        "اتصال‌های مشاهده‌شده در پراکسی‌های همین اجرا؛ همراه با نتیجه و خروجی محلی.",
        "آشنایی با محدودهٔ هر قابلیت، راه‌اندازی مرورگر و اطلاعات توسعه‌دهنده."
    };
    T C<T>(string key) where T : FrameworkElement { return (T)w.FindName(key); }
    static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
    public Shell(Window window, Settings settings, Router engine)
    {
        w = window;
        data = settings;
        router = engine;
        Netcontroll.Design.Theme.Attach(w);
        compact = w.Width < 1120;
        C<TextBlock>("Version").Text = "v" + typeof(Shell).Assembly.GetName().Version.ToString(3);
        C<TextBlock>("DeveloperEmail").MouseLeftButtonUp += (s, e) => Copy("peymanarjmand308@gmail.com");
        C<TextBox>("Search").TextChanged += (s, e) => { if (!navigating) RenderRows(); };
        w.SizeChanged += (s, e) =>
        {
            bool small = e.NewSize.Width < 1120;
            if (small == compact) return;
            compact = small;
            Layout();
            BuildNav();
            RenderRows();
        };
        w.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && C<Border>("SearchPanel").IsVisible)
            { C<TextBox>("Search").Focus(); C<TextBox>("Search").SelectAll(); e.Handled = true; }
        };
        router.ChromeRuleRequested = request => w.Dispatcher.InvokeAsync(new Func<Task<bool>>(
            async () => {
                if (busy || !w.IsEnabled || w.OwnedWindows.Cast<Window>().Any(x => x.IsVisible)) return false;
                return await CommitAsync(data.WithChromeRule(request), false, true);
            })).Task.Unwrap();
        Layout();
        BuildNav();
        Render();
        timer.Tick += (s, e) =>
        {
            Links();
            if (page == "chrome") UpdateChromeStatus();
            if (page == "activity") RenderRows();
            if (page == "overview") UpdateOverviewStats();
            if (page == "codex" && !busy) RefreshCodexActivity();
        };
        timer.Start();
    }
    void Layout()
    {
        C<Border>("Sidebar").Width = compact ? 72 : 228;
        C<StackPanel>("BrandName").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        C<Border>("SideHealth").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        C<StackPanel>("Credit").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        C<Grid>("Body").Margin = compact ? new Thickness(14, 14, 14, 10) : new Thickness(21, 18, 21, 12);
    }
    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        router.ChromeRuleRequested = null;
    }
    static string Mode(PathMode m)
    { return m == PathMode.Direct ? "اینترنت عادی" : m == PathMode.Vpn ? "فقط VPN" : "مسیر سیستم"; }
    static string ModeColor(PathMode mode)
    { return mode == PathMode.Direct ? "#75D2C0" : mode == PathMode.Vpn ? "#FF9A71" : "#BAC0D0"; }
    void Tell(string text) { C<TextBlock>("Status").Text = text; }
    async void Links()
    {
        C<TextBlock>("SideSummary").Text = "پیش‌فرض مرورگر: " + Mode(data.ChromeDefault);
        if (disposed || linksRefreshing) return;
        linksRefreshing = true;
        foreach (var key in new[] { "Direct", "Vpn" })
            if (String.IsNullOrEmpty(C<TextBlock>(key).Text)) C<TextBlock>(key).Text = "در حال شناسایی رابط…";
        try
        {
            var snapshot = await Task.Run(() => new[] { PathMode.Direct, PathMode.Vpn }.Select(mode =>
            {
                try
                {
                    var link = Network.Choose(mode);
                    string value = link.Name + "  ·  " + link.Source;
                    return new[] { value, value + "\nنشانی رابط محلی است؛ اتصال اینترنت را از آزمون مسیر بررسی کنید." };
                }
                catch (Exception ex)
                {
                    return new[] { mode == PathMode.Vpn ? "آداپتور VPN در دسترس نیست" : "رابط مستقیم در دسترس نیست", ex.GetBaseException().Message };
                }
            }).ToArray());
            if (disposed) return;
            await w.Dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var label = C<TextBlock>(i == 0 ? "Direct" : "Vpn");
                    label.Text = snapshot[i][0]; label.ToolTip = snapshot[i][1];
                }
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
        finally { linksRefreshing = false; }
    }
    void UpdateChromeStatus()
    {
        int count = router.ConfirmedChromeProfiles;
        var status = C<TextBlock>("ChromeStatus");
        status.Text = count == 0
            ? "افزونه هنوز دریافت قوانین فعلی را تأیید نکرده است. برای Chrome معمولی، «اتصال مرورگر» را انجام دهید."
            : count + " پروفایل Chrome دریافت قوانین فعلی را اخیراً تأیید کرده؛ برای اتصال‌های قبلی، سایت را دوباره بارگذاری کنید.";
        status.Foreground = B(count == 0 ? "#E9BD7D" : "#8CDACB");
        C<Border>("ChromeStatePanel").Background = B(count == 0 ? "#322C24" : "#202D2B");
    }
    void Navigate(string destination)
    {
        page = destination;
        routeFilter = "all";
        navigating = true;
        C<TextBox>("Search").Clear();
        navigating = false;
        BuildNav();
        Render();
        C<ScrollViewer>("ContentScroll").ScrollToTop();
    }
    void BuildNav()
    {
        var nav = C<StackPanel>("Nav");
        nav.Children.Clear();
        for (int i = 0; i < keys.Length; i++)
        {
            if (!compact && (i == 0 || i == 2 || i == 5))
                nav.Children.Add(new TextBlock { Text = i == 0 ? "کنترل اتصال" : i == 2 ? "برنامه‌ها" : "ابزارها",
                    Foreground = B("#858894"), FontSize = 10, Margin = new Thickness(8, i == 0 ? 0 : 14, 8, 6) });
            string k = keys[i];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = icons[i], FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, compact ? 0 : 10, 0) });
            if (!compact)
            {
                var label = new TextBlock { Text = titles[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1); row.Children.Add(label);
            }
            var button = Button("", () => Navigate(k));
            button.Content = row;
            button.ToolTip = titles[i];
            System.Windows.Automation.AutomationProperties.SetName(button, titles[i]);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Nav." + k);
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            button.Background = B(k == page ? "#462821" : "#202127");
            button.Foreground = B(k == page ? "#FF8150" : "#C8CBD3");
            button.BorderBrush = B(k == page ? "#894224" : "#41434B");
            button.BorderThickness = k == page ? new Thickness(1, 1, 3, 1) : new Thickness(1);
            button.Padding = new Thickness(compact ? 0 : 11, 10, compact ? 0 : 11, 10);
            button.MinHeight = 42;
            button.Margin = new Thickness(0, 0, 0, 7);
            nav.Children.Add(button);
        }
    }
    Button Button(string label, Action act, bool primary = false)
    {
        var button = new Button { Content = label };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        if (label.StartsWith("حذف")) button.Foreground = B("#ED8EA1");
        button.Click += (s, e) => { try { act(); } catch (Exception ex) { Tell(ex.GetBaseException().Message); } };
        return button;
    }
    Button MenuButton(string label, IEnumerable<Button> actions)
    {
        var more = new Button { Content = label, ToolTip = "گزینه‌های بیشتر", Padding = new Thickness(10, 4, 10, 4), MinHeight = 32 };
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft, Resources = w.Resources };
        foreach (var action in actions)
        {
            var original = action;
            var item = new MenuItem { Header = action.Content, Foreground = action.Foreground };
            item.Click += (s, e) => original.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            menu.Items.Add(item);
        }
        more.ContextMenu = menu;
        more.Click += (s, e) => { menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
        System.Windows.Automation.AutomationProperties.SetName(more, "گزینه‌های بیشتر");
        return more;
    }
    List<Rule> Collection(Settings settings)
    { return page == "ips" ? settings.Addresses : page == "apps" ? settings.Apps : settings.Domains; }
    void BuildFilters()
    {
        var filters = C<WrapPanel>("Filters");
        filters.Children.Clear();
        string[] values = page == "activity" ? new[] { "all", "vpn", "direct", "failed" }
                                             : new[] { "all", "vpn", "direct", "disabled" };
        string[] labels = { "همه", "VPN", "اینترنت عادی", page == "activity" ? "ناموفق" : "غیرفعال" };
        for (int i = 0; i < values.Length; i++)
        {
            string id = values[i];
            var b = Button(labels[i], () => { routeFilter = id; BuildFilters(); RenderRows(); });
            b.MinHeight = 28; b.Padding = new Thickness(11, 4, 11, 4); b.FontSize = 11;
            b.Background = B(routeFilter == id ? "#513021" : "#2C2D34");
            b.BorderBrush = B(routeFilter == id ? "#9C4A25" : "#3C3E47");
            b.Foreground = B(routeFilter == id ? "#FF9A71" : "#BFC2CC");
            filters.Children.Add(b);
        }
    }
    bool MatchesMode(PathMode mode, bool enabled)
    { return routeFilter == "all" || (routeFilter == "vpn" && enabled && mode == PathMode.Vpn) ||
        (routeFilter == "direct" && enabled && mode == PathMode.Direct) || (routeFilter == "disabled" && !enabled); }
    void Render()
    {
        int index = Array.IndexOf(keys, page);
        C<TextBlock>("Title").Text = titles[index];
        C<TextBlock>("Subtitle").Text = descriptions[index];
        Links();
        C<Border>("OverviewHero").Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        C<UniformGrid>("RouteStrip").Visibility = page == "about" ? Visibility.Collapsed : Visibility.Visible;
        bool list = page == "chrome" || page == "apps" || page == "ips" || page == "activity";
        C<Border>("SearchPanel").Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        C<Border>("ChromeStatePanel").Visibility = page == "chrome" ? Visibility.Visible : Visibility.Collapsed;
        var policy = C<StackPanel>("PolicyPanel");
        policy.Children.Clear();
        if (page == "chrome" || page == "overview") BuildChromePolicy(policy);
        var bar = C<WrapPanel>("Actions");
        bar.Children.Clear();
        if (page == "chrome" || page == "ips" || page == "apps")
            bar.Children.Add(Button(page == "ips" ? "＋  افزودن سرور" : page == "chrome" ? "＋  افزودن دامنه" : "＋  افزودن برنامه", () => Edit(null), true));
        if (page == "chrome")
        {
            UpdateChromeStatus();
            bar.Children.Add(Button("پنجرهٔ فقط VPN", () => LaunchChromeMode(PathMode.Vpn)));
            bar.Children.Add(Button("پنجرهٔ مستقیم", () => LaunchChromeMode(PathMode.Direct)));
            bar.Children.Add(MenuButton("اتصال مرورگر  ▾", new[] {
                Button("اتصال Chrome فعلی با افزونه", () => LaunchChrome(true)),
                Button("باز کردن مرورگر با قوانین دامنه", () => LaunchChrome(false)),
                Button("راهنمای تفاوت سایت و تب", () => Navigate("about")) }));
        }
        if (page == "ips")
        {
            ReadWindows();
            bar.Children.Add(Button(scanning ? "در حال اسکن…" : "بازخوانی ویندوز", () => Scan()));
            int unknown = discovered.Count(x => !x.Known && !data.Addresses.Any(r => r.Target == x.IP));
            if (unknown > 0) bar.Children.Add(Button((showOther ? "پنهان کردن سایر مسیرها" : "سایر مسیرهای ویندوز") + " (" + unknown + ")", () => { showOther = !showOther; Render(); }));
            bar.Children.Add(Button("اعمال در ویندوز", () => Commit(data.Clone(), true)));
        }
        if (page == "docker") bar.Children.Add(Button("راهنمای اتصال Docker", DockerHelp, true));
        if (page == "codex")
        {
            bar.Children.Add(Button("اجرای Codex Desktop", LaunchCodex, true));
            bar.Children.Add(Button("تشخیص خودکار", FindCodex));
            bar.Children.Add(Button("انتخاب فایل برنامه", BrowseCodex));
            bar.Children.Add(Button("کپی پراکسی", () => Copy("http://127.0.0.1:" + Router.CodexPort)));
        }
        if (page == "activity") bar.Children.Add(Button("پاک کردن نمایش", () => { Activity item; while (router.History.TryDequeue(out item)) { } RenderRows(); }));
        BuildFilters();
        Tell(page == "chrome" ? "قانون سایت بین تب‌های همان دامنه مشترک است؛ برای جداسازی گروه تب‌ها، از پنجرهٔ مستقل VPN یا مستقیم استفاده کنید."
            : page == "ips" ? "قوانین IP برای کل ویندوز اعمال می‌شوند؛ تغییر آن‌ها به تأیید مدیر ویندوز نیاز دارد."
            : page == "apps" || page == "codex" ? "پذیرش پراکسی به برنامه وابسته است؛ موفقیت واقعی را در «فعالیت اتصال‌ها» بررسی کنید."
            : page == "docker" ? "انتخاب مسیر روی اتصال‌های جدید پراکسی Docker اثر دارد؛ اتصال قبلی را دوباره برقرار کنید."
            : page == "activity" ? "فقط اتصال‌های عبوری از پراکسی نمایش داده می‌شوند؛ این صفحه پایش همهٔ ترافیک ویندوز نیست."
            : "سیاست ذخیره‌شده، رابط محلی و اتصال مشاهده‌شده سه وضعیت جدا هستند؛ برای اطمینان، مسیر را آزمون کنید.");
        RenderRows();
        if (page == "ips" && discoveryError.Length > 0) Tell(discoveryError);
        if (page == "ips" && !hasScanned && !scanning) Scan();
    }
    void BuildChromePolicy(StackPanel parent)
    {
        var panel = new StackPanel();
        var border = Surface(panel, 13); border.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(Line("پیش‌فرض مرورگر", 14, "#F4F4F5", true));
        panel.Children.Add(Line("برای درخواست‌های Chrome که از پراکسی Netcontroll عبور می‌کنند؛ قوانین اختصاصی دامنه اولویت دارند.", 11, "#A6A9B5"));
        var options = new WrapPanel { Margin = new Thickness(-3, 7, -3, 5) };
        foreach (var mode in new[] { PathMode.Direct, PathMode.Vpn, PathMode.System })
        {
            var selected = mode;
            string label = mode == PathMode.Direct ? "VPN فقط برای انتخاب‌شده‌ها" : mode == PathMode.Vpn ? "عادی فقط برای انتخاب‌شده‌ها" : "مسیر سیستم (حالت قبلی)";
            var b = Button(label, () => { if (data.ChromeDefault != selected) { var next = data.Clone(); next.ChromeDefault = selected; Commit(next, false); } });
            b.Background = B(mode == data.ChromeDefault ? "#462821" : "#202127");
            b.BorderBrush = B(mode == data.ChromeDefault ? "#AB4A24" : "#41434B");
            b.Foreground = B(mode == data.ChromeDefault ? "#FF9A71" : "#C4C7D0");
            b.ToolTip = mode == PathMode.Direct ? "پیشنهاد برای استفادهٔ انتخابی: سایت‌های بدون قانون، اینترنت عادی دارند." : Mode(mode);
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "ChromeDefault." + mode);
            options.Children.Add(b);
        }
        panel.Children.Add(options);
        panel.Children.Add(Line("بدون قانون: " + Mode(data.ChromeDefault) + "   ·   " + data.Domains.Count(x => x.Enabled && x.Mode == PathMode.Vpn) + " قانون VPN   ·   " + data.Domains.Count(x => x.Enabled && x.Mode == PathMode.Direct) + " قانون مستقیم", 11, ModeColor(data.ChromeDefault)));
        parent.Children.Add(border);
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
            if (page == "ips" && !busy)
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
            if (page == "ips" && !busy)
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
    static TextBlock Line(string text, int size, string color, bool bold = false)
    {
        return new TextBlock { Text = text, FontSize = size, Foreground = B(color),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
    }
    static Border Surface(UIElement child, int padding = 12)
    {
        return new Border { Background = B("#24252B"), CornerRadius = new CornerRadius(11),
            BorderBrush = B("#3C3E47"), BorderThickness = new Thickness(1), Padding = new Thickness(padding),
            Margin = new Thickness(0, 0, 0, 9), Child = child };
    }
    static Border Badge(string text)
    {
        bool vpn = text == "فقط VPN", direct = text == "اینترنت عادی" || text == "متصل", error = text == "ناموفق";
        return new Border { Background = B(vpn ? "#482D22" : direct ? "#233C35" : error ? "#492D36" : "#33353E"),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center,
            Child = Line(text, 10, vpn ? "#FF9A71" : direct ? "#83D6BF" : error ? "#F1A0B3" : "#BABEC9") };
    }
    void Card(string title, string detail, IEnumerable<Button> actions, string badge = null)
    {
        var stack = new StackPanel();
        var heading = new DockPanel();
        if (badge != null)
        {
            var tag = Badge(badge); tag.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(tag, Dock.Left); heading.Children.Add(tag);
        }
        heading.Children.Add(Line(title, 14, "#F4F4F5", true));
        stack.Children.Add(heading);
        if (!String.IsNullOrEmpty(detail)) stack.Children.Add(Line(detail, 12, "#B0B4C0"));
        var buttons = new WrapPanel { Margin = new Thickness(-3, 7, -3, -3) };
        foreach (var action in actions) buttons.Children.Add(action);
        if (buttons.Children.Count > 0) stack.Children.Add(buttons);
        C<StackPanel>("Rows").Children.Add(Surface(stack, 14));
    }
    void RuleRow(Rule rule, string extra, IEnumerable<Button> actions)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var symbol = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9),
            Background = B(rule.Enabled ? "#332C29" : "#2C2E35"), Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = icons[Array.IndexOf(keys, page)], FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16, Foreground = B(rule.Enabled ? "#E58A64" : "#888C99"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        grid.Children.Add(symbol);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var name = Line(rule.Name, 13, rule.Enabled ? "#F2F3F5" : "#9296A2", true);
        name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = rule.Name;
        info.Children.Add(name);
        info.Children.Add(new TextBlock { Text = rule.Target, FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Right, Foreground = B("#9EA3B2"), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = rule.Target });
        if (!String.IsNullOrEmpty(extra)) info.Children.Add(new TextBlock { Text = extra, FontSize = 10,
            Foreground = B("#858C9A"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), ToolTip = extra });
        Grid.SetColumn(info, 1); grid.Children.Add(info);
        var badge = Badge(rule.Enabled ? Mode(rule.Mode) : "غیرفعال");
        badge.Margin = new Thickness(0, 0, 9, 0); Grid.SetColumn(badge, 2); grid.Children.Add(badge);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var available = actions.ToArray();
        if (available.Length > 0)
        {
            available[0].MinHeight = 30; available[0].FontSize = 11; available[0].Padding = new Thickness(9, 4, 9, 4);
            buttons.Children.Add(available[0]);
        }
        if (available.Length > 1) buttons.Children.Add(MenuButton("•••", available.Skip(1)));
        Grid.SetColumn(buttons, 3); grid.Children.Add(buttons);
        var row = Surface(grid, 11); row.MinHeight = 70;
        C<StackPanel>("Rows").Children.Add(row);
    }
    Border Metric(string label, string value, string symbol, out TextBlock number)
    {
        var content = new StackPanel();
        var title = new DockPanel();
        title.Children.Add(new TextBlock { Text = symbol, FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14, Foreground = B("#D97B56"), Margin = new Thickness(0, 0, 8, 0) });
        title.Children.Add(Line(label, 11, "#ADB1BD"));
        content.Children.Add(title);
        number = Line(value, 24, "#F4F4F5", true); number.Margin = new Thickness(0, 7, 0, 0);
        content.Children.Add(number);
        var border = Surface(content, 13); border.Margin = new Thickness(4, 0, 4, 8); return border;
    }
    void UpdateOverviewStats()
    {
        if (overviewConnections != null) overviewConnections.Text = router.History.Count(x => x.Success).ToString();
        if (overviewProfiles != null) overviewProfiles.Text = router.ConfirmedChromeProfiles.ToString();
        if (windowsStatus != null) windowsStatus.Text = WindowsRouteStatus();
    }
    string WindowsRouteStatus()
    {
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netcontroll Routes", "route-status.json");
            if (!File.Exists(file)) return "وضعیت اعمال ویندوز: هنوز گزارشی از سرویس مسیرها دریافت نشده است.";
            var status = Settings.Json<Dictionary<string, object>>(File.ReadAllText(file));
            object success, requested, stamp, message;
            DateTime checkedAt;
            if (status == null || !status.TryGetValue("Success", out success) ||
                !status.TryGetValue("WindowsDefaultDirect", out requested) ||
                !status.TryGetValue("CheckedAtUtc", out stamp) ||
                !DateTime.TryParse(Convert.ToString(stamp), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out checkedAt))
                return "گزارش اعمال ویندوز قابل خواندن نیست؛ دوباره تنظیم مسیر را اعمال کنید.";
            if (!Convert.ToBoolean(success))
                return "آخرین اعمال مسیرها موفق نبود" + (status.TryGetValue("Message", out message) ? ": " + Convert.ToString(message) : ".");
            if (Convert.ToBoolean(requested) != data.WindowsDefaultDirect)
                return "گزارش سرویس با تنظیم ذخیره‌شده متفاوت است؛ دوباره حالت دلخواه را اعمال کنید.";
            var age = DateTime.UtcNow - checkedAt;
            if (age.TotalMinutes > 5 || age.TotalMinutes < -1)
                return "گزارش اعمال مسیر قدیمی است؛ آخرین ثبت: " + checkedAt.ToLocalTime().ToString("HH:mm") + ". وضعیت فعلی تأیید نشده است.";
            return "آخرین اعمال موفق مسیرها: " + checkedAt.ToLocalTime().ToString("HH:mm:ss") + "  ·  این گزارش، آزمون دسترسی اینترنت نیست.";
        }
        catch { return "خواندن گزارش سرویس مسیرها ممکن نشد؛ وضعیت فعلی تأیید نشده است."; }
    }
    TextBlock codexLastDetail;
    Border codexLastBadge;
    void RefreshCodexActivity()
    {
        if (codexLastDetail == null) return;
        var last = router.History.ToArray().LastOrDefault(x => x.Source == "Codex");
        codexLastDetail.Text = last == null ? "هنوز اتصالی در این اجرا دیده نشده است. در Codex یک درخواست تازه بفرستید."
            : last.Host + "   ·   " + last.Time.ToString("HH:mm:ss") + "\n" + (last.Success ? "خروجی محلی: " + last.Local : last.Message);
        if (codexLastBadge != null)
        {
            var tag = Badge(last == null ? "در انتظار اتصال" : last.Success ? "متصل" : "ناموفق");
            var child = tag.Child; tag.Child = null;
            codexLastBadge.Child = child;
            codexLastBadge.Background = tag.Background;
        }
    }
    void RenderOverview()
    {
        var stats = new UniformGrid { Columns = compact ? 2 : 4, Margin = new Thickness(-4, 0, -4, 4) };
        TextBlock unused;
        stats.Children.Add(Metric("قانون دامنهٔ فعال", data.Domains.Count(x => x.Enabled).ToString(), "\uE774", out unused));
        stats.Children.Add(Metric("قانون برنامهٔ فعال", data.Apps.Count(x => x.Enabled).ToString(), "\uECAA", out unused));
        stats.Children.Add(Metric("اتصال موفق این اجرا", "0", "\uE9D9", out overviewConnections));
        stats.Children.Add(Metric("تأیید افزونهٔ Chrome", "0", "\uE73E", out overviewProfiles));
        C<StackPanel>("Rows").Children.Add(stats); UpdateOverviewStats();
        var systemPanel = new StackPanel();
        systemPanel.Children.Add(Line("اینترنت عادی برای سایر برنامه‌های ویندوز", 14, "#F4F4F5", true));
        systemPanel.Children.Add(Line("تنظیم ذخیره‌شده: " + (data.WindowsDefaultDirect ? "اینترنت عادی" : "مسیر اصلی ویندوز"), 12, data.WindowsDefaultDirect ? "#83D6BF" : "#C0C4CF"));
        systemPanel.Children.Add(Line("برای حالت «فقط Codex از VPN»، این گزینه را فعال و Codex را از پروفایل VPN اجرا کنید. تغییر مسیرهای جدید نیازمند تأیید مدیر ویندوز است. Kill switch بعضی VPNها ممکن است اینترنت مستقیم را مسدود کند؛ اتصال‌های باز را دوباره برقرار کنید.", 12, "#ABB0BD"));
        windowsStatus = Line(WindowsRouteStatus(), 11, "#D5BD9C");
        windowsStatus.Margin = new Thickness(0, 7, 0, 4);
        System.Windows.Automation.AutomationProperties.SetAutomationId(windowsStatus, "WindowsRouteStatus");
        systemPanel.Children.Add(windowsStatus);
        var systemActions = new WrapPanel { Margin = new Thickness(-3, 5, -3, -3) };
        systemActions.Children.Add(Button("فعال‌سازی اینترنت عادی", () => SetWindowsDefault(true), !data.WindowsDefaultDirect));
        systemActions.Children.Add(Button("بازگشت به مسیر اصلی", () => SetWindowsDefault(false)));
        systemPanel.Children.Add(systemActions);
        C<StackPanel>("Rows").Children.Add(Surface(systemPanel, 14));
        Card("گروهی از تب‌ها، در پنجرهٔ مستقل",
            "این پنجره‌ها پروفایل جدا دارند؛ همهٔ درخواست‌های همان پنجره، از جمله سرویس‌های جانبی سایت، مسیر انتخابی را می‌گیرند. تب‌های Chrome معمولی با قوانین سایت و پیش‌فرض مرورگر مدیریت می‌شوند.",
            new[] { Button("باز کردن پنجرهٔ VPN", () => LaunchChromeMode(PathMode.Vpn), true),
                    Button("باز کردن پنجرهٔ مستقیم", () => LaunchChromeMode(PathMode.Direct)),
                    Button("مدیریت قوانین سایت‌ها", () => Navigate("chrome")) });
        Card("برنامه‌های آماده",
            "Codex Desktop و Docker پراکسی جدا دارند؛ مسیر آن‌ها مستقل از پیش‌فرض مرورگر است.",
            new[] { Button("Codex Desktop", () => Navigate("codex")), Button("Docker Desktop", () => Navigate("docker")),
                    Button("سایر برنامه‌ها", () => Navigate("apps")) });
    }
    void RenderCodex()
    {
        var panel = new StackPanel();
        panel.Children.Add(Line("مسیر اتصال Codex Desktop", 15, "#F4F4F5", true));
        panel.Children.Add(Line("تنها درخواست‌هایی که Codex به این پراکسی می‌فرستد از مسیر انتخابی عبور می‌کنند.", 12, "#ABB0BD"));
        var modes = new WrapPanel { Margin = new Thickness(-3, 8, -3, 8) };
        foreach (var mode in new[] { PathMode.Vpn, PathMode.Direct, PathMode.System })
        { var selected = mode; modes.Children.Add(Button(Mode(mode), () => { var n = data.Clone(); n.Codex = selected; Commit(n, false); }, data.Codex == mode)); }
        panel.Children.Add(modes);
        panel.Children.Add(Line("پراکسی اختصاصی", 11, "#9299A8"));
        panel.Children.Add(new TextBlock { Text = "http://127.0.0.1:" + Router.CodexPort, FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Right, FontSize = 14, FontFamily = new FontFamily("Consolas"), Foreground = B("#E6B399") });
        panel.Children.Add(Line("فایل برنامه", 11, "#9299A8"));
        var path = new TextBox { Text = String.IsNullOrWhiteSpace(data.CodexExecutable) ? "هنوز انتخاب نشده؛ «تشخیص خودکار» یا «انتخاب فایل برنامه» را بزنید." : data.CodexExecutable,
            IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 5, 0, 3), TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetAutomationId(path, "CodexExecutable");
        System.Windows.Automation.AutomationProperties.SetName(path, "مسیر فایل Codex Desktop");
        panel.Children.Add(path);
        C<StackPanel>("Rows").Children.Add(Surface(panel, 16));
        Card("اجرای درست برنامه",
            "ابتدا Codex را کامل ببندید؛ سپس «اجرای Codex Desktop» را بزنید تا نمونهٔ جدید تنظیمات پراکسی را بگیرد. باز بودن نمونهٔ قبلی ممکن است تنظیمات راه‌اندازی تازه را نپذیرد. این پروفایل مربوط به برنامهٔ دسکتاپ است.",
            new[] { Button("مشاهدهٔ فعالیت Codex", () => { Navigate("activity"); C<TextBox>("Search").Text = "Codex"; }) });
        var lastPanel = new StackPanel();
        var lastHeading = new DockPanel();
        codexLastBadge = Badge("در انتظار اتصال"); DockPanel.SetDock(codexLastBadge, Dock.Left);
        lastHeading.Children.Add(codexLastBadge); lastHeading.Children.Add(Line("آخرین اتصال مشاهده‌شده", 14, "#F4F4F5", true));
        lastPanel.Children.Add(lastHeading);
        codexLastDetail = Line("", 12, "#ADB1BD");
        System.Windows.Automation.AutomationProperties.SetAutomationId(codexLastDetail, "CodexObservedStatus");
        lastPanel.Children.Add(codexLastDetail);
        C<StackPanel>("Rows").Children.Add(Surface(lastPanel, 14));
        RefreshCodexActivity();
    }
    void RenderAbout()
    {
        Card("Netcontroll  " + typeof(Shell).Assembly.GetName().Version.ToString(3),
            "کنترل مسیر اتصال برای مرورگر، برنامه‌های سازگار با پراکسی و سرورهای ویندوز.", new Button[0]);
        var developer = new StackPanel();
        developer.Children.Add(Line("توسعه‌دهنده · Peyman Arjmand", 14, "#F4F4F5", true));
        var email = new TextBox { Text = "peymanarjmand308@gmail.com", IsReadOnly = true,
            FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 7, 0, 7), FontSize = 14 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(email, "DeveloperEmailField");
        System.Windows.Automation.AutomationProperties.SetName(email, "ایمیل توسعه‌دهنده");
        developer.Children.Add(email);
        developer.Children.Add(Button("کپی ایمیل توسعه‌دهنده", () => Copy("peymanarjmand308@gmail.com")));
        C<StackPanel>("Rows").Children.Add(Surface(developer, 15));
        Card("سایت با تب چه تفاوتی دارد؟",
            "قانون دامنه روی درخواست‌های همان میزبان در تمام تب‌های پروفایل متصل اعمال می‌شود. یک سایت می‌تواند از دامنه‌های دیگری برای ورود یا فایل‌ها استفاده کند. برای اینکه یک گروه تب همراه تمام درخواست‌های جانبی مسیر مستقل داشته باشد، پنجرهٔ VPN یا مستقیم را باز کنید؛ ورودها و داده‌های آن پروفایل جداست.",
            new[] { Button("اتصال Chrome فعلی", () => LaunchChrome(true)), Button("پنجرهٔ فقط VPN", () => LaunchChromeMode(PathMode.Vpn)) });
        Card("محدودهٔ برنامه‌ها و ویندوز",
            "پیش‌فرض مرورگر، پیش‌فرض کل ویندوز را عوض نمی‌کند. گزینهٔ اینترنت عادی در نمای کلی، مسیرهای ویندوز را با دسترسی مدیر تغییر می‌دهد. Codex، Docker و سایر برنامه‌ها باید به پراکسی اختصاصی وصل شوند. برنامه‌ای که پراکسی را نادیده می‌گیرد، با انتخاب مسیر در این صفحه الزاماً مسیریابی نمی‌شود. قفل فایروال فقط محدودیت ایجاد می‌کند و موتور مسیریابی نیست.", new Button[0]);
        Card("پس از به‌روزرسانی",
            "نصب‌کننده را اجرا کنید؛ تنظیمات کاربر در مسیر جداگانه نگه‌داری می‌شوند. سپس در chrome://extensions افزونهٔ Netcontroll را Reload کنید و از افزونه «اتصال این پروفایل» را بزنید. پس از تأیید دریافت قوانین، سایت‌ها و اتصال‌های قدیمی را دوباره باز کنید.",
            new[] { Button("باز کردن پوشهٔ تنظیمات", () => Process.Start(new ProcessStartInfo(Settings.Folder) { UseShellExecute = true })) });
    }
    void RenderRows()
    {
        var list = C<StackPanel>("Rows"); list.Children.Clear();
        overviewConnections = null; overviewProfiles = null; windowsStatus = null; codexLastDetail = null; codexLastBadge = null;
        string filter = C<TextBox>("Search").Text.Trim();
        Func<string, bool> match = text => (text ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        if (page == "overview") RenderOverview();
        else if (page == "codex") RenderCodex();
        else if (page == "about") RenderAbout();
        else if (page == "activity")
        {
            foreach (var item in router.History.ToArray().Reverse().Where(x => match(x.Source + " " + x.Host + " " + x.Local) &&
                (routeFilter == "all" || routeFilter == "vpn" && x.Mode == PathMode.Vpn || routeFilter == "direct" && x.Mode == PathMode.Direct || routeFilter == "failed" && !x.Success)))
                Card(item.Source + "  ·  " + item.Host,
                    item.Time.ToString("HH:mm:ss") + "   ·   " + Mode(item.Mode) + "   ·   " + (item.Success ? "خروجی محلی: " + item.Local : item.Message), new Button[0], item.Success ? "متصل" : "ناموفق");
        }
        else if (page == "docker")
        {
            Card("مسیر رجیستری و دانلود ایمیج", "پراکسی اختصاصی:  http://127.0.0.1:" + Router.DockerPort,
                new[] { Button("اینترنت عادی", () => SetDocker(PathMode.Direct), data.Docker == PathMode.Direct),
                    Button("فقط VPN", () => SetDocker(PathMode.Vpn), data.Docker == PathMode.Vpn),
                    Button("مسیر سیستم", () => SetDocker(PathMode.System), data.Docker == PathMode.System), Button("آزمون دانلود ایمیج", DockerTest) }, Mode(data.Docker));
            Card("یک‌بار اتصال؛ سپس تغییر مسیر از همین‌جا",
                "نشانی پراکسی را در تنظیمات HTTP و HTTPS مربوط به Docker Desktop قرار دهید. عبور همهٔ کانتینرها به تنظیمات Docker وابسته است؛ آزمون دانلود، فقط ارتباط رجیستری را بررسی می‌کند.",
                new[] { Button("کپی نشانی پراکسی", () => Copy("http://127.0.0.1:" + Router.DockerPort)), Button("مشاهدهٔ فعالیت Docker", () => { Navigate("activity"); C<TextBox>("Search").Text = "Docker"; }) });
            var last = router.History.ToArray().LastOrDefault(x => x.Source == "Docker");
            Card("آخرین اتصال Docker", last == null ? "هنوز در این اجرا اتصالی در پراکسی Docker مشاهده نشده است." : last.Host + "  ·  " + last.Time.ToString("HH:mm:ss") + "\n" + (last.Success ? last.Local : last.Message),
                new Button[0], last == null ? "در انتظار اتصال" : last.Success ? "متصل" : "ناموفق");
        }
        else
        {
            if (page == "apps" && data.Apps.Count == 0 && filter.Length == 0 && routeFilter == "all")
                Card("برنامه‌های آماده را از سایدبار انتخاب کنید", "Codex Desktop و Docker تنظیمات مستقل دارند. برای سایر برنامه‌های سازگار با پراکسی، یک فایل اجرایی اضافه کنید.",
                    new[] { Button("پروفایل Codex", () => Navigate("codex")), Button("پروفایل Docker", () => Navigate("docker")) });
            foreach (var rule in Collection(data).Where(x => match(x.Name + " " + x.Target) && MatchesMode(x.Mode, x.Enabled)))
            {
                var r = rule;
                var actions = new List<Button>();
                if (page == "apps") actions.Add(Button("اجرا", () => LaunchApp(r)));
                actions.Add(Button("ویرایش", () => Edit(r)));
                actions.Add(Button(r.Enabled ? "غیرفعال کردن" : "فعال کردن", () => Toggle(r)));
                if (page == "ips") actions.Add(Button("نام‌گذاری سرور", () => Rename(r.Target, r)));
                if (page == "chrome" || page == "ips") actions.Add(Button("تست اتصال TCP 443", () => Probe(r)));
                if (page == "apps") actions.Add(Button("کپی نشانی پراکسی", () => Copy("http://127.0.0.1:" + r.Port)));
                actions.Add(Button("حذف قانون", () => Delete(r)));
                string detail = page == "chrome" ? (r.Subdomains ? "همراه زیردامنه‌ها" : "فقط همین میزبان")
                    : page == "apps" ? "localhost:" + r.Port + (r.Guard ? "  ·  قفل فایروال" : "  ·  نیازمند پذیرش پراکسی") : "";
                if (page == "ips")
                { var actual = discovered.FirstOrDefault(x => x.IP == r.Target); detail = actual == null ? "مسیر اختصاصی مشاهده نشد" : actual.Description; }
                RuleRow(r, detail, actions);
            }
            if (page == "ips" && showOther)
                foreach (var item in discovered.Where(x => !x.Known && !data.Addresses.Any(r => r.Target == x.IP)))
                {
                    string label; if (!data.Labels.TryGetValue(item.IP, out label)) label = item.IP;
                    if (!match(label + " " + item.IP + " " + item.Adapter) || !MatchesMode(item.Mode, true)) continue;
                    var actions = new List<Button> { Button("نام‌گذاری", () => Rename(item.IP, null)) };
                    if (item.Saved != null) actions.Add(Button("حذف مسیر ذخیره‌شده", () => DeleteSaved(item.Saved)));
                    Card(label, item.IP + "\n" + item.Description, actions, item.Saved == null ? "مسیر سیستمی" : "ذخیره‌شده");
                }
        }
        int displayed = list.Children.Count - (page == "apps" && data.Apps.Count == 0 && filter.Length == 0 && routeFilter == "all" ? 1 : 0);
        C<TextBlock>("ResultCount").Text = displayed + " مورد";
        bool empty = list.Children.Count == 0;
        C<Border>("EmptyPanel").Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            bool filtering = filter.Length > 0 || routeFilter != "all";
            C<TextBlock>("EmptyTitle").Text = filtering ? "موردی با این فیلتر پیدا نشد" : page == "chrome" ? "اولین دامنه را اضافه کنید" : page == "ips" ? "هنوز سروری ثبت نشده" : page == "apps" ? "برنامه‌ای اضافه نشده" : "هنوز اتصالی ثبت نشده";
            C<TextBlock>("EmptyIcon").Text = filtering ? "\uE721" : icons[Array.IndexOf(keys, page)];
            C<TextBlock>("EmptyDetail").Text = filtering ? "عبارت جستجو یا فیلتر مسیر را تغییر دهید."
                : page == "chrome" ? "در حالت VPN انتخابی، دامنه‌ای را که به VPN نیاز دارد اضافه کنید؛ بقیهٔ سایت‌ها از مسیر مستقیم می‌روند."
                : page == "activity" ? "پس از اتصال مرورگر یا برنامه به پراکسی، درخواست‌های جدید اینجا نمایش داده می‌شوند."
                : "از نوار ابزار بالا یک مورد جدید اضافه کنید.";
        }
    }
    void SetWindowsDefault(bool enabled)
    {
        var next = data.Clone(); next.WindowsDefaultDirect = enabled;
        Commit(next, true);
    }
    async Task<string> DiscoverCodexAsync()
    {
        if (busy) return null;
        busy = true; w.IsEnabled = false;
        Tell("در حال پیدا کردن نسخهٔ دسکتاپ Codex…");
        try { return await Task.Run(() => AppProfiles.FindCodex()); }
        finally { busy = false; w.IsEnabled = true; }
    }
    async void FindCodex()
    {
        try
        {
            string found = await DiscoverCodexAsync();
            if (found == null) return;
            if (found.Length == 0) { Tell("Codex Desktop پیدا نشد. با «انتخاب فایل برنامه»، Codex.exe را مشخص کنید."); return; }
            var next = data.Clone(); next.CodexExecutable = found;
            await CommitAsync(next, false);
        }
        catch (Exception ex) { Tell(ex.GetBaseException().Message); }
    }
    void BrowseCodex()
    {
        var dialog = new OpenFileDialog { Title = "انتخاب Codex Desktop", Filter = "Windows applications|*.exe" };
        if (dialog.ShowDialog(w) != true) return;
        var next = data.Clone(); next.CodexExecutable = dialog.FileName;
        AppProfiles.CodexStartInfo(next);
        Commit(next, false);
    }
    async void LaunchCodex()
    {
        if (busy) return;
        try
        {
            if (String.IsNullOrWhiteSpace(data.CodexExecutable) || !File.Exists(data.CodexExecutable))
            {
                string found = await DiscoverCodexAsync();
                if (found == null) return;
                if (String.IsNullOrEmpty(found)) throw new FileNotFoundException("Codex Desktop پیدا نشد. ابتدا مسیر فایل برنامه را انتخاب کنید.");
                var next = data.Clone(); next.CodexExecutable = found;
                if (!await CommitAsync(next, false)) return;
            }
            Process.Start(AppProfiles.CodexStartInfo(data));
            Tell("درخواست اجرای Codex با مسیر " + Mode(data.Codex) + " فرستاده شد. عبور درخواست را در فعالیت اتصال‌ها بررسی کنید.");
        }
        catch (Exception ex) { Tell(ex.GetBaseException().Message); }
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
        bool removed = false;
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
            removed = true;
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
        if (removed) Scan();
    }
    void Edit(Rule old)
    {
        var rule = Editor.Open(w, page, old, page == "chrome" && data.ChromeDefault == PathMode.Direct ? PathMode.Vpn : PathMode.Direct);
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
        await CommitAsync(next, system);
    }
    async Task<bool> CommitAsync(Settings next, bool system, bool propagateErrors = false)
    {
        if (busy) return false;
        var previous = data;
        busy = true;
        w.IsEnabled = false;
        bool systemApplied = false, routerAttempted = false;
        bool appsOnly = page == "apps";
        try
        {
            previous = data.Clone();
            if (system)
            {
                bool takeover = false;
                string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Netcontroll Routes", "routes.json");
                object adoptedValue;
                bool adopted = page == "ips" && File.Exists(config) &&
                    Settings.Json<Dictionary<string, object>>(File.ReadAllText(config)).TryGetValue("Adopted", out adoptedValue) && Convert.ToBoolean(adoptedValue);
                if (page == "ips" && !adopted && File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DirectServerRoutes", "Update-Routes.ps1")))
                {
                    if (MessageBox.Show(w, "مدیریت مسیرهای قبلی این لپ‌تاپ به فهرست فعلی منتقل شود؟ فقط IPهای فعال همین فهرست حفظ خواهند شد.",
                        "انتقال مدیریت مسیرها", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return false;
                    takeover = true;
                }
                await Task.Run(() => ApplySystem(next, takeover, appsOnly));
                systemApplied = true;
            }
            routerAttempted = true;
            router.Configure(next);
            next.Save();
            data = next;
            // Persistence is complete. A visual refresh failure must not report the write as rejected.
            try
            {
                BuildNav(); Render();
                Tell(page == "chrome" ? "قانون ذخیره شد. دریافت قوانین در Chrome بالای فهرست نمایش داده می‌شود."
                    : "تغییرات ذخیره و اعمال شد؛ اتصال‌های قبلی را دوباره برقرار کنید.");
            }
            catch (Exception viewError) { Tell("تغییرات ذخیره شد، اما تازه‌سازی نما کامل نشد: " + viewError.GetBaseException().Message); }
            return true;
        }
        catch (Exception ex)
        {
            var recoveryErrors = new List<string>();
            if (systemApplied)
            {
                Tell("ثبت تغییر کامل نشد؛ در حال بازگرداندن تنظیمات قبلی ویندوز…");
                try { await Task.Run(() => ApplySystem(previous, false, appsOnly)); }
                catch (Exception rollback) { recoveryErrors.Add("بازگرداندن ویندوز انجام نشد: " + rollback.GetBaseException().Message); }
            }
            if (routerAttempted)
                try { router.Configure(previous); }
                catch (Exception rollback) { recoveryErrors.Add("بازگرداندن پراکسی انجام نشد: " + rollback.GetBaseException().Message); }
            data = previous;
            string message = "تغییر ثبت نشد. " + ex.GetBaseException().Message;
            if (recoveryErrors.Count > 0)
                message += "\nبازگشت کامل تأیید نشد. " + String.Join(" · ", recoveryErrors) + " تنظیم مسیر ویندوز را دوباره اعمال و گزارش را بررسی کنید.";
            else if (systemApplied) message += " تنظیمات قبلی ویندوز و پراکسی بازگردانده شد.";
            if (page == "overview") UpdateOverviewStats();
            Tell(message);
            if (propagateErrors) throw new IOException(message, ex);
            return false;
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
                          new { WindowsDefaultDirect = next.WindowsDefaultDirect,
                                Rules = next.Addresses.Where(x => x.Enabled)
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
            MessageBox.Show(w,
                            "در همان پروفایلی که سایت را باز می‌کنید، صفحهٔ chrome://extensions را باز کنید.\n\n" +
                            "۱. برای نصب اولیه، Developer mode را روشن کنید. Load unpacked را بزنید و مسیر کپی‌شده را وارد کنید.\n" +
                            "۲. اگر افزونه از قبل نصب است، پس از به‌روزرسانی برنامه روی Reload در کارت Netcontroll بزنید.\n" +
                            "۳. در افزونه «اتصال این پروفایل» را بزنید. بعد از تأیید قوانین، تب سایت را دوباره بارگذاری کنید.",
                            "اتصال پروفایل فعلی Chrome");
        }
        else
            Process.Start(exe,
                          "--user-data-dir=\"" + Path.Combine(Settings.Folder, "ChromeProfile") +
                              "\" --proxy-server=\"http://127.0.0.1:" + Router.ChromePort +
                              ("\" --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp " +
                               "--no-first-run --no-default-browser-check"));
    }
    void LaunchChromeMode(PathMode mode)
    {
        var folder = Path.Combine(Settings.Folder, mode == PathMode.Vpn ? "ChromeVpnProfile" : "ChromeDirectProfile");
        int port = mode == PathMode.Vpn ? Router.ChromeVpnPort : Router.ChromeDirectPort;
        Process.Start(ChromeExe(), "--user-data-dir=\"" + folder + "\" --proxy-server=\"http://127.0.0.1:" + port +
            "\" --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp --no-first-run --no-default-browser-check");
        Tell("پنجرهٔ مستقل با مسیر " + Mode(mode) + " باز شد؛ داده‌های ورود این پروفایل جداست. اتصال را در فعالیت‌ها بررسی کنید.");
    }
    void LaunchApp(Rule rule)
    {
        if (!rule.Enabled) throw new InvalidOperationException("ابتدا قانون را فعال کنید.");
        Process.Start(AppProfiles.BuildStartInfo(rule.Target, rule.Port));
        Tell("درخواست اجرا فرستاده شد. پذیرش پراکسی را در فعالیت اتصال‌ها بررسی کنید؛ نمونهٔ قبلی برنامه باید بسته باشد.");
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
