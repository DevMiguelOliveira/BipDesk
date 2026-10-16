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
        private void Application_Startup(object sender, StartupEventArgs e)
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

            // Garante configuração inicial do cliente RustDesk
            SessionLauncher.EnsureRustDeskClientConfig();

            var loginWindow = new LoginWindow();
            loginWindow.Show();
        }
    }
}
