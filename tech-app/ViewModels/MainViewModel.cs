using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using BipDesk.Tech.Models;
using BipDesk.Tech.Services;

namespace BipDesk.Tech.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ApiClient _api;
        private readonly SessionLauncher _launcher;
        private readonly DispatcherTimer _pollTimer;

        private List<Machine> _rawMachines = new();
        private Machine? _selectedMachine;
        private string _searchText = string.Empty;
        private string _activeFilter = "Todas"; // "Todas", "Online", "Favoritos"
        private bool _isLoading;
        private string _connectionStatus = "Conectando...";
        private string _connectionStatusColor = "#D29922";

        public ObservableCollection<Machine> FilteredMachines { get; } = new();
        public ObservableCollection<ActiveSession> ActiveSessions { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel()
        {
            _api = new ApiClient();
            _launcher = new SessionLauncher();

            ConnectCommand = new RelayCommand(async p => await ConnectToMachineAsync(p as Machine));
            RefreshCommand = new RelayCommand(async _ => await LoadMachinesAsync());
            SaveNotesCommand = new RelayCommand(async _ => await SaveNotesAsync());
            ToggleFavoriteCommand = new RelayCommand(async p => await ToggleFavoriteAsync(p as Machine));
            FocusSessionCommand = new RelayCommand(p => FocusSession(p as ActiveSession));
            CloseSessionCommand = new RelayCommand(p => CloseSession(p as ActiveSession));
            SetFilterCommand = new RelayCommand(p => SetFilter(p?.ToString() ?? "Todas"));

            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _pollTimer.Tick += async (s, e) => await PollMachinesAsync();
        }

        public async Task InitializeAsync()
        {
            IsLoading = true;
            // Tenta login com credenciais de admin ou token salvo
            bool logged = await _api.LoginAsync("admin", "BipDesk@2026");
            if (logged)
            {
                ConnectionStatus = "Nuvem Conectada (12ms)";
                ConnectionStatusColor = "#00E5A3";
                await LoadMachinesAsync();
                _pollTimer.Start();
            }
            else
            {
                ConnectionStatus = "Falha na conexão com a nuvem";
                ConnectionStatusColor = "#F8514F";
            }
            IsLoading = false;
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged();
                    ApplyFilter();
                }
            }
        }

        public string ActiveFilter
        {
            get => _activeFilter;
            set { _activeFilter = value; OnPropertyChanged(); ApplyFilter(); }
        }

        public Machine? SelectedMachine
        {
            get => _selectedMachine;
            set { _selectedMachine = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { _connectionStatus = value; OnPropertyChanged(); }
        }

        public string ConnectionStatusColor
        {
            get => _connectionStatusColor;
            set { _connectionStatusColor = value; OnPropertyChanged(); }
        }

        public int TotalCount => _rawMachines.Count;
        public int OnlineCount => _rawMachines.Count(m => m.IsOnline);

        public ICommand ConnectCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SaveNotesCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand FocusSessionCommand { get; }
        public ICommand CloseSessionCommand { get; }
        public ICommand SetFilterCommand { get; }

        public async Task LoadMachinesAsync()
        {
            var machines = await _api.GetMachinesAsync();
            _rawMachines = machines;
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(OnlineCount));
            ApplyFilter();
        }

        private async Task PollMachinesAsync()
        {
            var machines = await _api.GetMachinesAsync();
            _rawMachines = machines;
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(OnlineCount));
            ApplyFilter(keepSelection: true);
        }

        public void ApplyFilter(bool keepSelection = false)
        {
            var currentId = SelectedMachine?.Id;

            var query = _rawMachines.AsEnumerable();

            // Filtro de Tabs
            if (ActiveFilter == "Online")
                query = query.Where(m => m.IsOnline);
            else if (ActiveFilter == "Favoritos")
                query = query.Where(m => m.IsFavorite);

            // Filtro de Texto
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var term = SearchText.Trim().ToLower();
                query = query.Where(m =>
                    (m.Alias != null && m.Alias.ToLower().Contains(term)) ||
                    m.Hostname.ToLower().Contains(term) ||
                    (m.LoggedInUser != null && m.LoggedInUser.ToLower().Contains(term)) ||
                    (m.Notes != null && m.Notes.ToLower().Contains(term)) ||
                    m.ClientGroup.ToLower().Contains(term));
            }

            FilteredMachines.Clear();
            foreach (var item in query)
            {
                FilteredMachines.Add(item);
            }

            if (keepSelection && currentId.HasValue)
            {
                SelectedMachine = FilteredMachines.FirstOrDefault(m => m.Id == currentId.Value);
            }
            else if (FilteredMachines.Count > 0 && SelectedMachine == null)
            {
                SelectedMachine = FilteredMachines[0];
            }
        }

        private void SetFilter(string filter)
        {
            ActiveFilter = filter;
        }

        public async Task ConnectToMachineAsync(Machine? machine)
        {
            if (machine == null) machine = SelectedMachine;
            if (machine == null) return;

            // Solicita ticket temporário de 60s
            var ticket = await _api.GetConnectTicketAsync(machine.Id);
            if (ticket == null)
            {
                MessageBox.Show($"Não foi possível obter o ticket de conexão para {machine.DisplayName}. Verifique se o servidor está online.", "Falha de Conexão", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Inicia o processo BipDesk_Session nativo
            var proc = _launcher.LaunchSession(machine, ticket.Password, session =>
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    ActiveSessions.Remove(session);
                });
            });

            if (proc != null)
            {
                var session = new ActiveSession(machine, proc);
                ActiveSessions.Add(session);
            }
        }

        private async Task SaveNotesAsync()
        {
            if (SelectedMachine == null) return;
            await _api.UpdateMachineAsync(SelectedMachine.Id, SelectedMachine.Alias, SelectedMachine.Notes, SelectedMachine.IsFavorite, SelectedMachine.ClientGroup);
        }

        private async Task ToggleFavoriteAsync(Machine? machine)
        {
            if (machine == null) return;
            machine.IsFavorite = !machine.IsFavorite;
            await _api.UpdateMachineAsync(machine.Id, machine.Alias, machine.Notes, machine.IsFavorite, machine.ClientGroup);
            ApplyFilter(keepSelection: true);
        }

        private void FocusSession(ActiveSession? session)
        {
            if (session != null)
            {
                _launcher.BringToFront(session.Process);
            }
        }

        private void CloseSession(ActiveSession? session)
        {
            if (session != null)
            {
                try
                {
                    if (!session.Process.HasExited)
                    {
                        session.Process.Kill();
                    }
                }
                catch { }
                ActiveSessions.Remove(session);
            }
        }

        public async Task<string?> GenerateEnrollTokenAsync()
        {
            return await _api.CreateEnrollTokenAsync();
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
