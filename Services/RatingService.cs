using CounterStrikeSharp.API.Modules.Utils;
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
    private readonly DatabaseService _db;
    private readonly RankingConfig _config;

    public RatingService(DatabaseService db, RankingConfig config)
    {
        _db = db;
        _config = config;
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
        // 1. Insert match record
        var matchRecord = new MatchRecord
        {
            MatchGuid = matchGuid,
            Map = map,
            WinnerTeam = (int)winnerTeam,
            CtScore = ctScore,
            TScore = tScore,
            FinishedAt = DateTime.UtcNow
        };
        long matchId = await _db.InsertMatchAsync(matchRecord);

        // 2. Separate players by team and get current ratings
        var ctPlayers = new List<(MatchPlayerStats Stats, PlayerData Data)>();
        var tPlayers = new List<(MatchPlayerStats Stats, PlayerData Data)>();

        foreach (var (steamId, stats) in playerStats)
        {
            var playerData = await _db.GetOrCreatePlayerAsync(
                steamId.ToString(), stats.PlayerName, _config.InitialRating);

            if (stats.Team == CsTeam.CounterTerrorist)
                ctPlayers.Add((stats, playerData));
            else if (stats.Team == CsTeam.Terrorist)
                tPlayers.Add((stats, playerData));
        }

        // 3. Calculate team average ratings
        double ctAvgRating = ctPlayers.Count > 0
            ? ctPlayers.Average(p => p.Data.Rating)
            : _config.InitialRating;
        double tAvgRating = tPlayers.Count > 0
            ? tPlayers.Average(p => p.Data.Rating)
            : _config.InitialRating;

        // 4. Calculate rating changes for each player
        var ratingChanges = new List<RatingChange>();

        foreach (var (stats, playerData) in ctPlayers.Concat(tPlayers))
        {
            bool won = stats.Team == winnerTeam;
            double teamAvg = stats.Team == CsTeam.CounterTerrorist ? ctAvgRating : tAvgRating;
            double opponentAvg = stats.Team == CsTeam.CounterTerrorist ? tAvgRating : ctAvgRating;

            // Base Elo change
            int baseChange = EloCalculator.CalculateBaseChange(teamAvg, opponentAvg, won, _config.KFactor);

            // Performance swing
            int swing = SwingCalculator.CalculateSwing(stats, _config.MaxSwing);

            // Abandon penalty
            if (stats.Abandoned)
            {
                swing -= _config.AbandonPenalty;
            }

            // Total change
            int totalChange = baseChange + swing;

            // New rating (never below minimum)
            int newRating = Math.Max(playerData.Rating + totalChange, _config.MinRating);

            var ratingChange = new RatingChange
            {
                SteamId = playerData.SteamId,
                MatchId = matchId,
                OldRating = playerData.Rating,
                BaseChange = baseChange,
                PerformanceSwing = swing,
                TotalChange = newRating - playerData.Rating,
                NewRating = newRating,
                PlayerName = stats.PlayerName,
                Won = won
            };

            // 5. Persist to database
            await _db.UpdatePlayerAfterMatchAsync(
                playerData.SteamId, newRating, won,
                stats.Kills, stats.Deaths, stats.Assists,
                stats.Damage, stats.Mvps);

            await _db.InsertRatingChangeAsync(ratingChange);

            ratingChanges.Add(ratingChange);
        }

        return ratingChanges;
    }
}
