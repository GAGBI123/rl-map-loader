using System;
using System.IO;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.Platform
{
    /// <summary>Start on login via an XDG autostart entry (~/.config/autostart).</summary>
    public static class Autostart
    {
        private static string FilePath
        {
            get
            {
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(config))
                    config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(config, "autostart", "rl-map-loader.desktop");
            }
        }

        public static void Apply(bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    return;
                }
                var exe = Environment.ProcessPath ?? "rl-map-loader";
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=RL Map Loader\n" +
                    $"Exec=\"{exe}\" --tray\n" +
                    "Icon=rl-map-loader\n" +
                    "X-GNOME-Autostart-enabled=true\n");
            }
            catch (Exception ex)
            {
                Log.Write("Autostart failed: " + ex.Message);
            }
        }
    }
}
