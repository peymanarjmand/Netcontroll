using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Netcontroll
{
// Launch-scoped proxy settings never edit Windows' proxy or another process's environment.
public static class AppProfiles
{
    static readonly object discoveryGate = new object();
    static string discovered = "";
    static DateTime discoveredUntil = DateTime.MinValue;
    public static string ProxyAddress(int port)
    {
        if (port < 1024 || port > 65535)
            throw new ArgumentOutOfRangeException("port");
        return "http://127.0.0.1:" + port;
    }
    public static ProcessStartInfo BuildStartInfo(string executable, int port, bool chromium = false)
    {
        if (String.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable) ||
            !String.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
            throw new FileNotFoundException("فایل اجرایی برنامه پیدا نشد. مسیر فایل EXE را دوباره انتخاب کنید.");
        string path = Path.GetFullPath(executable), proxy = ProxyAddress(port);
        var start = new ProcessStartInfo(path) {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)
        };
        // Lowercase is needed by a number of Unix-derived clients; Windows treats the names
        // case-insensitively while these assignments also remain useful in the test harness.
        foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            start.EnvironmentVariables[key] = proxy;
        foreach (string key in new[] { "NO_PROXY", "no_proxy" })
            start.EnvironmentVariables[key] = "localhost,127.0.0.1,::1";
        start.EnvironmentVariables["NODE_USE_ENV_PROXY"] = "1";
        if (chromium)
            start.Arguments = "--proxy-server=\"" + proxy + "\" --disable-quic " +
                              "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";
        return start;
    }
    public static ProcessStartInfo CodexStartInfo(Settings settings)
    {
        string executable = String.IsNullOrWhiteSpace(settings.CodexExecutable)
                                ? FindCodex() : settings.CodexExecutable;
        if (String.IsNullOrWhiteSpace(executable))
            throw new FileNotFoundException("Codex Desktop پیدا نشد. فایل اجرایی نسخهٔ دسکتاپ را از همین صفحه انتخاب کنید.");
        if (!IsCodexDesktop(executable))
            throw new InvalidOperationException("این فایل به‌عنوان نسخهٔ دسکتاپ Codex شناسایی نشد. فایل برنامه را از پوشهٔ نصب انتخاب کنید.");
        return BuildStartInfo(executable, Router.CodexPort, true);
    }
    public static bool IsCodexDesktop(string executable)
    {
        if (String.IsNullOrWhiteSpace(executable))
            return false;
        string folder = Path.GetDirectoryName(executable);
        if (String.IsNullOrEmpty(folder))
            return false;
        return File.Exists(Path.Combine(folder, "chrome_elf.dll")) ||
               File.Exists(Path.Combine(folder, "resources", "app.asar")) ||
               File.Exists(Path.Combine(folder, "resources", "app", "package.json"));
    }
    public static string FindCodex()
    {
        lock (discoveryGate)
        {
            if (DateTime.UtcNow < discoveredUntil && (discovered.Length == 0 || File.Exists(discovered)))
                return discovered;
            discovered = FindCodexCore();
            discoveredUntil = DateTime.UtcNow.AddSeconds(20);
            return discovered;
        }
    }
    static string FindCodexCore()
    {
        // Prefer real application binaries to Store execution aliases, which may ignore
        // the launch environment. Only read registry data; never change package ACLs.
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            try
            {
                using (var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Codex.exe"))
                {
                    string path = key == null ? null : key.GetValue(null) as string;
                    if (IsExecutable(path) && IsCodexDesktop(path))
                        return path;
                }
            }
            catch (System.Security.SecurityException) {}
            catch (UnauthorizedAccessException) {}
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var folder in new[] {
            Path.Combine(local, "Programs", "Codex"), Path.Combine(local, "Codex"),
            Path.Combine(local, "Programs", "OpenAI", "Codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Codex")
        })
        {
            foreach (var relative in new[] { "Codex.exe", Path.Combine("app", "Codex.exe") })
            {
                var file = Path.Combine(folder, relative);
                if (IsExecutable(file) && IsCodexDesktop(file))
                    return file;
            }
            try
            {
                if (Directory.Exists(folder))
                    foreach (var version in Directory.GetDirectories(folder, "app-*")
                                                     .OrderByDescending(p => Directory.GetLastWriteTimeUtc(p)))
                    {
                        string file = Path.Combine(version, "Codex.exe");
                        if (IsExecutable(file) && IsCodexDesktop(file))
                            return file;
                    }
            }
            catch (IOException) {}
            catch (UnauthorizedAccessException) {}
        }
        try
        {
            using (var packages = Registry.CurrentUser.OpenSubKey(
                       @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                if (packages != null)
                    foreach (var name in packages.GetSubKeyNames().Where(n =>
                                 n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)))
                        using (var item = packages.OpenSubKey(name))
                        {
                            string folder = item == null ? null : item.GetValue("PackageRootFolder") as string;
                            if (String.IsNullOrWhiteSpace(folder))
                                continue;
                            foreach (string relative in new[] { Path.Combine("app", "Codex.exe"), "Codex.exe" })
                            {
                                string file = Path.Combine(folder, relative);
                                if (IsExecutable(file) && IsCodexDesktop(file))
                                    return file;
                            }
                        }
        }
        catch (System.Security.SecurityException) {}
        catch (UnauthorizedAccessException) {}
        catch (IOException) {}
        return FindStoreCodex();
    }
    static string FindStoreCodex()
    {
        // WindowsApps can be protected even when the package is launchable. Ask the
        // package manager for this user's registered install location; never take ownership.
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(powershell))
            return "";
        try
        {
            var start = new ProcessStartInfo(powershell,
                "-NoProfile -NonInteractive -Command \"Get-AppxPackage -Name 'OpenAI.Codex' | ForEach-Object { $_.InstallLocation }\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(4000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) {}
                    return "";
                }
                if (process.ExitCode != 0)
                    return "";
                foreach (string folder in output.GetAwaiter().GetResult().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    foreach (string relative in new[] { Path.Combine("app", "Codex.exe"), "Codex.exe" })
                    {
                        string file = Path.Combine(folder.Trim(), relative);
                        if (IsExecutable(file) && IsCodexDesktop(file))
                            return file;
                    }
            }
        }
        catch (System.ComponentModel.Win32Exception) {}
        catch (IOException) {}
        catch (InvalidOperationException) {}
        catch (ArgumentException) {}
        return "";
    }
    static bool IsExecutable(string path)
    {
        return !String.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) &&
               String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path);
    }
}
}
