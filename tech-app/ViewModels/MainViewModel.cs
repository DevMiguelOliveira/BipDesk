using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
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
        private readonly UpdateService _updater;
        private readonly DispatcherTimer _pollTimer;

        private List<Machine> _rawMachines = new();
        private Machine? _selectedMachine;
        private CompanyGroup? _selectedGroup;
        private string _searchText = string.Empty;
        private string _activeFilter = "Todas"; // "Todas", "Online", "EmAtendimento", "Favoritos"
        private bool _isLoading;
        private bool _isGridView = true; // true = Cards, false = Tabela Densa
        private string _connectionStatus = "Conectando...";
        private string _connectionStatusColor = "#D29922";
        private string _newNoteText = string.Empty;
        private string _statusNotification = string.Empty;
        private bool _hasStatusNotification;

        private bool _hasUpdateAvailable;
        private string _updateVersionText = string.Empty;
        private string _updateDownloadUrl = string.Empty;

        public ObservableCollection<Machine> FilteredMachines { get; } = new();
        public ObservableCollection<CompanyGroup> Groups { get; } = new();
        public ObservableCollection<MachineNote> CurrentMachineNotes { get; } = new();
        public ObservableCollection<ActiveSession> ActiveSessions { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(ApiClient? api = null)
        {
            _api = api ?? new ApiClient();
            _launcher = new SessionLauncher();
            _updater = new UpdateService();

            ConnectCommand = new RelayCommand(async p => await ConnectToMachineAsync(p as Machine));
            RefreshCommand = new RelayCommand(async _ => await LoadDataAsync());
            SaveMachineInfoCommand = new RelayCommand(async _ => await SaveMachineInfoAsync());
            ToggleFavoriteCommand = new RelayCommand(async p => await ToggleFavoriteAsync(p as Machine));
            FocusSessionCommand = new RelayCommand(p => FocusSession(p as ActiveSession));
            CloseSessionCommand = new RelayCommand(async p => await CloseSessionAsync(p as ActiveSession));
            SetFilterCommand = new RelayCommand(p => SetFilter(p?.ToString() ?? "Todas"));
            ToggleViewCommand = new RelayCommand(p => ToggleView(p?.ToString()));
            
            // Comandos Remotos Silenciosos
            RebootCommand = new RelayCommand(async p => await RebootMachineAsync(p as Machine ?? SelectedMachine));
            SendMessageCommand = new RelayCommand(async p => await SendMessageToUserAsync(p as Machine ?? SelectedMachine));
            RestartSpoolerCommand = new RelayCommand(async p => await RestartSpoolerAsync(p as Machine ?? SelectedMachine));

            // Notas Técnicas
            AddNoteCommand = new RelayCommand(async _ => await AddNoteAsync());

            // Atualização Automática
            ApplyUpdateCommand = new RelayCommand(async _ => await ApplyUpdateAsync());
            CheckUpdateCommand = new RelayCommand(async _ => await CheckForUpdatesAsync(true));

            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _pollTimer.Tick += async (s, e) => await PollDataAsync();
        }

        public async Task InitializeAsync()
        {
            IsLoading = true;
            if (!string.IsNullOrWhiteSpace(_api.CurrentUsername) && (_api.CurrentUsername != "admin" || !string.IsNullOrWhiteSpace(_api.CurrentFullName)))
            {
                ConnectionStatus = "Nuvem Conectada";
                ConnectionStatusColor = "#00E5A3";
                await LoadDataAsync();
                _pollTimer.Start();
                _ = CheckForUpdatesAsync(false);
            }
            else
            {
                // Tenta login automático apenas se houver credenciais salvas
                var savedUser = System.IO.File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk", "credentials.txt"));
                if (!string.IsNullOrEmpty(savedUser))
                {
                    var parts = savedUser.Split('|');
                    if (parts.Length == 2)
                    {
                        bool logged = await _api.LoginAsync(parts[0], parts[1]);
                        if (logged)
                        {
                            ConnectionStatus = "Nuvem Conectada";
                            ConnectionStatusColor = "#00E5A3";
                            await LoadDataAsync();
                            _pollTimer.Start();
                            _ = CheckForUpdatesAsync(false);
                            IsLoading = false;
                            return;
                        }
                    }
                }
                ConnectionStatus = "Falha na conexão com a nuvem";
                ConnectionStatusColor = "#F8514F";
            }
            IsLoading = false;
        }

        public bool HasUpdateAvailable
        {
            get => _hasUpdateAvailable;
            set { _hasUpdateAvailable = value; OnPropertyChanged(); }
        }

        public string UpdateVersionText
        {
            get => _updateVersionText;
            set { _updateVersionText = value; OnPropertyChanged(); }
        }

        public bool IsAdmin => _api.CurrentRole == "admin";
        public string CurrentUsername => _api.CurrentUsername;
        public string CurrentRoleBadge => _api.CurrentRole == "admin" ? "Admin" : "Técnico";
        public string CurrentRoleColor => _api.CurrentRole == "admin" ? "#F2CC60" : "#58A6FF";
        public string CurrentRoleBg => _api.CurrentRole == "admin" ? "#332711" : "#121F33";
        public string CurrentDisplayName
        {
            get
            {
                if (_api.CurrentRole == "admin" && (_api.CurrentUsername == "admin" || string.IsNullOrWhiteSpace(_api.CurrentFullName) || _api.CurrentFullName.Contains("Admin")))
                {
                    return $"@{_api.CurrentUsername}";
                }
                return !string.IsNullOrWhiteSpace(_api.CurrentFullName) ? _api.CurrentFullName : $"@{_api.CurrentUsername}";
            }
        }
        public string CurrentTechnicianName => CurrentDisplayName;
        public ApiClient Api => _api;

        public void Logout()
        {
            _pollTimer.Stop();
            _api.Logout();
        }

        public bool IsGridView
        {
            get => _isGridView;
            set
            {
                if (_isGridView != value)
                {
                    _isGridView = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsTableView));
                }
            }
        }

        public bool IsTableView => !IsGridView;

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
            set
            {
                if (_activeFilter != value)
                {
                    _activeFilter = value;
                    OnPropertyChanged();
                    ApplyFilter();
                }
            }
        }

        public CompanyGroup? SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (_selectedGroup != value)
                {
                    _selectedGroup = value;
                    OnPropertyChanged();
                    ApplyFilter();
                }
            }
        }

        public Machine? SelectedMachine
        {
            get => _selectedMachine;
            set
            {
                if (_selectedMachine != value)
                {
                    _selectedMachine = value;
                    OnPropertyChanged();
                    _ = LoadNotesForSelectedMachineAsync();
                }
            }
        }

        public string NewNoteText
        {
            get => _newNoteText;
            set { _newNoteText = value; OnPropertyChanged(); }
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

        public string StatusNotification
        {
            get => _statusNotification;
            set
            {
                _statusNotification = value;
                _hasStatusNotification = !string.IsNullOrWhiteSpace(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatusNotification));
            }
        }

        public bool HasStatusNotification => _hasStatusNotification;

        public int TotalCount => _rawMachines.Count;
        public int OnlineCount => _rawMachines.Count(m => m.IsOnline);
        public int BusyCount => _rawMachines.Count(m => m.IsBusy);

        public ICommand ConnectCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SaveMachineInfoCommand { get; }
        public ICommand ToggleFavoriteCommand { get; }
        public ICommand FocusSessionCommand { get; }
        public ICommand CloseSessionCommand { get; }
        public ICommand SetFilterCommand { get; }
        public ICommand ToggleViewCommand { get; }
        public ICommand RebootCommand { get; }
        public ICommand SendMessageCommand { get; }
        public ICommand RestartSpoolerCommand { get; }
        public ICommand AddNoteCommand { get; }
        public ICommand ApplyUpdateCommand { get; }
        public ICommand CheckUpdateCommand { get; }

        public async Task LoadDataAsync()
        {
            var groupsTask = _api.GetGroupsAsync();
            var machinesTask = _api.GetMachinesAsync();

            await Task.WhenAll(groupsTask, machinesTask);

            UpdateGroupsList(await groupsTask);
            _rawMachines = await machinesTask;

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(OnlineCount));
            OnPropertyChanged(nameof(BusyCount));
            ApplyFilter();
        }

        private async Task PollDataAsync()
        {
            var groupsTask = _api.GetGroupsAsync();
            var machinesTask = _api.GetMachinesAsync();

            await Task.WhenAll(groupsTask, machinesTask);

            UpdateGroupsList(await groupsTask);
            _rawMachines = await machinesTask;

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(OnlineCount));
            OnPropertyChanged(nameof(BusyCount));
            ApplyFilter(keepSelection: true);
        }

        private void UpdateGroupsList(List<CompanyGroup> fetchedGroups)
        {
            var currentSelectedName = SelectedGroup?.Name;

            var allGroup = new CompanyGroup
            {
                Name = "Todas as Empresas",
                TotalMachines = fetchedGroups.Sum(g => g.TotalMachines),
                OnlineMachines = fetchedGroups.Sum(g => g.OnlineMachines)
            };

            var list = new List<CompanyGroup> { allGroup };
            list.AddRange(fetchedGroups);

            Groups.Clear();
            foreach (var g in list)
            {
                Groups.Add(g);
            }

            if (!string.IsNullOrEmpty(currentSelectedName))
            {
                SelectedGroup = Groups.FirstOrDefault(g => g.Name == currentSelectedName) ?? Groups[0];
            }
            else if (SelectedGroup == null && Groups.Count > 0)
            {
                SelectedGroup = Groups[0];
            }
        }

        public void ApplyFilter(bool keepSelection = false)
        {
            var currentId = SelectedMachine?.Id;

            var query = _rawMachines.AsEnumerable();

            // Filtro por Empresa / Grupo Selecionado na Barra Lateral
            if (SelectedGroup != null && SelectedGroup.Name != "Todas as Empresas")
            {
                query = query.Where(m => string.Equals(m.ClientGroup, SelectedGroup.Name, StringComparison.OrdinalIgnoreCase));
            }

            // Filtro de Tabs de Status
            if (ActiveFilter == "Online")
                query = query.Where(m => m.IsOnline);
            else if (ActiveFilter == "EmAtendimento")
                query = query.Where(m => m.IsBusy);
            else if (ActiveFilter == "Favoritos")
                query = query.Where(m => m.IsFavorite);

            // Filtro de Texto (Pesquisa instantânea em 1000+ máquinas)
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var term = SearchText.Trim().ToLower();
                query = query.Where(m =>
                    (m.Alias != null && m.Alias.ToLower().Contains(term)) ||
                    m.Hostname.ToLower().Contains(term) ||
                    (m.LoggedInUser != null && m.LoggedInUser.ToLower().Contains(term)) ||
                    (m.IpAddress != null && m.IpAddress.ToLower().Contains(term)) ||
                    (m.RustdeskId.ToLower().Contains(term)) ||
                    (m.Department != null && m.Department.ToLower().Contains(term)) ||
                    (m.Notes != null && m.Notes.ToLower().Contains(term)) ||
                    (m.ClientGroup != null && m.ClientGroup.ToLower().Contains(term)));
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

        private void ToggleView(string? mode)
        {
            if (mode == "Table")
                IsGridView = false;
            else if (mode == "Grid")
                IsGridView = true;
            else
                IsGridView = !IsGridView;
        }

        public async Task ConnectToMachineAsync(Machine? machine)
        {
            if (machine == null) machine = SelectedMachine;
            if (machine == null) return;

            // 1. Tenta obter o ticket de conexão
            var ticket = await _api.GetConnectTicketAsync(machine.Id, force: false);
            if (ticket == null)
            {
                MessageBox.Show($"Não foi possível conectar a {machine.DisplayName}. Servidor offline ou máquina inacessível.", "Erro BipDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. Proteção de Concorrência Multi-Técnico
            if (ticket.IsBusy && ticket.BusyBy != _api.CurrentUsername)
            {
                var result = MessageBox.Show(
                    $"ATENÇÃO: A máquina '{machine.DisplayName}' já está em atendimento pelo técnico [{ticket.BusyBy}]!\n\n" +
                    "Deseja assumir o controle desta máquina e sobrepor a sessão?",
                    "Concorrência de Atendimento - BipDesk",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                // Assume com force=true
                ticket = await _api.GetConnectTicketAsync(machine.Id, force: true);
                if (ticket == null || string.IsNullOrEmpty(ticket.Password))
                {
                    MessageBox.Show("Não foi possível assumir a conexão.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // 3. Inicia o BipDesk_Session nativo customizado (baixa automaticamente se ainda não existir)
            var proc = await _launcher.LaunchSessionAsync(machine, ticket.Password, async session =>
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    ActiveSessions.Remove(session);
                });
                // Libera o lock da sessão no servidor
                await _api.ReleaseSessionAsync(machine.Id);
                await PollDataAsync();
            }, msg => ShowNotification(msg));

            if (proc != null)
            {
                var session = new ActiveSession(machine, proc);
                ActiveSessions.Add(session);
                machine.IsBusy = true;
                machine.BusyBy = _api.CurrentUsername;
                OnPropertyChanged(nameof(BusyCount));
            }
        }

        public async Task CloseSessionAsync(ActiveSession? session)
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
                await _api.ReleaseSessionAsync(session.Machine.Id);
                await PollDataAsync();
            }
        }

        private void FocusSession(ActiveSession? session)
        {
            if (session != null)
            {
                _launcher.BringToFront(session.Process);
            }
        }

        // --- Ações Remotas Silenciosas ---

        private async Task RebootMachineAsync(Machine? machine)
        {
            if (machine == null) return;

            var confirm = MessageBox.Show(
                $"Deseja enviar comando para REINICIAR a máquina '{machine.DisplayName}' silenciosamente?",
                "Reiniciar Computador - BipDesk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.Yes)
            {
                bool ok = await _api.SendCommandAsync(machine.Id, "reboot");
                if (ok)
                {
                    ShowNotification($"Comando de reinício enviado para {machine.DisplayName}.");
                }
                else
                {
                    MessageBox.Show("Falha ao enfileirar comando.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task RestartSpoolerAsync(Machine? machine)
        {
            if (machine == null) return;

            bool ok = await _api.SendCommandAsync(machine.Id, "restart_spooler");
            if (ok)
            {
                ShowNotification($"Comando de reinício do Spooler de Impressão enviado para {machine.DisplayName}.");
            }
            else
            {
                MessageBox.Show("Falha ao enviar comando.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task SendMessageToUserAsync(Machine? machine)
        {
            if (machine == null) return;

            // Prompt de mensagem simples e direto
            var msgDialog = new Views.SendMessageDialog(machine.DisplayName);
            if (msgDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(msgDialog.MessageText))
            {
                bool ok = await _api.SendCommandAsync(machine.Id, "message", msgDialog.MessageText);
                if (ok)
                {
                    ShowNotification($"Mensagem enviada com sucesso para a tela de {machine.DisplayName}.");
                }
                else
                {
                    MessageBox.Show("Falha ao enviar mensagem.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // --- Linha do Tempo e Notas Técnicas ---

        private async Task LoadNotesForSelectedMachineAsync()
        {
            CurrentMachineNotes.Clear();
            if (SelectedMachine == null) return;

            var notes = await _api.GetMachineNotesAsync(SelectedMachine.Id);
            foreach (var n in notes)
            {
                CurrentMachineNotes.Add(n);
            }
        }

        private async Task AddNoteAsync()
        {
            if (SelectedMachine == null || string.IsNullOrWhiteSpace(NewNoteText)) return;

            var content = NewNoteText.Trim();
            bool ok = await _api.AddMachineNoteAsync(SelectedMachine.Id, content);
            if (ok)
            {
                NewNoteText = string.Empty;
                await LoadNotesForSelectedMachineAsync();
                ShowNotification("Nota registrada com sucesso na linha do tempo.");
            }
        }

        private async Task SaveMachineInfoAsync()
        {
            if (SelectedMachine == null) return;
            await _api.UpdateMachineAsync(
                SelectedMachine.Id,
                SelectedMachine.Alias,
                SelectedMachine.Notes,
                SelectedMachine.IsFavorite,
                SelectedMachine.ClientGroup,
                SelectedMachine.Department);

            ShowNotification($"Informações de {SelectedMachine.DisplayName} salvas.");
        }

        private async Task ToggleFavoriteAsync(Machine? machine)
        {
            if (machine == null) return;
            machine.IsFavorite = !machine.IsFavorite;
            await _api.UpdateMachineAsync(machine.Id, machine.Alias, machine.Notes, machine.IsFavorite, machine.ClientGroup, machine.Department);
            ApplyFilter(keepSelection: true);
        }

        public async Task<string?> GenerateEnrollTokenAsync()
        {
            return await _api.CreateEnrollTokenAsync();
        }

        public async Task CheckForUpdatesAsync(bool showNotificationIfUpToDate = false)
        {
            var info = await _updater.CheckForUpdateAsync();
            if (info != null)
            {
                HasUpdateAvailable = true;
                UpdateVersionText = $"Nova versão {info.Version} disponível!";
                _updateDownloadUrl = info.DownloadUrl;
            }
            else if (showNotificationIfUpToDate)
            {
                ShowNotification("O BipDesk Admin já está na versão mais recente.");
            }
        }

        public async Task ApplyUpdateAsync()
        {
            if (string.IsNullOrEmpty(_updateDownloadUrl)) return;
            ShowNotification("Baixando atualização do BipDesk Admin...");
            await _updater.DownloadAndApplyUpdateAsync(_updateDownloadUrl, msg => ShowNotification(msg));
        }

        private void ShowNotification(string message)
        {
            StatusNotification = message;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (s, e) =>
            {
                StatusNotification = string.Empty;
                timer.Stop();
            };
            timer.Start();
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
