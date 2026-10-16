using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BipDesk.Tech.Models
{
    public class Machine : INotifyPropertyChanged
    {
        private int _id;
        private string _rustdeskId = string.Empty;
        private string _hostname = string.Empty;
        private string? _alias;
        private string _clientGroup = "Geral";
        private string _department = "Geral";
        private string? _notes;
        private bool _isFavorite;
        private bool _isOnline;
        private bool _isBusy;
        private string? _busyBy;
        private string? _loggedInUser;
        private string? _osVersion;
        private string? _ipAddress;
        private DateTime _lastSeenAt;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [JsonPropertyName("id")]
        public int Id
        {
            get => _id;
            set { if (_id != value) { _id = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("rustdesk_id")]
        public string RustdeskId
        {
            get => _rustdeskId;
            set
            {
                if (_rustdeskId != value)
                {
                    _rustdeskId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedRustdeskId));
                }
            }
        }

        [JsonPropertyName("hostname")]
        public string Hostname
        {
            get => _hostname;
            set
            {
                if (_hostname != value)
                {
                    _hostname = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        [JsonPropertyName("alias")]
        public string? Alias
        {
            get => _alias;
            set
            {
                if (_alias != value)
                {
                    _alias = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        [JsonPropertyName("client_group")]
        public string ClientGroup
        {
            get => _clientGroup;
            set
            {
                if (_clientGroup != value)
                {
                    _clientGroup = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SubtitleText));
                }
            }
        }

        [JsonPropertyName("department")]
        public string Department
        {
            get => _department;
            set
            {
                if (_department != value)
                {
                    _department = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SubtitleText));
                }
            }
        }

        [JsonPropertyName("notes")]
        public string? Notes
        {
            get => _notes;
            set { if (_notes != value) { _notes = value; OnPropertyChanged(); } }
        }

        [JsonPropertyName("is_favorite")]
        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FavoriteIcon));
                    OnPropertyChanged(nameof(FavoriteColor));
                }
            }
        }

        [JsonPropertyName("is_online")]
        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (_isOnline != value)
                {
                    _isOnline = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusColor));
                    OnPropertyChanged(nameof(StatusBadgeBg));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusShortText));
                    OnPropertyChanged(nameof(FormattedLastSeen));
                }
            }
        }

        [JsonPropertyName("is_busy")]
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusColor));
                    OnPropertyChanged(nameof(StatusBadgeBg));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusShortText));
                }
            }
        }

        [JsonPropertyName("busy_by")]
        public string? BusyBy
        {
            get => _busyBy;
            set
            {
                if (_busyBy != value)
                {
                    _busyBy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusShortText));
                }
            }
        }

        [JsonPropertyName("logged_in_user")]
        public string? LoggedInUser
        {
            get => _loggedInUser;
            set
            {
                if (_loggedInUser != value)
                {
                    _loggedInUser = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SubtitleText));
                }
            }
        }

        [JsonPropertyName("os_version")]
        public string? OsVersion
        {
            get => _osVersion;
            set
            {
                if (_osVersion != value)
                {
                    _osVersion = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OsAndIpText));
                }
            }
        }

        [JsonPropertyName("ip_address")]
        public string? IpAddress
        {
            get => _ipAddress;
            set
            {
                if (_ipAddress != value)
                {
                    _ipAddress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OsAndIpText));
                }
            }
        }

        [JsonPropertyName("last_seen_at")]
        public DateTime LastSeenAt
        {
            get => _lastSeenAt;
            set
            {
                if (_lastSeenAt != value)
                {
                    _lastSeenAt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedLastSeen));
                }
            }
        }

        public void UpdateFrom(Machine other, bool updateEditableFields = false)
        {
            IsOnline = other.IsOnline;
            IsBusy = other.IsBusy;
            BusyBy = other.BusyBy;
            LastSeenAt = other.LastSeenAt;
            LoggedInUser = other.LoggedInUser;
            IpAddress = other.IpAddress;
            OsVersion = other.OsVersion;
            Hostname = other.Hostname;
            RustdeskId = other.RustdeskId;

            if (updateEditableFields)
            {
                Alias = other.Alias;
                ClientGroup = other.ClientGroup;
                Department = other.Department;
                Notes = other.Notes;
                IsFavorite = other.IsFavorite;
            }
        }

        // Propriedades calculadas para exibição rápida e virtualizada
        public string DisplayName => !string.IsNullOrWhiteSpace(Alias) ? Alias : Hostname;

        public string FavoriteIcon => IsFavorite ? "★" : "☆";

        public string FavoriteColor => IsFavorite ? "#FBBF24" : "#64748B";

        public string StatusColor => !IsOnline ? "#94A3B8" : (IsBusy ? "#FBBF24" : "#22C55E");

        public string StatusBadgeBg => !IsOnline ? "#222935" : (IsBusy ? "#2D2311" : "#132B1E");

        public string StatusText => !IsOnline ? "Offline" : (IsBusy ? $"Em Atendimento ({BusyBy})" : "Online / Disponível");

        public string StatusShortText => !IsOnline ? "Offline" : (IsBusy ? $"Ocupado ({BusyBy})" : "Online");

        public string FormattedRustdeskId
        {
            get
            {
                if (string.IsNullOrWhiteSpace(RustdeskId)) return "---";
                if (RustdeskId.Length == 9)
                {
                    return $"{RustdeskId.Substring(0, 3)} {RustdeskId.Substring(3, 3)} {RustdeskId.Substring(6, 3)}";
                }
                if (RustdeskId.Length == 10)
                {
                    return $"{RustdeskId.Substring(0, 3)} {RustdeskId.Substring(3, 3)} {RustdeskId.Substring(6, 4)}";
                }
                return RustdeskId;
            }
        }

        public string FormattedLastSeen
        {
            get
            {
                if (IsOnline) return "Ativo agora";
                var utc = (LastSeenAt.Kind == DateTimeKind.Utc)
                    ? LastSeenAt
                    : DateTime.SpecifyKind(LastSeenAt, DateTimeKind.Utc);
                var local = utc.ToLocalTime();
                var diff = DateTime.Now - local;
                if (diff.TotalMinutes < 1 && diff.TotalMinutes >= 0) return "Há instantes";
                if (diff.TotalMinutes < 60 && diff.TotalMinutes >= 0) return $"Há {(int)diff.TotalMinutes} min";
                if (diff.TotalHours < 24 && diff.TotalHours >= 0) return $"Há {(int)diff.TotalHours} h";
                return local.ToString("dd/MM HH:mm");
            }
        }

        public string SubtitleText
        {
            get
            {
                var grp = string.IsNullOrWhiteSpace(ClientGroup) ? "Geral" : ClientGroup;
                var usr = string.IsNullOrWhiteSpace(LoggedInUser) ? "Sem usuário" : LoggedInUser;
                return $"{grp} • {usr}";
            }
        }

        public string OsAndIpText => $"{OsVersion ?? "Windows"} • IP: {IpAddress ?? "N/D"}";
    }

    public class ConnectTicket
    {
        [JsonPropertyName("rustdesk_id")]
        public string RustdeskId { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("expires_in_seconds")]
        public int ExpiresInSeconds { get; set; }

        [JsonPropertyName("is_busy")]
        public bool IsBusy { get; set; }

        [JsonPropertyName("busy_by")]
        public string? BusyBy { get; set; }
    }

    public class CompanyGroup
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Geral";

        [JsonPropertyName("total_machines")]
        public int TotalMachines { get; set; }

        [JsonPropertyName("online_machines")]
        public int OnlineMachines { get; set; }

        public string DisplayText => $"{Name} ({OnlineMachines}/{TotalMachines})";
    }

    public class MachineNote
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("author_username")]
        public string AuthorUsername { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        public string FormattedDate
        {
            get
            {
                var utc = (CreatedAt.Kind == DateTimeKind.Utc)
                    ? CreatedAt
                    : DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc);
                return utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            }
        }
    }

    public class Technician
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("full_name")]
        public string? FullName { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = "technician";

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        public string RoleBadge => Role == "admin" ? "Admin" : "Técnico";
    }
}
