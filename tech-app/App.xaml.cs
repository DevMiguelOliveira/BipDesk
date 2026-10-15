using System.Windows;
using BipDesk.Tech.Views;

namespace BipDesk.Tech
{
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            var loginWindow = new LoginWindow();
            loginWindow.Show();
        }
    }
}
