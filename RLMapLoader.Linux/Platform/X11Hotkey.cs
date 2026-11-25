using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia.Input;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.Platform
{
    public enum GrabKeyKind { Text, Up, Down, PageUp, PageDown, Home, End, Enter, Backspace, ClearText, Close }

    /// <summary>A key typed while the overlay holds the keyboard grab.</summary>
    public readonly record struct GrabbedKey(GrabKeyKind Kind, string? Text = null);

    public readonly record struct PixelPointLite(int X, int Y);

    /// <summary>
    /// X11 keyboard access for the overlay.
    /// <list type="bullet">
    /// <item>The global hotkey: XGrabKey on the root window.</item>
    /// <item>While the overlay is open: an active keyboard grab (XGrabKeyboard), so typing reaches the overlay while the
    /// game keeps focus. Wine ignores focus events caused by keyboard grabs, so the game is not deactivated or
    /// minimized ("tabbed out"); it only stops getting keys until the grab ends.</item>
    /// </list>
    /// Works on X11 sessions, and on Wayland while an X11/XWayland window has focus - which is the case for
    /// Rocket League running under Proton/Wine.
    /// </summary>
    public sealed class X11Hotkey : IDisposable
    {
        private const string LibX11 = "libX11.so.6";
        private const int KeyPressType = 2, KeyReleaseType = 3;
        private const int GrabModeAsync = 1, GrabSuccess = 0, AlreadyGrabbed = 1, GrabFrozen = 4;
        private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
        private const uint ModifierMask = ShiftMask | ControlMask | Mod1Mask | Mod4Mask;
        private const nuint CWOverrideRedirect = 1 << 9;
        private const int XSetWindowAttributesSize = 112, OverrideRedirectOffset = 88; // LP64 layout
        private static readonly IntPtr XA_WINDOW = 33;

        // Hotkey presses closer together than this are key auto-repeat (or a bounce), not a new press.
        // Longer than the usual auto-repeat delay (KDE 600 ms, X default 660 ms).
        private const long HotkeyRepeatMs = 700;
        // Safety net: never keep the keyboard grabbed forever if the overlay is left open untouched.
        private const long GrabIdleLimitMs = 120_000;

        [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
        [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
        [DllImport(LibX11)] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow,
                                                              int ownerEvents, int pointerMode, int keyboardMode);
        [DllImport(LibX11)] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
        [DllImport(LibX11)] private static extern int XGrabKeyboard(IntPtr display, IntPtr grabWindow, int ownerEvents,
                                                                   int pointerMode, int keyboardMode, IntPtr time);
        [DllImport(LibX11)] private static extern int XUngrabKeyboard(IntPtr display, IntPtr time);
        [DllImport(LibX11)] private static extern int XLookupString(IntPtr keyEvent, byte[] buffer, int bytes, out nuint keysym, IntPtr status);
        [DllImport(LibX11)] private static extern int XkbSetDetectableAutoRepeat(IntPtr display, int detectable, out int supported);
        [DllImport(LibX11)] private static extern IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);
        [DllImport(LibX11)] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr longOffset,
                                                                        IntPtr longLength, int delete, IntPtr reqType, out IntPtr actualType,
                                                                        out int actualFormat, out IntPtr nitems, out IntPtr bytesAfter, out IntPtr prop);
        [DllImport(LibX11)] private static extern int XChangeWindowAttributes(IntPtr display, IntPtr window, nuint valueMask, IntPtr attributes);
        [DllImport(LibX11)] private static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
                                                                   out int rootX, out int rootY, out int winX, out int winY, out uint mask);
        [DllImport(LibX11)] private static extern int XFree(IntPtr data);
        [DllImport(LibX11)] private static extern int XPending(IntPtr display);
        [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
        [DllImport(LibX11)] private static extern int XSync(IntPtr display, int discard);
        [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
        [DllImport(LibX11)] private static extern IntPtr XSetErrorHandler(IntPtr handler);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

        private static readonly XErrorHandler ErrorHandlerDelegate = OnXError;
        private static volatile bool _xError;

        private static readonly uint[] LockVariants = { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask }; // ignore Caps/Num Lock

        private readonly object _gate = new();
        private IntPtr _display;
        private IntPtr _root;
        private bool _libMissing;
        private int _keycode;
        private uint _mods;
        private Thread? _thread;
        private volatile bool _running;
        private long _lastHotkeyEvent = -HotkeyRepeatMs;
        private volatile bool _grabbing;
        private long _lastGrabActivity;

        /// <summary>The hotkey was pressed while the keyboard isn't grabbed. Raised on a background thread.</summary>
        public event Action? Pressed;

        /// <summary>A key was typed during the keyboard grab. Raised on a background thread. For <see cref="GrabKeyKind.Close"/>
        /// (Esc, the hotkey, or the idle limit) the grab has already been released.</summary>
        public event Action<GrabbedKey>? GrabbedKey;

        public static bool Available =>
            OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        public bool IsGrabbing => _grabbing;

        // ------------------------------------------------------------ connection
        private bool EnsureOpenLocked()
        {
            if (_display != IntPtr.Zero) return true;
            if (!Available || _libMissing) return false;
            try
            {
                _display = XOpenDisplay(IntPtr.Zero);
            }
            catch (DllNotFoundException)
            {
                _libMissing = true;
                Log.Write("libX11 not found: global hotkey unavailable");
                return false;
            }
            if (_display == IntPtr.Zero) return false;
            _root = XDefaultRootWindow(_display);
            // Auto-repeat then arrives as repeated key presses without fake releases in between.
            try { XkbSetDetectableAutoRepeat(_display, 1, out _); } catch (EntryPointNotFoundException) { }

            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "x11-input" };
            _thread.Start();
            return true;
        }

        private static int OnXError(IntPtr display, IntPtr errorEvent)
        {
            _xError = true; // BadAccess from XGrabKey (key taken), BadWindow, ...
            return 0;
        }

        /// <summary>Runs X requests with our error handler, then restores the one the UI toolkit installed.</summary>
        private bool TryRequestsLocked(Action requests)
        {
            _xError = false;
            var previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(ErrorHandlerDelegate));
            try
            {
                requests();
                XSync(_display, 0);
            }
            finally
            {
                XSetErrorHandler(previous);
            }
            return !_xError;
        }

        // ------------------------------------------------------------ hotkey
        /// <summary>Grabs the key. False when there's no X display or another app already owns that key.</summary>
        public bool Register(Key key, KeyModifiers modifiers)
        {
            Unregister();
            var keysym = KeyNames.ToX11Keysym(key);
            if (keysym == 0) return false;
            try
            {
                lock (_gate)
                {
                    if (!EnsureOpenLocked()) return false;
                    int keycode = XKeysymToKeycode(_display, (nuint)keysym);
                    if (keycode == 0) return false;

                    uint mods = 0;
                    if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= ShiftMask;
                    if (modifiers.HasFlag(KeyModifiers.Control)) mods |= ControlMask;
                    if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= Mod1Mask;
                    if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= Mod4Mask;

                    bool ok = TryRequestsLocked(() =>
                    {
                        foreach (var extra in LockVariants)
                            XGrabKey(_display, keycode, mods | extra, _root, 0, GrabModeAsync, GrabModeAsync);
                    });
                    if (!ok)
                    {
                        foreach (var extra in LockVariants) XUngrabKey(_display, keycode, mods | extra, _root);
                        XFlush(_display);
                        Log.Write($"Hotkey {key} is already grabbed by another program");
                        return false;
                    }
                    _keycode = keycode;
                    _mods = mods;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Hotkey registration failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Releases the hotkey (the X connection stays open for the overlay's keyboard grab).</summary>
        public void Unregister()
        {
            lock (_gate)
            {
                if (_display != IntPtr.Zero && _keycode != 0)
                {
                    foreach (var extra in LockVariants) XUngrabKey(_display, _keycode, _mods | extra, _root);
                    XFlush(_display);
                }
                _keycode = 0;
                _mods = 0;
            }
        }

        // ------------------------------------------------------------ keyboard grab
        /// <summary>
        /// Sends all keyboard input to us until <see cref="EndGrab"/>, without moving the input focus.
        /// Only useful while an X11 window has focus (on Wayland: an XWayland window such as the game).
        /// </summary>
        public bool TryGrabKeyboard()
        {
            int result = -1;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                lock (_gate)
                {
                    if (_grabbing) return true;
                    if (!EnsureOpenLocked()) return false;
                    result = XGrabKeyboard(_display, _root, 0, GrabModeAsync, GrabModeAsync, IntPtr.Zero);
                    if (result == GrabSuccess)
                    {
                        _grabbing = true;
                        _lastGrabActivity = Environment.TickCount64;
                        XFlush(_display);
                        return true;
                    }
                }
                if (result != AlreadyGrabbed && result != GrabFrozen) break;
                Thread.Sleep(25); // e.g. another program's shortcut key is still held down
            }
            Log.Write($"Keyboard grab failed (status {result}); the overlay will take focus instead");
            return false;
        }

        public void EndGrab()
        {
            lock (_gate) EndGrabLocked();
        }

        private void EndGrabLocked()
        {
            if (!_grabbing) return;
            _grabbing = false;
            if (_display == IntPtr.Zero) return;
            XUngrabKeyboard(_display, IntPtr.Zero);
            XFlush(_display);
        }

        // ------------------------------------------------------------ window helpers
        /// <summary>The window in root's _NET_ACTIVE_WINDOW, or zero. On KDE Wayland this is zero while a native
        /// Wayland window is active, and the X window id while an XWayland window (the game) is active.</summary>
        public IntPtr GetActiveWindow()
        {
            lock (_gate)
            {
                if (!EnsureOpenLocked()) return IntPtr.Zero;
                var atom = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 1);
                if (atom == IntPtr.Zero) return IntPtr.Zero;
                if (XGetWindowProperty(_display, _root, atom, IntPtr.Zero, 1, 0, XA_WINDOW, out _, out int format,
                        out var count, out _, out var prop) != 0)
                    return IntPtr.Zero;
                try
                {
                    return format == 32 && count != IntPtr.Zero && prop != IntPtr.Zero ? Marshal.ReadIntPtr(prop) : IntPtr.Zero;
                }
                finally
                {
                    if (prop != IntPtr.Zero) XFree(prop);
                }
            }
        }

        /// <summary>Mouse position on the screen (physical pixels), or null.</summary>
        public PixelPointLite? GetPointer()
        {
            lock (_gate)
            {
                if (!EnsureOpenLocked()) return null;
                return XQueryPointer(_display, _root, out _, out _, out int x, out int y, out _, out _, out _) != 0
                    ? new PixelPointLite(x, y)
                    : null;
            }
        }

        /// <summary>
        /// Marks an unmapped window override-redirect (or back to normal). Such windows are never managed or focused
        /// by the window manager and are drawn above everything, fullscreen games included - like tooltips and menus.
        /// </summary>
        public bool SetOverrideRedirect(IntPtr window, bool on)
        {
            if (window == IntPtr.Zero) return false;
            lock (_gate)
            {
                if (!EnsureOpenLocked()) return false;
                var attrs = Marshal.AllocHGlobal(XSetWindowAttributesSize);
                try
                {
                    for (int i = 0; i < XSetWindowAttributesSize; i += 8) Marshal.WriteInt64(attrs, i, 0);
                    Marshal.WriteInt32(attrs, OverrideRedirectOffset, on ? 1 : 0);
                    return TryRequestsLocked(() => XChangeWindowAttributes(_display, window, CWOverrideRedirect, attrs));
                }
                finally
                {
                    Marshal.FreeHGlobal(attrs);
                }
            }
        }

        // ------------------------------------------------------------ event loop
        private void Loop()
        {
            var ev = Marshal.AllocHGlobal(256); // XEvent is 192 bytes on 64-bit
            var text = new byte[32];
            var fire = new List<Action>();
            try
            {
                while (_running)
                {
                    lock (_gate)
                    {
                        if (_display == IntPtr.Zero) break;
                        while (XPending(_display) > 0)
                        {
                            XNextEvent(_display, ev);
                            HandleEventLocked(ev, text, fire);
                        }
                        if (_grabbing && Environment.TickCount64 - _lastGrabActivity > GrabIdleLimitMs)
                        {
                            EndGrabLocked();
                            Log.Write("Overlay idle: keyboard grab released");
                            fire.Add(() => GrabbedKey?.Invoke(new GrabbedKey(GrabKeyKind.Close)));
                        }
                    }
                    foreach (var action in fire)
                    {
                        try { action(); }
                        catch (Exception ex) { Log.Write("Key handler failed: " + ex.Message); }
                    }
                    fire.Clear();
                    Thread.Sleep(_grabbing ? 8 : 15);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ev);
            }
        }

        // XKeyEvent (LP64): type@0 ... state@80 keycode@84
        private void HandleEventLocked(IntPtr ev, byte[] text, List<Action> fire)
        {
            int type = Marshal.ReadInt32(ev);
            if (type != KeyPressType) return; // releases (and anything else) don't matter
            uint state = (uint)Marshal.ReadInt32(ev, 80);
            int keycode = Marshal.ReadInt32(ev, 84);

            if (_keycode != 0 && keycode == _keycode && (state & ModifierMask) == _mods)
            {
                long now = Environment.TickCount64;
                bool repeat = now - _lastHotkeyEvent < HotkeyRepeatMs;
                _lastHotkeyEvent = now;
                if (repeat) return;
                if (_grabbing)
                {
                    EndGrabLocked();
                    fire.Add(() => GrabbedKey?.Invoke(new GrabbedKey(GrabKeyKind.Close)));
                }
                else
                {
                    fire.Add(() => Pressed?.Invoke());
                }
                return;
            }

            if (!_grabbing) return;
            _lastGrabActivity = Environment.TickCount64;
            if (Decode(ev, state, text) is not { } key) return;
            if (key.Kind == GrabKeyKind.Close) EndGrabLocked(); // release at once, even if the UI is busy
            fire.Add(() => GrabbedKey?.Invoke(key));
        }

        private static GrabbedKey? Decode(IntPtr ev, uint state, byte[] buffer)
        {
            int n = XLookupString(ev, buffer, buffer.Length, out var keysym, IntPtr.Zero);
            bool ctrl = (state & ControlMask) != 0, alt = (state & Mod1Mask) != 0;
            switch ((ulong)keysym)
            {
                case 0xff1b: return new GrabbedKey(GrabKeyKind.Close);                      // Escape
                case 0xff0d: case 0xff8d: return new GrabbedKey(GrabKeyKind.Enter);         // Return, KP_Enter
                case 0xff52: case 0xff97: return new GrabbedKey(GrabKeyKind.Up);
                case 0xff54: case 0xff99: return new GrabbedKey(GrabKeyKind.Down);
                case 0xff55: case 0xff9a: return new GrabbedKey(GrabKeyKind.PageUp);
                case 0xff56: case 0xff9b: return new GrabbedKey(GrabKeyKind.PageDown);
                case 0xff50: case 0xff95: return new GrabbedKey(GrabKeyKind.Home);
                case 0xff57: case 0xff9c: return new GrabbedKey(GrabKeyKind.End);
                case 0xff08: return new GrabbedKey(ctrl ? GrabKeyKind.ClearText : GrabKeyKind.Backspace);
                case 0x75 or 0x55 when ctrl: return new GrabbedKey(GrabKeyKind.ClearText); // Ctrl+U
            }
            if (n <= 0 || ctrl || alt) return null;
            var s = Encoding.Latin1.GetString(buffer, 0, Math.Min(n, buffer.Length));
            foreach (var c in s)
                if (char.IsControl(c)) return null;
            return new GrabbedKey(GrabKeyKind.Text, s);
        }

        // ------------------------------------------------------------ cleanup
        public void Dispose()
        {
            EndGrab();
            Unregister();
            _running = false;
            _thread?.Join(500);
            _thread = null;
            lock (_gate)
            {
                if (_display != IntPtr.Zero)
                {
                    try { XCloseDisplay(_display); } catch { }
                }
                _display = IntPtr.Zero;
            }
        }
    }
}
