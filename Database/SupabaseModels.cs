using System.Text.Json.Serialization;
using MixRanking.Models;

namespace MixRanking.Database;

internal class SupabasePlayerRow
{
    [JsonPropertyName("steamid")] public required string SteamId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("rating")] public int Rating { get; set; }
    [JsonPropertyName("matches")] public int Matches { get; set; }
    [JsonPropertyName("wins")] public int Wins { get; set; }
    [JsonPropertyName("losses")] public int Losses { get; set; }
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("deaths")] public int Deaths { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
    [JsonPropertyName("damage")] public long Damage { get; set; }
    [JsonPropertyName("mvps")] public int Mvps { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }

    public PlayerData ToPlayerData() => new()
    {
        SteamId = SteamId, Name = Name, Rating = Rating, Matches = Matches,
        Wins = Wins, Losses = Losses, Kills = Kills, Deaths = Deaths,
        Assists = Assists, Damage = Damage, Mvps = Mvps,
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
    };
}

internal class SupabaseSeasonIdRow
{
    [JsonPropertyName("id")] public int Id { get; set; }
}

internal class SupabaseMatchRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("match_guid")] public string MatchGuid { get; set; } = string.Empty;
    [JsonPropertyName("map")] public string Map { get; set; } = string.Empty;
    [JsonPropertyName("winner_team")] public int WinnerTeam { get; set; }
    [JsonPropertyName("ct_score")] public int CtScore { get; set; }
    [JsonPropertyName("t_score")] public int TScore { get; set; }
    [JsonPropertyName("finished_at")] public DateTime FinishedAt { get; set; }

    public MatchRecord ToMatchRecord() => new()
    {
        Id = Id, MatchGuid = MatchGuid, Map = Map, WinnerTeam = WinnerTeam,
        CtScore = CtScore, TScore = TScore, FinishedAt = FinishedAt
    };
}

internal class SupabaseRatingHistoryRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("match_id")] public long MatchId { get; set; }
    [JsonPropertyName("steamid")] public string SteamId { get; set; } = string.Empty;
    [JsonPropertyName("old_rating")] public int OldRating { get; set; }
    [JsonPropertyName("base_change")] public int BaseChange { get; set; }
    [JsonPropertyName("performance_swing")] public int PerformanceSwing { get; set; }
    [JsonPropertyName("total_change")] public int TotalChange { get; set; }
    [JsonPropertyName("new_rating")] public int NewRating { get; set; }
    [JsonPropertyName("k_factor_used")] public int? KFactorUsed { get; set; }

    public RatingChange ToRatingChange() => new()
    {
        Id = Id, MatchId = MatchId, SteamId = SteamId, OldRating = OldRating,
        BaseChange = BaseChange, PerformanceSwing = PerformanceSwing,
        TotalChange = TotalChange, NewRating = NewRating, KFactorUsed = KFactorUsed
    };
}

internal class SupabaseWebSyncQueueRow
{
    [JsonPropertyName("steamid")] public required string SteamId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("rating")] public int Rating { get; set; }
    [JsonPropertyName("matches")] public int Matches { get; set; }
    [JsonPropertyName("wins")] public int Wins { get; set; }
    [JsonPropertyName("losses")] public int Losses { get; set; }
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("deaths")] public int Deaths { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
    [JsonPropertyName("damage")] public long Damage { get; set; }
    [JsonPropertyName("mvps")] public int Mvps { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }

    public PlayerData ToPlayerData() => new()
    {
        SteamId = SteamId, Name = Name, Rating = Rating, Matches = Matches,
        Wins = Wins, Losses = Losses, Kills = Kills, Deaths = Deaths,
        Assists = Assists, Damage = Damage, Mvps = Mvps, CreatedAt = CreatedAt
    };
}

internal class SupabaseWipePendingRow
{
    [JsonPropertyName("wipe_pending")] public bool WipePending { get; set; }
}
