using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Netcontroll.Design;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetcontrollSetup
{
static class Setup
{
    static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Netcontroll");
    static readonly string Shortcut =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Netcontroll.lnk");
    static readonly string DesktopShortcut =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Netcontroll.lnk");
    static readonly string ApplicationPath = Path.Combine(Root, "Netcontroll.exe");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Netcontroll";
    static readonly string[] Files = {
        "Netcontroll.exe",  "Main.xaml",         @"chrome\manifest.json", @"chrome\popup.html",
        @"chrome\popup.js", @"chrome\worker.js", @"system\Routes.ps1",    @"system\SavedRoutes.ps1",
        "README.md"
    };
    static void EnterDesktopMutex(Mutex mutex)
    {
        try
        {
            if (!mutex.WaitOne(0))
                throw new InvalidOperationException("ابتدا Netcontroll را از منوی کنار ساعت ویندوز ببندید.");
        }
        catch (AbandonedMutexException)
        {
            // WaitOne grants ownership when the previous process exited unexpectedly.
        }
    }
    static void Extract(string dir)
    {
        Directory.CreateDirectory(dir);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")) using (
            var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            foreach (var name in Files)
            {
                var entry = zip.Entries.Single(x => x.FullName.Replace('/', '\\') == name);
                string path = Path.Combine(dir, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var input = entry.Open()) using (var file = File.Create(path)) input.CopyTo(file);
            }
        }
    }
    static Process InstalledProcess()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName("Netcontroll"))
        {
            try
            {
                if (process.SessionId == session && String.Equals(process.MainModule.FileName, ApplicationPath,
                                                                  StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
        }
        return null;
    }

    static void StopForUpdate(bool allowed)
    {
        using (var process = InstalledProcess())
        {
            if (process == null)
                return;
            if (!allowed)
                throw new InvalidOperationException("برنامه باز شده است. آن را ببندید و دوباره نصب را بزنید.");
            EventWaitHandle signal;
            if (EventWaitHandle.TryOpenExisting(@"Local\Netcontroll.ExitForUpdate.1", out signal))
            {
                using (signal) signal.Set();
            }
            else if (new Version(process.MainModule.FileVersionInfo.FileVersion) <= new Version(1, 0, 1, 0))
            {
                // These releases only hide on WM_CLOSE and have no update-exit signal.
                process.Kill();
            }
            if (!process.WaitForExit(5000))
                throw new InvalidOperationException(
                    "ویرایش باز را تمام کنید و از منوی کنار ساعت از Netcontroll خارج شوید.");
        }
    }

    static void Install(bool stopRunning)
    {
        string parent = Path.GetDirectoryName(Root);
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, "Netcontroll.stage." + Guid.NewGuid().ToString("N")),
               backup = stage + ".previous";
        try
        {
            Extract(stage);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(stage, "Setup.exe"));
            StopForUpdate(stopRunning);
            using (var mutex = new Mutex(false, @"Local\Netcontroll.Desktop.1"))
            {
                EnterDesktopMutex(mutex);
                try
                {
                    bool updating = Directory.Exists(Root);
                    if (updating && !File.Exists(Path.Combine(Root, "Setup.exe")))
                        throw new IOException("پوشهٔ نصب در اختیار برنامهٔ دیگری است.");
                    var replaced = new Stack<string>();
                    try
                    {
                        ReplacePayload(stage, Root, backup, replaced);
                        CreateShortcut(Shortcut);
                        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                        {
                            key.SetValue("DisplayName", "Netcontroll");
                            key.SetValue("Publisher", "peymanarjmand");
                            key.SetValue("InstallLocation", Root);
                            key.SetValue("DisplayIcon", ApplicationPath);
                            key.SetValue("UninstallString", "\"" + Path.Combine(Root, "Setup.exe") + "\" --uninstall");
                            key.SetValue("DisplayVersion", Application.ProductVersion);
                        }
                    }
                    catch
                    {
                        RestorePayload(Root, backup, replaced);
                        if (!updating)
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                            if (IsOurShortcut(Shortcut))
                                File.Delete(Shortcut);
                            RemovePayload(Root);
                        }
                        throw;
                    }
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
        finally
        {
            if (Directory.Exists(stage))
                RemovePayload(stage);
        }
        if (Directory.Exists(backup))
            RemovePayload(backup);
    }
    static void ReplacePayload(string stage, string root, string backup, Stack<string> replaced)
    {
        // An open browser can hold the extension directory. Keep directory handles valid.
        foreach (string name in Files.Concat(new[] { "Setup.exe" }))
        {
            string target = Path.Combine(root, name), source = Path.Combine(stage, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target))
            {
                string previous = Path.Combine(backup, name);
                Directory.CreateDirectory(Path.GetDirectoryName(previous));
                File.Replace(source, target, previous);
            }
            else
                File.Move(source, target);
            replaced.Push(name);
        }
    }
    static void RestorePayload(string root, string backup, Stack<string> replaced)
    {
        foreach (string name in replaced)
        {
            string previous = Path.Combine(backup, name), target = Path.Combine(root, name);
            if (File.Exists(previous))
                File.Replace(previous, target, null);
            else
                File.Delete(target);
        }
        if (Directory.Exists(backup))
            RemovePayload(backup);
    }
    static bool IsOurShortcut(string path)
    {
        if (!File.Exists(path))
            return false;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        string target = shell.CreateShortcut(path).TargetPath;
        return String.Equals(target, ApplicationPath, StringComparison.OrdinalIgnoreCase);
    }
    static void CreateShortcut(string path)
    {
        if (File.Exists(path) && !IsOurShortcut(path))
            throw new IOException("یک میان‌بر هم‌نام با مقصد دیگری در این محل وجود " +
                                  "دارد.");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = ApplicationPath;
        link.WorkingDirectory = Root;
        link.IconLocation = ApplicationPath + ",0";
        link.Save();
    }
    static void RemovePayload(string dir)
    {
        foreach (string name in Files.Concat(new[] { "Setup.exe" }))
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path))
                File.Delete(path);
        }
        foreach (string sub in new[] { "chrome", "system" })
        {
            string p = Path.Combine(dir, sub);
            if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any())
                Directory.Delete(p);
        }
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }
    static void Uninstall()
    {
        using (var mutex = new Mutex(false, @"Local\Netcontroll.Desktop.1"))
        {
            EnterDesktopMutex(mutex);
            mutex.ReleaseMutex();
        }
        string rules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                    @"Netcontroll Routes\Routes.ps1");
        if (File.Exists(rules))
        {
            using (var p = Process.Start(new ProcessStartInfo(
                       Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                       "-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \"" + rules +
                           "\" -Action Remove") { UseShellExecute = true, Verb = "runas",
                                                    WindowStyle = ProcessWindowStyle.Hidden }))
            {
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new Exception("پاک‌سازی مسیرهای ویندوز انجام نشد.");
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (IsOurShortcut(Shortcut))
            File.Delete(Shortcut);
        if (IsOurShortcut(DesktopShortcut))
            File.Delete(DesktopShortcut);
        RemovePayload(Root);
    }
    static int ShowInstaller()
    {
        AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
        var window = Theme.LoadWindow("installer.xaml");
        Theme.Attach(window);
        var heading = (Controls.TextBlock)window.FindName("Heading");
        var intro = (Controls.TextBlock)window.FindName("Intro");
        var status = (Controls.TextBlock)window.FindName("SetupStatus");
        var location = (Controls.TextBox)window.FindName("InstallLocation");
        var primary = (Controls.Button)window.FindName("PrimaryAction");
        var close = (Controls.Button)window.FindName("CloseAction");
        var desktop = (Controls.CheckBox)window.FindName("DesktopShortcut");
        var art = (Wpf.FrameworkElement)window.FindName("RouteArt");
        ((Controls.TextBlock)window.FindName("VersionLabel")).Text =
            Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        location.Text = Root;
        bool updating = File.Exists(ApplicationPath);
        bool stopRunning;
        using (var process = InstalledProcess()) stopRunning = process != null;
        if (updating)
        {
            heading.Text = window.Title = "به‌روزرسانی Netcontroll";
            intro.Text = "نسخهٔ جدید جایگزین نسخهٔ قبلی می‌شود. قوانین و نام سرورها حفظ " +
                         "می‌شوند.";
            primary.Content = stopRunning ? "بستن برنامه و به‌روزرسانی"
                                          : "به‌روزرسانی برنامه";
            if (stopRunning)
                status.Text = "Netcontroll باز است. پیش از ادامه، ویرایش‌های بازتان را ذخیره " +
                              "کنید.";
        }
        window.SizeChanged += (s, e) => art.Visibility =
            window.ActualWidth < 665 ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        bool installed = false, installing = false;
        window.Closing += (s, e) =>
        {
            if (installing)
                e.Cancel = true;
        };
        close.Click += (s, e) => window.Close();
        primary.Click += async (s, e) =>
        {
            if (installed)
            {
                try
                {
                    Process.Start(ApplicationPath);
                    window.Close();
                }
                catch (Exception ex)
                {
                    status.Text = ex.GetBaseException().Message;
                }
                return;
            }
            if (installing)
                return;
            installing = true;
            primary.IsEnabled = close.IsEnabled = desktop.IsEnabled = false;
            status.Text = "در حال نصب فایل‌های برنامه…";
            try
            {
                await Task.Run(() => Install(stopRunning));
                installed = true;
                heading.Text = updating ? "به‌روزرسانی انجام شد" : "نصب انجام شد";
                intro.Text = "Netcontroll آمادهٔ اجراست.";
                primary.Content = "باز کردن برنامه";
                status.Text = "برنامه در منوی Start در دسترس است.";
                if (desktop.IsChecked == true)
                {
                    try
                    {
                        CreateShortcut(DesktopShortcut);
                        status.Text = "میان‌بر برنامه روی دسکتاپ و در منوی Start آماده " +
                                      "است.";
                    }
                    catch (Exception ex)
                    {
                        status.Text +=
                            "  میان‌بر دسکتاپ ساخته نشد. " + ex.GetBaseException().Message;
                    }
                }
            }
            catch (Exception ex)
            {
                status.Text = "نصب تکمیل نشد. " + ex.GetBaseException().Message;
                desktop.IsEnabled = true;
            }
            finally
            {
                installing = false;
                primary.IsEnabled = close.IsEnabled = true;
                primary.Focus();
            }
        };
        window.ShowDialog();
        return 0;
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--extract")
            {
                string path = Path.GetFullPath(args[1]);
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                    throw new IOException("Extraction directory must be empty");
                Extract(path);
                return 0;
            }
            if (args.Contains("--uninstall"))
            {
                if (MessageBox.Show("Netcontroll و قوانین سیستمی آن حذف شوند؟ تنظیمات شخصی حفظ می‌شوند. " +
                                    "ابتدا " +
                                        "افزونهٔ Chrome را قطع و پراکسی Docker را به تنظیم قبلی برگردانید.",
                                    "Netcontroll", MessageBoxButtons.YesNo) != DialogResult.Yes)
                    return 0;
                string tmp = Path.Combine(Path.GetTempPath(),
                                          "Netcontroll-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, tmp);
                Process.Start(tmp, "--finish-remove");
                return 0;
            }
            if (args.Contains("--finish-remove"))
            {
                System.Threading.Thread.Sleep(700);
                Uninstall();
                MessageBox.Show("برنامه حذف شد.", "Netcontroll");
                return 0;
            }
            return ShowInstaller();
        }
        catch (Exception ex)
        {
            if (args.Length != 2 || args[0] != "--extract")
                MessageBox.Show(ex.GetBaseException().Message, "Netcontroll", MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
            return 1;
        }
    }
}
}
