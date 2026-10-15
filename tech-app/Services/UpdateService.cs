using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;

namespace BipDesk.Tech.Services
{
    public class AppVersionInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = "https://bipdesk.duckdns.org/downloads/BipDesk_Admin.exe";

        [JsonPropertyName("release_notes")]
        public string? ReleaseNotes { get; set; }
    }

    public class UpdateService
    {
        public static readonly Version CurrentVersion = new Version(2, 1, 0);
        public const string CurrentVersionString = "2.1.0";

        private readonly HttpClient _http;
        public string BaseUrl { get; set; } = "https://bipdesk.duckdns.org";

        public UpdateService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async Task<AppVersionInfo?> CheckForUpdateAsync()
        {
            try
            {
                var info = await _http.GetFromJsonAsync<AppVersionInfo>($"{BaseUrl}/api/app/version");
                if (info != null && Version.TryParse(info.Version, out var remoteVer))
                {
                    if (remoteVer > CurrentVersion)
                    {
                        return info;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Erro ao verificar atualizacao: {ex.Message}");
            }
            return null;
        }

        public async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, Action<string>? onProgress = null)
        {
            try
            {
                onProgress?.Invoke("Baixando nova versao do BipDesk Admin...");

                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                {
                    currentExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BipDesk_Admin.exe");
                }

                var tempDir = Path.GetTempPath();
                var tempExe = Path.Combine(tempDir, $"BipDesk_Admin_New_{Guid.NewGuid():N}.exe");
                var updaterBat = Path.Combine(tempDir, "bipdesk_update.bat");

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                var data = await client.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempExe, data);

                onProgress?.Invoke("Reiniciando com a nova versao...");

                var batContent = $@"@echo off
timeout /t 1 /nobreak > nul
:retry
move /y ""{tempExe}"" ""{currentExe}"" > nul 2>&1
if errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto retry
)
start """" ""{currentExe}""
del ""%~f0""
";
                await File.WriteAllTextAsync(updaterBat, batContent);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{updaterBat}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                Process.Start(psi);
                Application.Current?.Dispatcher?.Invoke(() => Application.Current.Shutdown());
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] Falha ao aplicar atualizacao: {ex.Message}");
                return false;
            }
        }
    }
}
