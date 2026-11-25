using System;
using System.Diagnostics;
using System.IO;

namespace RLMapLoader.Services
{
    /// <summary>Facts about the Rocket League window that decide how the overlay can be shown.</summary>
    internal static class GameWindow
    {
        public static bool IsRocketLeague(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return false;
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName.Equals("RocketLeague", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True when the game runs in exclusive Fullscreen. Windows can't draw any other window over that, so the
        /// overlay has to minimize the game (like Alt+Tab). Borderless and Windowed don't have this problem.
        /// </summary>
        public static bool IsExclusiveFullscreen()
        {
            try
            {
                if (Native.SHQueryUserNotificationState(out int state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN)
                    return true;
            }
            catch { /* not available */ }
            return SettingsSayFullscreen() == true;
        }

        /// <summary>Rocket League's saved Display Mode: [SystemSettings] Fullscreen=True with Borderless=False.</summary>
        public static bool? SettingsSayFullscreen()
        {
            try
            {
                var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "My Games", "Rocket League", "TAGame", "Config", "TASystemSettings.ini");
                if (!File.Exists(ini)) return null;
                bool inSection = false;
                bool? fullscreen = null, borderless = null;
                foreach (var raw in File.ReadLines(ini))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('['))
                    {
                        if (inSection) break;
                        inSection = line.Equals("[SystemSettings]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line[..eq].Trim();
                    var value = line[(eq + 1)..].Trim();
                    bool on = value.Equals("True", StringComparison.OrdinalIgnoreCase);
                    if (key.Equals("Fullscreen", StringComparison.OrdinalIgnoreCase)) fullscreen = on;
                    else if (key.Equals("Borderless", StringComparison.OrdinalIgnoreCase)) borderless = on;
                }
                if (fullscreen == null) return null;
                return fullscreen == true && borderless != true;
            }
            catch
            {
                return null;
            }
        }
    }
}
