using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RLMapLoader.Models;
using RLMapLoader.Services;

namespace RLMapLoader.ViewModels
{
    public enum ToastKind { Info, Success, Error }

    public sealed class MainViewModel : ObservableObject
    {
        private readonly Dispatcher _ui;
        private FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _rescanDebounce;
        private bool _scanning;

        public AppSettings Settings { get; }
        public ObservableCollection<MapItem> Maps { get; } = new();
        public ListCollectionView MapsView { get; }
        public ListCollectionView OverlayView { get; }

        /// <summary>Raised to show a toast in the main window / overlay.</summary>
        public event Action<string, ToastKind>? ToastRequested;
        /// <summary>View hook: (title, message, primary, secondary) → true when primary chosen.</summary>
        public Func<string, string, string, string?, bool>? Ask { get; set; }
        /// <summary>View hook for errors that may offer a "restart as admin".</summary>
        public Action<UserFacingException>? ShowError { get; set; }
        public Action? RequestAdminRestart { get; set; }

        public MainViewModel(AppSettings settings, UpdateController updates)
        {
            _ui = Dispatcher.CurrentDispatcher;
            Settings = settings;
            Updates = updates;
            MapsView = new ListCollectionView(Maps) { Filter = o => Matches((MapItem)o, SearchText) };
            OverlayView = new ListCollectionView(Maps) { Filter = o => Matches((MapItem)o, OverlaySearchText) };
            _sortMode = settings.SortMode is "Name" or "Newest" or "Size" ? settings.SortMode : "Name";
            ApplySort();

            _rescanDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _rescanDebounce.Tick += async (_, _) => { _rescanDebounce.Stop(); await RescanAsync(); };

            LoadCommand = new AsyncCommand(async p => await LoadAsync(p as MapItem ?? SelectedMap), _ => !IsBusy);
            OverlayLoadCommand = new AsyncCommand(async p => await LoadAsync(p as MapItem ?? OverlaySelected), _ => !IsBusy);
            RestoreCommand = new AsyncCommand(async _ => await RestoreAsync(), _ => !IsBusy && State == UnderpassState.Custom);
            RefreshCommand = new AsyncCommand(async _ => { await RescanAsync(); Toast("Map list refreshed", ToastKind.Info); });
            OpenMapsFolderCommand = new RelayCommand(_ => OpenFolder(Settings.MapsPath), _ => MapsValid);
            OpenCookedFolderCommand = new RelayCommand(_ => OpenFolder(Settings.CookedPath), _ => CookedValid);
            ToggleSettingsCommand = new RelayCommand(() => SettingsOpen = !SettingsOpen);
            BrowseCookedCommand = new RelayCommand(BrowseCooked);
            BrowseMapsCommand = new RelayCommand(BrowseMaps);
            AutoDetectCommand = new RelayCommand(AutoDetect);
            AddMapsCommand = new AsyncCommand(async _ => await AddMapsAsync());
            SetImageCommand = new RelayCommand(p => { if (p is MapItem m) PickImage(m); });
            ClearImageCommand = new RelayCommand(p => { if (p is MapItem m) ClearImage(m); }, p => p is MapItem { HasCustomImage: true });
            ShowInExplorerCommand = new RelayCommand(p => { if (p is MapItem m) Reveal(m.Path); });
            ToggleFavoriteCommand = new RelayCommand(p => { if (p is MapItem m) ToggleFavorite(m); });
            SetSortCommand = new RelayCommand(p => { if (p is string s) SortMode = s; });
            UpdateNowCommand = new AsyncCommand(async _ => await Updates.InstallNowAsync(), _ => Updates.State != UpdateState.Downloading);
            CheckUpdatesCommand = new AsyncCommand(async _ => await Updates.CheckAsync(manual: true), _ => Updates.Enabled);
            CopyLogPathCommand = new RelayCommand(() => OpenFolder(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RLMapLoader")));

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

        // ---------------------------------------------------------------- commands
        public ICommand LoadCommand { get; }
        public ICommand OverlayLoadCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenMapsFolderCommand { get; }
        public ICommand OpenCookedFolderCommand { get; }
        public ICommand ToggleSettingsCommand { get; }
        public ICommand BrowseCookedCommand { get; }
        public ICommand BrowseMapsCommand { get; }
        public ICommand AutoDetectCommand { get; }
        public ICommand AddMapsCommand { get; }
        public ICommand SetImageCommand { get; }
        public ICommand ClearImageCommand { get; }
        public ICommand ShowInExplorerCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand SetSortCommand { get; }
        public ICommand CopyLogPathCommand { get; }
        public ICommand UpdateNowCommand { get; }
        public ICommand CheckUpdatesCommand { get; }

        // ---------------------------------------------------------------- updates
        public UpdateController Updates { get; }
        public string VersionText => $"RL Map Loader {Updater.CurrentVersionText}";

        public bool AutoUpdate
        {
            get => Settings.AutoUpdate;
            set
            {
                Settings.AutoUpdate = value;
                Settings.Save();
                OnPropertyChanged();
                Updates.AutoUpdateChanged();
            }
        }

        // ---------------------------------------------------------------- folders / settings
        public string CookedPath
        {
            get => Settings.CookedPath ?? "";
            set
            {
                var v = value?.Trim().Trim('"') ?? "";
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
                var v = value?.Trim().Trim('"') ?? "";
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

        public string CookedHint => CookedValid
            ? "Found Labs_Underpass_P.upk here"
            : "Pick rocketleague\\TAGame\\CookedPCConsole (or the rocketleague folder)";

        public string MapsHint => MapsValid
            ? $"{Maps.Count} map{(Maps.Count == 1 ? "" : "s")} found (sub-folders included)"
            : "Pick the folder where you keep downloaded .upk / .udk maps";

        private void OnFoldersChanged()
        {
            OnPropertyChanged(nameof(CookedValid));
            OnPropertyChanged(nameof(MapsValid));
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(CookedHint));
            OnPropertyChanged(nameof(MapsHint));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyText));
            _ = RefreshStatusAsync();
        }

        /// <summary>Accepts the rocketleague root, TAGame or CookedPCConsole and returns the CookedPCConsole path.</summary>
        private static string NormalizeCooked(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;
            foreach (var c in new[] { p, Path.Combine(p, "CookedPCConsole"), Path.Combine(p, "TAGame", "CookedPCConsole") })
                if (RlLocator.IsValidCooked(c)) return c;
            return p;
        }

        public bool RestoreOnExit
        {
            get => Settings.RestoreOnExit;
            set { Settings.RestoreOnExit = value; Settings.Save(); OnPropertyChanged(); }
        }

        public bool CloseToTray
        {
            get => Settings.CloseToTray;
            set { Settings.CloseToTray = value; Settings.Save(); OnPropertyChanged(); }
        }

        public bool StartMinimized
        {
            get => Settings.StartMinimized;
            set { Settings.StartMinimized = value; Settings.Save(); OnPropertyChanged(); }
        }

        public bool StartWithWindows
        {
            get => Settings.StartWithWindows;
            set
            {
                Settings.StartWithWindows = value;
                Settings.Save();
                StartupRegistration.Apply(value);
                OnPropertyChanged();
            }
        }

        private bool _settingsOpen;
        public bool SettingsOpen { get => _settingsOpen; set => Set(ref _settingsOpen, value); }

        private string _hotkeyText = "F3";
        public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }

        private bool _hotkeyOk = true;
        public bool HotkeyOk { get => _hotkeyOk; set => Set(ref _hotkeyOk, value); }

        private bool _capturingHotkey;
        public bool CapturingHotkey { get => _capturingHotkey; set => Set(ref _capturingHotkey, value); }

        // ---------------------------------------------------------------- search & sort
        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (Set(ref _searchText, value ?? "")) MapsView.Refresh(); }
        }

