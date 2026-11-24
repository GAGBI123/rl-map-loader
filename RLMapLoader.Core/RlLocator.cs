using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RLMapLoader.Services
{
    /// <summary>
    /// Finds Rocket League's TAGame/CookedPCConsole folder.
    /// Windows: Epic Games Launcher manifests, Steam libraries, common folders.
    /// Linux: Heroic / Legendary (Epic), Steam + Proton (native and Flatpak), Lutris and Bottles prefixes.
    /// </summary>
    public static class RlLocator
    {
        public static bool IsValidCooked(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            var t = PathUtil.FindChild(path, MapSwapper.TargetName);
            return t != null || PathUtil.FindChild(path, MapSwapper.TargetName + ".bak") != null;
        }

        public static string? FindCookedPath()
        {
            foreach (var root in CandidateRoots())
            {
                try
                {
                    var cooked = PathUtil.Resolve(root, "TAGame", "CookedPCConsole");
                    if (cooked != null && IsValidCooked(cooked)) return cooked;
                }
                catch { /* ignore malformed paths */ }
            }
            return null;
        }

        private static IEnumerable<string> CandidateRoots()
        {
            var list = new List<string>();
            if (OperatingSystem.IsWindows()) AddWindows(list);
            else AddLinux(list);
            return list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(PathUtil.Comparer);
        }

        // ------------------------------------------------------------------ Windows
        private static void AddWindows(List<string> list)
        {
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
                            if ((name.Contains("Rocket League", StringComparison.OrdinalIgnoreCase) || app == "Sugar") &&
                                r.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } s)
                                list.Add(s);
                        }
                        catch { /* skip unreadable manifest */ }
                    }
                }
            }
            catch { }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                                ?? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                    if (!string.IsNullOrEmpty(steam)) AddSteamLibraries(list, steam);
                }
            }
            catch { }

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName))
            {
                list.Add(Path.Combine(drive, "Program Files", "Epic Games", "rocketleague"));
                list.Add(Path.Combine(drive, "Epic Games", "rocketleague"));
                list.Add(Path.Combine(drive, "Games", "rocketleague"));
                list.Add(Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common", "rocketleague"));
                list.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "rocketleague"));
            }
        }

        // ------------------------------------------------------------------ Linux
        private static void AddLinux(List<string> list)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");

            // Epic via Heroic Games Launcher (native + Flatpak) or the legendary CLI
            foreach (var installed in new[]
                     {
                         Path.Combine(config, "heroic", "legendaryConfig", "legendary", "installed.json"),
                         Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "legendaryConfig", "legendary", "installed.json"),
                         Path.Combine(config, "legendary", "installed.json"),
                     })
            {
                try
                {
                    if (!File.Exists(installed)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(installed));
                    foreach (var app in doc.RootElement.EnumerateObject())
                    {
                        var v = app.Value;
                        if (v.ValueKind != JsonValueKind.Object) continue;
                        var title = v.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        if ((app.Name == "Sugar" || title.Contains("Rocket League", StringComparison.OrdinalIgnoreCase)) &&
                            v.TryGetProperty("install_path", out var ip) && ip.GetString() is { Length: > 0 } p)
                            list.Add(p);
                    }
                }
                catch { }
            }
            list.Add(Path.Combine(home, "Games", "Heroic", "rocketleague"));
            list.Add(Path.Combine(home, "Games", "Heroic", "RocketLeague"));

            // Steam (Proton)
            foreach (var steam in new[]
                     {
                         Path.Combine(home, ".steam", "steam"),
                         Path.Combine(home, ".local", "share", "Steam"),
                         Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                     })
            {
                try { if (Directory.Exists(steam)) AddSteamLibraries(list, steam); } catch { }
            }

            // Wine prefixes: Lutris, plain wine, Bottles
            foreach (var prefix in WinePrefixes(home))
                list.Add(Path.Combine(prefix, "drive_c", "Program Files", "Epic Games", "rocketleague"));
        }

        private static IEnumerable<string> WinePrefixes(string home)
        {
            var prefixes = new List<string>
            {
                Path.Combine(home, ".wine"),
                Path.Combine(home, "Games", "epic-games-store"),
                Path.Combine(home, "Games", "epic-games-launcher"),
                Path.Combine(home, "Games", "rocket-league"),
            };
            foreach (var bottles in new[]
                     {
                         Path.Combine(home, ".local", "share", "bottles", "bottles"),
                         Path.Combine(home, ".var", "app", "com.usebottles.bottles", "data", "bottles", "bottles"),
                     })
            {
                try { if (Directory.Exists(bottles)) prefixes.AddRange(Directory.EnumerateDirectories(bottles)); } catch { }
            }
            return prefixes;
        }

        private static void AddSteamLibraries(List<string> list, string steamRoot)
        {
            var libs = new List<string> { steamRoot };
            var vdf = PathUtil.Resolve(steamRoot, "steamapps", "libraryfolders.vdf");
            if (vdf != null && File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            list.AddRange(libs.Select(l => Path.Combine(l, "steamapps", "common", "rocketleague")));
        }
    }

    /// <summary>File-name helpers that also work on case-sensitive Linux file systems.</summary>
    public static class PathUtil
    {
        public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Existing child (file or folder) of <paramref name="dir"/> named <paramref name="name"/>, ignoring case.</summary>
        public static string? FindChild(string dir, string name)
        {
            var exact = Path.Combine(dir, name);
            if (File.Exists(exact) || Directory.Exists(exact)) return exact;
            if (OperatingSystem.IsWindows()) return null;
            try
            {
                return Directory.EnumerateFileSystemEntries(dir)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        /// <summary>Walks <paramref name="parts"/> below <paramref name="root"/> ignoring case; null when any part is missing.</summary>
        public static string? Resolve(string root, params string[] parts)
        {
            if (!Directory.Exists(root)) return null;
            var cur = root;
            foreach (var p in parts)
            {
                var next = FindChild(cur, p);
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }
    }
}
