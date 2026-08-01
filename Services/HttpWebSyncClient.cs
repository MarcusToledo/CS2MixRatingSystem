using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MixRanking.Config;
using MixRanking.Models;

namespace MixRanking.Services;

/// <summary>Cliente HTTP real do sync outbound para a plataforma web. Conexão sempre de saída.</summary>
public class HttpWebSyncClient : IWebSyncClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RankingConfig _config;

    public HttpWebSyncClient(RankingConfig config)
    {
        _config = config;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public Task<bool> SendPlayersAsync(IReadOnlyList<PlayerData> players)
    {
        var payload = new WebSyncBatchPayload
        {
            Players = players.Select(p => new WebSyncPlayerPayload
            {
                SteamId = p.SteamId,
                Name = p.Name,
                Rating = p.Rating,
                Matches = p.Matches,
                Wins = p.Wins,
                Losses = p.Losses,
                Kills = p.Kills,
                Deaths = p.Deaths,
                Assists = p.Assists,
                Damage = p.Damage,
                Mvps = p.Mvps,
                CreatedAt = p.CreatedAt
            }).ToList()
        };

        return PostAsync(payload);
    }

    public Task<bool> SendWipeAsync()
    {
        return PostAsync(new WebSyncWipePayload());
    }

    private async Task<bool> PostAsync<T>(T payload)
    {
        try
        {
            string json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, _config.WebSyncUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.WebSyncApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
