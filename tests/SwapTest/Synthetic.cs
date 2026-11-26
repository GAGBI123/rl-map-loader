using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RLMapLoader.Services;

/// <summary>
/// Swap tests against a fake game folder of generated packages, so they run anywhere (also on Linux CI):
///   SwapTest --synthetic &lt;workDir&gt;
/// The game's maps are package version 868/34; an "old" original is 868/32 (an earlier game build).
/// </summary>
static class Synthetic
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static string FP(string p) => File.Exists(p) ? MapSwapper.Fingerprint(p)! : "(missing)";

    static void Package(string path, ushort licensee, int seed, int size = 300_000)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        BitConverter.GetBytes(0x9E2A83C1u).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)868).CopyTo(bytes, 4);
        BitConverter.GetBytes(licensee).CopyTo(bytes, 6);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static async Task<bool> Throws(Func<Task> action, Func<UserFacingException, bool> expected)
    {
        try { await action(); return false; }
        catch (UserFacingException ex) { return expected(ex); }
    }

    public static async Task<int> Run(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var cooked = Path.Combine(root, "TAGame", "CookedPCConsole");
        var files = Path.Combine(root, "files");
        MapSwapper.SafetyPathOverride = Path.Combine(root, "safety", MapSwapper.TargetName);
        var safety = MapSwapper.SafetyPath;
        var target = Path.Combine(cooked, MapSwapper.TargetName);
        var bak = target + ".bak";

        foreach (var name in new[] { "Park_P", "Stadium_P", "Labs_Basin_P", "ARC_P", "Wasteland_P" })
            Package(Path.Combine(cooked, name + ".upk"), 34, name.GetHashCode());
        string original = Path.Combine(files, "original.upk"), oldOriginal = Path.Combine(files, "original-old.upk");
        string mapA = Path.Combine(files, "a.upk"), mapB = Path.Combine(files, "b.upk"), psyLike = Path.Combine(files, "psyonix-like.upk");
        Package(original, 34, 1);
        Package(oldOriginal, 32, 2, 600_000);
        Package(mapA, 0, 3);
        Package(mapB, 0, 4);
        Package(psyLike, 34, 5); // a custom map whose header looks like Psyonix's
        string fpOrig = FP(original), fpOld = FP(oldOriginal), fpA = FP(mapA), fpB = FP(mapB), fpPsy = FP(psyLike);
        void Reset() { foreach (var f in new[] { target, bak, safety, Path.Combine(Path.GetDirectoryName(safety)!, "loaded-map.txt") }) if (File.Exists(f)) File.Delete(f); }

        Check(MapSwapper.GameVersion(cooked) == (868 << 16 | 34), "game version read from the other maps");

        // 1. normal round trip
        Reset(); File.Copy(original, target);
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Original, "1 fresh install: Original");
        await MapSwapper.LoadAsync(cooked, mapA, null);
        Check(FP(target) == fpA && FP(bak) == fpOrig && FP(safety) == fpOrig, "1 load: map in place, original in .bak and safety copy");
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig && MapSwapper.GetStatus(cooked).State == UnderpassState.Original, "1 restore: original back");

        // 2. a custom map with a Psyonix-like header is never taken for the original
        Reset(); File.Copy(original, target);
        await MapSwapper.LoadAsync(cooked, psyLike, null);
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Custom, "2 Psyonix-like custom map shows as Custom");
        Check(FP(safety) == fpOrig, "2 safety copy not overwritten by it");
        await MapSwapper.LoadAsync(cooked, mapB, null);
        Check(FP(target) == fpB && FP(bak) == fpOrig, "2 loading over it keeps the real original in .bak");
        await MapSwapper.LoadAsync(cooked, psyLike, null);
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig, "2 restore after it puts the real original back");

        // 3. only an old original saved (e.g. a .bak from before a game update): don't put it back without asking
        Reset(); File.Copy(mapA, target); File.Copy(oldOriginal, bak);
        Check(await Throws(() => MapSwapper.RestoreAsync(cooked), e => e.OutdatedBackup), "3 restore refuses an outdated backup");
        Check(FP(target) == fpA && FP(bak) == fpOld, "3 nothing changed");
        await MapSwapper.RestoreAsync(cooked, allowOutdated: true);
        Check(FP(target) == fpOld, "3 'restore anyway' puts it back");
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Outdated, "3 status Outdated");
        Check(await Throws(() => MapSwapper.RestoreAsync(cooked), e => !e.OutdatedBackup && e.Message.Contains("Verify")),
              "3 restore with only the outdated file says to verify the game files");

        // 4. old .bak but a current safety copy: restore uses the current one
        Reset(); File.Copy(original, target); MapSwapper.GetStatus(cooked); // safety = current
        File.Copy(mapA, target, true); File.Copy(oldOriginal, bak);
        MapSwapper.GetStatus(cooked);
        Check(FP(safety) == fpOrig, "4 safety copy not replaced by the older .bak");
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig, "4 restore takes the current safety copy over the old .bak");

        // 5. outdated original in place, current copy saved: status Outdated, restore fixes it
        Reset(); Directory.CreateDirectory(Path.GetDirectoryName(safety)!); File.Copy(original, safety); File.Copy(oldOriginal, target);
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Outdated, "5 old original in place: Outdated");
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig && MapSwapper.GetStatus(cooked).State == UnderpassState.Original, "5 restore puts the current original in");

        // 6. loading with an old .bak and a current safety copy refreshes .bak
        Reset(); Directory.CreateDirectory(Path.GetDirectoryName(safety)!); File.Copy(original, safety); File.Copy(mapA, target); File.Copy(oldOriginal, bak);
        await MapSwapper.LoadAsync(cooked, mapB, null);
        Check(FP(target) == fpB && FP(bak) == fpOrig, "6 load replaces the old .bak with the current original");

        // 7. Underpass file missing: restore brings it back
        Reset(); File.Copy(original, bak);
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Missing, "7 missing file: Missing");
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig, "7 restore when the file was missing");

        // 8. Linux: the game (Proton/Wine) has the file open - refuse like Windows does
        if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/tail"))
        {
            Reset(); File.Copy(original, target);
            await MapSwapper.LoadAsync(cooked, mapA, null);
            var fake = Path.Combine(root, "RocketLeague.exe");
            File.Copy("/usr/bin/tail", fake);
            File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
            var psi = new ProcessStartInfo(fake) { UseShellExecute = false, RedirectStandardOutput = true };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(target);
            using var game = Process.Start(psi)!;
            Thread.Sleep(500);
            Check(await Throws(() => MapSwapper.RestoreAsync(cooked), e => e.Message.Contains("using the Underpass")), "8 restore refused while the game has the file open");
            Check(await Throws(() => MapSwapper.LoadAsync(cooked, mapB, null), e => e.Message.Contains("using the Underpass")), "8 load refused while the game has the file open");
            Check(FP(target) == fpA && FP(bak) == fpOrig, "8 nothing changed");
            game.Kill();
            game.WaitForExit();
            await MapSwapper.RestoreAsync(cooked);
            Check(FP(target) == fpOrig, "8 restore works once the game let go");
        }

        await WithManifest(Path.Combine(root, "epic"), original, mapA);

        // Heroic (Legendary) keeps the manifest in its config folder; installed.json says which install it belongs to
        if (OperatingSystem.IsLinux())
        {
            var game = Path.Combine(root, "heroic-game");
            var hcooked = Path.Combine(game, "TAGame", "CookedPCConsole");
            Package(Path.Combine(hcooked, "Park_P.upk"), 34, 7);
            var cfg = Path.Combine(root, "config");
            var legendary = Path.Combine(cfg, "heroic", "legendaryConfig", "legendary");
            Directory.CreateDirectory(Path.Combine(legendary, "manifests"));
            File.WriteAllText(Path.Combine(legendary, "installed.json"),
                "{\"Sugar\": {\"app_name\": \"Sugar\", \"install_path\": \"" + game + "\"}, \"Other\": {\"install_path\": \"/nowhere\"}}");
            WriteManifest(Path.Combine(legendary, "manifests", "Sugar.manifest"), ("TAGame/CookedPCConsole/Labs_Underpass_P.upk", original));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", cfg);
            var e = GameManifest.FindUnderpass(hcooked);
            Check(e != null && e.Manifest.EndsWith("Sugar.manifest"), "H Heroic manifest found through installed.json");
            Check(GameManifest.FindUnderpass(Path.Combine(root, "TAGame", "CookedPCConsole")) == null, "H not used for another install");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails;
    }

    /// <summary>A minimal binary Epic manifest (uncompressed) listing the given files with their SHA-1 and size.</summary>
    static void WriteManifest(string path, params (string name, string file)[] entries)
    {
        static void FStr(BinaryWriter w, string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); w.Write(b.Length + 1); w.Write(b); w.Write((byte)0); }
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        w.Write(14); w.Write((byte)0); w.Write(18); w.Write((byte)0); w.Write(0);   // meta: size 14, nothing else
        w.Write(9); w.Write((byte)0); w.Write(0);                                   // chunk data list: empty
        w.Write(0); w.Write((byte)0); w.Write(entries.Length);                      // file manifest list
        foreach (var e in entries) FStr(w, e.name);
        foreach (var _ in entries) w.Write(0);                                      // symlink targets
        foreach (var e in entries) { using var s = File.OpenRead(e.file); w.Write(System.Security.Cryptography.SHA1.HashData(s)); }
        foreach (var _ in entries) w.Write((byte)0);                                // flags
        foreach (var _ in entries) w.Write(0);                                      // install tags
        foreach (var e in entries) { w.Write(1); w.Write(28); w.Write(new byte[16]); w.Write(0); w.Write((int)new FileInfo(e.file).Length); }
        w.Flush();
        var data = body.ToArray();
        using var f = new BinaryWriter(File.Create(path));
        f.Write(0x44BEC00Cu); f.Write(41); f.Write(data.Length); f.Write(data.Length); f.Write(new byte[20]); f.Write((byte)0); f.Write(18);
        f.Write(data);
    }

    /// <summary>Epic / Heroic installs: the launcher's checksum decides what the original is.</summary>
    static async Task WithManifest(string game, string original, string mapA)
    {
        var cooked = Path.Combine(game, "TAGame", "CookedPCConsole");
        foreach (var name in new[] { "Park_P", "Stadium_P", "ARC_P" }) Package(Path.Combine(cooked, name + ".upk"), 34, name.Length);
        Directory.CreateDirectory(Path.Combine(game, ".egstore"));
        WriteManifest(Path.Combine(game, ".egstore", "TEST.manifest"),
            ("TAGame/CookedPCConsole/Park_P.upk", Path.Combine(cooked, "Park_P.upk")),
            ("TAGame/CookedPCConsole/Labs_Underpass_P.upk", original));
        var damaged = Path.Combine(game, "damaged.upk");
        Package(damaged, 34, 99, (int)new FileInfo(original).Length); // same header, version and size - wrong content
        MapSwapper.SafetyPathOverride = Path.Combine(game, "safety", MapSwapper.TargetName);
        var safety = MapSwapper.SafetyPath;
        var target = Path.Combine(cooked, MapSwapper.TargetName);
        var bak = target + ".bak";
        string fpOrig = FP(original), fpBad = FP(damaged), fpA = FP(mapA);
        void Reset() { foreach (var f in new[] { target, bak, safety }) if (File.Exists(f)) File.Delete(f); Directory.CreateDirectory(Path.GetDirectoryName(safety)!); }

        var entry = GameManifest.FindUnderpass(cooked);
        Check(entry != null && entry.Size == new FileInfo(original).Length, "M manifest read from .egstore");

        Reset(); File.Copy(original, target);
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Original, "M1 launcher's file in place: Original");
        File.Copy(damaged, target, true);
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Outdated, "M1 damaged copy (right header) is caught");
        Check(FP(safety) == fpOrig, "M1 safety copy kept the right one");
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig, "M1 restore replaces the damaged file with the right one");

        Reset(); File.Copy(mapA, target); File.Copy(damaged, bak); File.Copy(original, safety);
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig, "M2 restore skips a damaged .bak for the right safety copy");

        Reset(); File.Copy(mapA, target); File.Copy(damaged, bak);
        Check(await Throws(() => MapSwapper.RestoreAsync(cooked), e => e.OutdatedBackup), "M3 only a damaged copy saved: restore asks first");
        Check(FP(target) == fpA, "M3 nothing changed");
        Check(!File.Exists(safety), "M3 damaged copy not kept as the safety copy");

        Reset(); File.Copy(damaged, target); File.Copy(original, bak);
        await MapSwapper.LoadAsync(cooked, mapA, null);
        Check(FP(target) == fpA && FP(bak) == fpOrig, "M4 loading over a damaged file keeps the right .bak");
        _ = fpBad;
    }
}
