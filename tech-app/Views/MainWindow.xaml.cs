using System.Windows;
using System.Windows.Input;
using BipDesk.Tech.Models;
using BipDesk.Tech.ViewModels;

namespace BipDesk.Tech.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            Loaded += async (s, e) =>
            {
                await _vm.InitializeAsync();
            };
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Atalhos rápidos: Ctrl + K ou Ctrl + F para focar a pesquisa
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (e.Key == Key.K || e.Key == Key.F))
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            // Enter conecta imediatamente na máquina selecionada
            else if (e.Key == Key.Enter && !SearchBox.IsFocused)
            {
                if (_vm.SelectedMachine != null)
                {
                    _vm.ConnectCommand.Execute(_vm.SelectedMachine);
                    e.Handled = true;
                }
            }
        }

        private void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Duplo clique conecta imediatamente na máquina
            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement elem && elem.DataContext is Machine machine)
                {
                    _vm.ConnectCommand.Execute(machine);
                    e.Handled = true;
                }
            }
        }

        private async void AddMachine_Click(object sender, RoutedEventArgs e)
        {
            var token = await _vm.GenerateEnrollTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                var dialog = new EnrollDialog("https://bipdesk.duckdns.org", token)
                {
                    Owner = this
                };
                dialog.ShowDialog();
            }
            else
            {
                MessageBox.Show("Não foi possível gerar token de instalação. Verifique a conexão com o servidor.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
