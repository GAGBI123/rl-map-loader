using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace RLMapLoader.Linux.Platform
{
    /// <summary>Hotkey names (shared settings format with the Windows app) and X11 keysyms.</summary>
    public static class KeyNames
    {
        /// <summary>Settings store WPF-style names ("F3", "D1", "NumPad1", "Oem3"); map the ones Avalonia names differently.</summary>
        public static Key Parse(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Key.F3;
            if (name == "Oem3") return Key.OemTilde;
            return Enum.TryParse<Key>(name, out var k) ? k : Key.F3;
        }

        public static string Describe(Key key, KeyModifiers mods)
        {
            var parts = new List<string>();
            if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Super");
            parts.Add(key switch
            {
                >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
                Key.OemTilde => "`",
                Key.PageUp => "PgUp",
                Key.PageDown => "PgDn",
                Key.Scroll => "Scroll Lock",
                _ => key.ToString()
            });
            return string.Join(" + ", parts);
        }

        public static ulong ToX11Keysym(Key key)
        {
            if (key >= Key.F1 && key <= Key.F24) return 0xFFBEul + (ulong)(key - Key.F1);
            if (key >= Key.A && key <= Key.Z) return 0x61ul + (ulong)(key - Key.A);
            if (key >= Key.D0 && key <= Key.D9) return 0x30ul + (ulong)(key - Key.D0);
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return 0xFFB0ul + (ulong)(key - Key.NumPad0);
            return key switch
            {
                Key.Insert => 0xFF63,
                Key.Home => 0xFF50,
                Key.End => 0xFF57,
                Key.PageUp => 0xFF55,
                Key.PageDown => 0xFF56,
                Key.Pause => 0xFF13,
                Key.Scroll => 0xFF14,
                Key.OemTilde => 0x60,
                Key.OemMinus => 0x2D,
                Key.OemPlus => 0x3D,
                Key.Delete => 0xFFFF,
                _ => 0
            };
        }
    }
}
