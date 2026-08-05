using MixRanking.Models;

namespace MixRanking.Database;

/// <summary>Contrato de persistência do plugin. Implementações: SupabaseDatabaseService (produção), FakeDatabaseService (teste).</summary>
public interface IDatabaseService
{
    Task<bool> InitializeAsync();

    Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name, int initialRating);
    Task<PlayerData?> GetPlayerAsync(string steamId);
    Task<List<PlayerData>> GetTopPlayersAsync(int count = 10);
    Task<int> GetPlayerRankPositionAsync(string steamId);
    Task<int> GetTotalRankedPlayersAsync();
    Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId);
    Task<Dictionary<string, PlayerData>> GetPlayersBySteamIdsAsync(List<string> steamIds);

    Task<int> GetActiveSeasonIdAsync();

    Task<RatingChange?> GetLastRatingChangeAsync(string steamId);
    Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10);
    Task<MatchRecord?> GetMatchByIdAsync(long matchId);

    Task UpsertWebSyncQueueAsync(PlayerData player);
    Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit);
    Task ClearWebSyncQueueEntriesAsync(List<string> steamIds);
    Task<bool> IsWipePendingAsync();
    Task SetWipePendingAsync();
    Task ClearWipePendingAsync();

    Task WriteMatchEndResultAsync(MatchRecord match, List<MatchPlayerUpdate> updates, int initialRating, int activeSeasonId);
    Task SetPlayerRatingWithAuditAsync(string targetSteamId, int newRating, string? adminSteamId, string adminName, string? reason);
    Task ResetPlayerWithAuditAsync(string targetSteamId, int initialRating, string? adminSteamId, string adminName, string? reason);
    Task AdjustPlayerRatingWithAuditAsync(string targetSteamId, int amount, bool isAdd, int minRating, string? adminSteamId, string adminName, string? reason);
    Task ResetAllDataWithAuditAsync(string? adminSteamId, string adminName, string? reason);
}
