using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RLMapLoader.Services
{
    /// <summary>One downloadable update, read from latest.json on the public releases repo.</summary>
    public sealed class UpdateInfo
    {
        public Version Version { get; init; } = new(0, 0);
        public string Tag { get; init; } = "";
        public string Notes { get; init; } = "";
        public string FileName { get; init; } = "";
        public Uri Url { get; init; } = null!;
        public long Size { get; init; }
        public string Sha256 { get; init; } = "";
        public string DisplayVersion => Updater.Format(Version);
    }

    /// <summary>
    /// Self-update through GitHub Releases of the public repo, so anyone with the app can update without a GitHub
    /// account. (Versions 1.1.0-1.1.1 read GAGBI123/rl-map-loader-releases; its last release, 1.1.2, moved them here.)
    /// latest.json:  { "version": "1.2.0", "tag": "v1.2.0", "notes": "...",
    ///                 "assets": { "windows": { "file": "...", "size": 1, "sha256": "..." }, "windows-net9": {...}, "linux-x64": {...} } }
    /// </summary>
    public static class Updater
    {
        public const string ReleasesRepo = "GAGBI123/rl-map-loader";
        public static string ReleasesPage => $"https://github.com/{ReleasesRepo}/releases/latest";

        /// <summary>For testing: point the app at another latest.json (http/https URL).</summary>
        public static string? FeedOverride => Environment.GetEnvironmentVariable("RLMAPLOADER_UPDATE_FEED") is { Length: > 0 } f ? f : null;

        private static Uri FeedUri => new(FeedOverride ?? $"https://github.com/{ReleasesRepo}/releases/latest/download/latest.json");

        public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));
        public static string CurrentVersionText => Format(CurrentVersion);

        public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

        private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        /// <summary>A published single-file app. Builds run from the IDE never replace themselves.</summary>
#pragma warning disable IL3000 // an empty Location is exactly what identifies a single-file app here
        public static bool IsPublishedBuild => string.IsNullOrEmpty(typeof(Updater).Assembly.Location);

        /// <summary>Self-contained (bundles .NET) as opposed to needing the installed .NET runtime.</summary>
        public static bool IsSelfContained => string.IsNullOrEmpty(typeof(object).Assembly.Location);
