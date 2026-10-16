using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using BipDesk.Tech.Services;
using BipDesk.Tech.Views;

namespace BipDesk.Tech
{
    public partial class App : Application
    {
        private async void Application_Startup(object sender, StartupEventArgs e)
        {
            // Proteção contra falhas inesperadas (evita fechamento abrupto)
            DispatcherUnhandledException += (s, args) =>
            {
                Debug.WriteLine($"[App] DispatcherUnhandledException: {args.Exception.Message}");
                args.Handled = true; // Impede o fechamento do app
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Debug.WriteLine($"[App] UnhandledException: {args.ExceptionObject}");
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Debug.WriteLine($"[App] UnobservedTaskException: {args.Exception.Message}");
                args.SetObserved();
            };

            // Garante configuração inicial do cliente RustDesk e branding BipDesk
            SessionLauncher.EnsureRustDeskClientConfig();
            SessionLauncher.EnsureBipDeskAssetBranding();

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var debugFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk", "autologin_debug.txt");
            try { System.IO.File.WriteAllText(debugFile, $"[App] Application_Startup started at {DateTime.Now}\n"); } catch { }

            // Auto-login se houver credenciais salvas
            var credPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk", "credentials.txt");
            try { System.IO.File.AppendAllText(debugFile, $"[App] credPath={credPath}, Exists={System.IO.File.Exists(credPath)}\n"); } catch { }

            if (System.IO.File.Exists(credPath))
            {
                try
                {
                    var savedUser = System.IO.File.ReadAllText(credPath);
                    try { System.IO.File.AppendAllText(debugFile, $"[App] Raw savedUser='{savedUser}'\n"); } catch { }
                    var parts = savedUser.Split('|');
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        var api = new ApiClient { BaseUrl = "https://bipdesk.duckdns.org" };
                        try { System.IO.File.AppendAllText(debugFile, $"[App] Calling LoginAsync({parts[0].Trim()})...\n"); } catch { }
                        bool ok = await api.LoginAsync(parts[0].Trim(), parts[1].Trim());
                        try { System.IO.File.AppendAllText(debugFile, $"[App] LoginAsync result={ok}\n"); } catch { }
                        if (ok)
                        {
                            try { System.IO.File.AppendAllText(debugFile, "[App] Showing MainWindow and assigning as MainWindow!\n"); } catch { }
                            var mainWindow = new MainWindow(api);
                            MainWindow = mainWindow;
                            ShutdownMode = ShutdownMode.OnMainWindowClose;
                            mainWindow.Show();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(debugFile, $"[App] EXCEPTION: {ex}\n"); } catch { }
                }
            }

            try { System.IO.File.AppendAllText(debugFile, "[App] Showing LoginWindow fallback.\n"); } catch { }
            var loginWindow = new LoginWindow();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            loginWindow.Show();
        }
    }
}
