using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RLMapLoader.Services
{
    /// <summary>
    /// Reads what the launcher installed: the SHA-1 and size of Labs_Underpass_P.upk from the game's Epic manifest
    /// (Epic Games Launcher: &lt;game&gt;/.egstore; Legendary / Heroic: their manifests folder). That identifies the
    /// genuine current Underpass exactly, which a package header can't (an older or damaged copy has the same header).
    /// Steam keeps no readable checksums; there the loader falls back to package versions.
    /// </summary>
    public static class GameManifest
    {
        public sealed record FileEntry(string Name, string Sha1, long Size, string Manifest);

        private const uint Magic = 0x44BEC00C;
        private const string Underpass = "Labs_Underpass_P.upk";

        private static readonly object Gate = new();
        private static string? _cacheKey;
        private static FileEntry? _cached;

        /// <summary>The launcher's record of Labs_Underpass_P.upk for the install that owns <paramref name="cooked"/>, or null.</summary>
        public static FileEntry? FindUnderpass(string cooked)
        {
            var manifests = Candidates(cooked).Where(File.Exists).Distinct().ToList();
            var key = string.Join("|", manifests.Select(m => m + "@" + File.GetLastWriteTimeUtc(m).Ticks));
            lock (Gate)
            {
                if (key == _cacheKey) return _cached;
            }
            FileEntry? found = null;
            foreach (var m in manifests)
            {
                try
                {
                    found = ReadEntry(m, n => n.EndsWith("CookedPCConsole/" + Underpass, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    Log.Write($"Reading {m} failed: {ex.Message}");
                }
                if (found != null) break;
            }
            lock (Gate)
            {
                _cacheKey = key;
                _cached = found;
            }
            return found;
        }

        /// <summary>Manifests to look in, most likely first.</summary>
        private static IEnumerable<string> Candidates(string cooked)
        {
            // <game>/TAGame/CookedPCConsole -> <game>
            var game = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(cooked).TrimEnd('/', '\\')));
            if (game != null)
            {
                var egstore = Path.Combine(game, ".egstore");
                if (Directory.Exists(egstore))
                    foreach (var f in new DirectoryInfo(egstore).GetFiles("*.manifest").OrderByDescending(f => f.LastWriteTimeUtc))
                        yield return f.FullName;
            }
            if (OperatingSystem.IsWindows()) yield break;

            if (game == null) yield break;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
            foreach (var dir in new[]
                     {
                         Path.Combine(config, "heroic", "legendaryConfig", "legendary"),
                         Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "legendaryConfig", "legendary"),
                         Path.Combine(config, "legendary"),
                     })
            {
                // Legendary's installed.json says which app lives where; only that app's manifest describes this folder
                if (LegendaryAppAt(dir, game) is { } app) yield return Path.Combine(dir, "manifests", app + ".manifest");
            }
        }

        private static string? LegendaryAppAt(string legendaryDir, string game)
        {
            try
            {
                var installed = Path.Combine(legendaryDir, "installed.json");
                if (!File.Exists(installed)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(installed));
                foreach (var app in doc.RootElement.EnumerateObject())
                {
                    if (app.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
                        !app.Value.TryGetProperty("install_path", out var p) || p.GetString() is not { Length: > 0 } path) continue;
                    if (SameDir(path, game)) return app.Name;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Reading Legendary's installed.json failed: " + ex.Message);
            }
            return null;
        }

        private static bool SameDir(string a, string b)
        {
            static string Norm(string p)
            {
                try
                {
                    var full = Path.GetFullPath(p).TrimEnd('/', '\\');
                    var info = new DirectoryInfo(full);
                    return info.LinkTarget != null ? Path.GetFullPath(info.ResolveLinkTarget(true)!.FullName).TrimEnd('/', '\\') : full;
                }
                catch
                {
                    return p;
                }
            }
            return string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>The first file in a binary Epic manifest whose name matches, or null.</summary>
        public static FileEntry? ReadEntry(string manifestPath, Func<string, bool> match)
        {
            var raw = File.ReadAllBytes(manifestPath);
            if (raw.Length < 41 || BitConverter.ToUInt32(raw, 0) != Magic) return null; // JSON or unknown format
            int headerSize = BitConverter.ToInt32(raw, 4);
            int sizeUncompressed = BitConverter.ToInt32(raw, 8);
            byte storedAs = raw[36];
            byte[] data;
            if ((storedAs & 1) != 0)
            {
                using var z = new ZLibStream(new MemoryStream(raw, headerSize, raw.Length - headerSize), CompressionMode.Decompress);
                using var ms = new MemoryStream(Math.Max(0, sizeUncompressed));
                z.CopyTo(ms);
                data = ms.ToArray();
            }
            else
            {
                data = raw[headerSize..];
            }

            var r = new BinaryReader(new MemoryStream(data), Encoding.ASCII);
            // meta (starts at 0)
            int metaSize = r.ReadInt32();
            r.BaseStream.Position = metaSize;
            // chunk data list
            long cdlStart = r.BaseStream.Position;
            int cdlSize = r.ReadInt32();
            r.BaseStream.Position = cdlStart + cdlSize;
            // file manifest list
            r.ReadInt32(); // size
            r.ReadByte();  // version
            int count = r.ReadInt32();
            if (count < 0 || count > 2_000_000) return null;
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = ReadFString(r);
            for (int i = 0; i < count; i++) ReadFString(r); // symlink targets
            var hashes = new byte[count][];
            for (int i = 0; i < count; i++) hashes[i] = r.ReadBytes(20);
            r.BaseStream.Position += count; // flags
            for (int i = 0; i < count; i++)
            {
                int tags = r.ReadInt32();
                for (int t = 0; t < tags; t++) ReadFString(r);
            }
            int index = Array.FindIndex(names, n => match(n.Replace('\\', '/')));
            if (index < 0) return null;
            long size = 0;
            for (int i = 0; i <= index; i++)
            {
                int parts = r.ReadInt32();
                for (int p = 0; p < parts; p++)
                {
                    long start = r.BaseStream.Position;
                    int recordSize = r.ReadInt32();
                    r.BaseStream.Position += 16 + 4; // chunk guid, offset in chunk
                    int partSize = r.ReadInt32();
                    if (i == index) size += (uint)partSize;
                    r.BaseStream.Position = start + recordSize;
                }
            }
            return new FileEntry(names[index], Convert.ToHexString(hashes[index]).ToLowerInvariant(), size, manifestPath);
        }

        private static string ReadFString(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length == 0) return "";
            if (length < 0)
            {
                var bytes = r.ReadBytes(-length * 2);
                return Encoding.Unicode.GetString(bytes, 0, Math.Max(0, bytes.Length - 2));
            }
            var b = r.ReadBytes(length);
            return Encoding.UTF8.GetString(b, 0, Math.Max(0, b.Length - 1));
        }
    }
}
