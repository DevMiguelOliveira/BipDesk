using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BipDesk.Tech.Models;

namespace BipDesk.Tech.Services
{
    public class SessionLauncher
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public string FindSessionExecutable()
        {
            // 1. Diretório do aplicativo do técnico
            var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BipDesk_Session.exe");
            if (File.Exists(localPath)) return localPath;

            // 2. Program Files
            var pfPath = @"C:\Program Files\BipDesk\BipDesk_Session.exe";
            if (File.Exists(pfPath)) return pfPath;

            // 3. AppData Local
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk", "BipDesk_Session.exe");
            if (File.Exists(appDataPath)) return appDataPath;

            return "BipDesk_Session.exe";
        }

        public Process? LaunchSession(Machine machine, string password, Action<ActiveSession> onSessionTerminated)
        {
            try
            {
                var exePath = FindSessionExecutable();

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--connect {machine.RustdeskId} --password {password}",
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                var proc = Process.Start(startInfo);
                if (proc == null) return null;

                var session = new ActiveSession(machine, proc);

                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) =>
                {
                    onSessionTerminated?.Invoke(session);
                };

                return proc;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLauncher] Erro ao iniciar sessão: {ex.Message}");
                return null;
            }
        }

        public void BringToFront(Process process)
        {
            try
            {
                if (process != null && !process.HasExited && process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(process.MainWindowHandle);
                }
            }
            catch { }
        }
    }
}
