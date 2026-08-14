using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MixRanking.Config;
using MixRanking.Database;
using MixRanking.Models;
using MixRanking.Rating;

namespace MixRanking.Services;

/// <summary>
/// Orquestra o cálculo de rating ao final da partida.
/// Calcula Elo base + swing de performance para cada jogador.
/// </summary>
public class RatingService
{
    private readonly IDatabaseService _db;
    private readonly RankingConfig _config;
    private readonly WebSyncService _webSyncService;
    private readonly ILogger _logger;

    public RatingService(IDatabaseService db, RankingConfig config, WebSyncService webSyncService, ILogger logger)
    {
        _db = db;
        _config = config;
        _webSyncService = webSyncService;
        _logger = logger;
    }

    /// <summary>
    /// Processa o fim de uma partida: calcula ratings e persiste no banco.
    /// </summary>
    /// <param name="matchGuid">GUID único da partida.</param>
    /// <param name="map">Nome do mapa.</param>
    /// <param name="winnerTeam">Time vencedor (CsTeam).</param>
    /// <param name="ctScore">Placar CT.</param>
    /// <param name="tScore">Placar T.</param>
    /// <param name="playerStats">Estatísticas de todos os jogadores da partida.</param>
    /// <returns>Lista de mudanças de rating para exibição.</returns>
    public async Task<List<RatingChange>> ProcessMatchEndAsync(
        string matchGuid,
        string map,
        CsTeam winnerTeam,
        int ctScore,
        int tScore,
        Dictionary<ulong, MatchPlayerStats> playerStats)
    {
        // 1. Separate players by team and batch retrieve player data
        var steamIds = playerStats.Keys.Select(k => k.ToString()).ToList();
        var existingPlayers = await _db.GetPlayersBySteamIdsAsync(steamIds);

        var ctPlayers = new List<(MatchPlayerStats Stats, PlayerData Data)>();
        var tPlayers = new List<(MatchPlayerStats Stats, PlayerData Data)>();

        foreach (var (steamId, stats) in playerStats)
        {
            string steamIdStr = steamId.ToString();
            if (!existingPlayers.TryGetValue(steamIdStr, out var playerData))
            {
                playerData = new PlayerData
                {
                    SteamId = steamIdStr,
                    Name = stats.PlayerName,
                    Rating = _config.InitialRating,
                    Matches = 0
                };
            }

            if (stats.Team == CsTeam.CounterTerrorist)
                ctPlayers.Add((stats, playerData));
            else if (stats.Team == CsTeam.Terrorist)
                tPlayers.Add((stats, playerData));
        }

        // 2. Calculate team average ratings
        double ctAvgRating = ctPlayers.Count > 0
            ? ctPlayers.Average(p => p.Data.Rating)
            : _config.InitialRating;
        double tAvgRating = tPlayers.Count > 0
            ? tPlayers.Average(p => p.Data.Rating)
            : _config.InitialRating;

        // 3. Calculate rating changes for each player
        var ratingChanges = new List<RatingChange>();
        var playerUpdates = new List<MatchPlayerUpdate>();

        int activeSeasonId = await _db.GetActiveSeasonIdAsync();

        foreach (var (stats, playerData) in ctPlayers.Concat(tPlayers))
        {
            bool won = stats.Team == winnerTeam;
            double teamAvg = stats.Team == CsTeam.CounterTerrorist ? ctAvgRating : tAvgRating;
            double opponentAvg = stats.Team == CsTeam.CounterTerrorist ? tAvgRating : ctAvgRating;

            // Placement match K-factor logic
            int effectiveK = playerData.Matches < _config.PlacementMatchCount
                ? (int)Math.Round(_config.KFactor * _config.PlacementKFactorMultiplier)
                : _config.KFactor;

            // Base Elo change
            int baseChange = EloCalculator.CalculateBaseChange(teamAvg, opponentAvg, won, effectiveK);

            // Performance swing
            int swing = SwingCalculator.CalculateSwing(stats, _config.MaxSwing);

            // Abandon penalty
            if (stats.Abandoned)
            {
                swing -= _config.AbandonPenalty;
            }

            // Garante que vitória/derrota nunca sejam anuladas pelo swing — exceto para
            // abandonos, cuja AbandonPenalty é a penalidade intencional e não deve ser
            // neutralizada por essa garantia.
            if (!stats.Abandoned)
            {
                baseChange = EloCalculator.ApplyMinimumChangeGuarantee(baseChange, swing, won, _config.MinRatingChangeMagnitude);
            }

            // Total change
            int totalChange = baseChange + swing;

            // New rating (never below minimum)
            int newRating = Math.Max(playerData.Rating + totalChange, _config.MinRating);

            var ratingChange = new RatingChange
            {
                SteamId = playerData.SteamId,
                MatchId = 0, // Will be updated during transactional write
                OldRating = playerData.Rating,
                BaseChange = baseChange,
                PerformanceSwing = swing,
                TotalChange = newRating - playerData.Rating,
                NewRating = newRating,
                PlayerName = stats.PlayerName,
                Won = won,
                KFactorUsed = effectiveK
            };

            ratingChanges.Add(ratingChange);

            playerUpdates.Add(new MatchPlayerUpdate
            {
                Stats = stats,
                PlayerData = playerData,
                RatingChange = ratingChange,
                NewRating = newRating,
                Won = won
            });
        }

        // 4. Persist match record and updates in a single transaction
        var matchRecord = new MatchRecord
        {
            MatchGuid = matchGuid,
            Map = map,
            WinnerTeam = (int)winnerTeam,
            CtScore = ctScore,
            TScore = tScore,
            FinishedAt = DateTime.UtcNow
        };

        await _db.WriteMatchEndResultAsync(matchRecord, playerUpdates, _config.InitialRating, activeSeasonId);

        // 5. Web sync dirty-marking: aditivo, não crítico — falhas aqui nunca podem
        // impedir a devolução dos ratingChanges (o rating já foi persistido acima).
        try
        {
            var updatedSteamIds = playerUpdates.Select(u => u.PlayerData.SteamId).ToList();
            var freshPlayers = await _db.GetPlayersBySteamIdsAsync(updatedSteamIds);
            foreach (var freshPlayer in freshPlayers.Values)
            {
                await _webSyncService.MarkDirtyAsync(freshPlayer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MixRanking] Failed to mark players for web sync after match {MatchGuid}.", matchGuid);
        }

        return ratingChanges;
    }
}
