using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using BipDesk.Tech.Services;

namespace BipDesk.Tech.Views
{
    public partial class LoginWindow : Window
    {
        private const string ServerUrl = "https://bipdesk.duckdns.org";

        private class UserPrefs
        {
            public string Username { get; set; } = "";
            public bool RememberMe { get; set; } = true;
        }

        private readonly string _prefsPath;

        public LoginWindow()
        {
            InitializeComponent();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "BipDesk");
            Directory.CreateDirectory(folder);
            _prefsPath = Path.Combine(folder, "admin_prefs.json");

            LoadPreferences();

            Loaded += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(TxtUsername.Text))
                {
                    TxtUsername.Focus();
                }
                else
                {
                    TxtPassword.Focus();
                }
            };
        }

        private void LoadPreferences()
        {
            try
            {
                if (File.Exists(_prefsPath))
                {
                    string json = File.ReadAllText(_prefsPath);
                    var prefs = JsonSerializer.Deserialize<UserPrefs>(json);
                    if (prefs != null)
                    {
                        if (prefs.RememberMe && !string.IsNullOrWhiteSpace(prefs.Username))
                        {
                            TxtUsername.Text = prefs.Username;
                            ChkRemember.IsChecked = true;
                        }
                    }
                }
            }
            catch { }
        }

        private void SavePreferences(string username, bool remember)
        {
            try
            {
                var prefs = new UserPrefs
                {
                    Username = remember ? username : "",
                    RememberMe = remember
                };
                string json = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_prefsPath, json);
            }
            catch { }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string username = TxtUsername.Text.Trim();
            string password = TxtPassword.Password;

            if (string.IsNullOrWhiteSpace(username))
            {
                ShowError("Informe o nome de usuário.");
                TxtUsername.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                ShowError("Informe a senha de acesso.");
                TxtPassword.Focus();
                return;
            }

            TxtStatus.Visibility = Visibility.Collapsed;
            BtnLogin.IsEnabled = false;
            BtnLogin.Content = "Autenticando...";
            Cursor = Cursors.Wait;

            try
            {
                var api = new ApiClient
                {
                    BaseUrl = ServerUrl
                };

                bool success = await api.LoginAsync(username, password);
                if (success)
                {
                    SavePreferences(username, ChkRemember.IsChecked == true);

                    var mainWindow = new MainWindow(api);
                    mainWindow.Show();
                    Close();
                }
                else
                {
                    ShowError("Usuário ou senha incorretos.");
                }
            }
            catch (Exception ex)
            {
                ShowError($"Erro ao conectar: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Arrow;
                BtnLogin.IsEnabled = true;
                BtnLogin.Content = "Entrar";
            }
        }

        private void ShowError(string message)
        {
            TxtStatus.Text = message;
            TxtStatus.Visibility = Visibility.Visible;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnLogin_Click(sender, e);
            }
        }
    }
}
