using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLMapLoader.Services
{
    public sealed class AppSettings
    {
        public string? CookedPath { get; set; }
        public string? MapsPath { get; set; }

        /// <summary>WPF Key name, e.g. "F3".</summary>
        public string HotkeyKey { get; set; } = "F3";
        /// <summary>System.Windows.Input.ModifierKeys flags.</summary>
        public int HotkeyModifiers { get; set; }

        public bool RestoreOnExit { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool StartMinimized { get; set; }
        public bool StartWithWindows { get; set; }
        public bool TrayHintShown { get; set; }

        /// <summary>Download new versions in the background and install them when the app is idle.</summary>
        public bool AutoUpdate { get; set; } = true;
        /// <summary>Last version the user was told about (so "update available" isn't repeated every check).</summary>
        public string? NotifiedUpdateVersion { get; set; }

        public string SortMode { get; set; } = "Name";
        public Dictionary<string, string> CustomImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public double? OverlayLeftPx { get; set; }
        public double? OverlayTopPx { get; set; }

        [JsonIgnore]
        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RLMapLoader");

        [JsonIgnore]
        private static string FilePath => Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                    if (s != null)
                    {
                        // re-wrap with case-insensitive comparers (JSON creates default ones)
                        s.CustomImages = new Dictionary<string, string>(s.CustomImages ?? new(), StringComparer.OrdinalIgnoreCase);
                        s.Favorites = new HashSet<string>(s.Favorites ?? new(), StringComparer.OrdinalIgnoreCase);
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Settings load failed: " + ex.Message);
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write("Settings save failed: " + ex.Message);
            }
        }
    }

    public static class Log
    {
        private static readonly object Gate = new();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RLMapLoader");
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, "log.txt");
                    if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024) File.Delete(file);
                    File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                }
            }
            catch { /* logging must never throw */ }
        }
    }
}
