// MixRanking.Tests/FakeDatabaseService.cs
using MixRanking.Database;
using MixRanking.Models;

namespace MixRanking.Tests;

/// <summary>Fake em memória de IDatabaseService, reutilizado pelos testes que hoje usam SQLite em memória.</summary>
internal class FakeDatabaseService : IDatabaseService
{
    public Dictionary<string, PlayerData> Players { get; } = new();
    public List<MatchRecord> Matches { get; } = new();
    public List<RatingChange> RatingHistory { get; } = new();
    public Dictionary<string, PlayerData> WebSyncQueue { get; } = new();
    public bool WipePending { get; set; }
    public List<AdminAuditEntry> AdminAuditLog { get; } = new();

    private readonly List<(string SteamId, int RoundsPlayed)> _roundsPlayedLog = new();
    private long _nextMatchId = 1;
    private long _nextRatingHistoryId = 1;

    public record AdminAuditEntry(string Action, string? AdminSteamId, string AdminName, string? TargetSteamId, int? OldValue, int? NewValue, string? Reason);

    public Task<bool> InitializeAsync() => Task.FromResult(true);

    public Task<PlayerData> GetOrCreatePlayerAsync(string steamId, string name, int initialRating)
    {
        if (Players.TryGetValue(steamId, out var existing))
        {
            existing.Name = name;
            return Task.FromResult(Clone(existing));
        }
        var player = new PlayerData { SteamId = steamId, Name = name, Rating = initialRating };
        Players[steamId] = player;
        return Task.FromResult(Clone(player));
    }

    public Task<PlayerData?> GetPlayerAsync(string steamId)
        => Task.FromResult(Players.TryGetValue(steamId, out var p) ? Clone(p) : null);

    public Task<List<PlayerData>> GetTopPlayersAsync(int count = 10)
    {
        var top = Players.Values.Where(p => p.Matches > 0)
            .OrderByDescending(p => p.Rating)
            .Take(count)
            .Select(Clone)
            .ToList();
        return Task.FromResult(top);
    }

    public Task<int> GetPlayerRankPositionAsync(string steamId)
    {
        int targetRating = Players.TryGetValue(steamId, out var p) ? p.Rating : 0;
        int position = Players.Values.Count(x => x.Matches > 0 && x.Rating > targetRating) + 1;
        return Task.FromResult(position);
    }

    public Task<int> GetTotalRankedPlayersAsync()
        => Task.FromResult(Players.Values.Count(p => p.Matches > 0));

