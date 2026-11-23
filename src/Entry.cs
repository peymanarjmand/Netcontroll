using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Netcontroll
{
static class Entry
{
    internal static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
    [STAThread]
    public static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
        using (
            var updateExit = new EventWaitHandle(
                false, EventResetMode.AutoReset,
                @"Local\Netcontroll.ExitForUpdate.1")) using (var single =
                                                                  new Mutex(false,
                                                                            @"Local\Netcontroll.Desktop." +
                                                                            @"1")) using (var wake =
                                                                                              new EventWaitHandle(
                                                                                                  false,
                                                                                                  EventResetMode
                                                                                                      .AutoReset,
                                                                                                  @"Local\Netcon" +
                                                                                                  @"troll.Show.1"))
        {
            bool own = false;
            try
            {
                try
                {
                    own = single.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    own = true;
                }
                if (!own)
                {
                    wake.Set();
                    return 0;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var state = Settings.Load();
                using (var router = new Router())
                {
                    router.Configure(state);
                    Window w;
                    using (var x = File.OpenRead(Path.Combine(Base, "Main.xaml"))) w = (Window)XamlReader.Load(x);
                    var shell = new Shell(w, state, router);
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                    using (var tray = new Forms.NotifyIcon { Icon = icon, Text = "Netcontroll", Visible = true })
                    {
                        Action show = () =>
                        {
                            w.Show();
                            w.WindowState = WindowState.Normal;
                            w.Activate();
                        };
                        var menu = new Forms.ContextMenuStrip();
                        menu.Items.Add("نمایش Netcontroll", null, (s, e) => show());
                        bool quit = false;
                        menu.Items.Add("خروج", null,
                                       (s, e) =>
                                       {
                                           quit = true;
                                           app.Shutdown();
                                       });
                        tray.ContextMenuStrip = menu;
                        tray.DoubleClick += (s, e) => show();
                        w.Closing += (s, e) =>
                        {
                            if (!quit)
                            {
                                e.Cancel = true;
                                w.Hide();
                            }
                        };
                        var registration = ThreadPool.RegisterWaitForSingleObject(
                            wake, (o, t) => app.Dispatcher.BeginInvoke(show), null, -1, false);
                        var updateRegistration = ThreadPool.RegisterWaitForSingleObject(
                            updateExit,
                            (o, t) => app.Dispatcher.BeginInvoke(new Action(
                                () =>
                                {
                                    if (!shell.CanExitForUpdate ||
                                        app.Windows.Cast<Window>().Any(win => win != w && win.IsVisible))
                                        return;
                                    quit = true;
                                    app.Shutdown();
                                })),
                            null, -1, false);
                        try
                        {
                            app.MainWindow = w;
                            if (!args.Contains("--tray"))
                                w.Show();
                            app.Run();
                        }
                        finally
                        {
                            updateRegistration.Unregister(null);
                            registration.Unregister(null);
                            tray.Visible = false;
                            shell.Dispose();
                        }
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.GetBaseException().Message, "Netcontroll", MessageBoxButton.OK,
                                MessageBoxImage.Error);
                return 1;
            }
            finally
            {
                if (own)
                    single.ReleaseMutex();
            }
        }
    }
}
}
