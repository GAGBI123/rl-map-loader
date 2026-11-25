using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RLMapLoader.Linux.Models;
using RLMapLoader.Linux.Platform;
using RLMapLoader.Linux.ViewModels;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = null!;
        private readonly DispatcherTimer _toastTimer = new();

        public event Action? HotkeyCaptureStarted;
        public event Action<Key, KeyModifiers>? HotkeyCaptured;
        public event Action? HotkeyCaptureCancelled;
        public event Action? OverlayRequested;
        public event Action? QuitRequested;

        /// <summary>True when the app is really quitting (so closing doesn't go to the tray).</summary>
        public bool AllowClose { get; set; }

        public MainWindow() => InitializeComponent(); // designer

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            vm.ToastRequested += (msg, kind) => { if (IsVisible) ShowToast(msg, kind); };

            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Classes.Remove("shown"); };

            Activated += async (_, _) => await vm.RefreshStatusAsync();
            AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
            AddHandler(DragDrop.DragOverEvent, Window_DragOver);
            AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropHint.IsVisible = false);
            AddHandler(DragDrop.DropEvent, Window_Drop);
            Gallery.DoubleTapped += async (_, e) =>
            {
                if (FindItem(e.Source as Visual) is { } item) await _vm.LoadAsync(item);
            };
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // Closing as part of quitting the app (log out / shut down included) must never be cancelled:
            // the session manager would report that RL Map Loader cancelled the logout.
            bool appQuitting = e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
            if (!AllowClose && !appQuitting)
            {
                e.Cancel = true;
                if (_vm.CloseToTray) Hide();
                else QuitRequested?.Invoke();
                return;
            }
            base.OnClosing(e);
        }

        private void OverlayButton_Click(object? sender, RoutedEventArgs e) => OverlayRequested?.Invoke();

        private void SettingsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => _vm.SettingsOpen = false;

        private static MapItem? FindItem(Visual? v)
        {
            while (v != null)
            {
                if (v is Control { DataContext: MapItem m }) return m;
                if (v is ListBox) return null;
                v = v.GetVisualParent();
            }
            return null;
        }

        // ------------------------------------------------------------ keyboard & hotkey capture
        private async void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_vm.CapturingHotkey)
            {
                e.Handled = true;
                if (e.Key == Key.Escape)
                {
                    _vm.CapturingHotkey = false;
                    HotkeyCaptureCancelled?.Invoke();
                    return;
                }
                if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                    or Key.LWin or Key.RWin or Key.None) return;
                _vm.CapturingHotkey = false;
                HotkeyCaptured?.Invoke(e.Key, e.KeyModifiers);
                return;
            }

            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _vm.SettingsOpen)
            {
                _vm.SettingsOpen = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Gallery.IsKeyboardFocusWithin && _vm.SelectedMap != null)
            {
                e.Handled = true;
                await _vm.LoadAsync(_vm.SelectedMap);
            }
        }

        private void HotkeyButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_vm.CapturingHotkey) return;
            _vm.CapturingHotkey = true;
            HotkeyCaptureStarted?.Invoke();
        }

        private void HotkeyButton_LostFocus(object? sender, RoutedEventArgs e)
        {
            if (!_vm.CapturingHotkey) return;
            _vm.CapturingHotkey = false;
            HotkeyCaptureCancelled?.Invoke();
        }

        // ------------------------------------------------------------ toast
        public void ShowToast(string message, ToastKind kind)
        {
            ToastText.Text = message;
            ToastIcon.Data = (Geometry)this.FindResource(kind switch
            {
                ToastKind.Success => "IconCheck",
                ToastKind.Error => "IconAlert",
                _ => "IconInfo",
            })!;
            ToastIcon.Foreground = (IBrush)this.FindResource(kind switch
            {
                ToastKind.Success => "Green",
                ToastKind.Error => "Orange",
                _ => "BlueLight",
            })!;
            Toast.Classes.Add("shown");
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 5 : 3.2);
            _toastTimer.Start();
        }

        // ------------------------------------------------------------ drag & drop
        private static List<string> DroppedFiles(DragEventArgs e) =>
            e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList() ?? new List<string>();

        private static bool IsImportable(string f) =>
            MapLibrary.IsMapFile(f) || string.Equals(Path.GetExtension(f), ".zip", StringComparison.OrdinalIgnoreCase);

        private void Window_DragOver(object? sender, DragEventArgs e)
        {
            var files = DroppedFiles(e);
            bool maps = files.Any(IsImportable);
            bool image = files.Count == 1 && MapLibrary.IsImageFile(files[0]);
            var target = FindItem(e.Source as Visual);
            if (maps)
            {
                DropHintText.Text = "Drop to add maps";
                DropHint.IsVisible = true;
                e.DragEffects = DragDropEffects.Copy;
            }
            else if (image && target != null)
            {
                DropHint.IsVisible = false;
                e.DragEffects = DragDropEffects.Copy;
            }
            else
            {
                DropHint.IsVisible = image;
                DropHintText.Text = "Drop the image on a map card";
                e.DragEffects = DragDropEffects.None;
            }
        }

        private async void Window_Drop(object? sender, DragEventArgs e)
        {
            DropHint.IsVisible = false;
            var files = DroppedFiles(e);
            if (files.Count == 1 && MapLibrary.IsImageFile(files[0]))
            {
                if (FindItem(e.Source as Visual) is { } item) _vm.SetImage(item, files[0]);
                return;
            }
            var importable = files.Where(IsImportable).ToList();
            if (importable.Count > 0) await _vm.ImportAsync(importable);
        }

        // ------------------------------------------------------------ pickers (used by the view model)
        public async Task<string?> PickFolderAsync(string title)
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return result.FirstOrDefault()?.TryGetLocalPath();
        }

        public async Task<string[]?> PickMapFilesAsync()
        {
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add maps",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Rocket League maps") { Patterns = new[] { "*.upk", "*.udk", "*.zip", "*.UPK", "*.UDK", "*.ZIP" } },
                    FilePickerFileTypes.All,
                },
            });
            return result.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
        }

        public async Task<string?> PickImageAsync(string title)
        {
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
            });
            return result.FirstOrDefault()?.TryGetLocalPath();
        }
    }
}
