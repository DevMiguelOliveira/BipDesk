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

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("is_favorite")]
        public bool IsFavorite { get; set; }

        [JsonPropertyName("is_online")]
        public bool IsOnline { get; set; }

        [JsonPropertyName("logged_in_user")]
        public string? LoggedInUser { get; set; }

        [JsonPropertyName("os_version")]
        public string? OsVersion { get; set; }

        [JsonPropertyName("ip_address")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("last_seen_at")]
        public DateTime LastSeenAt { get; set; }

        // Propriedade calculada para exibição intuitiva
        public string DisplayName => !string.IsNullOrWhiteSpace(Alias) ? Alias : Hostname;

        public string StatusColor => IsOnline ? "#00E5A3" : "#8B949E";

        public string StatusText => IsOnline ? "Online" : $"Visto: {LastSeenAt.ToLocalTime():dd/MM HH:mm}";

        public string SubtitleText => $"{OsVersion ?? "Windows"} • {LoggedInUser ?? "Sem usuário"}";
    }

    public class ConnectTicket
    {
        [JsonPropertyName("rustdesk_id")]
        public string RustdeskId { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("expires_in_seconds")]
        public int ExpiresInSeconds { get; set; }
    }
}
