using System;
using System.Diagnostics;
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
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(Shortcut);
            link.TargetPath = Path.Combine(Root, "Netcontroll.exe");
            link.WorkingDirectory = Root;
            link.IconLocation = link.TargetPath + ",0";
            link.Save();
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Netcontroll");
                key.SetValue("Publisher", "peymanarjmand");
                key.SetValue("DisplayVersion", "1.0.0");
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
    static void RemovePayload(string dir)
    {
        foreach (string name in Files.Concat(new[] { "Setup.exe" }))
            File.Delete(Path.Combine(dir, name));
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
        File.Delete(Shortcut);
        RemovePayload(Root);
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
            if (MessageBox.Show("Netcontroll برای حساب ویندوز شما نصب شود؟", "نصب Netcontroll",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
                                MessageBoxOptions.RtlReading) != DialogResult.Yes)
                return 0;
            Install();
            if (MessageBox.Show("نصب انجام شد. برنامه باز شود؟", "Netcontroll", MessageBoxButtons.YesNo) ==
                DialogResult.Yes)
                Process.Start(Path.Combine(Root, "Netcontroll.exe"));
            return 0;
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
