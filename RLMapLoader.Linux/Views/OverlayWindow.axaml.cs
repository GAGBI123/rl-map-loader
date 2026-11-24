using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RLMapLoader.Linux.Models;
using RLMapLoader.Linux.ViewModels;

namespace RLMapLoader.Linux.Views
{
    /// <summary>Small always-on-top picker shown over the game with the hotkey (or "rl-map-loader --toggle-overlay").</summary>
    public partial class OverlayWindow : Window
    {
        private readonly MainViewModel _vm = null!;
        private int _suppressAutoHide;
        private bool _userMoved;

        public event Action? OpenMainRequested;

        public OverlayWindow() => InitializeComponent(); // designer

        public OverlayWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            AddHandler(KeyDownEvent, Overlay_KeyDown, RoutingStrategies.Tunnel);
            List.DoubleTapped += async (_, _) => await LoadSelectedAsync();
            Deactivated += (_, _) =>
            {
                if (_suppressAutoHide == 0 && !_vm.IsBusy && IsVisible) HideOverlay();
            };
        }

        public IDisposable SuppressAutoHide()
        {
            _suppressAutoHide++;
            return new Releaser(() => _suppressAutoHide--);
        }

        private sealed class Releaser : IDisposable
        {
            private Action? _a;
            public Releaser(Action a) => _a = a;
            public void Dispose() { _a?.Invoke(); _a = null; }
        }

        public void Toggle()
        {
            if (IsVisible) HideOverlay();
            else ShowOverlay();
        }

        public async void ShowOverlay()
        {
            _vm.OverlaySearchText = "";
            _vm.OverlaySelected = _vm.ActiveMap ?? _vm.OverlayMaps.FirstOrDefault();
            Flash.IsVisible = false;
            Place();
            Show();
            Activate();
            Topmost = false; // re-assert "keep above" for window managers that dropped it while hidden
            Topmost = true;
            Search.Focus();
            if (_vm.OverlaySelected != null) List.ScrollIntoView(_vm.OverlaySelected);
            await _vm.RefreshStatusAsync();
        }

        public void HideOverlay()
        {
            SavePosition();
            Hide();
        }

        private void Place()
        {
            var s = App.CurrentSettings;
            var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
            if (screen == null) return;
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            int w = (int)(Width * scale), h = (int)(Height * scale);
            if (s?.OverlayLeftPx is double lx && s.OverlayTopPx is double ty &&
                Screens.All.Any(sc => sc.WorkingArea.Contains(new PixelPoint((int)lx + 20, (int)ty + 20))))
            {
                Position = new PixelPoint((int)lx, (int)ty);
                return;
            }
            Position = new PixelPoint(area.Right - w - (int)(24 * scale), area.Y + Math.Max(0, (area.Height - h) / 2));
        }

        private void SavePosition()
        {
            if (!_userMoved || App.CurrentSettings is not { } s) return;
            _userMoved = false;
            s.OverlayLeftPx = Position.X;
            s.OverlayTopPx = Position.Y;
            s.Save();
        }

        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            BeginMoveDrag(e);
            _userMoved = true;
        }

        private async void Overlay_KeyDown(object? sender, KeyEventArgs e)
        {
            var items = _vm.OverlayMaps.ToList();
            int idx = _vm.OverlaySelected != null ? items.IndexOf(_vm.OverlaySelected) : -1;
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    HideOverlay();
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

        private async void LoadButton_Click(object? sender, RoutedEventArgs e) => await LoadSelectedAsync();

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
            Flash.IsVisible = true;
            await Task.Delay(1100);
            if (IsVisible) HideOverlay();
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => HideOverlay();

        private void OpenMain_Click(object? sender, RoutedEventArgs e)
        {
            HideOverlay();
            OpenMainRequested?.Invoke();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!App.IsShuttingDown)
            {
                e.Cancel = true;
                HideOverlay();
            }
            base.OnClosing(e);
        }
    }
}
