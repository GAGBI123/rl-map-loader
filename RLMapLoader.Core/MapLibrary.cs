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
                    if (!zip.Entries.Any(e => e.Length > 0 && IsMapFile(e.Name))) continue;
                    var top = SingleTopFolder(zip);
                    var dir = System.IO.Path.Combine(mapsRoot, SafeName(top ?? System.IO.Path.GetFileNameWithoutExtension(src)));
                    imported.AddRange(ExtractMapEntries(zip, dir, top != null, confirmOverwrite));
                }
            }
            return imported;
        }

        /// <summary>
        /// Zips in the maps folder count as maps: each zip that holds a .upk/.udk is unpacked next to itself (its own
        /// folder, keeping pictures and info.json) unless its maps are already there. The zip itself is kept.
        /// Returns the maps that were unpacked.
        /// </summary>
        public static List<string> UnpackZips(string root)
        {
            var unpacked = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return unpacked;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6, MatchCasing = MatchCasing.CaseInsensitive,
            };
            var zips = Directory.EnumerateFiles(root, "*.zip", opts).ToList();
            if (zips.Count == 0) return unpacked;

            // maps already on disk (name + size), so a zip that was unpacked before - by us or by hand - is left alone
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(root, "*", opts).Where(IsMapFile))
            {
                try { present.Add(MapKey(System.IO.Path.GetFileName(f), new FileInfo(f).Length)); } catch { }
            }

            foreach (var zipPath in zips)
            {
                try
                {
                    using var zip = ZipFile.OpenRead(zipPath);
                    var maps = zip.Entries.Where(e => e.Length > 0 && IsMapFile(e.Name) && !IsJunk(e.FullName)).ToList();
                    if (maps.Count == 0 || maps.All(e => present.Contains(MapKey(e.Name, e.Length)))) continue;

                    var top = SingleTopFolder(zip);
                    var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(zipPath)!,
                        SafeName(top ?? System.IO.Path.GetFileNameWithoutExtension(zipPath)));
                    var got = ExtractMapEntries(zip, target, top != null, confirmOverwrite: null);
                    unpacked.AddRange(got);
                    foreach (var e in maps) present.Add(MapKey(e.Name, e.Length));
                    Log.Write($"Unpacked {System.IO.Path.GetFileName(zipPath)} ({got.Count} map(s)) into {target}");
                }
                catch (Exception ex)
                {
                    // e.g. still downloading: tried again on the next scan
                    Log.Write($"Couldn't unpack {zipPath}: {ex.Message}");
                }
            }
            return unpacked;
        }

        private static string MapKey(string fileName, long size) => fileName + "|" + size;

        private static bool IsJunk(string entryPath) =>
            entryPath.Replace('\\', '/').StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase);

        /// <summary>The one folder every file in the zip sits in ("Ice Rings/..."), or null.</summary>
        private static string? SingleTopFolder(ZipArchive zip)
        {
            string? top = null;
            foreach (var e in zip.Entries)
            {
                if (e.Name.Length == 0 || IsJunk(e.FullName)) continue; // folder entries, macOS metadata
                var parts = e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return null;
                if (top == null) top = parts[0];
                else if (!top.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) return null;
            }
            return top == null || SafeName(top).Length == 0 ? null : top;
        }

        /// <summary>
        /// Extracts the maps, pictures and .json files of a zip into <paramref name="target"/>, keeping their sub-folders
        /// (minus the single top folder when <paramref name="stripTop"/>). Entries can't escape the target folder.
        /// Existing files are kept, except that with <paramref name="confirmOverwrite"/> pictures/json are replaced and
        /// maps are replaced when it returns true. Returns the map files written.
        /// </summary>
        private static List<string> ExtractMapEntries(ZipArchive zip, string target, bool stripTop, Func<string, bool>? confirmOverwrite)
        {
            var maps = new List<string>();
            var rootFull = System.IO.Path.GetFullPath(target).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            foreach (var e in zip.Entries)
            {
                if (e.Name.Length == 0 || e.Length == 0 || IsJunk(e.FullName)) continue;
                bool isMap = IsMapFile(e.Name);
                if (!isMap && !IsImageFile(e.Name) && !e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (stripTop && parts.Count > 1) parts.RemoveAt(0);
                if (parts.Any(x => x is "." or "..")) continue;
                var safe = parts.Select(SafeName).ToArray();
                if (safe.Any(x => x.Length == 0)) continue;
                var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(target, System.IO.Path.Combine(safe)));
                if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue; // "zip slip"

                if (File.Exists(dest))
                {
                    if (confirmOverwrite == null) continue;
                    if (isMap && !confirmOverwrite(e.Name)) continue;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                var tmp = dest + ".unzipping";
                e.ExtractToFile(tmp, overwrite: true);
                File.Move(tmp, dest, overwrite: true);
                if (isMap) maps.Add(dest);
            }
            return maps;
        }

        private static string SafeName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }
    }
}