        private string _overlaySearchText = "";
        public string OverlaySearchText
        {
            get => _overlaySearchText;
            set
            {
                if (!Set(ref _overlaySearchText, value ?? "")) return;
                OverlayView.Refresh();
                if (OverlaySelected == null || !OverlayView.Contains(OverlaySelected))
                    OverlaySelected = OverlayView.Cast<MapItem>().FirstOrDefault();
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
                ApplySort();
            }
        }

        private void ApplySort()
        {
            var cmp = new MapComparer(_sortMode);
            MapsView.CustomSort = cmp;
            OverlayView.CustomSort = cmp;
        }

        private static bool Matches(MapItem m, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            foreach (var tok in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!m.DisplayName.Contains(tok, StringComparison.OrdinalIgnoreCase) &&
                    !m.FileName.Contains(tok, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private sealed class MapComparer : IComparer
        {
            private readonly string _mode;
            public MapComparer(string mode) => _mode = mode;

            public int Compare(object? x, object? y)
            {
                var a = (MapItem)x!;
                var b = (MapItem)y!;
                int fav = b.IsFavorite.CompareTo(a.IsFavorite);
                if (fav != 0) return fav;
                int r = _mode switch
                {
                    "Newest" => b.Modified.CompareTo(a.Modified),
                    "Size" => b.Size.CompareTo(a.Size),
                    _ => 0
                };
                return r != 0 ? r : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        // ---------------------------------------------------------------- selection
        private MapItem? _selectedMap;
        public MapItem? SelectedMap { get => _selectedMap; set => Set(ref _selectedMap, value); }

        private MapItem? _overlaySelected;
        public MapItem? OverlaySelected { get => _overlaySelected; set => Set(ref _overlaySelected, value); }

        public int MapCount => Maps.Count;

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
                OnPropertyChanged(nameof(IsCustomActive));
                OnPropertyChanged(nameof(RestoreTooltip));
                OnPropertyChanged(nameof(HeroTitle));
                OnPropertyChanged(nameof(HeroSubtitle));
                OnPropertyChanged(nameof(HeroLabel));
                OnPropertyChanged(nameof(StatusShort));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsCustomActive => State == UnderpassState.Custom;

        public string RestoreTooltip => State == UnderpassState.Custom
            ? "Put the original Labs_Underpass_P.upk back (do this before playing online)"
            : "The original Underpass is already in place";

        private MapItem? _activeMap;
        public MapItem? ActiveMap
        {
            get => _activeMap;
            set
            {
                if (!Set(ref _activeMap, value)) return;
                OnPropertyChanged(nameof(HeroTitle));
                OnPropertyChanged(nameof(HeroSubtitle));
                OnPropertyChanged(nameof(StatusShort));
            }
        }

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
            _ => "Open Settings to point the loader at rocketleague\\TAGame\\CookedPCConsole."
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
            var snapshot = Maps.ToList();
            var (status, active) = await Task.Run(() =>
            {
                var st = MapSwapper.GetStatus(cooked);
                MapItem? match = null;
                if (st.State == UnderpassState.Custom)
                {
                    var sameSize = snapshot.Where(m => m.Size == st.CustomSize).ToList();
                    if (sameSize.Count == 1)
                    {
                        match = sameSize[0];
                    }
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
            foreach (var m in Maps) m.IsActive = ReferenceEquals(m, active);
            ActiveMap = active;
            State = status.State;
            OnPropertyChanged(nameof(HeroTitle));
        }

        // ---------------------------------------------------------------- busy
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        private double _progress;
        public double Progress { get => _progress; set => Set(ref _progress, value); }

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
                ShowError?.Invoke(ex);
            }
            catch (Exception ex)
            {
                Log.Write("Load failed: " + ex);
                ShowError?.Invoke(new UserFacingException("Loading the map failed:\n\n" + ex.Message));
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
                Log.Write("Restored original Underpass");
                return true;
            }
            catch (UserFacingException ex)
            {
                if (!quiet) ShowError?.Invoke(ex);
                Log.Write("Restore failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write("Restore failed: " + ex);
                if (!quiet) ShowError?.Invoke(new UserFacingException("Restoring failed:\n\n" + ex.Message));
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
                var unzipped = await Task.Run(() => MapLibrary.UnpackZips(root));
                var infos = await Task.Run(() => MapLibrary.Scan(root, customs));

                var existing = Maps.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
                var selectedPath = SelectedMap?.Path;
                var items = new List<MapItem>();
                foreach (var info in infos)
                {
                    if (existing.TryGetValue(info.Path, out var old))
                    {
                        if (old.Info != info) old.UpdateInfo(info);
                        items.Add(old);
                    }
                    else items.Add(new MapItem(info));
                }
                foreach (var it in items) it.IsFavorite = Settings.Favorites.Contains(it.Path);

                // apply diff to keep the list from flickering
                foreach (var gone in Maps.Where(m => !items.Contains(m)).ToList()) Maps.Remove(gone);
                foreach (var it in items.Where(i => !Maps.Contains(i))) Maps.Add(it);
                MapsView.Refresh();
                OverlayView.Refresh();

                SelectedMap = selectedPath != null ? Maps.FirstOrDefault(m => m.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)) : null;
                OnPropertyChanged(nameof(MapCount));
                OnPropertyChanged(nameof(MapsHint));
                LoadThumbnails();
                if (unzipped.Count > 0)
                    Toast(unzipped.Count == 1 ? "Unpacked a map from a zip file" : $"Unpacked {unzipped.Count} maps from zip files", ToastKind.Success);
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
            foreach (var m in Maps.Where(m => m.ImagePath != null && !m.ThumbnailRequested).ToList())
            {
                m.ThumbnailRequested = true;
                var path = m.ImagePath!;
                _ = Thumbnails.LoadAsync(path, 560).ContinueWith(t =>
                {
                    if (t.Result != null && string.Equals(m.ImagePath, path, StringComparison.OrdinalIgnoreCase))
                        m.Thumbnail = t.Result;
                }, TaskScheduler.FromCurrentSynchronizationContext());
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
                  path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) return;
            _ui.BeginInvoke(() => { _rescanDebounce.Stop(); _rescanDebounce.Start(); });
        }

        public void Toast(string message, ToastKind kind) => ToastRequested?.Invoke(message, kind);

        private void BrowseCooked()
        {
            var dlg = new OpenFolderDialog { Title = "Select rocketleague\\TAGame\\CookedPCConsole" };
            if (CookedValid) dlg.InitialDirectory = Settings.CookedPath;
            if (dlg.ShowDialog() != true) return;
            CookedPath = dlg.FolderName;
            if (!CookedValid) Toast("That folder doesn't contain Labs_Underpass_P.upk", ToastKind.Error);
        }

        private void BrowseMaps()
        {
            var dlg = new OpenFolderDialog { Title = "Select your custom maps folder" };
            if (MapsValid) dlg.InitialDirectory = Settings.MapsPath;
            if (dlg.ShowDialog() == true) MapsPath = dlg.FolderName;
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
            var dlg = new OpenFileDialog
            {
                Title = "Add maps",
                Filter = "Rocket League maps (*.upk;*.udk;*.zip)|*.upk;*.udk;*.zip|All files (*.*)|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;
            await ImportAsync(dlg.FileNames);
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
                var imported = await Task.Run(() => MapLibrary.Import(list, root,
                    name => _ui.Invoke(() => Ask?.Invoke("Replace map?", $"“{name}” already exists in your maps folder. Replace it?", "Replace", "Skip") ?? false)));
                await RescanAsync();
                Toast(imported.Count == 0 ? "No new maps were added" : $"Added {imported.Count} map{(imported.Count == 1 ? "" : "s")}",
                      imported.Count == 0 ? ToastKind.Info : ToastKind.Success);
            }
            catch (Exception ex)
            {
                Log.Write("Import failed: " + ex);
                ShowError?.Invoke(new UserFacingException("Adding maps failed:\n\n" + ex.Message));
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void PickImage(MapItem m)
        {
            var dlg = new OpenFileDialog
            {
                Title = $"Choose an image for {m.DisplayName}",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.jfif;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.jfif;*.webp;*.bmp;*.gif",
            };
            if (dlg.ShowDialog() == true) SetImage(m, dlg.FileName);
        }

        public void SetImage(MapItem m, string imagePath)
        {
            try
            {
                var dir = Path.Combine(AppSettings.Folder, "Images");
                Directory.CreateDirectory(dir);
                var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(m.Path.ToLowerInvariant())))[..16];
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
            MapsView.Refresh();
            OverlayView.Refresh();
        }

        private static void Reveal(string path)
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }

        private static void OpenFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }
    }
}
