using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BipDesk.Tech.Models;
using BipDesk.Tech.Views;

namespace BipDesk.Tech.Services
{
    public class SessionLauncher
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_RESTORE = 9;

        public static string? GetLocalRustDeskId()
        {
            try
            {
                // 1. Tenta obter o ID real executando --get-id no motor RustDesk
                var appDataRustDesk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rustdesk", "rustdesk.exe");
                var pfRustDesk = @"C:\Program Files\RustDesk\RustDesk.exe";
                var pfBipDesk = @"C:\Program Files\BipDesk\BipDesk_Session.exe";

                foreach (var exe in new[] { appDataRustDesk, pfRustDesk, pfBipDesk })
                {
                    if (File.Exists(exe))
                    {
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = exe,
                                Arguments = "--get-id",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                CreateNoWindow = true
                            };
                            using var p = Process.Start(psi);
                            if (p != null)
                            {
                                var outText = p.StandardOutput.ReadToEnd();
                                p.WaitForExit(2000);
                                var lines = outText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                for (int i = lines.Length - 1; i >= 0; i--)
                                {
                                    var line = lines[i].Trim();
                                    if (Regex.IsMatch(line, @"^\d{6,16}$"))
                                    {
                                        return line;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 2. Fallback: procura estritamente pela chave 'id =' no início da linha (NUNCA 'remote_id')
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var configPaths = new[]
                {
                    Path.Combine(appData, "RustDesk", "config", "RustDesk.toml"),
                    Path.Combine(appData, "BipDesk", "config", "BipDesk.toml"),
                    @"C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk.toml",
                    @"C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk\config\RustDesk.toml"
                };

                foreach (var cfg in configPaths)
                {
                    if (File.Exists(cfg))
                    {
                        var text = File.ReadAllText(cfg);
                        var match = Regex.Match(text, @"(?m)^id\s*=\s*['""](\d{6,16})['""]");
                        if (match.Success) return match.Groups[1].Value.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        public static async Task<IntPtr> WaitForSessionWindowAsync(int timeoutMs = 15000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                IntPtr found = IntPtr.Zero;
                EnumWindows((hwnd, lParam) =>
                {
                    var sb = new StringBuilder(256);
                    GetClassName(hwnd, sb, sb.Capacity);
                    var className = sb.ToString();
                    if (className == "RustdeskMultiWindow" || className.Contains("RustdeskMultiWindow"))
                    {
                        found = hwnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero) return found;
                await Task.Delay(200);
            }
            return IntPtr.Zero;
        }

        public static void EnsureBipDeskAssetBranding()
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var iconPath = Path.Combine(localAppData, "rustdesk", "data", "flutter_assets", "assets", "icon.svg");
                if (File.Exists(iconPath))
                {
                    var svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" width=""24"" height=""24"">
  <rect width=""24"" height=""24"" rx=""5"" fill=""#10B981""/>
  <path d=""M13 2L3 14h9l-1 8 10-12h-9l1-8z"" fill=""#FFFFFF""/>
</svg>";
                    File.WriteAllText(iconPath, svg);
                }
            }
            catch { }
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
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                var url = "https://bipdesk.duckdns.org/downloads/BipDesk_Session.exe";
                var bytes = await client.GetByteArrayAsync(url);

                // Verificação de integridade SHA-256 (ADR-007)
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha256.ComputeHash(bytes);
                var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
                Debug.WriteLine($"[SessionLauncher] BipDesk_Session.exe baixado com SHA-256: {hashHex}");

                var tempPath = targetPath + ".part";
                await File.WriteAllBytesAsync(tempPath, bytes);
                File.Move(tempPath, targetPath, overwrite: true);
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
                // Se BIPDESK_SERVER_HOST estiver definido, usa servidor customizado; caso contrário, usa a rede pública oficial da RustDesk
                var serverHost = Environment.GetEnvironmentVariable("BIPDESK_SERVER_HOST");
                var serverKey = Environment.GetEnvironmentVariable("BIPDESK_SERVER_KEY");

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

        private static void UpdateConfigFile(string filePath, string? serverHost, string? serverKey)
        {
            try
            {
                string tomlContent;
                if (!string.IsNullOrWhiteSpace(serverHost))
                {
                    tomlContent = $@"rendezvous_server = '{serverHost}'
relay_server = '{serverHost}'
api_server = 'https://{serverHost}'

[options]
custom-rendezvous-server = '{serverHost}'
relay-server = '{serverHost}'
api-server = 'https://{serverHost}'
key = '{serverKey ?? ""}'
allow-deep-link-password = 'Y'
allow-deep-link-server-settings = 'Y'
verification-method = 'use-permanent-password'
approve-mode = 'password'
";
                }
                else
                {
                    // Rede Oficial Pública da RustDesk (sem servidor nem chave privada forçados)
                    tomlContent = @"[options]
allow-deep-link-password = 'Y'
allow-deep-link-server-settings = 'Y'
verification-method = 'use-permanent-password'
approve-mode = 'password'
";
                }

                if (File.Exists(filePath))
                {
                    var current = File.ReadAllText(filePath);
                    if (current.Trim() == tomlContent.Trim())
                    {
                        return;
                    }
                }

                File.WriteAllText(filePath, tomlContent);
            }
            catch { }
        }


        public async Task<ActiveSession?> LaunchSessionAsync(
            Machine machine, 
            string password, 
            Action<ActiveSession> onSessionTerminated, 
            Action<ActiveSession>? onWindowAttached = null,
            Action<string>? statusCallback = null)
        {
            try
            {
                EnsureRustDeskClientConfig();
                EnsureBipDeskAssetBranding();
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

                // Captura e acopla a janela remota diretamente dentro do BipDesk Admin
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var hwnd = await WaitForSessionWindowAsync(15000);
                        if (hwnd != IntPtr.Zero)
                        {
                            session.WindowHandle = hwnd;
                            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                var host = new EmbeddedSessionHost(hwnd, $"BipDesk - {machine.DisplayName}");
                                session.HostControl = host;
                                onWindowAttached?.Invoke(session);
                            });

                            // Monitora ciclo de vida da janela remota em tempo real
                            _ = Task.Run(async () =>
                            {
                                while (true)
                                {
                                    await Task.Delay(1000);
                                    if (!IsWindow(hwnd))
                                    {
                                        Debug.WriteLine($"[SessionLauncher] Janela remota {hwnd} foi fechada pelo usuário ou desconectada.");
                                        try
                                        {
                                            onSessionTerminated?.Invoke(session);
                                        }
                                        catch { }
                                        break;
                                    }
                                }
                            });
                        }
                        else
                        {
                            Debug.WriteLine("[SessionLauncher] Timeout ao aguardar janela remota.");
                            try
                            {
                                onSessionTerminated?.Invoke(session);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SessionLauncher] Erro ao acoplar janela remota: {ex.Message}");
                        try
                        {
                            onSessionTerminated?.Invoke(session);
                        }
                        catch { }
                    }
                });

                return session;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLauncher] Erro ao iniciar sessão: {ex.Message}");
                return null;
            }
        }

        public async Task<ActiveSession?> LaunchDirectSessionAsync(
            string rustdeskId, 
            Action<ActiveSession> onSessionTerminated, 
            Action<ActiveSession>? onWindowAttached = null, 
            Action<string>? statusCallback = null)
        {
            var directMachine = new Machine
            {
                Id = 0,
                RustdeskId = rustdeskId,
                Hostname = $"ID-{rustdeskId}",
                Alias = $"Conexão Avulsa ({rustdeskId})"
            };
            return await LaunchSessionAsync(directMachine, "", onSessionTerminated, onWindowAttached, statusCallback);
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
