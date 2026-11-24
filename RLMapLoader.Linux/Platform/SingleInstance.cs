using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RLMapLoader.Services;

namespace RLMapLoader.Linux.Platform
{
    /// <summary>
    /// One running instance per user, reachable over a Unix socket. Lets desktop shortcuts run
    /// "rl-map-loader --toggle-overlay" (handy on Wayland, where apps can't grab keys globally).
    /// </summary>
    public static class SingleInstance
    {
        private static Socket? _listener;

        private static string SocketPath
        {
            get
            {
                var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                if (string.IsNullOrEmpty(runtime) || !Directory.Exists(runtime))
                    runtime = Path.GetTempPath();
                return Path.Combine(runtime, $"rl-map-loader-{Environment.UserName}.sock");
            }
        }

        /// <summary>Sends <paramref name="command"/> to a running instance. False when none is running.</summary>
        public static bool TrySend(string command)
        {
            if (OperatingSystem.IsWindows()) return false;
            try
            {
                using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.Connect(new UnixDomainSocketEndPoint(SocketPath));
                s.Send(Encoding.UTF8.GetBytes(command + "\n"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Starts listening; <paramref name="onCommand"/> is called on a background thread.</summary>
        public static void Listen(Action<string> onCommand)
        {
            if (OperatingSystem.IsWindows()) return;
            try
            {
                if (File.Exists(SocketPath)) File.Delete(SocketPath); // stale socket from a crash
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
                _listener.Listen(4);
                new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            using var client = _listener.Accept();
                            var buf = new byte[256];
                            int n = client.Receive(buf);
                            var cmd = Encoding.UTF8.GetString(buf, 0, Math.Max(0, n)).Trim();
                            if (cmd.Length > 0) onCommand(cmd);
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (Exception ex) { Log.Write("IPC: " + ex.Message); }
                    }
                }) { IsBackground = true, Name = "ipc" }.Start();
            }
            catch (Exception ex)
            {
                Log.Write("IPC listen failed: " + ex.Message);
            }
        }

        public static void Stop()
        {
            try { _listener?.Dispose(); } catch { }
            try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
        }
    }
}
