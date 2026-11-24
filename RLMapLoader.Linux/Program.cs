using System;
using System.Linq;
using Avalonia;
using RLMapLoader.Linux.Platform;

namespace RLMapLoader.Linux
{
    internal static class Program
    {
        /// <summary>
        /// rl-map-loader                  start (or bring the running instance to the front)
        /// rl-map-loader --toggle-overlay show/hide the in-game overlay of the running instance
        /// rl-map-loader --restore        put the original Underpass back
        /// rl-map-loader --tray           start hidden in the tray
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            string command = args.Contains("--toggle-overlay") ? "toggle-overlay"
                           : args.Contains("--restore") ? "restore"
                           : "show";

            // A running instance handles the command; this process just forwards it.
            if (SingleInstance.TrySend(command)) return 0;

            if (command == "toggle-overlay") App.StartWithOverlay = true;
            if (command == "restore") App.StartWithRestore = true;
            App.StartHidden = args.Contains("--tray");

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return 0;
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
