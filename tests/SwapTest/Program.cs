using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RLMapLoader.Services;

class P
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static string FP(string p) => File.Exists(p) ? MapSwapper.Fingerprint(p)! : "(missing)";

    static async Task<int> Main(string[] a)
    {
        if (a.Length == 2 && a[0] == "--synthetic") return await Synthetic.Run(a[1]);
        string root = a[0], original = a[1], mapA = a[2], mapB = a[3], mapsDir = a[4];
        var cooked = Path.Combine(root, "cooked");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(cooked);
        MapSwapper.SafetyPathOverride = Path.Combine(root, "safety", MapSwapper.TargetName);
        var target = Path.Combine(cooked, MapSwapper.TargetName); var bak = target + ".bak";
        string fpOrig = FP(original), fpA = FP(mapA), fpB = FP(mapB);

        // 1. fresh install: original in place
        File.Copy(original, target);
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Original, "1 status Original on fresh install");
        Check(File.Exists(MapSwapper.SafetyPath), "1 safety copy created");
        await MapSwapper.LoadAsync(cooked, mapA, null);
        Check(FP(target) == fpA && FP(bak) == fpOrig, "1 load A: target=A, bak=original");
        Check(MapSwapper.GetStatus(cooked).State == UnderpassState.Custom, "1 status Custom");
        Check(!File.Exists(target + ".rlml-tmp"), "1 no temp file left");

        // 2. load another map over a custom one
        await MapSwapper.LoadAsync(cooked, mapB, null);
        Check(FP(target) == fpB && FP(bak) == fpOrig, "2 load B: target=B, bak still original");

        // 3. restore
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig && !File.Exists(bak), "3 restore: original back, bak consumed");
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig, "3 restore twice is a no-op");

        // 4. state left by another loader: custom in place + .bak original
        File.Copy(mapA, target, true); File.Copy(original, bak, true);
        var st = MapSwapper.GetStatus(cooked);
        Check(st.State == UnderpassState.Custom && st.CustomFingerprint == fpA, "4 detects other loader's custom map");
        await MapSwapper.LoadAsync(cooked, mapB, null);
        Check(FP(target) == fpB && FP(bak) == fpOrig, "4 load over other loader keeps its .bak");

        // 5. game update replaced target with a (new) original while a stale .bak exists
        File.Copy(original, target, true);
        File.WriteAllBytes(bak, File.ReadAllBytes(original)); // stale copy (same content is fine)
        await MapSwapper.LoadAsync(cooked, mapA, null);
        Check(FP(target) == fpA && FP(bak) == fpOrig, "5 after update: new original becomes .bak");

        // 6. .bak deleted by someone: safety copy recreates it
        File.Delete(bak);
        await MapSwapper.LoadAsync(cooked, mapB, null);
        Check(FP(target) == fpB && FP(bak) == fpOrig, "6 missing .bak recovered from safety copy");
        File.Delete(bak);
        await MapSwapper.RestoreAsync(cooked);
        Check(FP(target) == fpOrig, "6 restore from safety copy when .bak is gone");

        // 7. no backup anywhere: refuse and change nothing
        File.Copy(mapA, target, true); if (File.Exists(bak)) File.Delete(bak); File.Delete(MapSwapper.SafetyPath);
        try { await MapSwapper.LoadAsync(cooked, mapB, null); Check(false, "7 should refuse without backup"); }
        catch (UserFacingException) { Check(FP(target) == fpA, "7 refused without backup, target untouched"); }

        // 8. file locked by the game (Windows locks; Linux is covered by --synthetic)
        File.Copy(original, target, true); if (File.Exists(bak)) File.Delete(bak);
        if (OperatingSystem.IsWindows())
        using (var lockH = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await MapSwapper.LoadAsync(cooked, mapA, null); Check(false, "8 should fail while locked"); }
            catch (UserFacingException ex) { Check(ex.Message.Contains("using the Underpass"), "8 friendly 'in use' error"); }
        }
        Check(FP(target) == fpOrig && !File.Exists(target + ".rlml-tmp"), "8 target intact + temp cleaned after lock failure");

        // 9. rejects non-package files
        var junk = Path.Combine(root, "junk.upk"); File.WriteAllText(junk, "not a map");
        try { await MapSwapper.LoadAsync(cooked, junk, null); Check(false, "9 junk accepted"); }
        catch (UserFacingException) { Check(FP(target) == fpOrig, "9 junk rejected"); }

        // 10. library scan of the real maps folder
        var maps = MapLibrary.Scan(mapsDir, new Dictionary<string, string>());
        foreach (var m in maps) Console.WriteLine($"     map: {m.DisplayName,-32} {m.Extension} {m.Size / 1048576.0,8:0.0} MB  img={(m.ImagePath ?? "-")}");
        Check(maps.Count >= 6, "10 scan finds the maps");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails;
    }
}
