using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

            return localPath;
        }

        public async Task<string> EnsureSessionExecutableAsync(Action<string>? statusCallback = null)
        {
            var existing = FindSessionExecutable();
            if (File.Exists(existing)) return existing;

            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, "BipDesk_Session.exe");

            if (File.Exists(targetPath)) return targetPath;

            try
            {
                statusCallback?.Invoke("Baixando motor de conexão BipDesk Session pela primeira vez...");
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                var url = "https://bipdesk.duckdns.org/downloads/BipDesk_Session.exe";
                var bytes = await client.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(targetPath, bytes);
                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLauncher] Falha ao baixar BipDesk_Session automaticamente: {ex.Message}");
                return existing;
            }
        }

        public async Task<Process?> LaunchSessionAsync(Machine machine, string password, Action<ActiveSession> onSessionTerminated, Action<string>? statusCallback = null)
        {
            try
            {
                var exePath = await EnsureSessionExecutableAsync(statusCallback);
                if (!File.Exists(exePath))
                {
                    Debug.WriteLine($"[SessionLauncher] Executável não encontrado: {exePath}");
                }

                var serverKey = Environment.GetEnvironmentVariable("BIPDESK_SERVER_KEY") ?? "gsTzsIewn3wB9umlEvZgT+Z9ZUKNxcAwbhl+bz2pJFw=";

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--connect {machine.RustdeskId} --password {password} --server bipdesk.duckdns.org --key {serverKey}",
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

        public async Task<Process?> LaunchDirectSessionAsync(string rustdeskId, Action<string>? statusCallback = null)
        {
            try
            {
                var exePath = await EnsureSessionExecutableAsync(statusCallback);
                var serverKey = Environment.GetEnvironmentVariable("BIPDESK_SERVER_KEY") ?? "gsTzsIewn3wB9umlEvZgT+Z9ZUKNxcAwbhl+bz2pJFw=";

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--connect {rustdeskId} --server bipdesk.duckdns.org --key {serverKey}",
                    UseShellExecute = false,
                    CreateNoWindow = false
                };

                return Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLauncher] Erro ao iniciar sessão direta: {ex.Message}");
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
