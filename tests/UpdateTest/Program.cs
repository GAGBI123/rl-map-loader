using RLMapLoader.Services;

// Self-update test harness.
//   1. publish this single-file as version A into a folder and as version B (> A) into a feed folder
//   2. write latest.json for B (key "test") into the feed folder and serve it over http
//   3. run A with RLMAPLOADER_UPDATE_FEED=http://.../latest.json
// A checks, downloads, verifies, swaps itself for B and starts B, which waits for A to exit and cleans up.
// Every step is appended to result.txt next to the exe.
var dir = Path.GetDirectoryName(Environment.ProcessPath)!;
var result = Path.Combine(dir, "result.txt");
void Note(string s) => File.AppendAllText(result, $"[{Updater.CurrentVersionText} pid {Environment.ProcessId}] {s}{Environment.NewLine}");

Note($"start published={Updater.IsPublishedBuild} args=[{string.Join(' ', args)}]");
if (Updater.UpdatedFrom(args) is { } from)
{
    Updater.WaitForPreviousInstance(args);
    Note($"previous instance gone; updated from {from}");
    Updater.CleanUp(afterUpdate: true);
    var leftovers = Directory.GetFiles(dir, Path.GetFileName(Environment.ProcessPath!) + ".*");
    Note("leftovers after clean-up: " + (leftovers.Length == 0 ? "none" : string.Join(", ", leftovers.Select(Path.GetFileName))));
    return 0;
}

var info = await Updater.CheckAsync(args.FirstOrDefault() ?? "test");
if (info == null) { Note("no update"); return 0; }
Note($"found {info.DisplayVersion} {info.Url} size={info.Size}");
try
{
    var ready = await Updater.DownloadAsync(info, new Progress<double>(_ => { }));
    Note("downloaded " + Path.GetFileName(ready));
    Updater.Install(ready);
    Note("installed; launching");
    Updater.LaunchUpdated(new[] { "--tray" });
    await Task.Delay(1500); // still running while the new one starts: it must wait for us
    Note("exiting");
}
catch (Exception ex)
{
    Note("FAILED: " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}
return 0;
