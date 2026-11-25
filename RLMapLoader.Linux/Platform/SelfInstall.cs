using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.Platform
{
    /// <summary>
    /// Makes the downloaded binary behave like an app: the first time it's opened from anywhere (Downloads, the extracted
    /// archive...) it copies itself to ~/.local/bin, adds "RL Map Loader" to the app menu and starts that copy. From then
    /// on it's opened from the menu and keeps itself up to date.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public static class SelfInstall
    {
        private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static string DataHome =>
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".local", "share");

        public static string InstalledPath => Path.Combine(Home, ".local", "bin", "rl-map-loader");
        private static string DesktopFile => Path.Combine(DataHome, "applications", "rl-map-loader.desktop");
        private static string IconFile => Path.Combine(DataHome, "icons", "hicolor", "512x512", "apps", "rl-map-loader.png");
        private static string VersionStamp => Path.Combine(DataHome, "RLMapLoader", "installed-version");

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

        /// <summary>
        /// Run before anything else. Returns true when the installed copy was started and this process should exit.
        /// </summary>
        public static bool TryInstallAndRelaunch(string[] args)
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !Updater.IsPublishedBuild || args.Contains("--no-install")) return false;

            if (SamePath(exe, InstalledPath))
            {
                EnsureMenuEntry();
                return false;
            }
            // Installed by a package manager (/usr/bin, /opt): leave it alone.
            if (exe.StartsWith("/usr/", StringComparison.Ordinal) || exe.StartsWith("/opt/", StringComparison.Ordinal)) return false;

            try
            {
                bool installedUpToDate = File.Exists(InstalledPath) && ReadStampedVersion() is { } v && v >= Updater.CurrentVersion;
                if (!installedUpToDate)
                {
                    StopRunningInstances();
                    Directory.CreateDirectory(Path.GetDirectoryName(InstalledPath)!);
                    var tmp = InstalledPath + ".download";
                    File.Copy(exe, tmp, overwrite: true);
                    File.SetUnixFileMode(tmp, (UnixFileMode)0b111_101_101); // 755
                    File.Move(tmp, InstalledPath, overwrite: true);         // atomic, also over a running copy
                    EnsureMenuEntry();
                    Log.Write($"Installed {Updater.CurrentVersionText} to {InstalledPath} (from {exe})");
                }

                var psi = new ProcessStartInfo(InstalledPath) { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                if (!installedUpToDate) psi.ArgumentList.Add("--installed");
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                // Couldn't install: just run from where we are.
                Log.Write("Self-install failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>"rl-map-loader --install": install (or reinstall) and add the menu entry without starting the app.</summary>
        public static int InstallOnly()
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return 1;
            try
            {
                if (!SamePath(exe, InstalledPath))
                {
                    StopRunningInstances();
                    Directory.CreateDirectory(Path.GetDirectoryName(InstalledPath)!);
                    var tmp = InstalledPath + ".download";
                    File.Copy(exe, tmp, overwrite: true);
                    File.SetUnixFileMode(tmp, (UnixFileMode)0b111_101_101);
                    File.Move(tmp, InstalledPath, overwrite: true);
                }
                EnsureMenuEntry();
                Console.WriteLine($"Installed RL Map Loader {Updater.CurrentVersionText}: {InstalledPath}");
                Console.WriteLine("Open it from your app menu. It keeps itself up to date.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Install failed: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// After the installed copy updated itself: refresh the menu entry and the version stamp, so opening an older
        /// downloaded copy later doesn't put that older version back.
        /// </summary>
        public static void AfterUpdate()
        {
            if (Environment.ProcessPath is { } exe && SamePath(exe, InstalledPath)) EnsureMenuEntry();
        }

        /// <summary>Writes the app-menu entry and icon (pointing at the installed binary) when missing or outdated.</summary>
        public static void EnsureMenuEntry()
        {
            try
            {
                if (!File.Exists(IconFile))
                {
                    using var res = typeof(SelfInstall).Assembly.GetManifestResourceStream("rl-map-loader.png");
                    if (res != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
                        using var f = File.Create(IconFile);
                        res.CopyTo(f);
                    }
                }

                var exec = $"\"{InstalledPath}\"";
                var entry =
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=RL Map Loader\n" +
                    "GenericName=Rocket League map loader\n" +
                    "Comment=Load Rocket League workshop maps into Underpass, with an in-game overlay\n" +
                    $"Exec={exec}\n" +
                    $"Icon={IconFile}\n" +
                    "Terminal=false\n" +
                    "Categories=Game;Utility;\n" +
                    "Keywords=rocket;league;workshop;map;underpass;\n" +
                    "StartupWMClass=rl-map-loader\n" +
                    "Actions=ToggleOverlay;Restore;\n\n" +
                    "[Desktop Action ToggleOverlay]\n" +
                    "Name=Show / hide overlay\n" +
                    $"Exec={exec} --toggle-overlay\n\n" +
                    "[Desktop Action Restore]\n" +
                    "Name=Restore original Underpass\n" +
                    $"Exec={exec} --restore\n";
                if (!File.Exists(DesktopFile) || File.ReadAllText(DesktopFile) != entry)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
                    File.WriteAllText(DesktopFile, entry);
                    TryRun("update-desktop-database", Path.GetDirectoryName(DesktopFile)!);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(VersionStamp)!);
                File.WriteAllText(VersionStamp, Updater.CurrentVersionText);
            }
            catch (Exception ex)
            {
                Log.Write("App menu entry: " + ex.Message);
            }
        }

        private static Version? ReadStampedVersion()
        {
            try { return File.Exists(VersionStamp) && Version.TryParse(File.ReadAllText(VersionStamp).Trim(), out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build), 0) : null; }
            catch { return null; }
        }

        /// <summary>
        /// Another RL Map Loader is running (maybe an older version): ask it to quit, or end it if it doesn't understand,
        /// so the new version takes over the hotkey and tray.
        /// </summary>
        private static void StopRunningInstances()
        {
            var others = Others();
            if (others.Count == 0) return;
            SingleInstance.TrySend("quit-for-update");
            if (WaitGone(others, TimeSpan.FromSeconds(6))) return;
            foreach (var pid in others) kill(pid, 15); // SIGTERM (versions before 1.1 don't know "quit-for-update")
            if (WaitGone(others, TimeSpan.FromSeconds(4))) return;
            foreach (var pid in others) kill(pid, 9);
            WaitGone(others, TimeSpan.FromSeconds(2));
        }

        private static List<int> Others()
        {
            var me = Environment.ProcessId;
            var list = new List<int>();
            foreach (var p in Process.GetProcessesByName("rl-map-loader"))
            {
                using (p)
                {
                    if (p.Id != me) list.Add(p.Id); // other users' processes can't be signalled anyway
                }
            }
            return list;
        }

        private static bool WaitGone(List<int> pids, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (pids.All(pid => !Directory.Exists($"/proc/{pid}") || IsZombie(pid))) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static bool IsZombie(int pid)
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                int close = stat.LastIndexOf(')');
                return close > 0 && close + 2 < stat.Length && stat[close + 2] == 'Z';
            }
            catch { return true; }
        }

        private static void TryRun(string program, string arg)
        {
            try
            {
                var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                psi.ArgumentList.Add(arg);
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { /* optional tool */ }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);
    }
}
