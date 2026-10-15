using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BipDesk.Tech.Models;
using BipDesk.Tech.Services;

namespace BipDesk.Tech.Views
{
    public partial class TechniciansDialog : Window
    {
        private readonly ApiClient _api;

        public TechniciansDialog(ApiClient api)
        {
            InitializeComponent();
            _api = api;

            Loaded += async (s, e) => await LoadTechniciansAsync();
        }

        private async Task LoadTechniciansAsync()
        {
            try
            {
                var list = await _api.GetTechniciansAsync();
                ItemsTechnicians.ItemsSource = list;
                TxtTotalCount.Text = $" ({list.Count})";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar técnicos: {ex.Message}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadTechniciansAsync();
        }

        private async void BtnCreateTechnician_Click(object sender, RoutedEventArgs e)
        {
            string fullName = TxtNewFullName.Text.Trim();
            string username = TxtNewUsername.Text.Trim();
            string password = TxtNewPassword.Text.Trim();
            string role = CbNewRole.SelectedIndex == 1 ? "admin" : "technician";

            if (string.IsNullOrWhiteSpace(fullName))
            {
                ShowFormStatus("Informe o nome completo do técnico.", false);
                TxtNewFullName.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowFormStatus("Informe um nome de usuário para login.", false);
                TxtNewUsername.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            {
                ShowFormStatus("A senha deve possuir pelo menos 4 caracteres.", false);
                TxtNewPassword.Focus();
                return;
            }

            BtnCreateTechnician.IsEnabled = false;
            BtnCreateTechnician.Content = "Cadastrando...";

            try
            {
                bool success = await _api.CreateTechnicianAsync(username, password, fullName, role);
                if (success)
                {
                    TxtNewFullName.Clear();
                    TxtNewUsername.Clear();
                    TxtNewPassword.Clear();
                    CbNewRole.SelectedIndex = 0;

                    ShowFormStatus("✅ Técnico cadastrado com sucesso!", true);
                    await LoadTechniciansAsync();
                }
                else
                {
                    ShowFormStatus("❌ Falha ao cadastrar: Usuário já existe ou sem permissão.", false);
                }
            }
            catch (Exception ex)
            {
                ShowFormStatus($"❌ Erro: {ex.Message}", false);
            }
            finally
            {
                BtnCreateTechnician.IsEnabled = true;
                BtnCreateTechnician.Content = "+ Criar Conta de Acesso";
            }
        }

        private async void BtnDeleteTechnician_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Technician tech)
            {
                if (string.Equals(tech.Username, _api.CurrentUsername, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Você não pode excluir sua própria conta de administrador em uso.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var result = MessageBox.Show(
                    $"Deseja realmente remover o acesso de '{tech.FullName ?? tech.Username}' (@{tech.Username})?\n\nEle não conseguirá mais conectar no BipDesk Admin.",
                    "Confirmar Exclusão de Técnico",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    bool ok = await _api.DeleteTechnicianAsync(tech.Id);
                    if (ok)
                    {
                        await LoadTechniciansAsync();
                    }
                    else
                    {
                        MessageBox.Show("Falha ao excluir técnico no servidor.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void ShowFormStatus(string msg, bool isSuccess)
        {
            TxtFormStatus.Text = msg;
            TxtFormStatus.Foreground = isSuccess ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00E5A3")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8514F"));
            TxtFormStatus.Visibility = Visibility.Visible;
        }
    }
}
