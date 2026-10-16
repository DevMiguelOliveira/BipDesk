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

            // Auto-login se houver credenciais salvas
            var credPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk", "credentials.txt");
            if (System.IO.File.Exists(credPath))
            {
                try
                {
                    var savedUser = System.IO.File.ReadAllText(credPath);
                    var parts = savedUser.Split('|');
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        var api = new ApiClient { BaseUrl = "https://bipdesk.duckdns.org" };
                        bool ok = await api.LoginAsync(parts[0].Trim(), parts[1].Trim());
                        if (ok)
                        {
                            var mainWindow = new MainWindow(api);
                            mainWindow.Show();
                            return;
                        }
                    }
                }
                catch { }
            }

            var loginWindow = new LoginWindow();
            loginWindow.Show();
        }
    }
}
