using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RLMapLoader.Services
{
    public sealed record MapInfo(string Path, string FileName, string DisplayName, string Extension,
                                 long Size, DateTime Modified, string? ImagePath, bool ImageIsCustom,
                                 string? Author = null, string? Description = null);

    public static class MapLibrary
    {
        public static readonly string[] MapExtensions = { ".upk", ".udk" };
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif" };
        private static readonly string[] PreferredImageWords = { "preview", "thumb", "cover", "banner", "screenshot", "image" };

        public static bool IsMapFile(string path) =>
            MapExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        public static bool IsImageFile(string path) =>
            ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        public static List<MapInfo> Scan(string root, IReadOnlyDictionary<string, string> customImages)
        {
            var result = new List<MapInfo>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;

            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
            var files = Directory.EnumerateFiles(root, "*", opts).Where(IsMapFile).ToList();
            var mapsPerDir = files.GroupBy(f => System.IO.Path.GetDirectoryName(f) ?? "", StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length < 1024) continue;
                    if (!LooksLikeMap(f)) continue;

                    string? img = null;
                    bool custom = false;
                    if (customImages.TryGetValue(f, out var ci) && File.Exists(ci)) { img = ci; custom = true; }
                    int inFolder = mapsPerDir.TryGetValue(fi.DirectoryName ?? "", out var c) ? c : 1;
                    img ??= FindImage(f, inFolder);

                    var baseName = System.IO.Path.GetFileNameWithoutExtension(f);
                    var meta = ReadInfo(f, inFolder, root);
                    result.Add(new MapInfo(f, fi.Name, meta.Title ?? Prettify(baseName), fi.Extension.TrimStart('.').ToUpperInvariant(),
                                           fi.Length, fi.LastWriteTime, img, custom, meta.Author, meta.Description));
                }
                catch (Exception ex)
                {
                    Log.Write($"Scan skipped {f}: {ex.Message}");
                }
            }
            return result;
        }

        private sealed record MapMeta(string? Title, string? Author, string? Description);

        /// <summary>
        /// Reads map details in the common download format: a folder named after the map holding the map file,
        /// preview.jpg and info.json ({"author": ..., "desc": ...}). A "MapName.json" next to the map works too.
        /// </summary>
        private static MapMeta ReadInfo(string mapPath, int mapsInFolder, string root)
        {
            var dir = System.IO.Path.GetDirectoryName(mapPath) ?? "";
            var candidates = new List<(string file, bool folderInfo)>
            {
                (System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(mapPath) + ".json"), false),
            };
            if (mapsInFolder == 1) candidates.Add((System.IO.Path.Combine(dir, "info.json"), true));

            foreach (var (file, folderInfo) in candidates)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file),
                        new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
                    var r = doc.RootElement;
                    string? Str(params string[] keys)
                    {
                        foreach (var k in keys)
                            foreach (var p in r.EnumerateObject())
                                if (p.Name.Equals(k, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                                    && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                                    return p.Value.GetString()!.Trim();
                        return null;
                    }
                    var title = Str("title", "name", "mapname");
                    bool ownFolder = folderInfo && !string.Equals(System.IO.Path.GetFullPath(dir).TrimEnd('\\'),
                                                                   System.IO.Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                    if (title == null && ownFolder) title = System.IO.Path.GetFileName(dir);
                    return new MapMeta(title, Str("author", "creator", "by"), Str("desc", "description", "about"));
                }
                catch (Exception ex)
                {
                    Log.Write($"Bad info file {file}: {ex.Message}");
                }
            }
            return new MapMeta(null, null, null);
        }

        /// <summary>Same-name image first; otherwise any image in a folder that holds only this map.</summary>
        private static string? FindImage(string mapPath, int mapsInFolder)
        {
            var dir = System.IO.Path.GetDirectoryName(mapPath);
            if (dir == null) return null;
            var baseName = System.IO.Path.GetFileNameWithoutExtension(mapPath);
            List<string> images;
            try { images = Directory.EnumerateFiles(dir).Where(IsImageFile).ToList(); }
            catch { return null; }
            if (images.Count == 0) return null;

            var same = images.FirstOrDefault(i =>
                string.Equals(System.IO.Path.GetFileNameWithoutExtension(i), baseName, StringComparison.OrdinalIgnoreCase));
            if (same != null) return same;

            var prefix = images.FirstOrDefault(i =>
                System.IO.Path.GetFileNameWithoutExtension(i).StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
            if (prefix != null) return prefix;

            if (mapsInFolder > 1) return null;

            var preferred = images.FirstOrDefault(i => PreferredImageWords.Any(w =>
                System.IO.Path.GetFileName(i).Contains(w, StringComparison.OrdinalIgnoreCase)));
            return preferred ?? images.OrderByDescending(i => new FileInfo(i).Length).First();
        }

        /// <summary>
        /// Filters out non-map packages (e.g. texture packs) when the package is readable;
        /// anything we can't parse is assumed to be a map so nothing real is ever hidden.
        /// </summary>
        private static bool LooksLikeMap(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt32() != 0x9E2A83C1) return false;
                br.ReadUInt16(); var licensee = br.ReadUInt16();
                if (licensee != 0) return true; // Psyonix-cooked / encrypted: can't inspect
                br.ReadInt32(); // header size
                SkipFString(br);
                br.ReadUInt32(); // package flags
                int nameCount = br.ReadInt32(), nameOffset = br.ReadInt32();
                if (nameCount <= 0 || nameCount > 500000 || nameOffset <= 0 || nameOffset >= fs.Length) return true;
                fs.Position = nameOffset;
                for (int i = 0; i < nameCount; i++)
                {
                    var n = ReadFString(br);
                    if (n == null) return true;
                    if (n.Equals("PersistentLevel", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("TheWorld", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("WorldInfo", StringComparison.OrdinalIgnoreCase)) return true;
                    br.ReadUInt64();
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void SkipFString(BinaryReader br)
        {
            int len = br.ReadInt32();
            br.BaseStream.Position += len >= 0 ? len : -len * 2;
        }

        private static string? ReadFString(BinaryReader br)
        {
            int len = br.ReadInt32();
            if (len == 0) return "";
            if (len > 0 && len < 1024) return Encoding.Latin1.GetString(br.ReadBytes(len)).TrimEnd('\0');
            if (len < 0 && len > -1024) return Encoding.Unicode.GetString(br.ReadBytes(-len * 2)).TrimEnd('\0');
            return null;
        }

        public static string Prettify(string raw)
        {
            var s = raw.Replace('_', ' ').Replace('-', ' ').Trim();
            var sb = new StringBuilder();
            int wordLen = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && c != ' ')
                {
                    char p = s[i - 1];
                    bool split =
                        (char.IsLower(p) && char.IsUpper(c)) ||
                        (char.IsLetter(p) && char.IsDigit(c) && wordLen >= 3) ||
                        (char.IsDigit(p) && char.IsLetter(c) && !(c is 's' or 'S' or 'v' or 'V' or 'x' or 'X') && wordLen >= 1 && i + 1 < s.Length && char.IsLetter(s[i + 1])) ||
                        (char.IsUpper(p) && char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1]));
                    if (split && p != ' ') { sb.Append(' '); wordLen = 0; }
                }
                if (c == ' ') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); wordLen = 0; continue; }
                sb.Append(c);
                wordLen++;
            }
            var result = sb.ToString().Trim();
            return result.Length == 0 ? raw : char.ToUpperInvariant(result[0]) + result[1..];
        }

        /// <summary>Copies maps (and zips containing maps + images) into the custom maps folder. Returns imported map paths.</summary>
        public static List<string> Import(IEnumerable<string> sources, string mapsRoot, Func<string, bool> confirmOverwrite)
        {
            var imported = new List<string>();
            Directory.CreateDirectory(mapsRoot);
            foreach (var src in sources)
            {
                var ext = System.IO.Path.GetExtension(src);
                if (IsMapFile(src))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(src);
                    var dir = System.IO.Path.Combine(mapsRoot, SafeName(name));
                    var dest = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(src));
                    if (string.Equals(System.IO.Path.GetFullPath(src), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(dest) && !confirmOverwrite(System.IO.Path.GetFileName(dest))) continue;
                    Directory.CreateDirectory(dir);
                    File.Copy(src, dest, overwrite: true);
                    // bring along same-named images that sit next to the source
                    var srcDir = System.IO.Path.GetDirectoryName(src);
                    if (srcDir != null)
                    {
                        foreach (var img in Directory.EnumerateFiles(srcDir).Where(IsImageFile)
                                     .Where(i => System.IO.Path.GetFileNameWithoutExtension(i).StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                            File.Copy(img, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(img)), overwrite: true);
                    }
                    imported.Add(dest);
                }
                else if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(src);
                    var entries = zip.Entries.Where(e => e.Length > 0 &&
                        (IsMapFile(e.Name) || IsImageFile(e.Name) || e.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase))).ToList();
                    var mapEntry = entries.FirstOrDefault(e => IsMapFile(e.Name));
                    if (mapEntry == null) continue;
                    // keep the map's own folder name from the zip ("Ice Rings/...") - it's the real title
                    var top = mapEntry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var folderName = top.Length > 1 ? top[^2] : System.IO.Path.GetFileNameWithoutExtension(src);
                    var dir = System.IO.Path.Combine(mapsRoot, SafeName(folderName));
                    Directory.CreateDirectory(dir);
                    foreach (var e in entries)
                    {
                        var dest = System.IO.Path.Combine(dir, SafeName(e.Name));
                        if (IsMapFile(dest) && File.Exists(dest) && !confirmOverwrite(e.Name)) continue;
                        e.ExtractToFile(dest, overwrite: true);
                        if (IsMapFile(dest)) imported.Add(dest);
                    }
                }
            }
            return imported;
        }

        private static string SafeName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }
    }
}
