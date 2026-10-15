using System.Windows;

namespace BipDesk.Tech.Views
{
    public partial class SendMessageDialog : Window
    {
        public string MessageText { get; private set; } = string.Empty;

        public SendMessageDialog(string machineName)
        {
            InitializeComponent();
            TargetMachineText.Text = $"Mensagem para {machineName}";
            MessageInput.Focus();
            MessageInput.SelectAll();
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            MessageText = MessageInput.Text.Trim();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
