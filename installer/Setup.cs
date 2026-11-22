using System;
using System.Diagnostics;
using System.Drawing;
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
    static void CheckClosed()
    {
        using (var m = new Mutex(false, @"Local\Netcontroll.Desktop.1"))
        {
            bool own = false;
            try
            {
                try
                {
                    own = m.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    own = true;
                }
                if (!own)
                    throw new InvalidOperationException("ابتدا Netcontroll را از منوی کنار ساعت ویندوز ببندید.");
            }
            finally
            {
                if (own)
                    m.ReleaseMutex();
            }
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
    static void Install()
    {
        CheckClosed();
        string parent = Path.GetDirectoryName(Root);
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, "Netcontroll.stage." + Guid.NewGuid().ToString("N")),
               backup = stage + ".previous";
        if (Directory.Exists(Root) && !File.Exists(Path.Combine(Root, "Setup.exe")))
            throw new IOException("پوشهٔ نصب در اختیار برنامهٔ دیگری است.");
        bool movedNewFiles = false;
        try
        {
            Extract(stage);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(stage, "Setup.exe"));
            if (Directory.Exists(Root))
                Directory.Move(Root, backup);
            Directory.Move(stage, Root);
            movedNewFiles = true;
            CreateShortcut(Shortcut);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Netcontroll");
                key.SetValue("Publisher", "peymanarjmand");
                key.SetValue("DisplayVersion", Application.ProductVersion);
                key.SetValue("InstallLocation", Root);
                key.SetValue("DisplayIcon", Path.Combine(Root, "Netcontroll.exe"));
                key.SetValue("UninstallString", "\"" + Path.Combine(Root, "Setup.exe") + "\" --uninstall");
            }
        }
        catch
        {
            if (Directory.Exists(backup))
            {
                if (Directory.Exists(Root))
                    RemovePayload(Root);
                if (!Directory.Exists(Root))
                    Directory.Move(backup, Root);
            }
            else if (movedNewFiles)
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                if (IsOurShortcut(Shortcut))
                    File.Delete(Shortcut);
                RemovePayload(Root);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(stage))
                RemovePayload(stage);
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
            throw new IOException("یک میان‌بر هم‌نام با مقصد دیگری در این محل وجود دارد.");
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
        CheckClosed();
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
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new Form())
        {
            form.Text = "نصب Netcontroll";
            form.StartPosition = FormStartPosition.CenterScreen;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.Font = new Font("Segoe UI", 10);
            form.BackColor = Color.White;
            form.ClientSize = new Size(500, 330);
            form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 15), ColumnCount = 1, RowCount = 6
            };
            foreach (int height in new[]{47, 40, 36, 41})
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            form.Controls.Add(layout);
            var title = new Label {Text = "نصب Netcontroll", Dock = DockStyle.Fill,
                RightToLeft = RightToLeft.Yes, Font = new Font(form.Font.FontFamily, 19, FontStyle.Bold),
                ForeColor = Color.FromArgb(20,65,57)};
            var description = new Label {Text = "برنامه برای حساب ویندوز شما در این پوشه نصب می‌شود.",
                Dock = DockStyle.Fill, RightToLeft = RightToLeft.Yes, TextAlign = ContentAlignment.MiddleLeft};
            var location = new TextBox {Text = Root, Dock = DockStyle.Fill, ReadOnly = true, Name = "InstallLocation"};
            var desktop = new CheckBox {Text = "میان‌بر روی دسکتاپ بساز", Checked = true, Name = "DesktopShortcut",
                Dock = DockStyle.Fill, RightToLeft = RightToLeft.Yes};
            var status = new Label {Dock = DockStyle.Fill, RightToLeft = RightToLeft.Yes,
                TextAlign = ContentAlignment.MiddleLeft, Name = "SetupStatus"};
            var buttons = new FlowLayoutPanel {Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft};
            var next = new Button {Text = "نصب برنامه", Name = "PrimaryAction", Size = new Size(145, 36),
                BackColor = Color.FromArgb(20,99,77), ForeColor = Color.White, FlatStyle = FlatStyle.Flat};
            var close = new Button {Text = "بستن", Size = new Size(85,36), DialogResult = DialogResult.Cancel};
            layout.Controls.Add(title); layout.Controls.Add(description); layout.Controls.Add(location);
            layout.Controls.Add(desktop); layout.Controls.Add(status); layout.Controls.Add(buttons);
            buttons.Controls.Add(next); buttons.Controls.Add(close);
            form.AcceptButton = next; form.CancelButton = close;
            bool installed = false;
            if (File.Exists(ApplicationPath))
                next.Text = "نصب مجدد / به‌روزرسانی";
            next.Click += (s,e) => {
                if (installed)
                {
                    try { Process.Start(ApplicationPath); form.Close(); }
                    catch (Exception ex) { status.Text = ex.GetBaseException().Message; }
                    return;
                }
                next.Enabled = close.Enabled = desktop.Enabled = false;
                form.UseWaitCursor = true;
                try
                {
                    status.Text = "در حال کپی فایل‌ها و ثبت در منوی Start…";
                    form.Refresh();
                    Install();
                    installed = true;
                    title.Text = "نصب انجام شد";
                    next.Text = "باز کردن برنامه";
                    status.Text = "Netcontroll از منوی Start قابل اجراست.";
                    if (desktop.Checked)
                    {
                        try { CreateShortcut(DesktopShortcut); status.Text = "از دسکتاپ یا منوی Start می‌توانید برنامه را باز کنید."; }
                        catch (Exception ex) { status.Text += " ساخت میان‌بر دسکتاپ انجام نشد. " + ex.GetBaseException().Message; }
                    }
                }
                catch (Exception ex)
                {
                    status.Text = "نصب تکمیل نشد. " + ex.GetBaseException().Message;
                    desktop.Enabled = true;
                }
                finally { form.UseWaitCursor = false; next.Enabled = close.Enabled = true; }
            };
            form.Shown += (s,e) => { form.Activate(); next.Focus(); };
            form.ShowDialog();
            return 0;
        }
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
                if (MessageBox.Show("Netcontroll و قوانین سیستمی آن حذف شوند؟ تنظیمات شخصی حفظ می‌شوند. ابتدا " +
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
