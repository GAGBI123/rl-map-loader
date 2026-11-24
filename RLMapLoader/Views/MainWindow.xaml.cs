using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RLMapLoader.Models;
using RLMapLoader.Services;
using RLMapLoader.ViewModels;

namespace RLMapLoader.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly DispatcherTimer _toastTimer;

        public event Action? HotkeyCaptureStarted;
        public event Action<Key, ModifierKeys>? HotkeyCaptured;
        public event Action? HotkeyCaptureCancelled;
        public event Action? OverlayRequested;
        public event Action? ExitRequested;

        /// <summary>Set by the app when it is really shutting down (so Close doesn't go to the tray).</summary>
        public bool AllowClose { get; set; }

        public MainWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); AnimateToast(false); };

            vm.ToastRequested += (msg, kind) => { if (IsVisible) ShowToast(msg, kind); };
            vm.PropertyChanged += Vm_PropertyChanged;

            StateChanged += (_, _) =>
            {
                Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            };
            Activated += async (_, _) => await vm.RefreshStatusAsync();
            PreviewKeyDown += Window_PreviewKeyDown;
            DragEnter += Window_DragEnter;
            DragOver += Window_DragOver;
            DragLeave += (_, _) => DropHint.Visibility = Visibility.Collapsed;
            Drop += Window_Drop;
        }

        // ------------------------------------------------------------ chrome
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void OverlayButton_Click(object sender, RoutedEventArgs e) => OverlayRequested?.Invoke();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                if (_vm.CloseToTray) Hide();
                else ExitRequested?.Invoke();
                return;
            }
            base.OnClosing(e);
        }

        // ------------------------------------------------------------ gallery
        private async void Gallery_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindItem(e.OriginalSource as DependencyObject) is { } item && e.ChangedButton == MouseButton.Left)
                await _vm.LoadAsync(item);
        }

        private async void Gallery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _vm.SelectedMap != null)
            {
                e.Handled = true;
                await _vm.LoadAsync(_vm.SelectedMap);
            }
        }

        private static MapItem? FindItem(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is FrameworkElement { DataContext: MapItem m }) return m;
                if (d is ListBox) return null;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // ------------------------------------------------------------ keyboard / hotkey capture
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_vm.CapturingHotkey)
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape)
                {
                    _vm.CapturingHotkey = false;
                    HotkeyCaptureCancelled?.Invoke();
                    return;
                }
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                    or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
                _vm.CapturingHotkey = false;
                HotkeyCaptured?.Invoke(key, Keyboard.Modifiers);
                return;
            }

            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
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
        }

        private void HotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.CapturingHotkey) return;
            _vm.CapturingHotkey = true;
            HotkeyCaptureStarted?.Invoke();
        }

        private void HotkeyButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_vm.CapturingHotkey) return;
            _vm.CapturingHotkey = false;
            HotkeyCaptureCancelled?.Invoke();
        }

        // ------------------------------------------------------------ settings drawer
        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SettingsOpen)) AnimateSettings(_vm.SettingsOpen);
        }

        private void SettingsBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => _vm.SettingsOpen = false;

        private void AnimateSettings(bool open)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(open ? 240 : 180);
            if (open) SettingsLayer.Visibility = Visibility.Visible;
            var slide = new DoubleAnimation(open ? 0 : 480, dur) { EasingFunction = ease };
            var fade = new DoubleAnimation(open ? 1 : 0, dur);
            if (!open) slide.Completed += (_, _) => { if (!_vm.SettingsOpen) SettingsLayer.Visibility = Visibility.Collapsed; };
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
            SettingsBackdrop.BeginAnimation(OpacityProperty, fade);
        }

        // ------------------------------------------------------------ toast
        public void ShowToast(string message, ToastKind kind)
        {
            ToastText.Text = message;
            (ToastIcon.Text, ToastIcon.Foreground) = kind switch
            {
                ToastKind.Success => ("", (Brush)FindResource("Green")),
                ToastKind.Error => ("", (Brush)FindResource("Orange")),
                _ => ("", (Brush)FindResource("BlueLight")),
            };
            AnimateToast(true);
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 5 : 3.2);
            _toastTimer.Start();
        }

        private void AnimateToast(bool show)
        {
            var dur = TimeSpan.FromMilliseconds(show ? 200 : 260);
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, dur));
            ToastShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(show ? 0 : 20, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // ------------------------------------------------------------ drag & drop
        private static string[] DroppedFiles(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) ? (string[])e.Data.GetData(DataFormats.FileDrop) : Array.Empty<string>();

        private static bool IsImportable(string f) =>
            MapLibrary.IsMapFile(f) || string.Equals(Path.GetExtension(f), ".zip", StringComparison.OrdinalIgnoreCase);

        private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDrag(e);
        private void Window_DragOver(object sender, DragEventArgs e) => UpdateDrag(e);

        private void UpdateDrag(DragEventArgs e)
        {
            var files = DroppedFiles(e);
            bool maps = files.Any(IsImportable);
            bool image = files.Length == 1 && MapLibrary.IsImageFile(files[0]);
            var target = FindItem(e.OriginalSource as DependencyObject);
            if (maps)
            {
                DropHintText.Text = "Drop to add maps";
                DropHint.Visibility = Visibility.Visible;
                e.Effects = DragDropEffects.Copy;
            }
            else if (image && target != null)
            {
                DropHint.Visibility = Visibility.Collapsed;
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                DropHint.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
                DropHintText.Text = "Drop the image on a map card";
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            var files = DroppedFiles(e);
            if (files.Length == 1 && MapLibrary.IsImageFile(files[0]))
            {
                if (FindItem(e.OriginalSource as DependencyObject) is { } item) _vm.SetImage(item, files[0]);
                return;
            }
            var importable = files.Where(IsImportable).ToList();
            if (importable.Count > 0) await _vm.ImportAsync(importable);
        }
    }
}
