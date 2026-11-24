using System;
using Microsoft.Win32;

namespace RLMapLoader.Services
{
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "RLMapLoader";

        public static void Apply(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled && Environment.ProcessPath is { } exe)
                    key.SetValue(ValueName, $"\"{exe}\" --tray");
                else if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName, false);
            }
            catch (Exception ex)
            {
                Log.Write("Startup registration failed: " + ex.Message);
            }
        }
    }
}
