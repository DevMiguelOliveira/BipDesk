using System.Windows;

namespace BipDesk.Tech.Views
{
    public partial class EnrollDialog : Window
    {
        public EnrollDialog(string serverUrl, string token)
        {
            InitializeComponent();
            TxtCommand.Text = $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"irm {serverUrl}/install/{token} | iex\"";
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(TxtCommand.Text);
            MessageBox.Show("Comando copiado para a área de transferência!", "BipDesk", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