    public Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId)
        => Task.FromResult(_roundsPlayedLog.Where(r => r.SteamId == steamId).Sum(r => r.RoundsPlayed));

    public Task<Dictionary<string, PlayerData>> GetPlayersBySteamIdsAsync(List<string> steamIds)
    {
        var result = new Dictionary<string, PlayerData>();
        foreach (var id in steamIds)
        {
            if (Players.TryGetValue(id, out var p)) result[id] = Clone(p);
        }
        return Task.FromResult(result);
    }

    public Task<int> GetActiveSeasonIdAsync() => Task.FromResult(1);

    public Task<RatingChange?> GetLastRatingChangeAsync(string steamId)
    {
        var change = RatingHistory.Where(r => r.SteamId == steamId).OrderByDescending(r => r.Id).FirstOrDefault();
        return Task.FromResult(change == null ? null : Clone(change));
    }

    public Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10)
    {
        var changes = RatingHistory.Where(r => r.SteamId == steamId)
            .OrderByDescending(r => r.Id)
            .Take(count)
            .Select(Clone)
            .ToList();
        return Task.FromResult(changes);
    }

    public Task<MatchRecord?> GetMatchByIdAsync(long matchId)
        => Task.FromResult(Matches.FirstOrDefault(m => m.Id == matchId));

    public Task UpsertWebSyncQueueAsync(PlayerData player)
    {
        WebSyncQueue[player.SteamId] = Clone(player);
        return Task.CompletedTask;
    }

    public Task<List<PlayerData>> GetPendingWebSyncEntriesAsync(int limit)
        => Task.FromResult(WebSyncQueue.Values.Take(limit).Select(Clone).ToList());

    public Task ClearWebSyncQueueEntriesAsync(List<string> steamIds)
    {
        foreach (var id in steamIds) WebSyncQueue.Remove(id);
        return Task.CompletedTask;
    }

    public Task<bool> IsWipePendingAsync() => Task.FromResult(WipePending);
    public Task SetWipePendingAsync() { WipePending = true; return Task.CompletedTask; }
    public Task ClearWipePendingAsync() { WipePending = false; return Task.CompletedTask; }

    public Task WriteMatchEndResultAsync(MatchRecord match, List<MatchPlayerUpdate> updates, int initialRating, int activeSeasonId)
    {
        long matchId = _nextMatchId++;
        match.Id = matchId;
        Matches.Add(match);

        foreach (var update in updates)
        {
            var stats = update.Stats;
            var playerData = update.PlayerData;
            var ratingChange = update.RatingChange;
            ratingChange.MatchId = matchId;

            if (!Players.TryGetValue(playerData.SteamId, out var player))
            {
                player = new PlayerData { SteamId = playerData.SteamId, Name = stats.PlayerName, Rating = playerData.Rating };
                Players[playerData.SteamId] = player;
            }
            else
            {
                player.Name = stats.PlayerName;
            }

            player.Rating = update.NewRating;
            player.Matches += 1;
            if (update.Won) player.Wins += 1; else player.Losses += 1;
            player.Kills += stats.Kills;
            player.Deaths += stats.Deaths;
            player.Assists += stats.Assists;
            player.Damage += stats.Damage;
            player.Mvps += stats.Mvps;
            player.UpdatedAt = DateTime.UtcNow;

            // Espelha o schema real: rating_history não persiste PlayerName/Won (só existem em memória pro chat).
            RatingHistory.Add(new RatingChange
            {
                Id = _nextRatingHistoryId++,
                MatchId = matchId,
                SteamId = ratingChange.SteamId,
                OldRating = ratingChange.OldRating,
                BaseChange = ratingChange.BaseChange,
                PerformanceSwing = ratingChange.PerformanceSwing,
                TotalChange = ratingChange.TotalChange,
                NewRating = ratingChange.NewRating,
                KFactorUsed = ratingChange.KFactorUsed
            });

            _roundsPlayedLog.Add((playerData.SteamId, stats.RoundsPlayed));
        }

        return Task.CompletedTask;
    }

    public Task SetPlayerRatingWithAuditAsync(string targetSteamId, int newRating, string? adminSteamId, string adminName, string? reason)
    {
        if (!Players.TryGetValue(targetSteamId, out var player)) throw new Exception("Jogador não encontrado.");
        int oldRating = player.Rating;
        player.Rating = newRating;
        AdminAuditLog.Add(new AdminAuditEntry("SET", adminSteamId, adminName, targetSteamId, oldRating, newRating, reason));
        return Task.CompletedTask;
    }

    public Task ResetPlayerWithAuditAsync(string targetSteamId, int initialRating, string? adminSteamId, string adminName, string? reason)
    {
        if (!Players.TryGetValue(targetSteamId, out var player)) throw new Exception("Jogador não encontrado.");
        int oldRating = player.Rating;
        player.Rating = initialRating;
        player.Matches = 0; player.Wins = 0; player.Losses = 0;
        player.Kills = 0; player.Deaths = 0; player.Assists = 0; player.Damage = 0; player.Mvps = 0;
        AdminAuditLog.Add(new AdminAuditEntry("RESET", adminSteamId, adminName, targetSteamId, oldRating, initialRating, reason));
        return Task.CompletedTask;
    }

    public Task AdjustPlayerRatingWithAuditAsync(string targetSteamId, int amount, bool isAdd, int minRating, string? adminSteamId, string adminName, string? reason)
    {
        if (!Players.TryGetValue(targetSteamId, out var player)) throw new Exception("Jogador não encontrado.");
        int oldRating = player.Rating;
        int newRating = isAdd ? oldRating + amount : Math.Max(oldRating - amount, minRating);
        player.Rating = newRating;
        AdminAuditLog.Add(new AdminAuditEntry(isAdd ? "ADD" : "REMOVE", adminSteamId, adminName, targetSteamId, oldRating, newRating, reason));
        return Task.CompletedTask;
    }

    public Task ResetAllDataWithAuditAsync(int initialRating, string? adminSteamId, string adminName, string? reason)
    {
        AdminAuditLog.Add(new AdminAuditEntry("WIPE", adminSteamId, adminName, null, null, null, reason));
        foreach (var player in Players.Values)
        {
            player.Rating = initialRating;
            player.Matches = 0; player.Wins = 0; player.Losses = 0;
            player.Kills = 0; player.Deaths = 0; player.Assists = 0; player.Damage = 0; player.Mvps = 0;
        }
        Matches.Clear();
        RatingHistory.Clear();
        WebSyncQueue.Clear();
        _roundsPlayedLog.Clear();
        WipePending = true;
        return Task.CompletedTask;
    }

    private static PlayerData Clone(PlayerData p) => new()
    {
        SteamId = p.SteamId, Name = p.Name, Rating = p.Rating, Matches = p.Matches,
        Wins = p.Wins, Losses = p.Losses, Kills = p.Kills, Deaths = p.Deaths,
        Assists = p.Assists, Damage = p.Damage, Mvps = p.Mvps,
        CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt
    };

    private static RatingChange Clone(RatingChange r) => new()
    {
        Id = r.Id, MatchId = r.MatchId, SteamId = r.SteamId, OldRating = r.OldRating,
        BaseChange = r.BaseChange, PerformanceSwing = r.PerformanceSwing,
        TotalChange = r.TotalChange, NewRating = r.NewRating, KFactorUsed = r.KFactorUsed
    };
}
