using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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

        public static string? GetLocalRustDeskId()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var localToml = Path.Combine(appData, "RustDesk", "config", "RustDesk_local.toml");
                if (File.Exists(localToml))
                {
                    var text = File.ReadAllText(localToml);
                    var match = Regex.Match(text, @"remote_id\s*=\s*['""]([^'""]+)['""]");
                    if (match.Success) return match.Groups[1].Value.Trim();
                }

                var mainToml = Path.Combine(appData, "RustDesk", "config", "RustDesk.toml");
                if (File.Exists(mainToml))
                {
                    var text = File.ReadAllText(mainToml);
                    var match = Regex.Match(text, @"id\s*=\s*['""]([^'""]+)['""]");
                    if (match.Success) return match.Groups[1].Value.Trim();
                }
            }
            catch { }
            return null;
        }

        public string FindSessionExecutable()
        {
            // 1. AppData Local RustDesk instalado (runtime completo com Flutter, DLLs e uni_links)
            var appDataRustDesk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rustdesk", "rustdesk.exe");
            if (File.Exists(appDataRustDesk)) return appDataRustDesk;

            // 2. Program Files RustDesk oficial instalado
            var pfRustDesk = @"C:\Program Files\RustDesk\RustDesk.exe";
            if (File.Exists(pfRustDesk)) return pfRustDesk;

            // 3. Diretório do aplicativo do técnico
            var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BipDesk_Session.exe");
            if (File.Exists(localPath)) return localPath;

            // 4. AppData Local BipDesk
            var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk", "BipDesk_Session.exe");
            if (File.Exists(appDataPath)) return appDataPath;

            // 5. Program Files BipDesk
            var pfBipDesk = @"C:\Program Files\BipDesk\BipDesk_Session.exe";
            if (File.Exists(pfBipDesk)) return pfBipDesk;

            return appDataRustDesk;
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

        public static void EnsureRustDeskClientConfig()
        {
            try
            {
                var serverHost = "bipdesk.duckdns.org";
                var serverKey = Environment.GetEnvironmentVariable("BIPDESK_SERVER_KEY") ?? "gsTzsIewn3wB9umlEvZgT+Z9ZUKNxcAwbhl+bz2pJFw=";

                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var roamingConfigDir = Path.Combine(appData, "RustDesk", "config");
                Directory.CreateDirectory(roamingConfigDir);
                UpdateConfigFile(Path.Combine(roamingConfigDir, "RustDesk2.toml"), serverHost, serverKey);

                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var localConfigDir = Path.Combine(localAppData, "RustDesk", "config");
                if (Directory.Exists(localConfigDir))
                {
                    UpdateConfigFile(Path.Combine(localConfigDir, "RustDesk2.toml"), serverHost, serverKey);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLauncher] Erro ao gravar configs RustDesk: {ex.Message}");
            }
        }

        private static void UpdateConfigFile(string filePath, string serverHost, string serverKey)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var current = File.ReadAllText(filePath);
                    if (current.Contains(serverHost) && current.Contains(serverKey))
                    {
                        return;
                    }
                }

                var tomlContent = $@"rendezvous_server = '{serverHost}'
relay_server = '{serverHost}'
api_server = 'https://{serverHost}'

[options]
custom-rendezvous-server = '{serverHost}'
relay-server = '{serverHost}'
api-server = 'https://{serverHost}'
key = '{serverKey}'
";
                File.WriteAllText(filePath, tomlContent);
            }
            catch { }
        }

        public async Task<Process?> LaunchSessionAsync(Machine machine, string password, Action<ActiveSession> onSessionTerminated, Action<string>? statusCallback = null)
        {
            try
            {
                EnsureRustDeskClientConfig();
                var exePath = await EnsureSessionExecutableAsync(statusCallback);

                var uri = $"rustdesk://{machine.RustdeskId}";
                if (!string.IsNullOrEmpty(password))
                {
                    uri += $"?password={Uri.EscapeDataString(password)}";
                }

                Process? proc = null;
                var startTime = DateTime.UtcNow;

                if (File.Exists(exePath))
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"\"{uri}\"",
                        UseShellExecute = false,
                        CreateNoWindow = false
                    };
                    proc = Process.Start(startInfo);
                }
                else
                {
                    proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = uri,
                        UseShellExecute = true
                    });
                }

                if (proc == null) return null;

                var session = new ActiveSession(machine, proc);

                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s, e) =>
                    {
                        // Se o processo de launcher encerrou em menos de 6 segundos, trata-se do handoff de IPC
                        // do Flutter/RustDesk para a instância de sessão principal.
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        if (elapsed < 6.0)
                        {
                            Debug.WriteLine($"[SessionLauncher] Processo de launcher encerrou em {elapsed:F1}s (handoff de IPC). Mantendo sessão ativa.");
                            return;
                        }

                        try
                        {
                            onSessionTerminated?.Invoke(session);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SessionLauncher] onSessionTerminated erro ignorado: {ex.Message}");
                        }
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SessionLauncher] Erro ao configurar Exited event: {ex.Message}");
                }

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
                EnsureRustDeskClientConfig();
                var exePath = await EnsureSessionExecutableAsync(statusCallback);
                var uri = $"rustdesk://{rustdeskId}";

                if (File.Exists(exePath))
                {
                    return Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"\"{uri}\"",
                        UseShellExecute = false,
                        CreateNoWindow = false
                    });
                }
                else
                {
                    return Process.Start(new ProcessStartInfo
                    {
                        FileName = uri,
                        UseShellExecute = true
                    });
                }
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
