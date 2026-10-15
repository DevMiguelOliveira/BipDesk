using System;
using System.Text.Json.Serialization;

namespace BipDesk.Tech.Models
{
    public class Machine
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("rustdesk_id")]
        public string RustdeskId { get; set; } = string.Empty;

        [JsonPropertyName("hostname")]
        public string Hostname { get; set; } = string.Empty;

        [JsonPropertyName("alias")]
        public string? Alias { get; set; }

        [JsonPropertyName("client_group")]
        public string ClientGroup { get; set; } = "Geral";

        [JsonPropertyName("department")]
        public string Department { get; set; } = "Geral";

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("is_favorite")]
        public bool IsFavorite { get; set; }

        [JsonPropertyName("is_online")]
        public bool IsOnline { get; set; }

        [JsonPropertyName("is_busy")]
        public bool IsBusy { get; set; }

        [JsonPropertyName("busy_by")]
        public string? BusyBy { get; set; }

        [JsonPropertyName("logged_in_user")]
        public string? LoggedInUser { get; set; }

        [JsonPropertyName("os_version")]
        public string? OsVersion { get; set; }

        [JsonPropertyName("ip_address")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("last_seen_at")]
        public DateTime LastSeenAt { get; set; }

        // Propriedades calculadas para exibição rápida e virtualizada
        public string DisplayName => !string.IsNullOrWhiteSpace(Alias) ? Alias : Hostname;

        public string StatusColor => !IsOnline ? "#8B949E" : (IsBusy ? "#F2CC60" : "#00E5A3");

        public string StatusBadgeBg => !IsOnline ? "#1B2028" : (IsBusy ? "#342811" : "#0A291E");

        public string StatusText => !IsOnline ? "Offline" : (IsBusy ? $"Em Atendimento ({BusyBy})" : "Online / Disponível");

        public string StatusShortText => !IsOnline ? "Offline" : (IsBusy ? $"Ocupado ({BusyBy})" : "Online");

        public string SubtitleText
        {
            get
            {
                var grp = string.IsNullOrWhiteSpace(ClientGroup) ? "Geral" : ClientGroup;
                var dep = string.IsNullOrWhiteSpace(Department) || Department == "Geral" ? "" : $" • {Department}";
                var usr = string.IsNullOrWhiteSpace(LoggedInUser) ? "Sem usuário" : LoggedInUser;
                return $"{grp}{dep} • {usr}";
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

        public string FormattedDate => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
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
    }
}
