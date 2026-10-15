using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using BipDesk.Tech.Models;

namespace BipDesk.Tech.Services
{
    public class ApiClient
    {
        private readonly HttpClient _http;
        private string? _token;

        public string BaseUrl { get; set; } = "https://bipdesk.duckdns.org";

        public string CurrentUsername { get; private set; } = "admin";
        public string? CurrentFullName { get; private set; } = "Administrador";
        public string CurrentRole { get; private set; } = "admin";

        public ApiClient()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(12);
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            try
            {
                var payload = new { username, password };
                var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/login", payload);
                if (!resp.IsSuccessStatusCode) return false;

                var data = await resp.Content.ReadFromJsonAsync<JsonElement>();
                if (data.TryGetProperty("access_token", out var tokenProp))
                {
                    _token = tokenProp.GetString();
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

                    if (data.TryGetProperty("username", out var u)) CurrentUsername = u.GetString() ?? username;
                    if (data.TryGetProperty("full_name", out var fn)) CurrentFullName = fn.GetString();
                    if (data.TryGetProperty("role", out var r)) CurrentRole = r.GetString() ?? "technician";

                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiClient] Login error: {ex.Message}");
            }
            return false;
        }

        public async Task<List<CompanyGroup>> GetGroupsAsync()
        {
            try
            {
                var list = await _http.GetFromJsonAsync<List<CompanyGroup>>($"{BaseUrl}/api/groups");
                return list ?? new List<CompanyGroup>();
            }
            catch
            {
                return new List<CompanyGroup>();
            }
        }

        public async Task<List<Machine>> GetMachinesAsync(string? group = null)
        {
            try
            {
                var url = $"{BaseUrl}/api/machines";
                if (!string.IsNullOrWhiteSpace(group) && group != "Todas")
                {
                    url += $"?group={Uri.EscapeDataString(group)}";
                }
                var list = await _http.GetFromJsonAsync<List<Machine>>(url);
                return list ?? new List<Machine>();
            }
            catch
            {
                return new List<Machine>();
            }
        }

        public async Task<ConnectTicket?> GetConnectTicketAsync(int machineId, bool force = false)
        {
            try
            {
                var url = $"{BaseUrl}/api/machines/{machineId}/connect?force={(force ? "true" : "false")}";
                var resp = await _http.PostAsync(url, null);
                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadFromJsonAsync<ConnectTicket>();
                }
            }
            catch { }
            return null;
        }

        public async Task<bool> ReleaseSessionAsync(int machineId)
        {
            try
            {
                var resp = await _http.PostAsync($"{BaseUrl}/api/machines/{machineId}/disconnect", null);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<bool> SendCommandAsync(int machineId, string commandType, string? payload = null)
        {
            try
            {
                var body = new { command_type = commandType, payload };
                var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/machines/{machineId}/commands", body);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<List<MachineNote>> GetMachineNotesAsync(int machineId)
        {
            try
            {
                var list = await _http.GetFromJsonAsync<List<MachineNote>>($"{BaseUrl}/api/machines/{machineId}/notes");
                return list ?? new List<MachineNote>();
            }
            catch
            {
                return new List<MachineNote>();
            }
        }

        public async Task<bool> AddMachineNoteAsync(int machineId, string content)
        {
            try
            {
                var body = new { content };
                var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/machines/{machineId}/notes", body);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<bool> UpdateMachineAsync(int machineId, string? alias, string? notes, bool? isFavorite, string? clientGroup, string? department = null)
        {
            try
            {
                var payload = new
                {
                    alias,
                    notes,
                    is_favorite = isFavorite,
                    client_group = clientGroup,
                    department
                };
                var resp = await _http.PatchAsJsonAsync($"{BaseUrl}/api/machines/{machineId}", payload);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<string?> CreateEnrollTokenAsync()
        {
            try
            {
                var resp = await _http.PostAsync($"{BaseUrl}/api/enroll-tokens", null);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<JsonElement>();
                    if (data.TryGetProperty("token", out var t))
                    {
                        return t.GetString();
                    }
                }
            }
            catch { }
            return null;
        }

        public async Task<bool> DeleteMachineAsync(int machineId)
        {
            try
            {
                var resp = await _http.DeleteAsync($"{BaseUrl}/api/machines/{machineId}");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<List<Technician>> GetTechniciansAsync()
        {
            try
            {
                var list = await _http.GetFromJsonAsync<List<Technician>>($"{BaseUrl}/api/technicians");
                return list ?? new List<Technician>();
            }
            catch
            {
                return new List<Technician>();
            }
        }

        public async Task<bool> CreateTechnicianAsync(string username, string password, string fullName, string role)
        {
            try
            {
                var body = new { username, password, full_name = fullName, role };
                var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/technicians", body);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<bool> DeleteTechnicianAsync(int techId)
        {
            try
            {
                var resp = await _http.DeleteAsync($"{BaseUrl}/api/technicians/{techId}");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public void Logout()
        {
            _token = null;
            _http.DefaultRequestHeaders.Authorization = null;
            CurrentUsername = "";
            CurrentFullName = null;
            CurrentRole = "technician";
        }
    }
}
