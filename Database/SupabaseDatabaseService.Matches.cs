using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using MixRanking.Models;
using MixRanking.Rating;

namespace MixRanking.Database;

public partial class SupabaseDatabaseService
{
    public async Task<int> GetPlayerTotalRoundsPlayedAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Post, "rpc/get_player_total_rounds", new { p_steamid = steamId });
        string json = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(json) || json == "null" ? 0 : int.Parse(json);
    }

    public async Task<int> GetActiveSeasonIdAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "seasons?is_active=eq.true&select=id&limit=1", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseSeasonIdRow>>(JsonOptions) ?? new();
        return rows.Count > 0 ? rows[0].Id : 1;
    }

    public async Task<RatingChange?> GetLastRatingChangeAsync(string steamId)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"rating_history?steamid=eq.{Uri.EscapeDataString(steamId)}&order=id.desc&limit=1", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseRatingHistoryRow>>(JsonOptions) ?? new();
        return rows.Count > 0 ? rows[0].ToRatingChange() : null;
    }

    public async Task<List<RatingChange>> GetRatingHistoryAsync(string steamId, int count = 10)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"rating_history?steamid=eq.{Uri.EscapeDataString(steamId)}&order=id.desc&limit={count}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseRatingHistoryRow>>(JsonOptions) ?? new();
        return rows.Select(r => r.ToRatingChange()).ToList();
    }

    public async Task<MatchRecord?> GetMatchByIdAsync(long matchId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"matches?id=eq.{matchId}", null);
        var rows = await response.Content.ReadFromJsonAsync<List<SupabaseMatchRow>>(JsonOptions) ?? new();
        return rows.Count > 0 ? rows[0].ToMatchRecord() : null;
    }

    public async Task WriteMatchEndResultAsync(MatchRecord match, List<MatchPlayerUpdate> updates, int initialRating, int activeSeasonId)
    {
        var payload = new
        {
            match = new
            {
                match_guid = match.MatchGuid,
                map = match.Map,
                winner_team = match.WinnerTeam,
                ct_score = match.CtScore,
                t_score = match.TScore,
                finished_at = match.FinishedAt.ToString("o"),
                season_id = activeSeasonId
            },
            updates = updates.Select(u => new
            {
                steamid = u.PlayerData.SteamId,
                player_name = u.Stats.PlayerName,
                old_rating = u.RatingChange.OldRating,
                base_change = u.RatingChange.BaseChange,
                performance_swing = u.RatingChange.PerformanceSwing,
                total_change = u.RatingChange.TotalChange,
                new_rating = u.NewRating,
                k_factor_used = u.RatingChange.KFactorUsed,
                won = u.Won,
                team = (int)u.Stats.Team,
                kills = u.Stats.Kills,
                deaths = u.Stats.Deaths,
                assists = u.Stats.Assists,
                damage = u.Stats.Damage,
                rounds_played = u.Stats.RoundsPlayed,
                rounds_survived = u.Stats.RoundsSurvived,
                rounds_with_kill = u.Stats.RoundsWithKill,
                mvps = u.Stats.Mvps,
                opening_kills = u.Stats.OpeningKills,
                opening_deaths = u.Stats.OpeningDeaths,
                trade_kills = u.Stats.TradeKills,
                flash_assists = u.Stats.FlashAssists,
                adr = u.Stats.Adr,
                kd_ratio = u.Stats.KdRatio,
                kast_percent = SwingCalculator.CalculateKastPercent(u.Stats),
                abandoned = u.Stats.Abandoned
            }),
            p_initial_rating = initialRating,
            p_active_season_id = activeSeasonId
        };

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Post, "rpc/write_match_end_result", payload);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                double delaySeconds = Math.Pow(2, attempt - 1);
                _logger.LogWarning(ex, "[MixRanking] write_match_end_result attempt {Attempt}/{Max} failed, retrying in {Delay}s.",
                    attempt, maxAttempts, delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }
    }
}
