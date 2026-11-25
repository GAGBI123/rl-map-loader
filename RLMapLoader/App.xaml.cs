using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RLMapLoader.Services;
using RLMapLoader.ViewModels;
using RLMapLoader.Views;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace RLMapLoader
{
    public partial class App : Application
    {
        private const string MutexName = "RLMapLoader.SingleInstance.v1";
        private const string ShowEventName = "RLMapLoader.Show.v1";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private AppSettings _settings = null!;
        private MainViewModel _vm = null!;
        private MainWindow _main = null!;
        private OverlayWindow _overlay = null!;
        private HotkeyManager? _hotkeys;
        private Forms.NotifyIcon? _tray;
        private UpdateController _updates = null!;

        public static AppSettings? CurrentSettings { get; private set; }
        public static bool IsShuttingDown { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // After an update the new exe starts while the old one is still closing: let it finish first.
            Updater.WaitForPreviousInstance(e.Args);
            string? updatedFrom = Updater.UpdatedFrom(e.Args);

            _mutex = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                IsShuttingDown = true;
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showEvent.WaitOne())
                {
                    if (IsShuttingDown) break;
                    Dispatcher.BeginInvoke(ShowMain);
                }
            }) { IsBackground = true, Name = "show-signal" }.Start();

            DispatcherUnhandledException += (_, a) =>
            {
                Log.Write("Unhandled: " + a.Exception);
                a.Handled = true;
                DialogWindow.Show(VisibleOwner(), "Something went wrong", a.Exception.Message, isError: true);
            };

            _settings = AppSettings.Load();
            CurrentSettings = _settings;
            Updater.CleanUp(afterUpdate: updatedFrom != null);
            _updates = new UpdateController(_settings, Updater.IsSelfContained ? "windows" : "windows-net9",
                a => Dispatcher.BeginInvoke(a));
            _vm = new MainViewModel(_settings, _updates);
            _main = new MainWindow(_vm);
            _overlay = new OverlayWindow(_vm);

            _vm.ShowError = ShowError;
            _vm.Ask = (title, msg, primary, secondary) =>
            {
                using (_overlay.SuppressAutoHide())
                    return DialogWindow.Show(VisibleOwner(), title, msg, primary, secondary);
            };

            var hwnd = new WindowInteropHelper(_main).EnsureHandle();
            _hotkeys = new HotkeyManager(hwnd);
            _hotkeys.Pressed += () => _overlay.Toggle();
            RegisterHotkey();

            _main.HotkeyCaptureStarted += () => _hotkeys.Unregister();
            _main.HotkeyCaptureCancelled += RegisterHotkey;
            _main.HotkeyCaptured += (key, mods) =>
            {
                _settings.HotkeyKey = key.ToString();
                _settings.HotkeyModifiers = (int)mods;
                _settings.Save();
                RegisterHotkey();
                if (_vm.HotkeyOk) _vm.Toast($"Overlay hotkey set to {_vm.HotkeyText}", ToastKind.Success);
            };
            _main.OverlayRequested += () => _overlay.Toggle();
            _main.ExitRequested += ExitApp;
            _main.IsVisibleChanged += (_, _) => { if (!_main.IsVisible) MaybeShowTrayHint(); };
            _overlay.OpenMainRequested += ShowMain;

            SetupTray();
            if (_settings.StartWithWindows) StartupRegistration.Apply(true); // keep the path fresh if the exe moved

            bool HasArg(string a) => e.Args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
            bool startHidden = (HasArg("--tray") || _settings.StartMinimized) && !HasArg("--show");
            if (!startHidden || !_vm.IsConfigured) _main.Show();
            if (!_vm.IsConfigured) _vm.SettingsOpen = true;

            _ = _vm.RescanAsync();
            SetupUpdates(updatedFrom);
        }

        // ------------------------------------------------------------ updates
        private void SetupUpdates(string? updatedFrom)
        {
            // Restart on its own only when nobody would notice: window closed to the tray, overlay hidden, nothing loading.
            _updates.IsIdle = () => !_vm.IsBusy && !_overlay.IsVisible && !_main.IsVisible;
            _updates.RestartArgs = () => _main.IsVisible ? new[] { "--show" } : new[] { "--tray" };
            _updates.ExitForUpdate = Shutdown_;
            _updates.OpenUrl = url =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            };
            _updates.Notify += (message, important) =>
            {
                if (_main.IsVisible) _vm.Toast(message, ToastKind.Info);
                else if (important) _tray?.ShowBalloonTip(5000, "RL Map Loader", message + " – open the app to install it.", Forms.ToolTipIcon.Info);
            };
            _updates.Start();

            if (updatedFrom != null)
            {
                var msg = $"Updated to version {Updater.CurrentVersionText}";
                Log.Write($"{msg} (from {updatedFrom})");
                if (_main.IsVisible) _vm.Toast(msg, ToastKind.Success);
                else _tray?.ShowBalloonTip(4000, "RL Map Loader", msg, Forms.ToolTipIcon.Info);
            }
        }

        private void RegisterHotkey()
        {
            if (_hotkeys == null) return;
            var key = Enum.TryParse<Key>(_settings.HotkeyKey, out var k) ? k : Key.F3;
            var mods = (ModifierKeys)_settings.HotkeyModifiers;
            bool ok = _hotkeys.Register(key, mods);
            _vm.HotkeyText = HotkeyManager.Describe(key, mods);
            _vm.HotkeyOk = ok;
            if (_tray?.ContextMenuStrip?.Items[1] is Forms.ToolStripItem item) item.Text = $"Show overlay ({_vm.HotkeyText})";
        }

        private Window? VisibleOwner() =>
            _overlay is { IsVisible: true } ? _overlay : _main is { IsVisible: true } ? _main : null;

        private void ShowError(UserFacingException ex)
        {
            using (_overlay.SuppressAutoHide())
            {
                if (ex.NeedsAdmin)
                {
                    if (DialogWindow.Show(VisibleOwner(), "Permission needed", ex.Message, "Restart as administrator", "Cancel", isError: true))
                        RestartAsAdmin();
                }
                else
                {
                    DialogWindow.Show(VisibleOwner(), "Couldn't do that", ex.Message, isError: true);
                }
            }
        }

        private void ShowMain()
        {
            if (!_main.IsVisible) _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            Native.ForceForeground(new WindowInteropHelper(_main).Handle);
        }

        // ------------------------------------------------------------ tray
        private void SetupTray()
        {
            Drawing.Icon icon;
            try
            {
                var res = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
                icon = res != null ? new Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize) : Drawing.SystemIcons.Application;
            }
            catch { icon = Drawing.SystemIcons.Application; }

            var menu = new Forms.ContextMenuStrip { Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()), ShowImageMargin = false };
            menu.Font = new Drawing.Font("Segoe UI", 9.5f);
            menu.ForeColor = Drawing.Color.FromArgb(238, 242, 250);
            menu.Items.Add("Open RL Map Loader", null, (_, _) => ShowMain());
            menu.Items.Add($"Show overlay ({_vm.HotkeyText})", null, (_, _) => _overlay.ShowOverlay());
            menu.Items.Add("Restore original Underpass", null, async (_, _) => await _vm.RestoreAsync());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());
            menu.Opening += (_, _) => menu.Items[2].Enabled = _vm.IsCustomActive;

            _tray = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = "RL Map Loader",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowMain(); };
        }

        private void MaybeShowTrayHint()
        {
            if (IsShuttingDown || _settings.TrayHintShown || _tray == null) return;
            _settings.TrayHintShown = true;
            _settings.Save();
            _tray.ShowBalloonTip(4000, "Still running in the tray",
                $"Press {_vm.HotkeyText} in Rocket League to open the map overlay. Right-click the tray icon to exit.",
                Forms.ToolTipIcon.Info);
        }

        private sealed class DarkMenuColors : Forms.ProfessionalColorTable
        {
            private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(24, 32, 51);
            private static readonly Drawing.Color Hi = Drawing.Color.FromArgb(38, 48, 74);
            public override Drawing.Color ToolStripDropDownBackground => Bg;
            public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(38, 48, 74);
            public override Drawing.Color MenuItemBorder => Hi;
            public override Drawing.Color MenuItemSelected => Hi;
            public override Drawing.Color MenuItemSelectedGradientBegin => Hi;
            public override Drawing.Color MenuItemSelectedGradientEnd => Hi;
            public override Drawing.Color ImageMarginGradientBegin => Bg;
            public override Drawing.Color ImageMarginGradientMiddle => Bg;
            public override Drawing.Color ImageMarginGradientEnd => Bg;
            public override Drawing.Color SeparatorDark => Hi;
            public override Drawing.Color SeparatorLight => Bg;
        }

        // ------------------------------------------------------------ exit
        private void ExitApp()
        {
            if (IsShuttingDown) return;
            if (_settings.RestoreOnExit && RlLocator.IsValidCooked(_settings.CookedPath) &&
                MapSwapper.GetStatus(_settings.CookedPath).State == UnderpassState.Custom)
            {
                try
                {
                    MapSwapper.RestoreAsync(_settings.CookedPath!).GetAwaiter().GetResult();
                    Log.Write("Restored original Underpass on exit");
                }
                catch (Exception ex)
                {
                    Log.Write("Restore on exit failed: " + ex.Message);
                    if (!DialogWindow.Show(VisibleOwner(), "Couldn't restore Underpass",
                            (ex is UserFacingException ? ex.Message : "Restoring the original map failed: " + ex.Message) +
                            "\n\nExit anyway? The custom map will stay in place.", "Exit anyway", "Stay open", isError: true))
                        return;
                }
            }
            _updates.ApplyOnExit();
            Shutdown_();
        }

        private void Shutdown_()
        {
            IsShuttingDown = true;
            _updates?.Stop();
            try { _hotkeys?.Dispose(); } catch { }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _main.AllowClose = true;
            try { _showEvent?.Set(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _mutex = null;
            Shutdown();
        }

        private void RestartAsAdmin()
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _mutex = null;
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                IsShuttingDown = true;
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                _main.AllowClose = true;
                Shutdown();
            }
            catch (Win32Exception)
            {
                // UAC was cancelled: keep running as we are
                _mutex = new Mutex(true, MutexName, out _);
            }
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            try
            {
                if (_settings.RestoreOnExit && MapSwapper.GetStatus(_settings.CookedPath).State == UnderpassState.Custom)
                    MapSwapper.RestoreAsync(_settings.CookedPath!).GetAwaiter().GetResult();
            }
            catch { }
            base.OnSessionEnding(e);
        }
    }
}