#pragma warning restore IL3000

        public static string? ExePath => Environment.ProcessPath;

        public static bool ExeFolderWritable()
        {
            try
            {
                var dir = Path.GetDirectoryName(ExePath);
                if (string.IsNullOrEmpty(dir)) return false;
                var probe = Path.Combine(dir, $".rlml-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
            })
            { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"RLMapLoader/{CurrentVersionText}");
            return http;
        }

        /// <summary>The newer release for <paramref name="assetKey"/>, or null when this version is the latest.</summary>
        public static async Task<UpdateInfo?> CheckAsync(string assetKey, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var feed = FeedUri;
            using var resp = await Http.GetAsync(feed, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null; // nothing published yet
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = Normalize(Version.Parse(root.GetProperty("version").GetString()!.Trim().TrimStart('v', 'V')));
            if (version <= CurrentVersion) return null;
            if (!root.TryGetProperty("assets", out var assets) || !assets.TryGetProperty(assetKey, out var asset))
            {
                Log.Write($"Update {Format(version)} has no '{assetKey}' download");
                return null;
            }

            var file = asset.GetProperty("file").GetString()!;
            var tag = root.TryGetProperty("tag", out var t) && t.GetString() is { Length: > 0 } tg ? tg : "v" + Format(version);
            Uri url;
            if (asset.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } explicitUrl)
                url = new Uri(feed, explicitUrl);
            else if (feed.AbsoluteUri.Contains("/releases/latest/download/", StringComparison.Ordinal))
                // pin the exact release, so a release published meanwhile can't mismatch the checksum
                url = new Uri(feed.AbsoluteUri[..feed.AbsoluteUri.IndexOf("/releases/latest/download/", StringComparison.Ordinal)]
                              + $"/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(file)}");
            else
                url = new Uri(feed, Uri.EscapeDataString(file));

            return new UpdateInfo
            {
                Version = version,
                Tag = tag,
                Notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "",
                FileName = file,
                Url = url,
                Size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0,
                Sha256 = asset.TryGetProperty("sha256", out var h) ? (h.GetString() ?? "").Trim().ToLowerInvariant() : "",
            };
        }

        /// <summary>Downloads and verifies the update next to the running app. Returns the ready-to-install file.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
        {
            var exe = ExePath ?? throw new InvalidOperationException("Unknown app location");
            var partial = exe + ".download";
            var ready = exe + ".update-" + info.DisplayVersion;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(15));
            using (var resp = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? info.Size;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                await using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                        hash.AppendData(buf, 0, n);
                        done += n;
                        if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                    }
                    if (info.Size > 0 && done != info.Size)
                        throw new InvalidDataException($"Download was incomplete ({done} of {info.Size} bytes)");
                }
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (info.Sha256.Length > 0 && actual != info.Sha256)
                {
                    TryDelete(partial);
                    throw new InvalidDataException("The download didn't match its checksum");
                }
            }

            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(partial, ExecutableMode);
            File.Move(partial, ready, overwrite: true);
            return ready;
        }

        private const UnixFileMode ExecutableMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        /// <summary>
        /// Swaps the downloaded file in for the running app. Windows can't overwrite a running exe but can rename it, so the
        /// old one becomes "*.old" (deleted at the next start). On Linux a rename replaces the file while the running
        /// process keeps its own copy.
        /// </summary>
        public static void Install(string readyFile)
        {
            var exe = ExePath ?? throw new InvalidOperationException("Unknown app location");
            if (OperatingSystem.IsWindows())
            {
                var old = exe + ".old";
                if (File.Exists(old) && !TryDelete(old)) old = exe + $".old-{DateTime.Now.Ticks}";
                File.Move(exe, old);
                try { File.Move(readyFile, exe); }
                catch
                {
                    File.Move(old, exe);
                    throw;
                }
            }
            else
            {
                File.SetUnixFileMode(readyFile, ExecutableMode);
                File.Move(readyFile, exe, overwrite: true);
            }
        }

        /// <summary>Starts the (updated) app; it waits for this process to exit before taking over.</summary>
        public static void LaunchUpdated(IEnumerable<string> extraArgs)
        {
            var psi = new ProcessStartInfo(ExePath!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath!)! };
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--updated-from");
            psi.ArgumentList.Add(CurrentVersionText);
            foreach (var a in extraArgs) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }

        /// <summary>With "--wait-pid N": waits (up to 20 s) for the previous app process to exit.</summary>
        public static void WaitForPreviousInstance(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
            var until = DateTime.UtcNow.AddSeconds(20);
            try
            {
                using var p = Process.GetProcessById(pid);
                if (OperatingSystem.IsWindows())
                {
                    p.WaitForExit(20_000);
                    return;
                }
                while (DateTime.UtcNow < until && !p.HasExited) Thread.Sleep(100);
            }
            catch { /* already gone */ }
        }

        /// <summary>The version this process was updated from ("--updated-from 1.0.0"), if any.</summary>
        public static string? UpdatedFrom(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--updated-from", StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Removes leftovers of earlier updates: the old exe, unfinished downloads, old extracted runtimes.</summary>
        public static void CleanUp(bool afterUpdate)
        {
            try
            {
                var exe = ExePath;
                var dir = Path.GetDirectoryName(exe);
                if (exe == null || dir == null) return;
                var name = Path.GetFileName(exe);
                foreach (var f in Directory.EnumerateFiles(dir, name + ".*"))
                {
                    var suffix = Path.GetFileName(f)[name.Length..];
                    if (suffix == ".old" || suffix.StartsWith(".old-") || suffix == ".download" || suffix.StartsWith(".update-"))
                        TryDelete(f);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Update clean-up: " + ex.Message);
            }
            if (afterUpdate) CleanUpExtractedRuntimes();
        }

        /// <summary>
        /// Self-contained single-file builds unpack their native libraries to ".net/&lt;app&gt;/&lt;hash&gt;" (in the home
        /// folder on Linux, %TEMP% on Windows). Every version gets a new folder, so drop the ones of older versions.
        /// </summary>
        private static void CleanUpExtractedRuntimes()
        {
            try
            {
                var sep = Path.DirectorySeparatorChar;
                var marker = $"{sep}.net{sep}";
                string? current = null;
                using (var self = Process.GetCurrentProcess())
                {
                    foreach (ProcessModule m in self.Modules)
                    {
                        var path = m.FileName;
                        if (string.IsNullOrEmpty(path)) continue;
                        int k = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                        if (k < 0) continue;
                        var parts = path[(k + marker.Length)..].Split(sep);
                        if (parts.Length < 3) continue;
                        current = Path.Combine(path[..(k + marker.Length)], parts[0], parts[1]);
                        break;
                    }
                }
                if (current == null) return;
                var appDir = Path.GetDirectoryName(current)!;
                var appName = Path.GetFileNameWithoutExtension(ExePath ?? "");
                if (!Path.GetFileName(appDir).Equals(appName, StringComparison.OrdinalIgnoreCase)) return;
                foreach (var d in Directory.GetDirectories(appDir))
                {
                    if (string.Equals(Path.GetFullPath(d), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        Directory.Delete(d, recursive: true);
                        Log.Write("Removed old runtime files: " + d);
                    }
                    catch { /* in use or gone */ }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Runtime clean-up: " + ex.Message);
            }
        }

        public static bool IsGameRunning()
        {
            try
            {
                // Windows: RocketLeague.exe; Linux (Proton/Wine): the process name is cut to "RocketLeague.ex"
                return Process.GetProcesses().Any(p =>
                {
                    using (p)
                    {
                        try { return p.ProcessName.StartsWith("RocketLeague", StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }
                });
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public enum UpdateState { Off, Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

    /// <summary>
    /// Update policy shared by the Windows and Linux apps. Checks shortly after start and every few hours. With automatic
    /// updates on, a new version is downloaded in the background and installed (with a quick restart) once the app sits
    /// in the tray and Rocket League isn't running - or when the app quits.
    /// All members are used on the UI thread.
    /// </summary>
    public sealed class UpdateController : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly AppSettings _settings;
        private readonly string _assetKey;
        private readonly Action<Action> _post;
        private Timer? _checkTimer, _idleTimer;
        private string? _readyFile;
        private bool _restartWhenReady;
        private bool _readyNotified;

        public UpdateController(AppSettings settings, string assetKey, Action<Action> postToUi)
        {
            _settings = settings;
            _assetKey = assetKey;
            _post = postToUi;
            Enabled = Updater.IsPublishedBuild || Updater.FeedOverride != null;
            CanSelfUpdate = Enabled && Updater.ExeFolderWritable();
            State = Enabled ? UpdateState.Idle : UpdateState.Off;
        }

        // ------------------------------------------------------------ app hooks
        /// <summary>True when a restart wouldn't bother the user (window closed to tray, game not running, not busy).</summary>
        public Func<bool> IsIdle { get; set; } = () => false;
        /// <summary>Extra arguments for the restarted app (e.g. "--tray").</summary>
        public Func<IEnumerable<string>> RestartArgs { get; set; } = Array.Empty<string>;
        /// <summary>Quit right away for the restart: no "restore Underpass on exit", no questions.</summary>
        public Action? ExitForUpdate { get; set; }
        public Action<string>? OpenUrl { get; set; }

        /// <summary>State changed (bind StatusText / ButtonText etc. again).</summary>
        public event Action? Changed;
        /// <summary>(message, important) - important ones may show even when the app is in the tray.</summary>
        public event Action<string, bool>? Notify;

        public bool Enabled { get; }
        /// <summary>The app file can be replaced (otherwise updates open the download page).</summary>
        public bool CanSelfUpdate { get; }
        public UpdateState State { get; private set; }
        public UpdateInfo? Update { get; private set; }
        public double Progress { get; private set; }
        public string? Error { get; private set; }
        public DateTime? LastChecked { get; private set; }

        public string CurrentVersion => Updater.CurrentVersionText;

        public bool ShowButton => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready
                                  || (State == UpdateState.Failed && Update != null);

        public string ButtonText => State switch
        {
            UpdateState.Downloading => $"Updating… {Progress:P0}",
            UpdateState.Ready => "Restart to update",
            _ when !CanSelfUpdate => $"Get version {Update?.DisplayVersion}",
            _ => $"Update to {Update?.DisplayVersion}",
        };

        public string StatusText => State switch
        {
            UpdateState.Off => "Updates are off in development builds.",
            UpdateState.Idle when !_settings.CheckForUpdates => "Automatic update checks are off. The app only goes online when you click Check for updates.",
            UpdateState.Idle => "Updates are checked automatically.",
            UpdateState.Checking => "Checking for updates…",
            UpdateState.UpToDate => $"You're up to date{CheckedSuffix}.",
            UpdateState.Available when !CanSelfUpdate => $"Version {Update?.DisplayVersion} is available. This copy can't replace itself (its folder is read-only), so the button opens the download page.",
            UpdateState.Available => $"Version {Update?.DisplayVersion} is available.",
            UpdateState.Downloading => $"Downloading version {Update?.DisplayVersion}… {Progress:P0}",
            UpdateState.Ready when _settings.AutoUpdate => $"Version {Update?.DisplayVersion} is downloaded. It installs when RL Map Loader is in the tray and Rocket League is closed, or when you quit.",
            UpdateState.Ready => $"Version {Update?.DisplayVersion} is downloaded and installs when you click Restart to update.",
            UpdateState.Failed => Error ?? "Updating failed.",
            _ => "",
        };

        public string Notes => Update?.Notes ?? "";
        public bool HasNotes => Update != null && !string.IsNullOrWhiteSpace(Update.Notes);

        private string CheckedSuffix => LastChecked is { } t ? $" (checked {t:HH:mm})" : "";

        public void Start()
        {
            if (!Enabled)
            {
                RaiseChanged();
                return;
            }
            _checkTimer = new Timer(_ => _post(() =>
            {
                if (_settings.CheckForUpdates) _ = CheckAsync(manual: false);
            }), null, TimeSpan.FromSeconds(8), TimeSpan.FromHours(3));
            _idleTimer = new Timer(_ => _post(TryInstallWhenIdle), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public void Stop()
        {
            _checkTimer?.Dispose();
            _idleTimer?.Dispose();
        }

        public async Task CheckAsync(bool manual)
        {
            if (!Enabled || State is UpdateState.Checking or UpdateState.Downloading) return;
            if (State == UpdateState.Ready)
            {
                if (manual) Notify?.Invoke($"Version {Update?.DisplayVersion} is ready to install", false);
                return;
            }
            var before = State;
            Set(UpdateState.Checking);
            UpdateInfo? info;
            try
            {
                info = await Updater.CheckAsync(_assetKey);
            }
            catch (Exception ex)
            {
                Log.Write("Update check failed: " + ex.Message);
                Error = "Couldn't check for updates: " + Friendly(ex);
                if (manual) Set(UpdateState.Failed);
                else Set(before is UpdateState.Available or UpdateState.UpToDate ? before : UpdateState.Idle);
                if (manual) Notify?.Invoke(Error, false);
                return;
            }

            LastChecked = DateTime.Now;
            if (info == null)
            {
                Update = null;
                Set(UpdateState.UpToDate);
                if (manual) Notify?.Invoke($"You're up to date (version {Updater.CurrentVersionText})", false);
                return;
            }

            bool isNew = Update?.Version != info.Version;
            Update = info;
            Error = null;
            Set(UpdateState.Available);
            if (isNew) Log.Write($"Update available: {info.DisplayVersion}");

            if (_settings.AutoUpdate && _settings.CheckForUpdates && CanSelfUpdate)
            {
                await DownloadAsync();
            }
            else if (manual || _settings.NotifiedUpdateVersion != info.DisplayVersion)
            {
                _settings.NotifiedUpdateVersion = info.DisplayVersion;
                _settings.Save();
                Notify?.Invoke($"RL Map Loader {info.DisplayVersion} is available", true);
            }
        }

        /// <summary>The title-bar button: download (if needed), install and restart.</summary>
        public async Task InstallNowAsync()
        {
            if (Update == null)
            {
                await CheckAsync(manual: true);
                return;
            }
            if (!CanSelfUpdate)
            {
                OpenUrl?.Invoke(Updater.ReleasesPage);
                return;
            }
            switch (State)
            {
                case UpdateState.Ready:
                    InstallAndRestart();
                    break;
                case UpdateState.Downloading:
                    _restartWhenReady = true;
                    break;
                default:
                    _restartWhenReady = true;
                    await DownloadAsync();
                    break;
            }
        }

        /// <summary>Call when the "check for updates" switch changes.</summary>
        public void CheckForUpdatesChanged()
        {
            if (!_settings.CheckForUpdates && State is UpdateState.UpToDate) State = UpdateState.Idle;
            RaiseChanged();
            if (_settings.CheckForUpdates && State is UpdateState.Idle or UpdateState.UpToDate) _ = CheckAsync(manual: false);
        }

        /// <summary>Call when the automatic-updates switch changes.</summary>
        public void AutoUpdateChanged()
        {
            RaiseChanged();
            if (_settings.AutoUpdate && CanSelfUpdate && State == UpdateState.Available) _ = DownloadAsync();
        }

        /// <summary>The app is quitting: install a downloaded update so the next start runs it.</summary>
        public void ApplyOnExit()
        {
            if (State != UpdateState.Ready || _readyFile == null) return;
            try
            {
                Updater.Install(_readyFile);
                _readyFile = null;
                Log.Write($"Installed update {Update?.DisplayVersion} on exit");
            }
            catch (Exception ex)
            {
                Log.Write("Installing the update on exit failed: " + ex.Message);
            }
        }

        private async Task DownloadAsync()
        {
            if (Update == null || State == UpdateState.Downloading) return;
            Progress = 0;
            Set(UpdateState.Downloading);
            var progress = new Progress<double>(p =>
            {
                if (State != UpdateState.Downloading || p - Progress < 0.01 && p < 1) return;
                Progress = p;
                RaiseChanged();
            });
            try
            {
                var update = Update;
                _readyFile = await Updater.DownloadAsync(update, progress);
                Log.Write($"Downloaded update {update.DisplayVersion}");
            }
            catch (Exception ex)
            {
                Log.Write("Update download failed: " + ex);
                Error = "Downloading the update failed: " + Friendly(ex);
                _restartWhenReady = false;
                Set(UpdateState.Failed);
                Notify?.Invoke(Error, false);
                return;
            }

            Set(UpdateState.Ready);
            if (_restartWhenReady)
            {
                InstallAndRestart();
                return;
            }
            TryInstallWhenIdle();
            if (State == UpdateState.Ready && !_readyNotified)
            {
                _readyNotified = true;
                Notify?.Invoke($"Update {Update?.DisplayVersion} is ready – click Restart to update, or it installs once the app is in the tray", false);
            }
        }

        private void TryInstallWhenIdle()
        {
            if (State != UpdateState.Ready || !_settings.AutoUpdate) return;
            bool idle;
            try { idle = IsIdle() && !Updater.IsGameRunning(); }
            catch { idle = false; }
            if (idle) InstallAndRestart();
        }

        private void InstallAndRestart()
        {
            if (_readyFile == null) return;
            _restartWhenReady = false;
            try
            {
                Updater.Install(_readyFile);
                _readyFile = null;
                Log.Write($"Installed update {Update?.DisplayVersion}; restarting");
            }
            catch (Exception ex)
            {
                Log.Write("Installing the update failed: " + ex);
                Error = "Couldn't replace the app file: " + Friendly(ex);
                Set(UpdateState.Failed);
                Notify?.Invoke(Error, false);
                return;
            }
            try
            {
                Updater.LaunchUpdated(RestartArgs());
            }
            catch (Exception ex)
            {
                // the new version is in place; it runs next time
                Log.Write("Restarting after the update failed: " + ex.Message);
                Notify?.Invoke("Updated – restart RL Map Loader to use the new version", true);
                return;
            }
            ExitForUpdate?.Invoke();
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private static readonly string[] BoundProperties =
            { nameof(State), nameof(Update), nameof(Progress), nameof(Error), nameof(ShowButton), nameof(ButtonText),
              nameof(StatusText), nameof(Notes), nameof(HasNotes), nameof(CanSelfUpdate) };

        private void RaiseChanged()
        {
            Changed?.Invoke();
            var h = PropertyChanged;
            if (h == null) return;
            foreach (var name in BoundProperties) h(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        private void Set(UpdateState s)
        {
            State = s;
            RaiseChanged();
        }

        private static string Friendly(Exception ex) => ex switch
        {
            TaskCanceledException or OperationCanceledException => "the connection timed out",
            HttpRequestException h when h.StatusCode is { } code => $"the server answered {(int)code}",
            HttpRequestException => "no connection to GitHub",
            UnauthorizedAccessException => "no permission to write the app file",
            IOException io => io.Message,
            _ => ex.Message,
        };
    }
}
