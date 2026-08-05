using System.Net.Http.Json;
using MixRanking.Models;

namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task UpsertWebSyncQueueAsync(PlayerData player)
    {
        var row = new
        {
            steamid = player.SteamId,
            name = player.Name,
            rating = player.Rating,
            matches = player.Matches,
            wins = player.Wins,
            losses = player.Losses,
            kills = player.Kills,
            deaths = player.Deaths,
            assists = player.Assists,
            damage = player.Damage,
            mvps = player.Mvps,
            created_at = player.CreatedAt.ToString("o")
        };
        using var response = await SendAsync(HttpMethod.Post, "web_sync_queue", row,
            new Dictionary<string, string> { ["Prefer"] = "resolution=merge-duplicates" });
    }

    public async Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit)
    {
        using var response = await SendAsync(HttpMethod.Get, $"web_sync_queue?limit={limit}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseWebSyncQueueRow>>(JsonOptions) ?? new();
        return rows.Select(r => r.ToPlayerData()).ToList();
    }

    public async Task ClearWebSyncQueueEntriesAsync(List<string> steamIds)
    {
        if (steamIds.Count == 0) return;
        string idList = string.Join(",", steamIds.Select(Uri.EscapeDataString));
        using var response = await SendAsync(HttpMethod.Delete, $"web_sync_queue?steamid=in.({idList})", null);
    }

    public async Task<bool> IsWipePendingAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "web_sync_state?id=eq.1&select=wipe_pending", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseWipePendingRow>>(JsonOptions) ?? new();
        return rows.Count > 0 && rows[0].WipePending;
    }

    public async Task SetWipePendingAsync()
    {
        using var response = await SendAsync(HttpMethod.Patch, "web_sync_state?id=eq.1", new { wipe_pending = true });
    }

    public async Task ClearWipePendingAsync()
    {
        using var response = await SendAsync(HttpMethod.Patch, "web_sync_state?id=eq.1", new { wipe_pending = false });
    }
}
