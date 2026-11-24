using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RLMapLoader.Linux.Models;
using RLMapLoader.Linux.Platform;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.ViewModels
{
    public enum ToastKind { Info, Success, Error }

    /// <summary>Linux counterpart of the Windows MainViewModel; all file logic lives in RLMapLoader.Core.</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _rescanDebounce;
        private bool _scanning;
        private readonly List<Command> _commands = new();
        private static readonly SemaphoreSlim ThumbGate = new(3);

        public AppSettings Settings { get; }
        public List<MapItem> AllMaps { get; } = new();
        public ObservableCollection<MapItem> Maps { get; } = new();
        public ObservableCollection<MapItem> OverlayMaps { get; } = new();

        public event Action<string, ToastKind>? ToastRequested;

        // view hooks
        public Func<string, string, string, string?, Task<bool>>? AskAsync { get; set; }
        public Func<UserFacingException, Task>? ShowErrorAsync { get; set; }
        public Func<string, Task<string?>>? PickFolderAsync { get; set; }
        public Func<Task<string[]?>>? PickMapFilesAsync { get; set; }
        public Func<string, Task<string?>>? PickImageAsync { get; set; }

        public MainViewModel(AppSettings settings)
        {
            Settings = settings;
            _sortMode = settings.SortMode is "Name" or "Newest" or "Size" ? settings.SortMode : "Name";
            _rescanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _rescanDebounce.Tick += async (_, _) => { _rescanDebounce.Stop(); await RescanAsync(); };

            LoadCommand = Cmd(async p => await LoadAsync(p as MapItem ?? SelectedMap), _ => !IsBusy);
            RestoreCommand = Cmd(async _ => await RestoreAsync(), _ => !IsBusy && State == UnderpassState.Custom);
            RefreshCommand = Cmd(async _ => { await RescanAsync(); Toast("Map list refreshed", ToastKind.Info); });
            OpenMapsFolderCommand = Cmd(_ => OpenPath(Settings.MapsPath), _ => MapsValid);
            OpenCookedFolderCommand = Cmd(_ => OpenPath(Settings.CookedPath), _ => CookedValid);
            OpenLogsCommand = Cmd(_ => OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RLMapLoader")));
            ToggleSettingsCommand = Cmd(_ => SettingsOpen = !SettingsOpen);
            BrowseCookedCommand = Cmd(async _ => await BrowseCookedAsync());
            BrowseMapsCommand = Cmd(async _ => await BrowseMapsAsync());
            AutoDetectCommand = Cmd(_ => AutoDetect());
            AddMapsCommand = Cmd(async _ => await AddMapsAsync());
            SetImageCommand = Cmd(async p => { if (p is MapItem m) await PickImageForAsync(m); });
            ClearImageCommand = Cmd(p => { if (p is MapItem m) ClearImage(m); });
            ShowInFolderCommand = Cmd(p => { if (p is MapItem m) OpenPath(Path.GetDirectoryName(m.Path)); });
            ToggleFavoriteCommand = Cmd(p => { if (p is MapItem m) ToggleFavorite(m); });
            SetSortCommand = Cmd(p => { if (p is string s) SortMode = s; });

            if (!RlLocator.IsValidCooked(Settings.CookedPath))
            {
                var found = RlLocator.FindCookedPath();
                if (found != null)
                {
                    Settings.CookedPath = found;
                    Settings.Save();
                }
            }
            SetupWatcher();
        }

        private Command Cmd(Func<object?, Task> run, Func<object?, bool>? can = null)
        {
            var c = new Command(run, can);
            _commands.Add(c);
            return c;
        }

        private Command Cmd(Action<object?> run, Func<object?, bool>? can = null)
        {
            var c = new Command(run, can);
            _commands.Add(c);
            return c;
        }

        private void RefreshCommands()
        {
            foreach (var c in _commands) c.Refresh();
        }

        public Command LoadCommand { get; }
        public Command RestoreCommand { get; }
        public Command RefreshCommand { get; }
        public Command OpenMapsFolderCommand { get; }
        public Command OpenCookedFolderCommand { get; }
        public Command OpenLogsCommand { get; }
        public Command ToggleSettingsCommand { get; }
        public Command BrowseCookedCommand { get; }
        public Command BrowseMapsCommand { get; }
        public Command AutoDetectCommand { get; }
        public Command AddMapsCommand { get; }
        public Command SetImageCommand { get; }
        public Command ClearImageCommand { get; }
        public Command ShowInFolderCommand { get; }
        public Command ToggleFavoriteCommand { get; }
        public Command SetSortCommand { get; }

        // ---------------------------------------------------------------- folders & settings
        public string CookedPath
        {
            get => Settings.CookedPath ?? "";
            set
            {
                var v = (value ?? "").Trim().Trim('"');
                if (v == (Settings.CookedPath ?? "")) return;
                Settings.CookedPath = NormalizeCooked(v);
                Settings.Save();
                OnPropertyChanged();
                OnFoldersChanged();
            }
        }

        public string MapsPath
        {
            get => Settings.MapsPath ?? "";
            set
            {
                var v = (value ?? "").Trim().Trim('"');
                if (v == (Settings.MapsPath ?? "")) return;
                Settings.MapsPath = v;
                Settings.Save();
                OnPropertyChanged();
                OnFoldersChanged();
                SetupWatcher();
                _ = RescanAsync();
            }
        }

        public bool CookedValid => RlLocator.IsValidCooked(Settings.CookedPath);
        public bool MapsValid => !string.IsNullOrWhiteSpace(Settings.MapsPath) && Directory.Exists(Settings.MapsPath);
        public bool IsConfigured => CookedValid && MapsValid;
        public string CookedHint => CookedValid ? "Found Labs_Underpass_P.upk here" : "Pick rocketleague/TAGame/CookedPCConsole (or the rocketleague folder)";
        public string MapsHint => MapsValid
            ? $"{AllMaps.Count} map{(AllMaps.Count == 1 ? "" : "s")} found (sub-folders included)"
            : "Pick the folder where you keep downloaded .upk / .udk maps";

        private void OnFoldersChanged()
        {
            foreach (var p in new[] { nameof(CookedValid), nameof(MapsValid), nameof(IsConfigured), nameof(CookedHint),
                                      nameof(MapsHint), nameof(EmptyTitle), nameof(EmptyText), nameof(IsNotConfigured) })
                OnPropertyChanged(p);
            RefreshCommands();
            _ = RefreshStatusAsync();
        }

        private static string NormalizeCooked(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;
            var candidates = new[] { p, PathUtil.Resolve(p, "CookedPCConsole"), PathUtil.Resolve(p, "TAGame", "CookedPCConsole") };
            foreach (var c in candidates)
                if (c != null && RlLocator.IsValidCooked(c)) return c;
            return p;
        }

        public bool RestoreOnExit { get => Settings.RestoreOnExit; set { Settings.RestoreOnExit = value; Settings.Save(); OnPropertyChanged(); } }
        public bool CloseToTray { get => Settings.CloseToTray; set { Settings.CloseToTray = value; Settings.Save(); OnPropertyChanged(); } }
        public bool StartMinimized { get => Settings.StartMinimized; set { Settings.StartMinimized = value; Settings.Save(); OnPropertyChanged(); } }

        public bool StartWithWindows
        {
            get => Settings.StartWithWindows;
            set
            {
                Settings.StartWithWindows = value;
                Settings.Save();
                Autostart.Apply(value);
                OnPropertyChanged();
            }
        }

        private bool _settingsOpen;
        public bool SettingsOpen { get => _settingsOpen; set => Set(ref _settingsOpen, value); }

        private string _hotkeyText = "F3";
        public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }

        private bool _hotkeyOk = true;
        public bool HotkeyOk
        {
            get => _hotkeyOk;
            set { if (Set(ref _hotkeyOk, value)) OnPropertyChanged(nameof(HotkeyFailed)); }
        }
        public bool HotkeyFailed => !_hotkeyOk;

        private bool _capturingHotkey;
        public bool CapturingHotkey
        {
            get => _capturingHotkey;
            set { if (Set(ref _capturingHotkey, value)) OnPropertyChanged(nameof(HotkeyButtonText)); }
        }
        public string HotkeyButtonText => CapturingHotkey ? "Press a key…" : HotkeyText;

        public string ToggleCommandLine => $"\"{Environment.ProcessPath ?? "rl-map-loader"}\" --toggle-overlay";

        // ---------------------------------------------------------------- search, sort, views
        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (Set(ref _searchText, value ?? "")) RebuildMain(); }
        }

        private string _overlaySearchText = "";
        public string OverlaySearchText
        {
            get => _overlaySearchText;
            set
            {
                if (!Set(ref _overlaySearchText, value ?? "")) return;
                RebuildOverlay();
                if (OverlaySelected == null || !OverlayMaps.Contains(OverlaySelected)) OverlaySelected = OverlayMaps.FirstOrDefault();
            }
        }

        private string _sortMode;
        public string SortMode
        {
            get => _sortMode;
            set
            {
                if (!Set(ref _sortMode, value)) return;
                Settings.SortMode = value;
                Settings.Save();
                OnPropertyChanged(nameof(SortIsName));
                OnPropertyChanged(nameof(SortIsNewest));
                OnPropertyChanged(nameof(SortIsSize));
                RebuildMain();
                RebuildOverlay();
            }
        }
        public bool SortIsName { get => _sortMode == "Name"; set { if (value) SortMode = "Name"; } }
        public bool SortIsNewest { get => _sortMode == "Newest"; set { if (value) SortMode = "Newest"; } }
        public bool SortIsSize { get => _sortMode == "Size"; set { if (value) SortMode = "Size"; } }

        private IEnumerable<MapItem> Sorted(IEnumerable<MapItem> items)
        {
            var fav = items.OrderByDescending(m => m.IsFavorite);
            var sorted = _sortMode switch
            {
                "Newest" => fav.ThenByDescending(m => m.Modified),
                "Size" => fav.ThenByDescending(m => m.Size),
                _ => fav.ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            };
            return sorted.ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        }

        private static bool Matches(MapItem m, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            foreach (var tok in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!m.DisplayName.Contains(tok, StringComparison.OrdinalIgnoreCase) &&
                    !m.FileName.Contains(tok, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static void Sync(ObservableCollection<MapItem> target, List<MapItem> wanted)
        {
            for (int i = target.Count - 1; i >= 0; i--)
                if (!wanted.Contains(target[i])) target.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++)
            {
                int at = target.IndexOf(wanted[i]);
                if (at == i) continue;
                if (at >= 0) target.Move(at, i);
                else target.Insert(i, wanted[i]);
            }
        }

        private void RebuildMain()
        {
            Sync(Maps, Sorted(AllMaps.Where(m => Matches(m, _searchText))).ToList());
            OnPropertyChanged(nameof(MapCount));
            OnPropertyChanged(nameof(HasNoMaps));
        }

        private void RebuildOverlay()
        {
            Sync(OverlayMaps, Sorted(AllMaps.Where(m => Matches(m, _overlaySearchText))).ToList());
            OnPropertyChanged(nameof(OverlayEmpty));
        }

        private MapItem? _selectedMap;
        public MapItem? SelectedMap { get => _selectedMap; set => Set(ref _selectedMap, value); }

        private MapItem? _overlaySelected;
        public MapItem? OverlaySelected { get => _overlaySelected; set => Set(ref _overlaySelected, value); }

        public int MapCount => AllMaps.Count;
        public bool HasNoMaps => AllMaps.Count == 0;
        public bool OverlayEmpty => OverlayMaps.Count == 0;
        public bool IsNotConfigured => !IsConfigured;
        public string EmptyTitle => !IsConfigured ? "Let's set up your folders" : "No maps here yet";
        public string EmptyText => !IsConfigured
            ? "Choose your Rocket League folder and the folder where you keep custom maps."
            : "Drop .upk, .udk or .zip files on this window, or use Add maps.";

        // ---------------------------------------------------------------- Underpass status
        private UnderpassState _state = UnderpassState.NotConfigured;
        public UnderpassState State
        {
            get => _state;
            set
            {
                if (!Set(ref _state, value)) return;
                foreach (var p in new[] { nameof(IsCustomActive), nameof(HeroTitle), nameof(HeroSubtitle), nameof(HeroLabel), nameof(StatusShort) })
                    OnPropertyChanged(p);
                RefreshCommands();
            }
        }

        public bool IsCustomActive => State == UnderpassState.Custom;

        private MapItem? _activeMap;
        public MapItem? ActiveMap
        {
            get => _activeMap;
            set
            {
                if (!Set(ref _activeMap, value)) return;
                OnPropertyChanged(nameof(HasActiveMap));
                OnPropertyChanged(nameof(HeroTitle));
                OnPropertyChanged(nameof(HeroSubtitle));
                OnPropertyChanged(nameof(StatusShort));
            }
        }
        public bool HasActiveMap => _activeMap != null;

        public string HeroLabel => State switch
        {
            UnderpassState.Custom => "NOW LOADED IN UNDERPASS",
            UnderpassState.Original => "UNDERPASS IS ORIGINAL",
            UnderpassState.Missing => "UNDERPASS FILE MISSING",
            _ => "NOT SET UP"
        };

        public string HeroTitle => State switch
        {
            UnderpassState.Custom => ActiveMap?.DisplayName ?? "Unknown custom map",
            UnderpassState.Original => "Original Underpass",
            UnderpassState.Missing => "Labs_Underpass_P.upk is missing",
            _ => "Choose your Rocket League folder"
        };

        public string HeroSubtitle => State switch
        {
            UnderpassState.Custom => ActiveMap == null
                ? "A custom map (not in your maps folder) is in place. Restore the original before playing online."
                : "In Rocket League: Training → Free Play → pick Underpass.",
            UnderpassState.Original => "Pick a map below and hit Load – then start Free Play on Underpass.",
            UnderpassState.Missing => "Restore the original, or verify game files in your launcher.",
            _ => "Open Settings to point the loader at rocketleague/TAGame/CookedPCConsole."
        };

        public string StatusShort => State switch
        {
            UnderpassState.Custom => "Active: " + (ActiveMap?.DisplayName ?? "custom map"),
            UnderpassState.Original => "Underpass is original",
            UnderpassState.Missing => "Underpass missing",
            _ => "Not set up"
        };

        public async Task RefreshStatusAsync()
        {
            var cooked = Settings.CookedPath;
            var snapshot = AllMaps.ToList();
            var (status, active) = await Task.Run(() =>
            {
                var st = MapSwapper.GetStatus(cooked);
                MapItem? match = null;
                if (st.State == UnderpassState.Custom)
                {
                    var sameSize = snapshot.Where(m => m.Size == st.CustomSize).ToList();
                    if (sameSize.Count == 1) match = sameSize[0];
                    else if (sameSize.Count > 1)
                    {
                        var quick = sameSize.Where(m => MapSwapper.Fingerprint(m.Path) == st.CustomFingerprint).ToList();
                        if (quick.Count == 1) match = quick[0];
                        else if (quick.Count > 1)
                        {
                            var deep = MapSwapper.DeepFingerprint(MapSwapper.TargetPath(cooked!));
                            match = quick.FirstOrDefault(m => MapSwapper.DeepFingerprint(m.Path) == deep);
                        }
                    }
                }
                return (st, match);
            });
            foreach (var m in AllMaps) m.IsActive = ReferenceEquals(m, active);
            ActiveMap = active;
            State = status.State;
            OnPropertyChanged(nameof(HeroTitle));
        }

        // ---------------------------------------------------------------- busy
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { if (Set(ref _isBusy, value)) RefreshCommands(); }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressText)); }
        }
        public string ProgressText => $"{Math.Round(_progress * 100)}%";

        private string _busyText = "";
        public string BusyText { get => _busyText; set => Set(ref _busyText, value); }

        // ---------------------------------------------------------------- actions
        public async Task<bool> LoadAsync(MapItem? item)
        {
            if (item == null || IsBusy) return false;
            if (!CookedValid)
            {
                Toast("Set your Rocket League folder first", ToastKind.Error);
                SettingsOpen = true;
                return false;
            }
            if (item.IsActive && State == UnderpassState.Custom)
            {
                Toast($"“{item.DisplayName}” is already loaded", ToastKind.Info);
                return true;
            }
            IsBusy = true;
            Progress = 0;
            BusyText = $"Loading {item.DisplayName}…";
            try
            {
                var progress = new Progress<double>(p => Progress = p);
                await MapSwapper.LoadAsync(Settings.CookedPath!, item.Path, progress);
                await RefreshStatusAsync();
                Toast($"Loaded “{item.DisplayName}” – start Free Play on Underpass", ToastKind.Success);
                Log.Write($"Loaded {item.Path}");
                return true;
            }
            catch (UserFacingException ex)
            {
                if (ShowErrorAsync != null) await ShowErrorAsync(ex);
            }
            catch (Exception ex)
            {
                Log.Write("Load failed: " + ex);
                if (ShowErrorAsync != null) await ShowErrorAsync(new UserFacingException("Loading the map failed:\n\n" + ex.Message));
            }
            finally
            {
                IsBusy = false;
                await RefreshStatusAsync();
            }
            return false;
        }

        public async Task<bool> RestoreAsync(bool quiet = false)
        {
            if (!CookedValid || IsBusy) return false;
            IsBusy = true;
            BusyText = "Restoring original Underpass…";
            Progress = 1;
            try
            {
                await MapSwapper.RestoreAsync(Settings.CookedPath!);
                await RefreshStatusAsync();
                if (!quiet) Toast("Original Underpass restored – safe to play online", ToastKind.Success);
                return true;
            }
            catch (UserFacingException ex)
            {
                Log.Write("Restore failed: " + ex.Message);
                if (!quiet && ShowErrorAsync != null) await ShowErrorAsync(ex);
            }
            catch (Exception ex)
            {
                Log.Write("Restore failed: " + ex);
                if (!quiet && ShowErrorAsync != null) await ShowErrorAsync(new UserFacingException("Restoring failed:\n\n" + ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
            return false;
        }

        public async Task RescanAsync()
        {
            if (_scanning) { _rescanDebounce.Stop(); _rescanDebounce.Start(); return; }
            _scanning = true;
            try
            {
                var root = Settings.MapsPath ?? "";
                var customs = new Dictionary<string, string>(Settings.CustomImages, StringComparer.OrdinalIgnoreCase);
                var infos = await Task.Run(() => MapLibrary.Scan(root, customs));

                var existing = AllMaps.ToDictionary(m => m.Path, StringComparer.Ordinal);
                var items = new List<MapItem>();
                foreach (var info in infos)
                {
                    if (existing.TryGetValue(info.Path, out var old))
                    {
                        if (old.Info != info) old.UpdateInfo(info);
                        items.Add(old);
                    }
                    else items.Add(new MapItem(info, this));
                }
                foreach (var it in items) it.IsFavorite = Settings.Favorites.Contains(it.Path);
                AllMaps.Clear();
                AllMaps.AddRange(items);
                RebuildMain();
                RebuildOverlay();
                OnPropertyChanged(nameof(MapsHint));
                LoadThumbnails();
                await RefreshStatusAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Rescan failed: " + ex);
            }
            finally
            {
                _scanning = false;
            }
        }

        private void LoadThumbnails()
        {
            foreach (var m in AllMaps.Where(m => m.ImagePath != null && !m.ThumbnailRequested).ToList())
            {
                m.ThumbnailRequested = true;
                var path = m.ImagePath!;
                _ = Task.Run(async () =>
                {
                    await ThumbGate.WaitAsync();
                    try
                    {
                        using var fs = File.OpenRead(path);
                        var bmp = Bitmap.DecodeToWidth(fs, 560, BitmapInterpolationMode.HighQuality);
                        await Dispatcher.UIThread.InvokeAsync(() => { if (m.ImagePath == path) m.Thumbnail = bmp; });
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Thumbnail failed {path}: {ex.Message}");
                    }
                    finally
                    {
                        ThumbGate.Release();
                    }
                });
            }
        }

        private void SetupWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
            if (!MapsValid) return;
            try
            {
                _watcher = new FileSystemWatcher(Settings.MapsPath!)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                FileSystemEventHandler h = (_, e) => Poke(e.FullPath);
                _watcher.Created += h;
                _watcher.Deleted += h;
                _watcher.Changed += h;
                _watcher.Renamed += (_, e) => Poke(e.FullPath);
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.Write("Watcher failed: " + ex.Message);
            }
        }

        private void Poke(string path)
        {
            if (!(MapLibrary.IsMapFile(path) || MapLibrary.IsImageFile(path) || !Path.HasExtension(path) ||
                  path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return;
            Dispatcher.UIThread.Post(() => { _rescanDebounce.Stop(); _rescanDebounce.Start(); });
        }

        public void Toast(string message, ToastKind kind) => ToastRequested?.Invoke(message, kind);

        private async Task BrowseCookedAsync()
        {
            if (PickFolderAsync == null) return;
            var folder = await PickFolderAsync("Select rocketleague/TAGame/CookedPCConsole");
            if (folder == null) return;
            CookedPath = folder;
            if (!CookedValid) Toast("That folder doesn't contain Labs_Underpass_P.upk", ToastKind.Error);
        }

        private async Task BrowseMapsAsync()
        {
            if (PickFolderAsync == null) return;
            var folder = await PickFolderAsync("Select your custom maps folder");
            if (folder != null) MapsPath = folder;
        }

        private void AutoDetect()
        {
            var found = RlLocator.FindCookedPath();
            if (found == null)
            {
                Toast("Couldn't find Rocket League automatically – use Browse", ToastKind.Error);
                return;
            }
            CookedPath = found;
            Toast("Found Rocket League at " + found, ToastKind.Success);
        }

        private async Task AddMapsAsync()
        {
            if (!MapsValid)
            {
                Toast("Choose your custom maps folder first", ToastKind.Error);
                SettingsOpen = true;
                return;
            }
            if (PickMapFilesAsync == null) return;
            var files = await PickMapFilesAsync();
            if (files is { Length: > 0 }) await ImportAsync(files);
        }

        public async Task ImportAsync(IEnumerable<string> files)
        {
            if (!MapsValid)
            {
                Toast("Choose your custom maps folder first", ToastKind.Error);
                SettingsOpen = true;
                return;
            }
            var list = files.ToList();
            IsBusy = true;
            BusyText = "Adding maps…";
            Progress = 1;
            try
            {
                var root = Settings.MapsPath!;
                var imported = await Task.Run(() => MapLibrary.Import(list, root, name =>
                    AskAsync == null ||
                    Dispatcher.UIThread.InvokeAsync(() => AskAsync("Replace map?", $"“{name}” already exists in your maps folder. Replace it?", "Replace", "Skip"))
                        .GetAwaiter().GetResult()));
                await RescanAsync();
                Toast(imported.Count == 0 ? "No new maps were added" : $"Added {imported.Count} map{(imported.Count == 1 ? "" : "s")}",
                      imported.Count == 0 ? ToastKind.Info : ToastKind.Success);
            }
            catch (Exception ex)
            {
                Log.Write("Import failed: " + ex);
                if (ShowErrorAsync != null) await ShowErrorAsync(new UserFacingException("Adding maps failed:\n\n" + ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task PickImageForAsync(MapItem m)
        {
            if (PickImageAsync == null) return;
            var img = await PickImageAsync($"Choose an image for {m.DisplayName}");
            if (img != null) SetImage(m, img);
        }

        public void SetImage(MapItem m, string imagePath)
        {
            try
            {
                var dir = Path.Combine(AppSettings.Folder, "Images");
                Directory.CreateDirectory(dir);
                var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(m.Path)))[..16];
                var dest = Path.Combine(dir, key + Path.GetExtension(imagePath).ToLowerInvariant());
                foreach (var old in Directory.EnumerateFiles(dir, key + ".*")) File.Delete(old);
                File.Copy(imagePath, dest, overwrite: true);
                Settings.CustomImages[m.Path] = dest;
                Settings.Save();
                m.UpdateInfo(m.Info with { ImagePath = dest, ImageIsCustom = true });
                LoadThumbnails();
                Toast($"Image set for “{m.DisplayName}”", ToastKind.Success);
            }
            catch (Exception ex)
            {
                Toast("Couldn't use that image: " + ex.Message, ToastKind.Error);
            }
        }

        private void ClearImage(MapItem m)
        {
            if (Settings.CustomImages.Remove(m.Path)) Settings.Save();
            _ = RescanAsync();
        }

        private void ToggleFavorite(MapItem m)
        {
            m.IsFavorite = !m.IsFavorite;
            if (m.IsFavorite) Settings.Favorites.Add(m.Path); else Settings.Favorites.Remove(m.Path);
            Settings.Save();
            RebuildMain();
            RebuildOverlay();
        }

        private static void OpenPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open", $"\"{path}\"")
                    { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Log.Write("Open folder failed: " + ex.Message);
            }
        }
    }
}
