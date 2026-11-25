using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RLMapLoader.Linux.Models;
using RLMapLoader.Linux.Platform;
using RLMapLoader.Linux.ViewModels;

namespace RLMapLoader.Linux.Views
{
    /// <summary>
    /// Small always-on-top picker shown over the game with the hotkey (or "rl-map-loader --toggle-overlay").
    /// Over the game it opens without taking focus: the window is override-redirect (never focused by the window
    /// manager, drawn above fullscreen windows) and keys come from an X keyboard grab, so Rocket League stays active.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private readonly MainViewModel _vm = null!;
        private readonly X11Hotkey? _x11;
        private int _suppressAutoHide;
        private bool _userMoved;
        private bool _keepFocusMode;     // shown without focus; keys arrive through the X keyboard grab
        private bool? _overrideRedirect; // what the X window is currently set to

        public event Action? OpenMainRequested;

        public OverlayWindow() => InitializeComponent(); // designer

        public OverlayWindow(MainViewModel vm, X11Hotkey? x11)
        {
            InitializeComponent();
            _vm = vm;
            _x11 = x11;
            DataContext = vm;
            AddHandler(KeyDownEvent, Overlay_KeyDown, RoutingStrategies.Tunnel);
            List.DoubleTapped += async (_, _) => await LoadSelectedAsync();
            Deactivated += (_, _) =>
            {
                if (!_keepFocusMode && _suppressAutoHide == 0 && !_vm.IsBusy && IsVisible) HideOverlay();
            };
        }

        /// <summary>True while shown over the game without focus (dialogs shouldn't use it as their owner then).</summary>
        public bool IsKeepingGameFocus => IsVisible && _keepFocusMode;

        public IntPtr XWindow => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        public IDisposable SuppressAutoHide()
        {
            _suppressAutoHide++;
            return new Releaser(() => _suppressAutoHide--);
        }

        /// <summary>Dialogs need the keyboard: release the grab while one is open, take it back afterwards.</summary>
        public IDisposable PauseKeyboardGrab()
        {
            if (!_keepFocusMode || _x11 == null) return new Releaser(() => { });
            _x11.EndGrab();
            return new Releaser(() =>
            {
                if (IsVisible && _keepFocusMode) _x11.TryGrabKeyboard();
            });
        }

        private sealed class Releaser : IDisposable
        {
            private Action? _a;
            public Releaser(Action a) => _a = a;
            public void Dispose() { _a?.Invoke(); _a = null; }
        }

        /// <param name="keepGameFocus">Open without taking focus from the focused (X11) window - the game.</param>
        public void Toggle(bool keepGameFocus)
        {
            if (IsVisible) HideOverlay();
            else ShowOverlay(keepGameFocus);
        }

        public async void ShowOverlay(bool keepGameFocus = false)
        {
            if (IsVisible)
            {
                if (!_keepFocusMode) Activate();
                return;
            }
            _vm.OverlaySearchText = "";
            _vm.OverlaySelected = _vm.ActiveMap ?? _vm.OverlayMaps.FirstOrDefault();
            Flash.IsVisible = false;

            _keepFocusMode = keepGameFocus && _x11 != null && _x11.TryGrabKeyboard();
            SetOverrideRedirect(_keepFocusMode); // only possible while unmapped, so before Show()
            Place();
            Show();
            if (!_keepFocusMode)
            {
                Activate();
                Topmost = false; // re-assert "keep above" for window managers that dropped it while hidden
                Topmost = true;
                Search.Focus();
            }
            if (_vm.OverlaySelected != null) List.ScrollIntoView(_vm.OverlaySelected);
            await _vm.RefreshStatusAsync();
        }

        private void SetOverrideRedirect(bool on)
        {
            if (_x11 == null || _overrideRedirect == on) return;
            if (_x11.SetOverrideRedirect(XWindow, on)) _overrideRedirect = on;
        }

        public void HideOverlay()
        {
            _x11?.EndGrab();
            _dragging = false;
            SavePosition();
            Hide();
            _keepFocusMode = false;
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

        // ------------------------------------------------------------ moving the window
        // Window managers don't move override-redirect windows, so in keep-focus mode the header drag is done here.
        private bool _dragging;
        private PixelPoint _dragStartWindow, _dragStartPointer;

        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _userMoved = true;
            if (!_keepFocusMode)
            {
                BeginMoveDrag(e);
                return;
            }
            if (_x11?.GetPointer() is not { } p) return;
            _dragging = true;
            _dragStartWindow = Position;
            _dragStartPointer = new PixelPoint(p.X, p.Y);
            e.Pointer.Capture(sender as IInputElement);
            e.Handled = true;
        }

        private void Header_PointerMoved(object? sender, PointerEventArgs e)
        {
            // Screen coordinates straight from X: the window's own Position lags behind while it moves.
            if (!_dragging || _x11?.GetPointer() is not { } p) return;
            Position = new PixelPoint(_dragStartWindow.X + p.X - _dragStartPointer.X, _dragStartWindow.Y + p.Y - _dragStartPointer.Y);
        }

        private void Header_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
        }

        // ------------------------------------------------------------ input
        /// <summary>Keys typed while the overlay holds the keyboard grab (it has no focus then).</summary>
        public async void HandleGrabbedKey(GrabbedKey key)
        {
            if (!IsVisible) return;
            switch (key.Kind)
            {
                case GrabKeyKind.Close: HideOverlay(); break;
                case GrabKeyKind.Text: _vm.OverlaySearchText += key.Text; break;
                case GrabKeyKind.Backspace:
                    if (_vm.OverlaySearchText.Length > 0) _vm.OverlaySearchText = _vm.OverlaySearchText[..^1];
                    break;
                case GrabKeyKind.ClearText: _vm.OverlaySearchText = ""; break;
                case GrabKeyKind.Up: MoveSelection(-1); break;
                case GrabKeyKind.Down: MoveSelection(1); break;
                case GrabKeyKind.PageUp: MoveSelection(-5); break;
                case GrabKeyKind.PageDown: MoveSelection(5); break;
                case GrabKeyKind.Home: MoveSelection(int.MinValue / 2); break;
                case GrabKeyKind.End: MoveSelection(int.MaxValue / 2); break;
                case GrabKeyKind.Enter: await LoadSelectedAsync(); break;
            }
        }

        private async void Overlay_KeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    HideOverlay();
                    break;
                case Key.Down: e.Handled = true; MoveSelection(1); break;
                case Key.Up: e.Handled = true; MoveSelection(-1); break;
                case Key.PageDown: e.Handled = true; MoveSelection(5); break;
                case Key.PageUp: e.Handled = true; MoveSelection(-5); break;
                case Key.Enter:
                    e.Handled = true;
                    await LoadSelectedAsync();
                    break;
            }
        }

        private void MoveSelection(int delta)
        {
            var items = _vm.OverlayMaps.ToList();
            if (items.Count == 0) return;
            int idx = _vm.OverlaySelected != null ? items.IndexOf(_vm.OverlaySelected) : -1;
            long target = Math.Clamp((long)idx + delta, 0, items.Count - 1);
            var item = items[(int)target];
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
