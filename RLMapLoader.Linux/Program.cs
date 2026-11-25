using System;
using System.Linq;
using Avalonia;
using RLMapLoader.Linux.Platform;
using RLMapLoader.Services;

namespace RLMapLoader.Linux
{
    internal static class Program
    {
        /// <summary>
        /// rl-map-loader                  start (or bring the running instance to the front)
        /// rl-map-loader --toggle-overlay show/hide the in-game overlay of the running instance
        /// rl-map-loader --restore        put the original Underpass back
        /// rl-map-loader --tray           start hidden in the tray
        /// rl-map-loader --install        copy to ~/.local/bin and add the app-menu entry, then exit
        /// rl-map-loader --no-install     run from where it is (don't copy itself to ~/.local/bin)
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            string command = args.Contains("--toggle-overlay") ? "toggle-overlay"
                           : args.Contains("--restore") ? "restore"
                           : "show";

            if (OperatingSystem.IsLinux() && args.Contains("--install")) return SelfInstall.InstallOnly();

            // Restarting after an update: the previous process is still closing.
            bool restarted = args.Contains("--wait-pid");
            Updater.WaitForPreviousInstance(args);

            if (!restarted)
            {
                // Opened from a download: install to ~/.local/bin + app menu, then run that copy.
                if (OperatingSystem.IsLinux() && SelfInstall.TryInstallAndRelaunch(args)) return 0;
                // A running instance handles the command; this process just forwards it.
                if (SingleInstance.TrySend(command)) return 0;
            }

            if (command == "toggle-overlay") App.StartWithOverlay = true;
            if (command == "restore") App.StartWithRestore = true;
            App.StartHidden = args.Contains("--tray");
            App.ForceShow = args.Contains("--show");
            App.JustInstalled = args.Contains("--installed");
            App.UpdatedFrom = Updater.UpdatedFrom(args);

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
