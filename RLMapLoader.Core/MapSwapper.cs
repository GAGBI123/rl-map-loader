using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RLMapLoader.Services
{
    /// <summary>
    /// Outdated: an original Underpass from an older Rocket League version is in place (the game can crash opening it).
    /// </summary>
    public enum UnderpassState { NotConfigured, Original, Custom, Missing, Outdated }

    public sealed record SwapStatus(UnderpassState State, long CustomSize = 0, string? CustomFingerprint = null);

    /// <summary>An error whose message is meant to be shown to the user as-is.</summary>
    public sealed class UserFacingException : Exception
    {
        public bool NeedsAdmin { get; }
        /// <summary>The only saved original is from an older Rocket League version; restoring it anyway is possible.</summary>
        public bool OutdatedBackup { get; init; }
        public UserFacingException(string message, bool needsAdmin = false, Exception? inner = null) : base(message, inner)
            => NeedsAdmin = needsAdmin;
    }

    /// <summary>
    /// Swaps a custom map into Labs_Underpass_P.upk.
    /// The original is kept as Labs_Underpass_P.upk.bak (same convention as other RL map loaders)
    /// plus a safety copy in %LOCALAPPDATA%\RLMapLoader\Backup.
    /// Originals are recognised by their package header: Psyonix packages have a non-zero licensee version,
    /// UDK-built custom maps have 0. A map this app loaded is never taken for the original, whatever its header says.
    /// All maps of one game build share a package version (file + licensee version); an original with an older one is
    /// from an earlier build (e.g. a .bak made before a game update) and is never put back without asking.
    /// </summary>
    public static class MapSwapper
    {
        public const string TargetName = "Labs_Underpass_P.upk";
        private const uint PackageTag = 0x9E2A83C1;

        /// <summary>For tests: redirect the private safety copy.</summary>
        public static string? SafetyPathOverride { get; set; }

        public static string SafetyPath => SafetyPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RLMapLoader", "Backup", TargetName);

        public static bool IsUnrealPackage(string path) => ReadHeader(path) is { } h && h.tag == PackageTag;

        public static bool IsOriginalPackage(string path) =>
            ReadHeader(path) is { } h && h.tag == PackageTag && h.licensee != 0;

        private static (uint tag, ushort version, ushort licensee)? ReadHeader(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> b = stackalloc byte[8];
                if (fs.Read(b) < 8) return null;
                return (BitConverter.ToUInt32(b), BitConverter.ToUInt16(b[4..]), BitConverter.ToUInt16(b[6..]));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Size + SHA-256 of the first 64 KB: cheap and unique enough to tell maps apart.</summary>
        public static string? Fingerprint(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[Math.Min(65536, fs.Length)];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return fs.Length + ":" + Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, read)));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Stronger identity for files of equal size: whole file when small, otherwise 32 evenly spread 128 KB samples.
        /// </summary>
        public static string? DeepFingerprint(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[128 * 1024];
                long len = fs.Length;
                if (len <= 16L * 1024 * 1024)
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.AppendData(buf, 0, n);
                }
                else
                {
                    const int samples = 32;
                    for (int i = 0; i < samples; i++)
                    {
                        fs.Position = (len - buf.Length) * i / (samples - 1);
                        int n = fs.Read(buf, 0, buf.Length);
                        sha.AppendData(buf, 0, n);
                    }
                }
                return len + ":" + Convert.ToHexString(sha.GetHashAndReset());
            }
            catch
            {
                return null;
            }
        }

        public static string TargetPath(string cooked) => PathUtil.FindChild(cooked, TargetName) ?? Path.Combine(cooked, TargetName);

        public static string BakPath(string cooked) => PathUtil.FindChild(cooked, TargetName + ".bak") ?? TargetPath(cooked) + ".bak";

        public static SwapStatus GetStatus(string? cooked)
        {
            if (!RlLocator.IsValidCooked(cooked)) return new SwapStatus(UnderpassState.NotConfigured);
            var target = TargetPath(cooked!);
            if (IsOriginal(target))
            {
                TryEnsureSafetyCopy(target);
                return new SwapStatus(IsCurrent(target, GameVersion(cooked!)) ? UnderpassState.Original : UnderpassState.Outdated);
            }
            TryEnsureSafetyCopy(BakPath(cooked!));
            if (!File.Exists(target)) return new SwapStatus(UnderpassState.Missing);
            return new SwapStatus(UnderpassState.Custom, new FileInfo(target).Length, Fingerprint(target));
        }

        public static bool HasAnyBackup(string cooked) => BestBackup(cooked, out _) != null;

        // ------------------------------------------------------------ what counts as the original

        private static string LoadedRecordPath => Path.Combine(Path.GetDirectoryName(SafetyPath)!, "loaded-map.txt");

        /// <summary>The file is the map this app swapped in last (its fingerprint is noted when loading).</summary>
        private static bool IsMapWeLoaded(string path)
        {
            try
            {
                if (!File.Exists(LoadedRecordPath)) return false;
                var noted = File.ReadAllText(LoadedRecordPath).Trim();
                return noted.Length > 0 && noted == Fingerprint(path);
            }
            catch
            {
                return false;
            }
        }

        private static void NoteLoadedMap(string? fingerprint)
        {
            try
            {
                if (fingerprint == null)
                {
                    if (File.Exists(LoadedRecordPath)) File.Delete(LoadedRecordPath);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(LoadedRecordPath)!);
                File.WriteAllText(LoadedRecordPath, fingerprint);
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't note the loaded map: " + ex.Message);
            }
        }

        /// <summary>A Psyonix package that isn't a map this app loaded (some custom maps carry a Psyonix-like header).</summary>
        private static bool IsOriginal(string path) => File.Exists(path) && IsOriginalPackage(path) && !IsMapWeLoaded(path);

        /// <summary>File version and licensee version of a package in one number (0 when unreadable).</summary>
        private static int PackageVersion(string path) => ReadHeader(path) is { } h && h.tag == PackageTag ? (h.version << 16) | h.licensee : 0;

        private static string VersionText(int v) => $"{v >> 16}/{v & 0xFFFF}";

        /// <summary>
        /// The package version of the installed Rocket League build: the most common one among its other maps
        /// (0 when it can't be told).
        /// </summary>
        public static int GameVersion(string cooked)
        {
            try
            {
                var counts = new System.Collections.Generic.Dictionary<int, int>();
                int seen = 0;
                var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
                foreach (var f in Directory.EnumerateFiles(cooked, "*_P.upk", options))
                {
                    if (Path.GetFileName(f).StartsWith("Labs_Underpass", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ReadHeader(f) is not { } h || h.tag != PackageTag || h.licensee == 0) continue;
                    int v = (h.version << 16) | h.licensee;
                    counts[v] = (counts.TryGetValue(v, out var n) ? n : 0) + 1;
                    if (++seen >= 15) break;
                }
                int best = 0, bestCount = 0;
                foreach (var (v, n) in counts)
                    if (n > bestCount) (best, bestCount) = (v, n);
                return best;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsCurrent(string path, int gameVersion) => gameVersion == 0 || PackageVersion(path) >= gameVersion;

        /// <summary>
        /// The saved original to put back: .bak or the safety copy, the newer package version first (.bak on a tie).
        /// <paramref name="outdated"/>: it's from an older game build than the one installed.
        /// </summary>
        private static string? BestBackup(string cooked, out bool outdated)
        {
            outdated = false;
            string? best = null;
            int bestVersion = -1;
            foreach (var candidate in new[] { BakPath(cooked), SafetyPath })
            {
                if (!IsOriginal(candidate)) continue;
                int v = PackageVersion(candidate);
                if (v > bestVersion) (best, bestVersion) = (candidate, v);
            }
            if (best != null) outdated = !IsCurrent(best, GameVersion(cooked));
            return best;
        }

        /// <summary>
        /// Keep a private copy of the original Underpass so a deleted .bak can never lose it. Never replaced by a map
        /// this app loaded, or by an older version of the original.
        /// </summary>
        private static void TryEnsureSafetyCopy(string originalCandidate)
        {
            try
            {
                if (!IsOriginal(originalCandidate)) return;
                var safety = SafetyPath;
                if (File.Exists(safety) && Fingerprint(safety) == Fingerprint(originalCandidate)) return;
                if (IsOriginal(safety) && PackageVersion(safety) > PackageVersion(originalCandidate)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(safety)!);
                File.Copy(originalCandidate, safety + ".tmp", overwrite: true);
                File.Move(safety + ".tmp", safety, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write("Safety copy failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ the game holding the file (Linux)

        /// <summary>
        /// Windows refuses to replace a file the game has open. Linux doesn't, and swapping Underpass under the running
        /// game (Proton / Wine) can crash it, so look at the game's open files instead.
        /// </summary>
        private static void ThrowIfGameHasItOpen(string target)
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(target)) return;
            if (OpenedByGame(target) is not { } who) return;
            Log.Write($"{who} has {target} open");
            throw new UserFacingException(InUseMessage);
        }

        private const string InUseMessage =
            "Rocket League is using the Underpass map right now.\n\nLeave Free Play (go back to the main menu), then try again.";

        private static string? OpenedByGame(string path)
        {
            try
            {
                var wanted = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(path) };
                if (RealPath(path) is { } real) wanted.Add(real);
                foreach (var proc in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(proc), out var pid) || pid == Environment.ProcessId) continue;
                    string comm;
                    try { comm = File.ReadAllText(Path.Combine(proc, "comm")).Trim(); }
                    catch { continue; }
                    // the game under Proton/Wine shows up as "RocketLeague.ex"; wineserver keeps the game's files open too
                    if (!comm.StartsWith("RocketLeague", StringComparison.OrdinalIgnoreCase) && comm != "wineserver") continue;
                    System.Collections.Generic.IEnumerable<string> fds;
                    try { fds = Directory.EnumerateFileSystemEntries(Path.Combine(proc, "fd")).ToArray(); }
                    catch { continue; } // not ours to look at
                    foreach (var fd in fds)
                    {
                        string? link;
                        try { link = new FileInfo(fd).LinkTarget; }
                        catch { continue; }
                        if (link != null && wanted.Contains(link)) return $"{comm} (pid {pid})";
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Checking the game's open files failed: " + ex.Message);
            }
            return null;
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr NativeRealPath(string path, IntPtr resolved);

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "free")]
        private static extern void NativeFree(IntPtr p);

        private static string? RealPath(string path)
        {
            try
            {
                var p = NativeRealPath(path, IntPtr.Zero);
                if (p == IntPtr.Zero) return null;
                try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p); }
                finally { NativeFree(p); }
            }
            catch
            {
                return null;
            }
        }

        public static async Task LoadAsync(string cooked, string mapPath, IProgress<double>? progress, CancellationToken ct = default)
        {
            var target = TargetPath(cooked);
            var bak = BakPath(cooked!);
            var tmp = target + ".rlml-tmp";

            if (!File.Exists(mapPath))
                throw new UserFacingException("That map file no longer exists. Try refreshing the list.");
            if (!IsUnrealPackage(mapPath))
                throw new UserFacingException("This file isn't a valid Rocket League map package (.upk / .udk).");

            await Task.Run(() =>
            {
                ThrowIfGameHasItOpen(target);
                if (!IsOriginal(target) && !HasAnyBackup(cooked))
                    throw new UserFacingException(
                        "Couldn't find the original Underpass map to keep as a backup, so nothing was changed.\n\n" +
                        "Verify Rocket League's files in the Epic Games / Steam launcher to restore it, then try again.");
            }, ct);

            try
            {
                // 1) copy the custom map next to the target first (the slow part) - the game files are untouched meanwhile
                await CopyWithProgressAsync(mapPath, tmp, progress, ct);

                await Task.Run(() =>
                {
                    // 2) keep the original as .bak (replacing a stale .bak after a game update)
                    if (IsOriginal(target))
                    {
                        TryEnsureSafetyCopy(target);
                        File.Move(target, bak, overwrite: true);
                    }
                    else if (BestBackup(cooked, out _) is { } best && !string.Equals(Path.GetFullPath(best), Path.GetFullPath(bak), StringComparison.Ordinal))
                    {
                        // .bak is missing, or older than the safety copy
                        File.Copy(best, bak, overwrite: true);
                    }

                    // 3) swap the custom map in (noted first, so it's never mistaken for the original)
                    NoteLoadedMap(Fingerprint(tmp));
                    try
                    {
                        File.Move(tmp, target, overwrite: true);
                    }
                    catch
                    {
                        if (!File.Exists(target) && File.Exists(bak)) File.Copy(bak, target);
                        throw;
                    }
                }, ct);
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(tmp);
                throw new UserFacingException(
                    "Windows blocked writing to the Rocket League folder.\n\nRestart RL Map Loader as administrator and try again.",
                    needsAdmin: true, inner: ex);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                TryDelete(tmp);
                throw new UserFacingException(
                    "Rocket League is using the Underpass map right now.\n\nLeave Free Play (go back to the main menu), then load the map again.",
                    inner: ex);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>
        /// Puts the original Underpass back. A saved original from an older game build would crash the game, so it's only
        /// used with <paramref name="allowOutdated"/> (otherwise a UserFacingException with OutdatedBackup is thrown).
        /// </summary>
        public static Task RestoreAsync(string cooked, bool allowOutdated = false) => Task.Run(() =>
        {
            var target = TargetPath(cooked);
            var bak = BakPath(cooked!);
            var tmp = target + ".rlml-tmp";
            try
            {
                int game = GameVersion(cooked);
                bool targetIsOriginal = IsOriginal(target);
                if (targetIsOriginal && IsCurrent(target, game)) return;

                var source = BestBackup(cooked, out bool outdated);
                if (source != null && !(targetIsOriginal && PackageVersion(source) <= PackageVersion(target)))
                {
                    if (outdated && !allowOutdated)
                        throw new UserFacingException(OutdatedMessage(source, game)) { OutdatedBackup = true };
                    ThrowIfGameHasItOpen(target);
                    if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(bak), StringComparison.Ordinal))
                    {
                        TryEnsureSafetyCopy(bak);
                        File.Move(bak, target, overwrite: true);
                    }
                    else
                    {
                        File.Copy(source, tmp, overwrite: true);
                        File.Move(tmp, target, overwrite: true);
                    }
                    NoteLoadedMap(null);
                    if (!IsOriginalPackage(target))
                        throw new UserFacingException("Putting the original Underpass back didn't work.\n\nVerify Rocket League's files in the Epic Games / Steam launcher to restore it.");
                    Log.Write($"Restored the original Underpass from {Path.GetFileName(source)} ({new FileInfo(target).Length} bytes, " +
                              $"package {VersionText(PackageVersion(target))}, game {VersionText(game)})");
                    return;
                }
                if (targetIsOriginal)
                {
                    // an older original is in place and nothing newer is saved: only the launcher can fix it
                    throw new UserFacingException(
                        "The Underpass file in the game folder is from an older version of Rocket League, and no newer copy is saved.\n\n" +
                        "Verify Rocket League's files in the Epic Games / Steam / Heroic launcher – that puts the current Underpass back.");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                TryDelete(tmp);
                throw new UserFacingException(
                    "Windows blocked writing to the Rocket League folder.\n\nRestart RL Map Loader as administrator and try again.",
                    needsAdmin: true, inner: ex);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                TryDelete(tmp);
                throw new UserFacingException(InUseMessage, inner: ex);
            }
            throw new UserFacingException(
                "No backup of the original Underpass map was found.\n\nVerify Rocket League's files in the Epic Games / Steam launcher to restore it.");
        });

        private static string OutdatedMessage(string backup, int game)
        {
            Log.Write($"Saved original {backup} is package {VersionText(PackageVersion(backup))}, the game is {VersionText(game)}");
            return "The saved copy of the original Underpass is from an older version of Rocket League. Putting it back would " +
                   "likely crash the game when you open Underpass.\n\n" +
                   "Best fix: verify Rocket League's files in the Epic Games / Steam / Heroic launcher – that puts the current " +
                   "Underpass back (RL Map Loader keeps that one from then on).";
        }

        private static bool IsSharingViolation(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
        }

        private static async Task CopyWithProgressAsync(string src, string dst, IProgress<double>? progress, CancellationToken ct)
        {
            const int bufSize = 4 * 1024 * 1024;
            await using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var d = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, bufSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = Math.Max(1, s.Length), done = 0;
            var buffer = new byte[bufSize];
            int n;
            progress?.Report(0);
            while ((n = await s.ReadAsync(buffer.AsMemory(0, bufSize), ct)) > 0)
            {
                await d.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                progress?.Report((double)done / total);
            }
            await d.FlushAsync(ct);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
