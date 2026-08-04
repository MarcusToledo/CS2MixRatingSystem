using System.Net.Http.Json;
using MixRanking.Models;

namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name, int initialRating)
    {
        var existing = await GetPlayerAsync(steamId);
        if (existing != null)
        {
            if (existing.Name != name)
            {
                using var patchResponse = await SendAsync(HttpMethod.Patch, $"players?steamid=eq.{Uri.EscapeDataString(steamId)}", new { name });
                existing.Name = name;
            }
            return existing;
        }

        using var response = await SendAsync(HttpMethod.Post, "players", new { steamid = steamId, name, rating = initialRating });
        return new PlayerData { SteamId = steamId, Name = name, Rating = initialRating };
    }

    public async Task<PlayerData?> GetPlayerAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"players?steamid=eq.{Uri.EscapeDataString(steamId)}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions);
        return rows is { Count: > 0 } ? rows[0].ToPlayerData() : null;
    }

    public async Task<List<PlayerData>> GetTopPlayersAsync(int count = 10)
    {
        using var response = await SendAsync(HttpMethod.Get, $"players?matches=gt.0&order=rating.desc&limit={count}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions) ?? new();
        return rows.Select(r => r.ToPlayerData()).ToList();
    }

    public async Task<int> GetPlayerRankPositionAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Get, "players?matches=gt.0&select=steamid,rating&order=rating.desc", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions) ?? new();
        int targetRating = rows.FirstOrDefault(r => r.SteamId == steamId)?.Rating ?? 0;
        return rows.Count(r => r.Rating > targetRating) + 1;
    }

    public async Task<int> GetTotalRankedPlayersAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "players?matches=gt.0&select=steamid", null,
            new Dictionary<string, string> { ["Prefer"] = "count=exact" });
        if (!response.Content.Headers.TryGetValues("Content-Range", out var values)) return 0;
        string contentRange = values.First();
        string totalPart = contentRange.Split('/').Last();
        return totalPart == "*" ? 0 : int.Parse(totalPart);
    }

    public async Task<Dictionary<string, PlayerData>> GetPlayersBySteamIdsAsync(List<string> steamIds)
    {
        if (steamIds.Count == 0) return new();
        string idList = string.Join(",", steamIds.Select(Uri.EscapeDataString));
        using var response = await SendAsync(HttpMethod.Get, $"players?steamid=in.({idList})", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabasePlayerRow>>(JsonOptions) ?? new();
        return rows.ToDictionary(r => r.SteamId, r => r.ToPlayerData());
    }
}
