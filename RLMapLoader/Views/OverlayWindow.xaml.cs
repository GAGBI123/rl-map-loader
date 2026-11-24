using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using RLMapLoader.Models;
using RLMapLoader.Services;
using RLMapLoader.ViewModels;

namespace RLMapLoader.Views
{
    /// <summary>Small always-on-top picker shown over the game with the global hotkey.</summary>
    public partial class OverlayWindow : Window
    {
        private readonly MainViewModel _vm;
        private IntPtr _previousForeground;
        private bool _suppressAutoHide;

        public event Action? OpenMainRequested;

        public OverlayWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            PreviewKeyDown += Overlay_PreviewKeyDown;
            Deactivated += (_, _) =>
            {
                if (!_suppressAutoHide && !_vm.IsBusy && IsVisible) HideOverlay(restoreFocus: false);
            };
            vm.PropertyChanged += Vm_PropertyChanged;
        }

        public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

        /// <summary>Dialogs opened from the overlay must not make it auto-hide.</summary>
        public IDisposable SuppressAutoHide()
        {
            _suppressAutoHide = true;
            return new Releaser(() => _suppressAutoHide = false);
        }

        private sealed class Releaser : IDisposable
        {
            private readonly Action _a;
            public Releaser(Action a) => _a = a;
            public void Dispose() => _a();
        }

        public void Toggle()
        {
            if (IsVisible) HideOverlay(restoreFocus: true);
            else ShowOverlay();
        }

        public async void ShowOverlay()
        {
            var fg = Native.GetForegroundWindow();
            var mine = new WindowInteropHelper(this).Handle;
            if (fg != mine) _previousForeground = fg;

            _vm.OverlaySearchText = "";
            _vm.OverlaySelected = _vm.ActiveMap ?? _vm.OverlayView.Cast<MapItem>().FirstOrDefault();
            Flash.Visibility = Visibility.Collapsed;

            Opacity = 0;
            Show();
            PlaceOnGameMonitor();
            Native.ForceForeground(Handle);
            Activate();
            Search.Focus();
            Keyboard.Focus(Search);
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            if (_vm.OverlaySelected != null) List.ScrollIntoView(_vm.OverlaySelected);
            await _vm.RefreshStatusAsync();
        }

        public void HideOverlay(bool restoreFocus)
        {
            SaveUserPosition();
            Hide();
            if (restoreFocus && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground))
                Native.ForceForeground(_previousForeground);
        }

        // ------------------------------------------------------------ placement (in physical pixels)
        private bool _userMoved;

        private void PlaceOnGameMonitor()
        {
            var hwnd = Handle;
            var anchor = _previousForeground != IntPtr.Zero ? _previousForeground : hwnd;
            var mon = Native.MonitorFromWindow(anchor, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(mon, ref info)) return;
            double scale = Native.MonitorScale(mon);
            int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
            var work = info.rcWork;

            int x, y;
            var s = App.CurrentSettings;
            if (s?.OverlayLeftPx is double lx && s.OverlayTopPx is double ty &&
                lx >= work.Left - 40 && lx + w <= work.Right + 40 && ty >= work.Top - 40 && ty + h <= work.Bottom + 40)
            {
                x = (int)lx;
                y = (int)ty;
            }
            else
            {
                x = work.Right - w - (int)(24 * scale);
                y = work.Top + Math.Max(0, (work.Bottom - work.Top - h) / 2);
            }
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
        }

        private void SaveUserPosition()
        {
            if (!_userMoved || App.CurrentSettings is not { } s) return;
            _userMoved = false;
            var src = PresentationSource.FromVisual(this);
            double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            s.OverlayLeftPx = Math.Round(Left * scale);
            s.OverlayTopPx = Math.Round(Top * scale);
            s.Save();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            _userMoved = true;
        }

        // ------------------------------------------------------------ input
        private async void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var items = _vm.OverlayView.Cast<MapItem>().ToList();
            int idx = _vm.OverlaySelected != null ? items.IndexOf(_vm.OverlaySelected) : -1;
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    HideOverlay(restoreFocus: true);
                    break;
                case Key.Down when items.Count > 0:
                    e.Handled = true;
                    Select(items[Math.Min(items.Count - 1, idx + 1)]);
                    break;
                case Key.Up when items.Count > 0:
                    e.Handled = true;
                    Select(items[Math.Max(0, idx - 1)]);
                    break;
                case Key.PageDown when items.Count > 0:
                    e.Handled = true;
                    Select(items[Math.Min(items.Count - 1, idx + 5)]);
                    break;
                case Key.PageUp when items.Count > 0:
                    e.Handled = true;
                    Select(items[Math.Max(0, idx - 5)]);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    await LoadSelectedAsync();
                    break;
            }
        }

        private void Select(MapItem item)
        {
            _vm.OverlaySelected = item;
            List.ScrollIntoView(item);
        }

        private async void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) await LoadSelectedAsync();
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e) => await LoadSelectedAsync();

        private async Task LoadSelectedAsync()
        {
            var item = _vm.OverlaySelected;
            if (item == null || _vm.IsBusy) return;
            bool ok;
            using (SuppressAutoHide())
            {
                ok = await _vm.LoadAsync(item);
            }
            if (!ok || !IsVisible) return;
            FlashText.Text = $"✓ {item.DisplayName} is ready – start Free Play on Underpass";
            Flash.Visibility = Visibility.Visible;
            Flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            await Task.Delay(1100);
            if (IsVisible) HideOverlay(restoreFocus: true);
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy)) LoadButton.IsEnabled = !_vm.IsBusy;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => HideOverlay(restoreFocus: true);

        private void OpenMain_Click(object sender, RoutedEventArgs e)
        {
            HideOverlay(restoreFocus: false);
            OpenMainRequested?.Invoke();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // never destroy the overlay; just hide it
            if (!App.IsShuttingDown)
            {
                e.Cancel = true;
                HideOverlay(restoreFocus: true);
            }
            base.OnClosing(e);
        }
    }
}
