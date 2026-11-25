using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using RLMapLoader.Linux.Platform;
using RLMapLoader.Linux.ViewModels;
using RLMapLoader.Linux.Views;
using RLMapLoader.Services;

namespace RLMapLoader.Linux
{
    public partial class App : Application
    {
        public static AppSettings? CurrentSettings { get; private set; }
        public static bool IsShuttingDown { get; private set; }
        public static bool StartHidden { get; set; }
        public static bool StartWithOverlay { get; set; }
        public static bool StartWithRestore { get; set; }

        private IClassicDesktopStyleApplicationLifetime _desktop = null!;
        private AppSettings _settings = null!;
        private MainViewModel _vm = null!;
        private MainWindow _main = null!;
        private OverlayWindow _overlay = null!;
        private readonly X11Hotkey _hotkey = new();
        private TrayIcon? _tray;
        private NativeMenuItem? _trayOverlayItem;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            {
                base.OnFrameworkInitializationCompleted();
                return;
            }
            _desktop = desktop;
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Write("Unhandled: " + e.Exception);
                e.Handled = true;
            };

            _settings = AppSettings.Load();
            CurrentSettings = _settings;
            _vm = new MainViewModel(_settings);
            _main = new MainWindow(_vm);
            _overlay = new OverlayWindow(_vm, X11Hotkey.Available ? _hotkey : null);

            _vm.ShowErrorAsync = ShowErrorAsync;
            _vm.AskAsync = async (title, msg, primary, secondary) =>
            {
                using (_overlay.SuppressAutoHide())
                using (_overlay.PauseKeyboardGrab())
                    return await DialogWindow.ShowAsync(VisibleOwner(), title, msg, primary, secondary);
            };
            _vm.PickFolderAsync = t => _main.PickFolderAsync(t);
            _vm.PickMapFilesAsync = () => _main.PickMapFilesAsync();
            _vm.PickImageAsync = t => _main.PickImageAsync(t);

            // The hotkey arrived through X, so an X11 window (the game) has focus: open without taking it.
            _hotkey.Pressed += () => Dispatcher.UIThread.Post(() => _overlay.Toggle(keepGameFocus: true));
            _hotkey.GrabbedKey += key => Dispatcher.UIThread.Post(() => _overlay.HandleGrabbedKey(key));
            RegisterHotkey();
            _main.HotkeyCaptureStarted += () => _hotkey.Unregister();
            _main.HotkeyCaptureCancelled += RegisterHotkey;
            _main.HotkeyCaptured += (key, mods) =>
            {
                _settings.HotkeyKey = key.ToString();
                _settings.HotkeyModifiers = (int)mods;
                _settings.Save();
                RegisterHotkey();
                if (_vm.HotkeyOk) _vm.Toast($"Overlay hotkey set to {_vm.HotkeyText}", ToastKind.Success);
            };
            _main.OverlayRequested += () => _overlay.Toggle(keepGameFocus: false);
            _main.QuitRequested += () => _ = QuitAsync();
            _overlay.OpenMainRequested += ShowMain;

            SingleInstance.Listen(cmd => Dispatcher.UIThread.Post(() => HandleCommand(cmd)));
            SetupTray();
            if (_settings.StartWithWindows) Autostart.Apply(true); // keep the path fresh if the binary moved

            bool hidden = (StartHidden || _settings.StartMinimized) && _vm.IsConfigured;
            if (!hidden && !StartWithOverlay) _main.Show();
            if (!_vm.IsConfigured) { _main.Show(); _vm.SettingsOpen = true; }

