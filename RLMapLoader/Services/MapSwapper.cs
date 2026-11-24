using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RLMapLoader.Services
{
    public enum UnderpassState { NotConfigured, Original, Custom, Missing }

    public sealed record SwapStatus(UnderpassState State, long CustomSize = 0, string? CustomFingerprint = null);

    /// <summary>An error whose message is meant to be shown to the user as-is.</summary>
    public sealed class UserFacingException : Exception
    {
        public bool NeedsAdmin { get; }
        public UserFacingException(string message, bool needsAdmin = false, Exception? inner = null) : base(message, inner)
            => NeedsAdmin = needsAdmin;
    }

    /// <summary>
    /// Swaps a custom map into Labs_Underpass_P.upk.
    /// The original is kept as Labs_Underpass_P.upk.bak (same convention as other RL map loaders)
    /// plus a safety copy in %LOCALAPPDATA%\RLMapLoader\Backup.
    /// Originals are recognised by their package header: Psyonix packages have a non-zero licensee version,
    /// UDK-built custom maps have 0.
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

        public static string TargetPath(string cooked) => Path.Combine(cooked, TargetName);

        public static SwapStatus GetStatus(string? cooked)
        {
            if (!RlLocator.IsValidCooked(cooked)) return new SwapStatus(UnderpassState.NotConfigured);
            var target = Path.Combine(cooked!, TargetName);
            var bak = target + ".bak";
            TryEnsureSafetyCopy(bak);
            if (!File.Exists(target)) return new SwapStatus(UnderpassState.Missing);
            if (IsOriginalPackage(target))
            {
                TryEnsureSafetyCopy(target);
                return new SwapStatus(UnderpassState.Original);
            }
            return new SwapStatus(UnderpassState.Custom, new FileInfo(target).Length, Fingerprint(target));
        }

        public static bool HasAnyBackup(string cooked)
        {
            var bak = Path.Combine(cooked, TargetName + ".bak");
            return (File.Exists(bak) && IsOriginalPackage(bak)) || (File.Exists(SafetyPath) && IsOriginalPackage(SafetyPath));
        }

        /// <summary>Keep a private copy of the original Underpass so a deleted .bak can never lose it.</summary>
        private static void TryEnsureSafetyCopy(string originalCandidate)
        {
            try
            {
                if (!File.Exists(originalCandidate) || !IsOriginalPackage(originalCandidate)) return;
                var safety = SafetyPath;
                if (File.Exists(safety) && Fingerprint(safety) == Fingerprint(originalCandidate)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(safety)!);
                File.Copy(originalCandidate, safety + ".tmp", overwrite: true);
                File.Move(safety + ".tmp", safety, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write("Safety copy failed: " + ex.Message);
            }
        }

        public static async Task LoadAsync(string cooked, string mapPath, IProgress<double>? progress, CancellationToken ct = default)
        {
            var target = Path.Combine(cooked, TargetName);
            var bak = target + ".bak";
            var tmp = target + ".rlml-tmp";

            if (!File.Exists(mapPath))
                throw new UserFacingException("That map file no longer exists. Try refreshing the list.");
            if (!IsUnrealPackage(mapPath))
                throw new UserFacingException("This file isn't a valid Rocket League map package (.upk / .udk).");

            await Task.Run(() =>
            {
                bool targetIsOriginal = File.Exists(target) && IsOriginalPackage(target);
                if (!targetIsOriginal && !HasAnyBackup(cooked))
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
                    if (File.Exists(target) && IsOriginalPackage(target))
                    {
                        TryEnsureSafetyCopy(target);
                        File.Move(target, bak, overwrite: true);
                    }
                    else if (!(File.Exists(bak) && IsOriginalPackage(bak)))
                    {
                        File.Copy(SafetyPath, bak, overwrite: true);
                    }

                    // 3) swap the custom map in
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

        public static Task RestoreAsync(string cooked) => Task.Run(() =>
        {
            var target = Path.Combine(cooked, TargetName);
            var bak = target + ".bak";
            try
            {
                if (File.Exists(target) && IsOriginalPackage(target)) return;
                if (File.Exists(bak) && IsOriginalPackage(bak))
                {
                    TryEnsureSafetyCopy(bak);
                    File.Move(bak, target, overwrite: true);
                    return;
                }
                if (File.Exists(SafetyPath) && IsOriginalPackage(SafetyPath))
                {
                    File.Copy(SafetyPath, target + ".rlml-tmp", overwrite: true);
                    File.Move(target + ".rlml-tmp", target, overwrite: true);
                    return;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UserFacingException(
                    "Windows blocked writing to the Rocket League folder.\n\nRestart RL Map Loader as administrator and try again.",
                    needsAdmin: true, inner: ex);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new UserFacingException(
                    "Rocket League is using the Underpass map right now.\n\nLeave Free Play (go back to the main menu), then try again.",
                    inner: ex);
            }
            throw new UserFacingException(
                "No backup of the original Underpass map was found.\n\nVerify Rocket League's files in the Epic Games / Steam launcher to restore it.");
        });

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
