using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
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
            FlushDnsCommand = new RelayCommand(async p => await FlushDnsAsync(p as Machine ?? SelectedMachine));
            RenewIpCommand = new RelayCommand(async p => await RenewIpAsync(p as Machine ?? SelectedMachine));
            LockWorkstationCommand = new RelayCommand(async p => await LockWorkstationAsync(p as Machine ?? SelectedMachine));
            GpUpdateCommand = new RelayCommand(async p => await GpUpdateAsync(p as Machine ?? SelectedMachine));
            PingMachineCommand = new RelayCommand(async p => await PingMachineAsync(p as Machine ?? SelectedMachine));
            CopySummaryCommand = new RelayCommand(p => CopyMachineSummary(p as Machine ?? SelectedMachine));

            // Conexão Rápida e Ações Auxiliares (Estilo TeamViewer)
            QuickConnectCommand = new RelayCommand(async _ => await QuickConnectAsync());
            DeleteMachineCommand = new RelayCommand(async p => await DeleteMachineAsync(p as Machine ?? SelectedMachine));
            CopyIdCommand = new RelayCommand(p => CopyId(p?.ToString()));
            CopyIpCommand = new RelayCommand(p => CopyIp(p?.ToString()));

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
                ConnectionStatusColor = "#22C55E";
                await LoadDataAsync();
                _pollTimer.Start();
                _ = CheckForUpdatesAsync(false);
            }
            else
            {
                // Tenta login automático apenas se houver credenciais salvas
                var credPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BipDesk", "credentials.txt");
                if (File.Exists(credPath))
                {
                    try
                    {
                        var savedUser = File.ReadAllText(credPath);
                        if (!string.IsNullOrEmpty(savedUser))
                        {
                            var parts = savedUser.Split('|');
                            if (parts.Length == 2)
                            {
                                bool logged = await _api.LoginAsync(parts[0], parts[1]);
                                if (logged)
                                {
                                    ConnectionStatus = "Nuvem Conectada";
                                    ConnectionStatusColor = "#22C55E";
                                    await LoadDataAsync();
                                    _pollTimer.Start();
                                    _ = CheckForUpdatesAsync(false);
                                    IsLoading = false;
                                    return;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignora falha de leitura
                    }
                }
                ConnectionStatus = "Falha na conexão com a nuvem";
                ConnectionStatusColor = "#EF4444";
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

        private string _quickConnectId = string.Empty;
        public string QuickConnectId
        {
            get => _quickConnectId;
            set { _quickConnectId = value; OnPropertyChanged(); }
        }

        private int? _lastNotesMachineId = null;

        public Machine? SelectedMachine
        {
            get => _selectedMachine;
            set
            {
                if (_selectedMachine != value)
                {
                    _selectedMachine = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelectedMachine));
                    if (_selectedMachine != null && _selectedMachine.Id != _lastNotesMachineId)
                    {
                        _lastNotesMachineId = _selectedMachine.Id;
                        _ = LoadNotesForSelectedMachineAsync();
                    }
                    else if (_selectedMachine == null)
                    {
                        _lastNotesMachineId = null;
                        CurrentMachineNotes.Clear();
                    }
                }
            }
        }

        public bool HasSelectedMachine => SelectedMachine != null;

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
        public ICommand FlushDnsCommand { get; }
        public ICommand RenewIpCommand { get; }
        public ICommand LockWorkstationCommand { get; }
        public ICommand GpUpdateCommand { get; }
        public ICommand PingMachineCommand { get; }
        public ICommand CopySummaryCommand { get; }
        public ICommand AddNoteCommand { get; }
        public ICommand ApplyUpdateCommand { get; }
        public ICommand CheckUpdateCommand { get; }
        public ICommand QuickConnectCommand { get; }
        public ICommand DeleteMachineCommand { get; }
        public ICommand CopyIdCommand { get; }
        public ICommand CopyIpCommand { get; }

        public async Task LoadDataAsync()
        {
            var groupsTask = _api.GetGroupsAsync();
            var machinesTask = _api.GetMachinesAsync();

            await Task.WhenAll(groupsTask, machinesTask);

            var groups = await groupsTask;
            var machines = await machinesTask;

            void Apply()
            {
                UpdateGroupsList(groups);
                _rawMachines = machines;

                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(OnlineCount));
                OnPropertyChanged(nameof(BusyCount));
                ApplyFilter();
            }

            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                Application.Current.Dispatcher.Invoke(Apply);
            else
                Apply();
        }

        private async Task PollDataAsync()
        {
            try
            {
                var groupsTask = _api.GetGroupsAsync();
                var machinesTask = _api.GetMachinesAsync();

                await Task.WhenAll(groupsTask, machinesTask);

                var newGroups = await groupsTask;
                var newMachines = await machinesTask;

                void ExecutePollUpdate()
                {
                    UpdateGroupsList(newGroups);

                    // Atualiza as máquinas existentes EM-LOCAL (sem substituir as instâncias e sem limpar coleções)
                    var currentSelectedId = SelectedMachine?.Id;

                    var newMachineMap = newMachines.ToDictionary(m => m.Id);
                    var toRemove = new List<Machine>();

                    foreach (var existing in _rawMachines)
                    {
                        if (newMachineMap.TryGetValue(existing.Id, out var updated))
                        {
                            // Se o técnico estiver com a máquina aberta no drawer, preserva campos que ele pode estar digitando
                            bool isCurrentlyEditing = (existing.Id == currentSelectedId);
                            existing.UpdateFrom(updated, updateEditableFields: !isCurrentlyEditing);
                        }
                        else
                        {
                            toRemove.Add(existing);
                        }
                    }

                    // Remove máquinas excluídas
                    foreach (var rem in toRemove)
                    {
                        _rawMachines.Remove(rem);
                        FilteredMachines.Remove(rem);
                        if (SelectedMachine?.Id == rem.Id)
                        {
                            SelectedMachine = FilteredMachines.FirstOrDefault();
                        }
                    }

                    // Adiciona novas máquinas que apareceram
                    var existingIds = new HashSet<int>(_rawMachines.Select(m => m.Id));
                    foreach (var m in newMachines)
                    {
                        if (!existingIds.Contains(m.Id))
                        {
                            _rawMachines.Add(m);
                            if (IsMachineMatchingFilter(m))
                            {
                                FilteredMachines.Add(m);
                            }
                        }
                    }

                    OnPropertyChanged(nameof(TotalCount));
                    OnPropertyChanged(nameof(OnlineCount));
                    OnPropertyChanged(nameof(BusyCount));
                }

                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                    Application.Current.Dispatcher.Invoke(ExecutePollUpdate);
                else
                    ExecutePollUpdate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainViewModel] Erro no polling periódico: {ex.Message}");
            }
        }

        private bool IsMachineMatchingFilter(Machine m)
        {
            if (SelectedGroup != null && SelectedGroup.Name != "Todas as Empresas")
            {
                if (!string.Equals(m.ClientGroup, SelectedGroup.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (ActiveFilter == "Online" && !m.IsOnline) return false;
            if ((ActiveFilter == "EmAtendimento" || ActiveFilter == "Busy") && !m.IsBusy) return false;
            if (ActiveFilter == "Favoritos" && !m.IsFavorite) return false;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var term = SearchText.Trim().ToLower();
                return (m.Alias != null && m.Alias.ToLower().Contains(term)) ||
                       m.Hostname.ToLower().Contains(term) ||
                       (m.LoggedInUser != null && m.LoggedInUser.ToLower().Contains(term)) ||
                       (m.IpAddress != null && m.IpAddress.ToLower().Contains(term)) ||
                       (m.RustdeskId.ToLower().Contains(term)) ||
                       (m.Department != null && m.Department.ToLower().Contains(term)) ||
                       (m.Notes != null && m.Notes.ToLower().Contains(term)) ||
                       (m.ClientGroup != null && m.ClientGroup.ToLower().Contains(term));
            }

            return true;
        }

        private void UpdateGroupsList(List<CompanyGroup> fetchedGroups)
        {
            void ExecuteUpdate()
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

                // Atualiza contadores em-local para não disparar CollectionChanged se os nomes forem os mesmos
                if (Groups.Count == list.Count && Groups.Select(g => g.Name).SequenceEqual(list.Select(g => g.Name)))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        Groups[i].TotalMachines = list[i].TotalMachines;
                        Groups[i].OnlineMachines = list[i].OnlineMachines;
                    }
                    return;
                }

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

            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                Application.Current.Dispatcher.Invoke(ExecuteUpdate);
            else
                ExecuteUpdate();
        }

        public void ApplyFilter(bool keepSelection = false)
        {
            void ExecuteApply()
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
                else if (ActiveFilter == "EmAtendimento" || ActiveFilter == "Busy")
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
                        m.RustdeskId.ToLower().Contains(term) ||
                        (m.ClientGroup != null && m.ClientGroup.ToLower().Contains(term)));
                }

                FilteredMachines.Clear();
                foreach (var item in query)
                {
                    FilteredMachines.Add(item);
                }

                if (keepSelection && currentId.HasValue)
                {
                    var found = FilteredMachines.FirstOrDefault(m => m.Id == currentId.Value);
                    if (found != null && SelectedMachine?.Id != found.Id)
                    {
                        SelectedMachine = found;
                    }
                }
                else if (FilteredMachines.Count > 0 && SelectedMachine == null)
                {
                    SelectedMachine = FilteredMachines[0];
                }
            }

            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                Application.Current.Dispatcher.Invoke(ExecuteApply);
            else
                ExecuteApply();
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

            // Alerta amigável se o técnico tentar conectar ao próprio computador atual
            var localId = SessionLauncher.GetLocalRustDeskId();
            if (!string.IsNullOrEmpty(localId) && string.Equals(localId, machine.RustdeskId, StringComparison.OrdinalIgnoreCase))
            {
                var proceed = MessageBox.Show(
                    $"ATENÇÃO: A máquina selecionada '{machine.DisplayName}' (ID: {machine.RustdeskId}) é este mesmo computador atual onde você está operando o BipDesk Admin.\n\n" +
                    "O motor de conexão remota não permite que um computador controle a si mesmo em loop. Para testar o suporte remoto, instale o BipDesk Host em outro computador (físico ou máquina virtual) ou conecte-se a outro cliente da lista.\n\nDeseja mesmo assim tentar abrir a sessão local?",
                    "Acesso ao Próprio Dispositivo - BipDesk",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (proceed != MessageBoxResult.Yes)
                {
                    return;
                }
            }

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

            // 3. Inicia o motor de sessão oficial via protocolo URI rustdesk://
            var proc = await _launcher.LaunchSessionAsync(machine, ticket.Password, session =>
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    try
                    {
                        ActiveSessions.Remove(session);
                        session.Machine.IsBusy = false;
                        session.Machine.BusyBy = null;
                        OnPropertyChanged(nameof(BusyCount));
                    }
                    catch { }
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _api.ReleaseSessionAsync(machine.Id);
                    }
                    catch { }
                });
            }, msg => ShowNotification(msg));

            if (proc != null)
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    var existingSession = ActiveSessions.FirstOrDefault(s => s.Machine.Id == machine.Id);
                    if (existingSession == null)
                    {
                        var session = new ActiveSession(machine, proc);
                        ActiveSessions.Add(session);
                    }
                    machine.IsBusy = true;
                    machine.BusyBy = _api.CurrentUsername;
                    OnPropertyChanged(nameof(BusyCount));
                });
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

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    ActiveSessions.Remove(session);
                    session.Machine.IsBusy = false;
                    session.Machine.BusyBy = null;
                    OnPropertyChanged(nameof(BusyCount));
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _api.ReleaseSessionAsync(session.Machine.Id);
                    }
                    catch { }
                });
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

        private async Task FlushDnsAsync(Machine? machine)
        {
            if (machine == null) return;
            bool ok = await _api.SendCommandAsync(machine.Id, "flush_dns");
            if (ok)
            {
                ShowNotification($"Comando para limpar cache DNS enviado para {machine.DisplayName}.");
            }
            else
            {
                MessageBox.Show("Falha ao enviar comando.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RenewIpAsync(Machine? machine)
        {
            if (machine == null) return;
            bool ok = await _api.SendCommandAsync(machine.Id, "renew_ip");
            if (ok)
            {
                ShowNotification($"Comando para renovar IP (DHCP) enviado para {machine.DisplayName}.");
            }
            else
            {
                MessageBox.Show("Falha ao enviar comando.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LockWorkstationAsync(Machine? machine)
        {
            if (machine == null) return;
            bool ok = await _api.SendCommandAsync(machine.Id, "lock_workstation");
            if (ok)
            {
                ShowNotification($"Comando para bloquear estação de trabalho enviado para {machine.DisplayName}.");
            }
            else
            {
                MessageBox.Show("Falha ao enviar comando.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GpUpdateAsync(Machine? machine)
        {
            if (machine == null) return;
            bool ok = await _api.SendCommandAsync(machine.Id, "gpupdate");
            if (ok)
            {
                ShowNotification($"Comando gpupdate /force enviado para {machine.DisplayName}.");
            }
            else
            {
                MessageBox.Show("Falha ao enviar comando.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyMachineSummary(Machine? machine)
        {
            if (machine == null) machine = SelectedMachine;
            if (machine == null) return;

            var summary = $"[BipDesk Suporte Técnico]\n" +
                          $"Dispositivo: {machine.DisplayName} ({machine.Hostname})\n" +
                          $"ID BipDesk: {machine.FormattedRustdeskId}\n" +
                          $"Endereço IP: {machine.IpAddress ?? "N/D"}\n" +
                          $"Usuário Ativo: {machine.LoggedInUser ?? "Sem usuário"}\n" +
                          $"Sistema: {machine.OsVersion ?? "Windows"}\n" +
                          $"Empresa / Cliente: {machine.ClientGroup}\n" +
                          $"Status: {machine.StatusText} (Último visto: {machine.FormattedLastSeen})";

            try
            {
                Clipboard.SetText(summary);
                ShowNotification("Dados completos do dispositivo copiados para o chamado!");
            }
            catch (Exception ex)
            {
                ShowNotification($"Erro ao copiar dados: {ex.Message}");
            }
        }

        private async Task PingMachineAsync(Machine? machine)
        {
            if (machine == null) machine = SelectedMachine;
            if (machine == null) return;

            var ip = machine.IpAddress;
            if (string.IsNullOrWhiteSpace(ip) || ip == "N/D")
            {
                ShowNotification("Endereço IP não informado para este dispositivo.");
                return;
            }

            try
            {
                ShowNotification($"Testando ping para {ip}...");
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, 2000);
                if (reply.Status == IPStatus.Success)
                {
                    ShowNotification($"Ping para {ip}: {reply.RoundtripTime}ms (Respondendo!)");
                }
                else
                {
                    ShowNotification($"Ping para {ip}: {reply.Status} (Sem resposta direta)");
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"Erro ao testar conectividade: {ex.Message}");
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

        public async Task QuickConnectAsync()
        {
            var target = !string.IsNullOrWhiteSpace(SearchText) ? SearchText : QuickConnectId;
            if (string.IsNullOrWhiteSpace(target))
            {
                if (SelectedMachine != null)
                {
                    await ConnectToMachineAsync(SelectedMachine);
                    return;
                }
                MessageBox.Show("Digite o ID ou selecione uma máquina para conectar.", "Conexão Rápida - BipDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var cleanTarget = target.Replace(" ", "").Trim();

            // 1. Procura se existe máquina na lista local cadastrada por ID exato ou nome
            var existing = _rawMachines.FirstOrDefault(m => 
                m.RustdeskId.Replace(" ", "").Trim() == cleanTarget ||
                string.Equals(m.DisplayName, target, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Hostname, target, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                await ConnectToMachineAsync(existing);
                return;
            }

            // Se filtrou apenas 1 máquina na busca atual
            if (FilteredMachines.Count == 1)
            {
                await ConnectToMachineAsync(FilteredMachines[0]);
                return;
            }

            // 2. Conexão direta por ID avulso (Estilo TeamViewer)
            ShowNotification($"Iniciando sessão para o ID {cleanTarget}...");
            var proc = await _launcher.LaunchDirectSessionAsync(cleanTarget, msg => ShowNotification(msg));
            if (proc != null)
            {
                var directMachine = new Machine
                {
                    Id = 0,
                    RustdeskId = cleanTarget,
                    Hostname = $"ID-{cleanTarget}",
                    Alias = $"Conexão Avulsa ({cleanTarget})"
                };
                var session = new ActiveSession(directMachine, proc);
                ActiveSessions.Add(session);
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (s, e) =>
                    {
                        try
                        {
                            Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                ActiveSessions.Remove(session);
                            });
                        }
                        catch { }
                    };
                }
                catch { }
            }
        }

        public async Task DeleteMachineAsync(Machine? machine)
        {
            if (machine == null) machine = SelectedMachine;
            if (machine == null || machine.Id <= 0) return;

            var res = MessageBox.Show(
                $"Deseja realmente desvincular e remover a máquina '{machine.DisplayName}' do painel BipDesk?\n\nEsta ação remove o registro do inventário na nuvem.",
                "Remover Máquina - BipDesk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                bool ok = await _api.DeleteMachineAsync(machine.Id);
                if (ok)
                {
                    _rawMachines.Remove(machine);
                    if (SelectedMachine?.Id == machine.Id)
                    {
                        SelectedMachine = null;
                    }
                    ShowNotification($"Dispositivo {machine.DisplayName} removido.");
                    await LoadDataAsync();
                }
                else
                {
                    MessageBox.Show("Não foi possível remover a máquina.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CopyId(string? id)
        {
            var target = !string.IsNullOrWhiteSpace(id) ? id : SelectedMachine?.RustdeskId;
            if (!string.IsNullOrWhiteSpace(target))
            {
                Clipboard.SetText(target);
                ShowNotification($"ID {target} copiado para a área de transferência!");
            }
        }

        private void CopyIp(string? ip)
        {
            var target = !string.IsNullOrWhiteSpace(ip) ? ip : SelectedMachine?.IpAddress;
            if (!string.IsNullOrWhiteSpace(target))
            {
                Clipboard.SetText(target);
                ShowNotification($"IP {target} copiado!");
            }
        }

        private async Task SaveMachineInfoAsync()
        {
            if (SelectedMachine == null) return;
            bool ok = await _api.UpdateMachineAsync(
                SelectedMachine.Id,
                SelectedMachine.Alias,
                SelectedMachine.Notes,
                SelectedMachine.IsFavorite,
                SelectedMachine.ClientGroup,
                SelectedMachine.Department);

            if (ok)
            {
                ShowNotification($"Informações de {SelectedMachine.DisplayName} salvas com sucesso.");
                await LoadDataAsync();
            }
            else
            {
                MessageBox.Show("Não foi possível salvar as alterações.", "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task ToggleFavoriteAsync(Machine? machine)
        {
            if (machine == null) machine = SelectedMachine;
            if (machine == null) return;
            machine.IsFavorite = !machine.IsFavorite;
            await _api.UpdateMachineAsync(machine.Id, machine.Alias, machine.Notes, machine.IsFavorite, machine.ClientGroup, machine.Department);
            ShowNotification(machine.IsFavorite ? $"★ {machine.DisplayName} adicionado aos favoritos!" : $"☆ {machine.DisplayName} removido dos favoritos.");
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