            Dispatcher.UIThread.Post(async () =>
            {
                await _vm.RescanAsync();
                if (StartWithOverlay) _overlay.ShowOverlay(OtherX11WindowActive());
                if (StartWithRestore) await _vm.RestoreAsync();
            });
            base.OnFrameworkInitializationCompleted();
        }

        private void HandleCommand(string cmd)
        {
            switch (cmd)
            {
                case "toggle-overlay": _overlay.Toggle(OtherX11WindowActive()); break;
                case "restore": _ = _vm.RestoreAsync(); break;
                default: ShowMain(); break;
            }
        }

        /// <summary>
        /// True when another program's X11 window is active (e.g. the game under Proton), so the overlay can open
        /// without taking focus. False for native Wayland windows - the keyboard grab can't reach those, so the
        /// overlay takes focus the normal way.
        /// </summary>
        private bool OtherX11WindowActive()
        {
            if (!X11Hotkey.Available) return false;
            var active = _hotkey.GetActiveWindow();
            return active != IntPtr.Zero && active != _overlay.XWindow && active != (_main.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        }

        private void RegisterHotkey()
        {
            var key = KeyNames.Parse(_settings.HotkeyKey);
            var mods = (Avalonia.Input.KeyModifiers)_settings.HotkeyModifiers;
            bool ok = _hotkey.Register(key, mods);
            _vm.HotkeyText = KeyNames.Describe(key, mods);
            _vm.HotkeyOk = ok;
            if (_trayOverlayItem != null) _trayOverlayItem.Header = $"Show overlay ({_vm.HotkeyText})";
        }

        // The overlay is an unmanaged window while it keeps the game focused; dialogs then stand on their own.
        private Window? VisibleOwner() =>
            _overlay.IsVisible && !_overlay.IsKeepingGameFocus ? _overlay : _main.IsVisible ? _main : null;

        private async Task ShowErrorAsync(UserFacingException ex)
        {
            using (_overlay.SuppressAutoHide())
            using (_overlay.PauseKeyboardGrab())
            {
                var msg = ex.NeedsAdmin
                    ? "Linux blocked writing to the Rocket League folder. Make sure your user owns it (for example: chown -R $USER on the game folder), then try again."
                    : ex.Message;
                await DialogWindow.ShowAsync(VisibleOwner(), ex.NeedsAdmin ? "Permission needed" : "Couldn't do that", msg, isError: true);
            }
        }

        private void ShowMain()
        {
            if (!_main.IsVisible) _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        }

        private void SetupTray()
        {
            try
            {
                var open = new NativeMenuItem("Open RL Map Loader");
                open.Click += (_, _) => ShowMain();
                _trayOverlayItem = new NativeMenuItem($"Show overlay ({_vm.HotkeyText})");
                _trayOverlayItem.Click += (_, _) => _overlay.ShowOverlay();
                var restore = new NativeMenuItem("Restore original Underpass");
                restore.Click += async (_, _) => await _vm.RestoreAsync();
                var quit = new NativeMenuItem("Quit");
                quit.Click += async (_, _) => await QuitAsync();

                var menu = new NativeMenu();
                menu.Add(open);
                menu.Add(_trayOverlayItem);
                menu.Add(restore);
                menu.Add(new NativeMenuItemSeparator());
                menu.Add(quit);

                using var stream = AssetLoader.Open(new Uri("avares://rl-map-loader/Assets/logo.png"));
                _tray = new TrayIcon
                {
                    Icon = new WindowIcon(stream),
                    ToolTipText = "RL Map Loader",
                    Menu = menu,
                    IsVisible = true,
                };
                _tray.Clicked += (_, _) => ShowMain();
                TrayIcon.SetIcons(this, new TrayIcons { _tray });
            }
            catch (Exception ex)
            {
                Log.Write("Tray icon unavailable: " + ex.Message);
            }
        }

        public async Task QuitAsync()
        {
            if (IsShuttingDown) return;
            if (_settings.RestoreOnExit && MapSwapper.GetStatus(_settings.CookedPath).State == UnderpassState.Custom)
            {
                try
                {
                    await MapSwapper.RestoreAsync(_settings.CookedPath!);
                    Log.Write("Restored original Underpass on quit");
                }
                catch (Exception ex)
                {
                    Log.Write("Restore on quit failed: " + ex.Message);
                    if (!await DialogWindow.ShowAsync(VisibleOwner(), "Couldn't restore Underpass",
                            ex.Message + "\n\nQuit anyway? The custom map will stay in place.", "Quit anyway", "Stay open", isError: true))
                        return;
                }
            }
            IsShuttingDown = true;
            _hotkey.Dispose();
            SingleInstance.Stop();
            if (_tray != null) _tray.IsVisible = false;
            _main.AllowClose = true;
            _desktop.Shutdown();
        }
    }
}
