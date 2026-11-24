using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace RLMapLoader.Services
{
    /// <summary>System-wide hotkey via RegisterHotKey (works while the game has focus).</summary>
    public sealed class HotkeyManager : IDisposable
    {
        private const int Id = 0xB0F3;
        private readonly IntPtr _hwnd;
        private readonly HwndSource _source;
        private bool _registered;

        public event Action? Pressed;

        public HotkeyManager(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("No HwndSource for hotkey window");
            _source.AddHook(WndProc);
        }

        public bool Register(Key key, ModifierKeys modifiers)
        {
            Unregister();
            if (key == Key.None) return false;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            uint mods = Native.MOD_NOREPEAT;
            if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
            if (modifiers.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
            if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
            if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= Native.MOD_WIN;
            _registered = Native.RegisterHotKey(_hwnd, Id, mods, vk);
            if (!_registered) Log.Write($"RegisterHotKey failed for {Describe(key, modifiers)}");
            return _registered;
        }

        public void Unregister()
        {
            if (_registered) Native.UnregisterHotKey(_hwnd, Id);
            _registered = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
            {
                handled = true;
                Pressed?.Invoke();
            }
            return IntPtr.Zero;
        }

        public static string Describe(Key key, ModifierKeys mods)
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join(" + ", parts);
        }

        private static string KeyName(Key key) => key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            Key.Back => "Backspace",
            Key.Scroll => "Scroll Lock",
            Key.Pause => "Pause",
            _ => key.ToString()
        };

        public void Dispose()
        {
            Unregister();
            _source.RemoveHook(WndProc);
        }
    }
}
