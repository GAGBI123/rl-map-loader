using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Input;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.Platform
{
    /// <summary>
    /// Global hotkey through X11 (XGrabKey on the root window).
    /// Works on X11 sessions, and on Wayland while an X11/XWayland window has focus -
    /// which is the case for Rocket League running under Proton/Wine.
    /// </summary>
    public sealed class X11Hotkey : IDisposable
    {
        private const string LibX11 = "libX11.so.6";
        private const int KeyPress = 2;
        private const int GrabModeAsync = 1;
        private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;

        [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
        [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(LibX11)] private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
        [DllImport(LibX11)] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow,
                                                              int ownerEvents, int pointerMode, int keyboardMode);
        [DllImport(LibX11)] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
        [DllImport(LibX11)] private static extern int XPending(IntPtr display);
        [DllImport(LibX11)] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
        [DllImport(LibX11)] private static extern int XSync(IntPtr display, int discard);
        [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
        [DllImport(LibX11)] private static extern IntPtr XSetErrorHandler(IntPtr handler);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

        private static readonly XErrorHandler ErrorHandlerDelegate = OnXError;
        private static volatile bool _grabFailed;

        private readonly object _gate = new();
        private IntPtr _display;
        private IntPtr _root;
        private int _keycode;
        private uint _mods;
        private Thread? _thread;
        private volatile bool _running;
        private long _lastPressTicks;

        public event Action? Pressed;

        public static bool Available =>
            OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        /// <summary>Grabs the key. False when there's no X display or another app already owns that key.</summary>
        public bool Register(Key key, KeyModifiers modifiers)
        {
            Unregister();
            if (!Available) return false;
            var keysym = KeyNames.ToX11Keysym(key);
            if (keysym == 0) return false;
            try
            {
                lock (_gate)
                {
                    _display = XOpenDisplay(IntPtr.Zero);
                    if (_display == IntPtr.Zero) return false;
                    _root = XDefaultRootWindow(_display);
                    _keycode = XKeysymToKeycode(_display, (nuint)keysym);
                    if (_keycode == 0) { Close(); return false; }

                    _mods = 0;
                    if (modifiers.HasFlag(KeyModifiers.Shift)) _mods |= ShiftMask;
                    if (modifiers.HasFlag(KeyModifiers.Control)) _mods |= ControlMask;
                    if (modifiers.HasFlag(KeyModifiers.Alt)) _mods |= Mod1Mask;
                    if (modifiers.HasFlag(KeyModifiers.Meta)) _mods |= Mod4Mask;

                    // Our error handler only during the grab (BadAccess = key taken), then restore whatever
                    // handler the UI toolkit installed.
                    _grabFailed = false;
                    var previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(ErrorHandlerDelegate));
                    foreach (var extra in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask }) // ignore Caps/Num Lock
                        XGrabKey(_display, _keycode, _mods | extra, _root, 0, GrabModeAsync, GrabModeAsync);
                    XSync(_display, 0);
                    XSetErrorHandler(previous);

                    if (_grabFailed)
                    {
                        Log.Write($"Hotkey {key} is already grabbed by another program");
                        Close();
                        return false;
                    }
                }
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "x11-hotkey" };
                _thread.Start();
                return true;
            }
            catch (DllNotFoundException)
            {
                Log.Write("libX11 not found: global hotkey unavailable");
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("Hotkey registration failed: " + ex.Message);
                Close();
                return false;
            }
        }

        private static int OnXError(IntPtr display, IntPtr errorEvent)
        {
            _grabFailed = true; // typically BadAccess from XGrabKey
            return 0;
        }

        private void Loop()
        {
            var ev = Marshal.AllocHGlobal(256); // XEvent is 192 bytes on 64-bit
            try
            {
                while (_running)
                {
                    bool fired = false;
                    lock (_gate)
                    {
                        if (_display == IntPtr.Zero) break;
                        while (XPending(_display) > 0)
                        {
                            XNextEvent(_display, ev);
                            if (Marshal.ReadInt32(ev) == KeyPress) fired = true;
                        }
                    }
                    if (fired)
                    {
                        long now = Environment.TickCount64;
                        if (now - _lastPressTicks > 250) // ignore key auto-repeat
                        {
                            _lastPressTicks = now;
                            Pressed?.Invoke();
                        }
                    }
                    Thread.Sleep(15);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ev);
            }
        }

        public void Unregister()
        {
            _running = false;
            _thread?.Join(500);
            _thread = null;
            lock (_gate)
            {
                if (_display != IntPtr.Zero && _keycode != 0)
                {
                    foreach (var extra in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask })
                        XUngrabKey(_display, _keycode, _mods | extra, _root);
                    XFlush(_display);
                }
                Close();
            }
        }

        private void Close()
        {
            if (_display != IntPtr.Zero)
            {
                try { XCloseDisplay(_display); } catch { }
            }
            _display = IntPtr.Zero;
            _keycode = 0;
        }

        public void Dispose() => Unregister();
    }
}
