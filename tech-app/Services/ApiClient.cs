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

        public ApiClient()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(10);
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
                    return true;
                }
            }
            catch
            {
                // Conexão offline ou falha
            }
            return false;
        }

        public async Task<List<Machine>> GetMachinesAsync()
        {
            try
            {
                var list = await _http.GetFromJsonAsync<List<Machine>>($"{BaseUrl}/api/machines");
                return list ?? new List<Machine>();
            }
            catch
            {
                return new List<Machine>();
            }
        }

        public async Task<ConnectTicket?> GetConnectTicketAsync(int machineId)
        {
            try
            {
                var resp = await _http.PostAsync($"{BaseUrl}/api/machines/{machineId}/connect", null);
                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadFromJsonAsync<ConnectTicket>();
                }
            }
            catch { }
            return null;
        }

        public async Task<bool> UpdateMachineAsync(int machineId, string? alias, string? notes, bool? isFavorite, string? clientGroup)
        {
            try
            {
                var payload = new
                {
                    alias,
                    notes,
                    is_favorite = isFavorite,
                    client_group = clientGroup
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
    }
}
