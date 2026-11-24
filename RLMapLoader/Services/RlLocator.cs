using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RLMapLoader.Services
{
    /// <summary>Finds Rocket League's TAGame\CookedPCConsole folder for Epic and Steam installs.</summary>
    public static class RlLocator
    {
        public static bool IsValidCooked(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            var t = Path.Combine(path, MapSwapper.TargetName);
            return File.Exists(t) || File.Exists(t + ".bak");
        }

        public static string? FindCookedPath()
        {
            foreach (var root in CandidateRoots())
            {
                try
                {
                    var cooked = Path.Combine(root, "TAGame", "CookedPCConsole");
                    if (IsValidCooked(cooked)) return cooked;
                }
                catch { /* ignore malformed paths */ }
            }
            return null;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            var list = new List<string>();

            // Epic Games launcher manifests
            try
            {
                var manifests = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (Directory.Exists(manifests))
                {
                    foreach (var f in Directory.EnumerateFiles(manifests, "*.item"))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(f));
                            var r = doc.RootElement;
                            var name = r.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? "" : "";
                            var app = r.TryGetProperty("AppName", out var an) ? an.GetString() ?? "" : "";
                            if (name.Contains("Rocket League", StringComparison.OrdinalIgnoreCase) || app == "Sugar")
                            {
                                if (r.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } s)
                                    list.Add(s);
                            }
                        }
                        catch { /* skip unreadable manifest */ }
                    }
                }
            }
            catch { }

            // Steam libraries
            try
            {
                var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                var libs = new List<string>();
                if (!string.IsNullOrEmpty(steam))
                {
                    libs.Add(steam);
                    var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                    {
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                    }
                }
                list.AddRange(libs.Select(l => Path.Combine(l, "steamapps", "common", "rocketleague")));
            }
            catch { }

            // Common defaults
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName))
            {
                list.Add(Path.Combine(drive, "Program Files", "Epic Games", "rocketleague"));
                list.Add(Path.Combine(drive, "Epic Games", "rocketleague"));
                list.Add(Path.Combine(drive, "Games", "rocketleague"));
                list.Add(Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common", "rocketleague"));
                list.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "rocketleague"));
            }

            return list.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
